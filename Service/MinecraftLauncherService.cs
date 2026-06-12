﻿using AsetLauncher.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Security.Principal;

namespace AsetLauncher.Services
{
    public sealed class MinecraftLauncherService
    {
        private const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
        private const string AssetBaseUrl = "https://resources.download.minecraft.net";
        private const string FabricGameVersionsUrl = "https://meta.fabricmc.net/v2/versions/game";
        private const string FabricLoaderVersionsUrl = "https://meta.fabricmc.net/v2/versions/loader";
        private const string ForgePromotionsUrl = "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json";
        private const string ForgeMavenBaseUrl = "https://maven.minecraftforge.net/net/minecraftforge/forge";
        private const string FeaturedServerName = "AsetLauncher";
        private const string FeaturedServerAddress = "play.asetlauncher.ru";

        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 1024 };
        private static readonly HttpClient Http = CreateHttpClient();
        private static readonly ConcurrentDictionary<int, Process> RunningMinecraftProcesses = new ConcurrentDictionary<int, Process>();
        public static event Action<int> MinecraftProcessStarted;
        public static event Action<int, int> MinecraftProcessExited;

        private readonly string _root;
        private readonly string _versions;
        private readonly string _libraries;
        private readonly string _assets;
        private readonly string _runtime;
        private readonly string _temp;

        // Hosts file management constants
        private static readonly string HostsPath = @"C:\Windows\System32\drivers\etc\hosts";
        private static readonly string[] HostsRedirects = new[]
        {
            "127.0.0.1 authserver.mojang.com",
            "127.0.0.1 sessionserver.mojang.com",
            "127.0.0.1 api.minecraftservices.com",
            "127.0.0.1 services.minecraft.net",
            "127.0.0.1 account.mojang.com"
        };

        private static readonly string[] HostsDomainsToCheck = new[]
        {
            "authserver.mojang.com",
            "sessionserver.mojang.com",
            "api.minecraftservices.com",
            "services.minecraft.net",
            "account.mojang.com"
        };

        public static string GetMinecraftRootPath()
        {
            var roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(roamingAppData))
            {
                roamingAppData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "AppData",
                    "Roaming");
            }

            return Path.Combine(roamingAppData, "asetlaucher_minecraft");
        }

        public MinecraftLauncherService()
        {
            _root = GetMinecraftRootPath();
            _versions = Path.Combine(_root, "versions");
            _libraries = Path.Combine(_root, "libraries");
            _assets = Path.Combine(_root, "assets");
            _runtime = Path.Combine(_root, "runtime");
            _temp = Path.Combine(_root, "temp");

            var legacyExecutableRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "minecraft");
            var legacyLocalAppDataRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AsetLauncher",
                "minecraft");

            TryMigrateLegacyRoot(legacyExecutableRoot, _root);
            TryMigrateLegacyRoot(legacyLocalAppDataRoot, _root);

            Directory.CreateDirectory(_root);
            Directory.CreateDirectory(_versions);
            Directory.CreateDirectory(_libraries);
            Directory.CreateDirectory(_assets);
            Directory.CreateDirectory(_runtime);
            Directory.CreateDirectory(_temp);

            LauncherLogService.Info("Minecraft root: " + _root);
        }

        public async Task<List<MinecraftVersionEntry>> GetVanillaVersionsAsync(CancellationToken ct)
        {
            var manifest = await GetJsonObjectAsync(ManifestUrl, ct).ConfigureAwait(false);
            var versions = Arr(manifest, "versions");
            var list = new List<MinecraftVersionEntry>();

            foreach (var item in versions)
            {
                ct.ThrowIfCancellationRequested();
                var obj = Dict(item);
                if (obj == null)
                {
                    continue;
                }

                var id = Str(obj, "id");
                var type = Str(obj, "type", "unknown");
                var url = Str(obj, "url");
                var releaseRaw = Str(obj, "releaseTime");

                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                DateTime release;
                if (!DateTime.TryParse(releaseRaw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out release))
                {
                    release = DateTime.MinValue;
                }

                list.Add(new MinecraftVersionEntry
                {
                    Id = id,
                    Type = type,
                    ReleaseTime = release,
                    MetadataUrl = url
                });
            }

            return list.OrderByDescending(v => v.ReleaseTime).ThenByDescending(v => v.Id).ToList();
        }

        public async Task<List<MinecraftVersionEntry>> GetVanillaAndFabricVersionsAsync(CancellationToken ct)
        {
            var vanilla = await GetVanillaVersionsAsync(ct).ConfigureAwait(false);
            List<MinecraftVersionEntry> fabric;
            List<MinecraftVersionEntry> forge;
            try
            {
                fabric = await GetFabricVersionsAsync(vanilla, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LauncherLogService.Exception("Не удалось загрузить список Fabric версий. Используется только Vanilla.", ex);
                fabric = new List<MinecraftVersionEntry>();
            }
            try
            {
                forge = await GetForgeVersionsAsync(vanilla, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LauncherLogService.Exception("Не удалось загрузить список Forge версий. Используется только Vanilla/Fabric.", ex);
                forge = new List<MinecraftVersionEntry>();
            }

            var combined = new List<MinecraftVersionEntry>(vanilla.Count + fabric.Count + forge.Count);
            combined.AddRange(vanilla);
            combined.AddRange(fabric);
            combined.AddRange(forge);

            return combined
                .OrderByDescending(v => v.ReleaseTime)
                .ThenByDescending(v => v.Id)
                .ToList();
        }

        private async Task<List<MinecraftVersionEntry>> GetFabricVersionsAsync(List<MinecraftVersionEntry> vanillaVersions, CancellationToken ct)
        {
            var result = new List<MinecraftVersionEntry>();
            if (vanillaVersions == null || vanillaVersions.Count == 0)
            {
                return result;
            }

            var gameVersionsRaw = await GetJsonArrayAsync(FabricGameVersionsUrl, ct).ConfigureAwait(false);
            var loadersRaw = await GetJsonArrayAsync(FabricLoaderVersionsUrl, ct).ConfigureAwait(false);

            var stableLoader = loadersRaw
                .Select(Dict)
                .Where(d => d != null)
                .FirstOrDefault(d => ToBool(Obj(d, "stable")))
                ?? loadersRaw.Select(Dict).FirstOrDefault(d => d != null);

            var loaderVersion = Str(stableLoader, "version");
            if (string.IsNullOrWhiteSpace(loaderVersion))
            {
                return result;
            }

            var vanillaById = vanillaVersions
                .GroupBy(v => v.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToDictionary(v => v.Id, v => v, StringComparer.OrdinalIgnoreCase);

            foreach (var item in gameVersionsRaw)
            {
                ct.ThrowIfCancellationRequested();

                var game = Dict(item);
                if (game == null)
                {
                    continue;
                }

                var gameVersion = Str(game, "version");
                if (string.IsNullOrWhiteSpace(gameVersion))
                {
                    continue;
                }

                MinecraftVersionEntry vanillaEntry;
                if (!vanillaById.TryGetValue(gameVersion, out vanillaEntry))
                {
                    continue;
                }

                var isSnapshot = string.Equals(vanillaEntry.Type, "snapshot", StringComparison.OrdinalIgnoreCase);
                var fabricId = "fabric-loader-" + loaderVersion + "-" + gameVersion;
                var metadataUrl = "https://meta.fabricmc.net/v2/versions/loader/"
                    + Uri.EscapeDataString(gameVersion)
                    + "/"
                    + Uri.EscapeDataString(loaderVersion)
                    + "/profile/json";

                result.Add(new MinecraftVersionEntry
                {
                    Id = fabricId,
                    Type = isSnapshot ? "fabric-snapshot" : "fabric-release",
                    ReleaseTime = vanillaEntry.ReleaseTime,
                    MetadataUrl = metadataUrl,
                    DisplayName = gameVersion + " (Fabric)"
                });
            }

            return result;
        }

        private async Task<List<MinecraftVersionEntry>> GetForgeVersionsAsync(List<MinecraftVersionEntry> vanillaVersions, CancellationToken ct)
        {
            var result = new List<MinecraftVersionEntry>();
            if (vanillaVersions == null || vanillaVersions.Count == 0)
            {
                return result;
            }

            var promotions = await GetJsonObjectAsync(ForgePromotionsUrl, ct).ConfigureAwait(false);
            var promos = Dict(Obj(promotions, "promos"));
            if (promos == null || promos.Count == 0)
            {
                return result;
            }

            var vanillaById = vanillaVersions
                .GroupBy(v => v.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToDictionary(v => v.Id, v => v, StringComparer.OrdinalIgnoreCase);

            var selectedForgeByGameVersion = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in promos)
            {
                ct.ThrowIfCancellationRequested();

                var promoKey = pair.Key ?? string.Empty;
                var forgeVersion = Convert.ToString(pair.Value, CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(forgeVersion))
                {
                    continue;
                }

                string gameVersion;
                var isLatest = false;
                if (promoKey.EndsWith("-latest", StringComparison.OrdinalIgnoreCase))
                {
                    gameVersion = promoKey.Substring(0, promoKey.Length - "-latest".Length);
                    isLatest = true;
                }
                else if (promoKey.EndsWith("-recommended", StringComparison.OrdinalIgnoreCase))
                {
                    gameVersion = promoKey.Substring(0, promoKey.Length - "-recommended".Length);
                }
                else
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(gameVersion))
                {
                    continue;
                }

                if (isLatest || !selectedForgeByGameVersion.ContainsKey(gameVersion))
                {
                    selectedForgeByGameVersion[gameVersion] = forgeVersion.Trim();
                }
            }

            foreach (var pair in selectedForgeByGameVersion)
            {
                ct.ThrowIfCancellationRequested();

                var gameVersion = pair.Key;
                var forgeVersion = pair.Value;

                MinecraftVersionEntry vanillaEntry;
                if (!vanillaById.TryGetValue(gameVersion, out vanillaEntry))
                {
                    continue;
                }

                // Forge builds are tied to release versions.
                if (!string.Equals(vanillaEntry.Type, "release", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!SupportsForgeInstaller(gameVersion))
                {
                    continue;
                }
                var versionId = BuildForgeVersionId(gameVersion, forgeVersion);

                result.Add(new MinecraftVersionEntry
                {
                    Id = versionId,
                    Type = "forge-release",
                    ReleaseTime = vanillaEntry.ReleaseTime,
                    MetadataUrl = string.Empty,
                    DisplayName = gameVersion + " (Forge)"
                });
            }

            return result;
        }

        public async Task InstallAndLaunchAsync(MinecraftVersionEntry version, LauncherAccount account, LauncherSettings settings, IProgress<string> status, CancellationToken ct)
        {
            if (version == null)
            {
                throw new ArgumentNullException(nameof(version));
            }

            if (settings == null)
            {
                settings = new LauncherSettings();
            }

            if (account == null)
            {
                account = new LauncherAccount
                {
                    Type = "offline",
                    Nickname = Environment.UserName
                };
            }

            // Sync hosts file based on Minecraft version before installation/launch
            SyncAuthHostsRedirects(version.Id);

            if (IsForgeVersionEntry(version))
            {
                ReportStatus(status, "Установка Forge...");
                var resolvedForgeVersionId = await EnsureForgeInstalledAsync(version, status, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(resolvedForgeVersionId))
                {
                    version = new MinecraftVersionEntry
                    {
                        Id = resolvedForgeVersionId,
                        Type = version.Type,
                        ReleaseTime = version.ReleaseTime,
                        MetadataUrl = string.Empty,
                        DisplayName = version.DisplayName
                    };
                }
            }

            ReportStatus(status, "Загрузка метаданных версии...");
            var metadata = await ResolveVersionMetadataAsync(version, ct).ConfigureAwait(false);
            var versionId = Str(metadata, "id", version.Id);
            LauncherLogService.Info("Выбрана версия: " + versionId);

            var versionDir = Path.Combine(_versions, versionId);
            Directory.CreateDirectory(versionDir);
            File.WriteAllText(Path.Combine(versionDir, versionId + ".json"), Json.Serialize(metadata), Encoding.UTF8);

            ReportStatus(status, "Проверка Java runtime...");
            var javaMajor = GetRequiredJavaMajor(metadata, versionId);
            var javaExe = await EnsureJavaRuntimeAsync(javaMajor, status, ct).ConfigureAwait(false);

            ReportStatus(status, "Установка клиента...");
            var downloads = Dict(Obj(metadata, "downloads"));
            var client = Dict(downloads != null ? Obj(downloads, "client") : null);
            if (client == null)
            {
                throw new InvalidOperationException("В версии нет downloads.client.");
            }

            var clientJar = Path.Combine(versionDir, versionId + ".jar");
            await DownloadIfNeededAsync(Str(client, "url"), clientJar, Str(client, "sha1"), ct).ConfigureAwait(false);

            ReportStatus(status, "Установка библиотек...");
            var nativesDir = Path.Combine(versionDir, versionId + "-natives");
            RecreateDirectory(nativesDir);
            var classpath = await EnsureLibrariesAsync(metadata, nativesDir, status, ct).ConfigureAwait(false);
            classpath.Add(clientJar);

            ReportStatus(status, "Установка ассетов...");
            var assetIndexId = await EnsureAssetsAsync(metadata, status, ct).ConfigureAwait(false);

            EnsureFeaturedServerInListSafe();

            ReportStatus(status, "Запуск...");
            var args = BuildLaunchArgs(metadata, versionId, account, classpath, nativesDir, assetIndexId, settings);
            StartMinecraft(javaExe, args);
        }

        private async Task<string> EnsureForgeInstalledAsync(MinecraftVersionEntry version, IProgress<string> status, CancellationToken ct)
        {
            string gameVersion;
            string forgeVersion;
            if (!TryParseForgeDescriptor(version, out gameVersion, out forgeVersion))
            {
                throw new InvalidOperationException("Не удалось определить версию Forge для установки.");
            }

            if (!SupportsForgeInstaller(gameVersion))
            {
                throw new InvalidOperationException(
                    "Автоматическая установка Forge поддерживается для версий Minecraft 1.12+.");
            }

            var expectedVersionId = BuildForgeVersionId(gameVersion, forgeVersion);
            if (HasLocalVersionMetadata(expectedVersionId))
            {
                LauncherLogService.Info("Forge уже установлена: " + expectedVersionId);
                return expectedVersionId;
            }

            if (HasLocalVersionMetadata(version.Id))
            {
                LauncherLogService.Info("Forge уже установлена: " + version.Id);
                return version.Id;
            }

            var installerFolder = Path.Combine(_temp, "forge-installer");
            Directory.CreateDirectory(installerFolder);
            EnsureForgeLauncherProfile();

            var installerFileName = "forge-" + gameVersion + "-" + forgeVersion + "-installer.jar";
            var installerPath = Path.Combine(installerFolder, installerFileName);
            var installerUrl = ForgeMavenBaseUrl
                + "/"
                + Uri.EscapeDataString(gameVersion + "-" + forgeVersion)
                + "/"
                + Uri.EscapeDataString("forge-" + gameVersion + "-" + forgeVersion + "-installer.jar");

            ReportStatus(status, "Скачивание Forge installer...");
            await DownloadIfNeededAsync(installerUrl, installerPath, null, ct).ConfigureAwait(false);

            var installerProfileVersion = TryReadForgeInstallerVersionId(installerPath);
            if (!string.IsNullOrWhiteSpace(installerProfileVersion))
            {
                expectedVersionId = installerProfileVersion;
            }

            var installProfile = TryReadForgeInstallProfile(installerPath);
            if (installProfile != null)
            {
                ReportStatus(status, "Подготовка зависимостей Forge...");
                await EnsureForgeInstallerLibrariesAsync(installProfile, status, ct).ConfigureAwait(false);
            }

            if (HasLocalVersionMetadata(expectedVersionId))
            {
                LauncherLogService.Info("Forge уже установлена: " + expectedVersionId);
                return expectedVersionId;
            }

            var javaMajor = GetRequiredJavaMajorByGameVersion(gameVersion);
            var javaExe = await EnsureJavaRuntimeAsync(javaMajor, status, ct).ConfigureAwait(false);

            ReportStatus(status, "Запуск Forge installer...");
            Exception firstInstallError = null;
            try
            {
                await RunLoggedProcessAsync(
                    javaExe,
                    "-jar " + Quote(installerPath) + " --installClient " + Quote(_root),
                    _root,
                    "FORGE-INSTALL",
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                firstInstallError = ex;
                LauncherLogService.Exception("Forge installer (режим с путём) завершился ошибкой, пробуем альтернативный режим.", ex);

                if (installProfile != null)
                {
                    await EnsureForgeInstallerLibrariesAsync(installProfile, status, ct).ConfigureAwait(false);
                }

                await RunLoggedProcessAsync(
                    javaExe,
                    "-jar " + Quote(installerPath) + " --installClient",
                    _root,
                    "FORGE-INSTALL",
                    ct).ConfigureAwait(false);
            }

            if (HasLocalVersionMetadata(expectedVersionId))
            {
                return expectedVersionId;
            }

            var discoveredVersionId = FindInstalledForgeVersionId(gameVersion, forgeVersion);
            if (!string.IsNullOrWhiteSpace(discoveredVersionId))
            {
                return discoveredVersionId;
            }

            if (firstInstallError != null)
            {
                throw new InvalidOperationException(
                    "Forge installer завершился, но установленная версия не найдена. Первая ошибка: " + firstInstallError.Message);
            }

            throw new InvalidOperationException("Forge installer завершился, но установленная версия не найдена.");
        }

        private async Task<Dictionary<string, object>> ResolveVersionMetadataAsync(MinecraftVersionEntry version, CancellationToken ct)
        {
            var metadata = await LoadVersionMetadataAsync(version, ct).ConfigureAwait(false);
            return await ResolveInheritedMetadataAsync(metadata, ct, new HashSet<string>(StringComparer.OrdinalIgnoreCase))
                .ConfigureAwait(false);
        }

        private async Task<Dictionary<string, object>> LoadVersionMetadataAsync(MinecraftVersionEntry version, CancellationToken ct)
        {
            if (version == null)
            {
                throw new ArgumentNullException(nameof(version));
            }

            if (!string.IsNullOrWhiteSpace(version.MetadataUrl))
            {
                return await GetJsonObjectAsync(version.MetadataUrl, ct).ConfigureAwait(false);
            }

            return await LoadVersionMetadataByIdAsync(version.Id, ct).ConfigureAwait(false);
        }

        private async Task<Dictionary<string, object>> LoadVersionMetadataByIdAsync(string versionId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var localMetadataPath = Path.Combine(_versions, versionId ?? string.Empty, (versionId ?? string.Empty) + ".json");
            if (File.Exists(localMetadataPath))
            {
                var localJson = File.ReadAllText(localMetadataPath, Encoding.UTF8);
                var localMetadata = Json.DeserializeObject(localJson) as Dictionary<string, object>;
                if (localMetadata != null)
                {
                    return localMetadata;
                }

                throw new InvalidOperationException("Локальный JSON версии поврежден: " + localMetadataPath);
            }

            var manifest = await GetJsonObjectAsync(ManifestUrl, ct).ConfigureAwait(false);
            foreach (var item in Arr(manifest, "versions"))
            {
                var obj = Dict(item);
                if (obj == null)
                {
                    continue;
                }

                var id = Str(obj, "id");
                if (!string.Equals(id, versionId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var url = Str(obj, "url");
                if (string.IsNullOrWhiteSpace(url))
                {
                    break;
                }

                return await GetJsonObjectAsync(url, ct).ConfigureAwait(false);
            }

            throw new InvalidOperationException("Нет метаданных версии в манифесте и локальном кэше: " + versionId);
        }

        private async Task<Dictionary<string, object>> ResolveInheritedMetadataAsync(
            Dictionary<string, object> metadata,
            CancellationToken ct,
            HashSet<string> seenIds)
        {
            if (metadata == null)
            {
                throw new InvalidOperationException("Пустые метаданные версии.");
            }

            var versionId = Str(metadata, "id");
            if (!string.IsNullOrWhiteSpace(versionId))
            {
                if (!seenIds.Add(versionId))
                {
                    throw new InvalidOperationException("Обнаружено циклическое наследование версии: " + versionId);
                }
            }

            var parentId = Str(metadata, "inheritsFrom");
            if (string.IsNullOrWhiteSpace(parentId))
            {
                return metadata;
            }

            var parentMetadata = await LoadVersionMetadataByIdAsync(parentId, ct).ConfigureAwait(false);
            var resolvedParent = await ResolveInheritedMetadataAsync(parentMetadata, ct, seenIds).ConfigureAwait(false);
            return MergeVersionMetadata(resolvedParent, metadata);
        }

        private static Dictionary<string, object> MergeVersionMetadata(
            Dictionary<string, object> parent,
            Dictionary<string, object> child)
        {
            var merged = Dict(CloneObject(parent)) ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            foreach (var pair in child)
            {
                if (pair.Key == null)
                {
                    continue;
                }

                if (pair.Key.Equals("inheritsFrom", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (pair.Key.Equals("libraries", StringComparison.OrdinalIgnoreCase))
                {
                    merged[pair.Key] = MergeLibraries(
                        Arr(parent, "libraries"),
                        Arr(child, "libraries"));
                    continue;
                }

                if (pair.Key.Equals("arguments", StringComparison.OrdinalIgnoreCase))
                {
                    var parentArguments = Dict(Obj(parent, "arguments"));
                    var childArguments = Dict(Obj(child, "arguments"));
                    merged[pair.Key] = MergeArguments(parentArguments, childArguments);
                    continue;
                }

                merged[pair.Key] = CloneObject(pair.Value);
            }

            return merged;
        }

        private static object[] MergeLibraries(object[] parentLibraries, object[] childLibraries)
        {
            var ordered = new List<Dictionary<string, object>>();

            foreach (var item in parentLibraries)
            {
                var dict = Dict(CloneObject(item));
                if (dict != null)
                {
                    ordered.Add(dict);
                }
            }

            foreach (var item in childLibraries)
            {
                var dict = Dict(CloneObject(item));
                if (dict == null)
                {
                    continue;
                }

                var name = Str(dict, "name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    ordered.Add(dict);
                    continue;
                }

                var replaced = false;
                for (var i = 0; i < ordered.Count; i++)
                {
                    var existing = ordered[i];
                    var existingName = Str(existing, "name");
                    if (!string.Equals(name, existingName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    ordered[i] = dict;
                    replaced = true;
                    break;
                }

                if (!replaced)
                {
                    ordered.Add(dict);
                }
            }

            return ordered.Cast<object>().ToArray();
        }

        private static Dictionary<string, object> MergeArguments(
            Dictionary<string, object> parentArguments,
            Dictionary<string, object> childArguments)
        {
            var merged = Dict(CloneObject(parentArguments))
                ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            foreach (var pair in childArguments ?? new Dictionary<string, object>())
            {
                merged[pair.Key] = CloneObject(pair.Value);
            }

            merged["jvm"] = MergeArgumentList(Arr(parentArguments, "jvm"), Arr(childArguments, "jvm"));
            merged["game"] = MergeArgumentList(Arr(parentArguments, "game"), Arr(childArguments, "game"));
            return merged;
        }

        private static object[] MergeArgumentList(object[] parentArguments, object[] childArguments)
        {
            var merged = new List<object>();
            merged.AddRange(parentArguments.Select(CloneObject));
            merged.AddRange(childArguments.Select(CloneObject));
            return merged.ToArray();
        }

        private static object CloneObject(object source)
        {
            if (source == null)
            {
                return null;
            }

            return Json.DeserializeObject(Json.Serialize(source));
        }

        private async Task<List<string>> EnsureLibrariesAsync(Dictionary<string, object> metadata, string nativesDir, IProgress<string> status, CancellationToken ct)
        {
            var result = new List<string>();
            var libs = Arr(metadata, "libraries");

            for (var i = 0; i < libs.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var lib = Dict(libs[i]);
                if (lib == null || !Allowed(lib))
                {
                    continue;
                }

                if (i % 25 == 0)
                {
                    ReportStatus(status, $"Установка библиотек... ({i + 1}/{libs.Length})");
                }

                var downloads = Dict(Obj(lib, "downloads"));
                var artifact = Dict(downloads != null ? Obj(downloads, "artifact") : null);
                var natives = Dict(Obj(lib, "natives"));
                var winKey = Str(natives, "windows");
                var hasWindowsNatives = !string.IsNullOrWhiteSpace(winKey);

                if (artifact != null)
                {
                    var path = Norm(Str(artifact, "path"));
                    var url = Str(artifact, "url");
                    var sha1 = Str(artifact, "sha1");
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        var full = Path.Combine(_libraries, path);
                        try
                        {
                            await DownloadLibraryWithFallbackAsync(
                                lib,
                                path,
                                url,
                                full,
                                sha1,
                                ct).ConfigureAwait(false);
                            result.Add(full);
                        }
                        catch (Exception ex)
                        {
                            // Some legacy libraries with natives (for example jinput-platform) do not have a base jar.
                            if (hasWindowsNatives)
                            {
                                LauncherLogService.Warn("Пропущен необязательный основной jar библиотеки с natives: " + Str(lib, "name") + ". " + ex.Message);
                            }
                            else
                            {
                                throw;
                            }
                        }
                    }
                }
                else
                {
                    var name = Str(lib, "name");
                    var baseUrl = NormalizeRepositoryUrl(Str(lib, "url", "https://libraries.minecraft.net/"));
                    var relative = BuildLibraryPath(name);
                    if (!string.IsNullOrWhiteSpace(relative))
                    {
                        var full = Path.Combine(_libraries, relative);
                        try
                        {
                            await DownloadLibraryWithFallbackAsync(
                                lib,
                                relative,
                                baseUrl.TrimEnd('/') + "/" + relative.Replace("\\", "/"),
                                full,
                                null,
                                ct).ConfigureAwait(false);
                            result.Add(full);
                        }
                        catch (Exception ex)
                        {
                            // Some legacy libraries with natives (for example jinput-platform) do not have a base jar.
                            if (hasWindowsNatives)
                            {
                                LauncherLogService.Warn("Пропущен необязательный основной jar библиотеки с natives: " + name + ". " + ex.Message);
                            }
                            else
                            {
                                throw;
                            }
                        }
                    }
                }

                if (natives == null)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(winKey))
                {
                    continue;
                }

                var key = winKey.Replace("${arch}", Environment.Is64BitOperatingSystem ? "64" : "32");
                if (downloads != null)
                {
                    var classifiers = Dict(Obj(downloads, "classifiers"));
                    var nativeArtifact = Dict(classifiers != null ? Obj(classifiers, key) : null)
                        ?? Dict(classifiers != null ? Obj(classifiers, winKey) : null);

                    if (nativeArtifact == null)
                    {
                        continue;
                    }

                    var np = Norm(Str(nativeArtifact, "path"));
                    var nu = Str(nativeArtifact, "url");
                    var ns = Str(nativeArtifact, "sha1");
                    if (string.IsNullOrWhiteSpace(np))
                    {
                        continue;
                    }

                    var jar = Path.Combine(_libraries, np);
                    await DownloadLibraryWithFallbackAsync(lib, np, nu, jar, ns, ct).ConfigureAwait(false);
                    ExtractNatives(jar, nativesDir);
                }
                else
                {
                    // Legacy metadata branch: natives are encoded in the classifier value only.
                    var name = Str(lib, "name");
                    var baseUrl = NormalizeRepositoryUrl(Str(lib, "url", "https://libraries.minecraft.net/"));
                    var np = BuildLibraryPath(name, key);
                    if (string.IsNullOrWhiteSpace(np))
                    {
                        np = BuildLibraryPath(name, winKey);
                    }

                    if (string.IsNullOrWhiteSpace(np))
                    {
                        continue;
                    }

                    var jar = Path.Combine(_libraries, np);
                    await DownloadLibraryWithFallbackAsync(
                        lib,
                        np,
                        baseUrl.TrimEnd('/') + "/" + np.Replace("\\", "/"),
                        jar,
                        null,
                        ct).ConfigureAwait(false);
                    ExtractNatives(jar, nativesDir);
                }
            }

            return result.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private async Task<string> EnsureAssetsAsync(Dictionary<string, object> metadata, IProgress<string> status, CancellationToken ct)
        {
            var indexObj = Dict(Obj(metadata, "assetIndex"));
            if (indexObj == null)
            {
                return Str(metadata, "assets", "legacy");
            }

            var indexId = Str(indexObj, "id", Str(metadata, "assets", "legacy"));
            var indexUrl = Str(indexObj, "url");
            var indexSha1 = Str(indexObj, "sha1");
            if (string.IsNullOrWhiteSpace(indexUrl))
            {
                throw new InvalidOperationException("URL asset index пустой.");
            }

            var indexPath = Path.Combine(_assets, "indexes", indexId + ".json");
            await DownloadIfNeededAsync(indexUrl, indexPath, indexSha1, ct).ConfigureAwait(false);

            var indexJson = File.ReadAllText(indexPath, Encoding.UTF8);
            var index = Json.DeserializeObject(indexJson) as Dictionary<string, object>;
            var objects = index != null ? Dict(Obj(index, "objects")) : null;
            if (objects == null)
            {
                return indexId;
            }

            var current = 0;
            var total = objects.Count;
            foreach (var item in objects)
            {
                ct.ThrowIfCancellationRequested();
                current++;

                if (current % 300 == 0)
                {
                    ReportStatus(status, $"Установка ассетов... ({current}/{total})");
                }

                var obj = Dict(item.Value);
                var hash = Str(obj, "hash");
                if (string.IsNullOrWhiteSpace(hash) || hash.Length < 2)
                {
                    continue;
                }

                var xx = hash.Substring(0, 2);
                var path = Path.Combine(_assets, "objects", xx, hash);
                var url = AssetBaseUrl + "/" + xx + "/" + hash;
                await DownloadIfNeededAsync(url, path, hash, ct).ConfigureAwait(false);
            }

            return indexId;
        }

        private async Task<string> EnsureJavaRuntimeAsync(int major, IProgress<string> status, CancellationToken ct)
        {
            var target = Path.Combine(_runtime, "java-" + major);
            var existing = FindJavaExe(target);
            if (!string.IsNullOrWhiteSpace(existing))
            {
                LauncherLogService.Info("Найдена установленная Java " + major + ": " + existing);
                return existing;
            }

            ReportStatus(status, $"Загрузка Java {major}...");
            var arch = Environment.Is64BitOperatingSystem ? "x64" : "x86";
            var api = $"https://api.adoptium.net/v3/assets/latest/{major}/hotspot?release_type=ga&os=windows&architecture={arch}&image_type=jre";

            var json = await Http.GetStringAsync(api).ConfigureAwait(false);
            var arr = Json.DeserializeObject(json) as object[];
            if (arr == null || arr.Length == 0)
            {
                throw new InvalidOperationException($"Adoptium не вернул Java {major}.");
            }

            var first = Dict(arr[0]);
            var binary = Dict(first != null ? Obj(first, "binary") : null);
            var package = Dict(binary != null ? Obj(binary, "package") : null);
            var link = Str(package, "link");
            if (string.IsNullOrWhiteSpace(link))
            {
                throw new InvalidOperationException("Ссылка на архив Java не найдена.");
            }

            var zip = Path.Combine(_temp, "java-" + major + ".zip");
            var unpack = Path.Combine(_temp, "java-" + major + "-extract");
            await DownloadIfNeededAsync(link, zip, null, ct).ConfigureAwait(false);

            if (Directory.Exists(unpack))
            {
                Directory.Delete(unpack, true);
            }

            Directory.CreateDirectory(unpack);
            ZipFile.ExtractToDirectory(zip, unpack);

            if (Directory.Exists(target))
            {
                Directory.Delete(target, true);
            }

            Directory.Move(unpack, target);
            TryDelete(zip);

            var java = FindJavaExe(target);
            if (string.IsNullOrWhiteSpace(java))
            {
                throw new InvalidOperationException("Java скачана, но java.exe не найден.");
            }

            LauncherLogService.Info("Java " + major + " установлена: " + java);
            return java;
        }

        private List<string> BuildLaunchArgs(Dictionary<string, object> metadata, string versionId, LauncherAccount account, List<string> classpath, string nativesDir, string assetsIndexId, LauncherSettings settings)
        {
            var args = new List<string>();
            var jvm = new List<string>();
            var game = new List<string>();
            classpath = PrepareClasspathForLaunch(classpath);
            LogSelectedClasspathEntries(classpath);

            var playerName = string.IsNullOrWhiteSpace(account != null ? account.Nickname : null)
                ? "Player"
                : account.Nickname.Trim();

            var accountType = account != null ? account.Type : "offline";
            var isAuthorized = string.Equals(accountType, "authorized", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(account != null ? account.AccessToken : null);

            var uuid = NormalizeUuid(account != null ? account.Uuid : null);
            if (string.IsNullOrWhiteSpace(uuid))
            {
                uuid = OfflineUuid(playerName);
            }

            var accessToken = isAuthorized ? account.AccessToken : "0";
            var clientToken = isAuthorized ? (account.ClientToken ?? string.Empty) : string.Empty;
            var userType = isAuthorized
                ? (string.IsNullOrWhiteSpace(account.UserType) ? "mojang" : account.UserType)
                : "legacy";

            var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "auth_player_name", playerName },
                { "version_name", versionId },
                { "game_directory", _root },
                { "assets_root", _assets },
                { "assets_index_name", assetsIndexId },
                { "auth_uuid", uuid },
                { "auth_access_token", accessToken },
                { "auth_session", accessToken },
                { "auth_xuid", "" },
                { "clientid", clientToken },
                { "user_type", userType },
                { "user_properties", "{}" },
                { "version_type", Str(metadata, "type", "release") },
                { "natives_directory", nativesDir },
                { "launcher_name", "AsetLauncher" },
                { "launcher_version", "1.0" },
                { "classpath", string.Join(";", classpath) },
                { "classpath_separator", ";" },
                { "library_directory", _libraries }
            };

            var modern = Dict(Obj(metadata, "arguments"));
            if (modern != null)
            {
                ParseArguments(Arr(modern, "jvm"), jvm);
                ParseArguments(Arr(modern, "game"), game);
            }
            else
            {
                jvm.Add("-Djava.library.path=${natives_directory}");
                jvm.Add("-cp");
                jvm.Add("${classpath}");
                game.AddRange(SplitLegacy(Str(metadata, "minecraftArguments")));
            }

            jvm = jvm
                .Where(a => !a.StartsWith("-Xmx", StringComparison.OrdinalIgnoreCase)
                    && !a.StartsWith("-Xms", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var maxRamMb = NormalizeRamMb(settings.MaxRamMb);
            var minRamMb = Math.Min(512, maxRamMb);

            jvm.Insert(0, "-Xmx" + maxRamMb + "M");
            jvm.Insert(0, "-Xms" + minRamMb + "M");

            args.AddRange(jvm.Select(a => ReplaceVars(a, vars)));

            var mainClass = Str(metadata, "mainClass");
            if (string.IsNullOrWhiteSpace(mainClass))
            {
                throw new InvalidOperationException("mainClass отсутствует.");
            }

            args.Add(mainClass);
            args.AddRange(game.Select(a => ReplaceVars(a, vars)));
            return args;
        }

        private List<string> PrepareClasspathForLaunch(IEnumerable<string> rawClasspath)
        {
            var result = new List<string>();
            if (rawClasspath == null)
            {
                return result;
            }

            var exactPathToIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var libraryIdentityToIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in rawClasspath)
            {
                if (string.IsNullOrWhiteSpace(item))
                {
                    continue;
                }

                var fullPath = NormalizeClasspathPath(item);

                int existingPathIndex;
                if (exactPathToIndex.TryGetValue(fullPath, out existingPathIndex))
                {
                    continue;
                }

                string libraryIdentity;
                if (TryGetLibraryIdentity(fullPath, out libraryIdentity))
                {
                    int existingLibraryIndex;
                    if (libraryIdentityToIndex.TryGetValue(libraryIdentity, out existingLibraryIndex))
                    {
                        // Metadata order matters for inherited versions (child overrides parent),
                        // so keep the latest entry for the same library key.
                        result[existingLibraryIndex] = fullPath;
                        exactPathToIndex[fullPath] = existingLibraryIndex;
                        continue;
                    }

                    var nextIndex = result.Count;
                    result.Add(fullPath);
                    exactPathToIndex[fullPath] = nextIndex;
                    libraryIdentityToIndex[libraryIdentity] = nextIndex;
                    continue;
                }

                exactPathToIndex[fullPath] = result.Count;
                result.Add(fullPath);
            }

            return result;
        }

        private string NormalizeClasspathPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path.Trim();
            }
        }

        private bool TryGetLibraryIdentity(string fullPath, out string identity)
        {
            identity = string.Empty;
            if (string.IsNullOrWhiteSpace(fullPath))
            {
                return false;
            }

            var librariesRoot = Path.GetFullPath(_libraries)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            if (!fullPath.StartsWith(librariesRoot, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var relative = fullPath.Substring(librariesRoot.Length)
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            var parts = relative.Split(new[] { Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

            // Expected: group/path/artifact/version/file.jar
            if (parts.Length < 4)
            {
                return false;
            }

            var artifactIndex = parts.Length - 3;
            if (artifactIndex <= 0)
            {
                return false;
            }

            var group = string.Join(".", parts.Take(artifactIndex));
            var artifact = parts[artifactIndex];
            if (string.IsNullOrWhiteSpace(group) || string.IsNullOrWhiteSpace(artifact))
            {
                return false;
            }

            var version = parts[parts.Length - 2];
            var fileName = Path.GetFileNameWithoutExtension(parts[parts.Length - 1]) ?? string.Empty;
            var classifier = GetLibraryClassifier(fileName, artifact, version);
            identity = group + ":" + artifact + ":" + classifier;
            return true;
        }

        private static string GetLibraryClassifier(string fileNameWithoutExtension, string artifact, string version)
        {
            if (string.IsNullOrWhiteSpace(fileNameWithoutExtension)
                || string.IsNullOrWhiteSpace(artifact)
                || string.IsNullOrWhiteSpace(version))
            {
                return string.Empty;
            }

            var prefix = artifact + "-" + version;
            if (fileNameWithoutExtension.Equals(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            var withSeparator = prefix + "-";
            if (fileNameWithoutExtension.StartsWith(withSeparator, StringComparison.OrdinalIgnoreCase))
            {
                return fileNameWithoutExtension.Substring(withSeparator.Length);
            }

            // Keep unexpected naming patterns unique so we do not accidentally merge unrelated jars.
            return fileNameWithoutExtension;
        }

        private static void LogSelectedClasspathEntries(List<string> classpath)
        {
            if (classpath == null || classpath.Count == 0)
            {
                return;
            }

            var selected = classpath
                .Where(p =>
                    p.IndexOf("log4j", StringComparison.OrdinalIgnoreCase) >= 0
                    || p.IndexOf("terminalconsoleappender", StringComparison.OrdinalIgnoreCase) >= 0
                    || p.IndexOf("modlauncher", StringComparison.OrdinalIgnoreCase) >= 0
                    || p.IndexOf("net\\minecraftforge\\forge\\", StringComparison.OrdinalIgnoreCase) >= 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (selected.Count == 0)
            {
                return;
            }

            foreach (var path in selected)
            {
                LauncherLogService.Info("Classpath: " + path);
            }
        }

        private static void ParseArguments(object[] source, List<string> target)
        {
            foreach (var item in source)
            {
                if (item is string text)
                {
                    target.Add(text);
                    continue;
                }

                var obj = Dict(item);
                if (obj == null || !Allowed(obj))
                {
                    continue;
                }

                var value = Obj(obj, "value");
                if (value is string single)
                {
                    target.Add(single);
                    continue;
                }

                var arr = value as object[];
                if (arr == null)
                {
                    continue;
                }

                foreach (var part in arr)
                {
                    if (part is string s)
                    {
                        target.Add(s);
                    }
                }
            }
        }

        private static bool Allowed(Dictionary<string, object> obj)
        {
            var rules = Arr(obj, "rules");
            if (rules.Length == 0)
            {
                return true;
            }

            var allow = false;
            foreach (var r in rules)
            {
                var rule = Dict(r);
                if (rule == null || !RuleMatch(rule))
                {
                    continue;
                }

                allow = Str(rule, "action", "allow").Equals("allow", StringComparison.OrdinalIgnoreCase);
            }

            return allow;
        }

        private static bool RuleMatch(Dictionary<string, object> rule)
        {
            var os = Dict(Obj(rule, "os"));
            if (os != null)
            {
                var name = Str(os, "name");
                if (!string.IsNullOrWhiteSpace(name) && !name.Equals("windows", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var arch = Str(os, "arch");
                if (!string.IsNullOrWhiteSpace(arch))
                {
                    var is64 = Environment.Is64BitOperatingSystem;
                    if (arch.Equals("x86", StringComparison.OrdinalIgnoreCase) && is64)
                    {
                        return false;
                    }

                    if ((arch.Equals("x86_64", StringComparison.OrdinalIgnoreCase) || arch.Equals("amd64", StringComparison.OrdinalIgnoreCase)) && !is64)
                    {
                        return false;
                    }
                }
            }

            var features = Dict(Obj(rule, "features"));
            if (features != null && features.Count > 0)
            {
                foreach (var f in features.Values)
                {
                    if (f is bool b && b)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private void StartMinecraft(string javaExe, List<string> args)
        {
            if (!File.Exists(javaExe))
            {
                throw new FileNotFoundException("java.exe не найден", javaExe);
            }

            var psi = new ProcessStartInfo
            {
                FileName = javaExe,
                WorkingDirectory = _root,
                Arguments = string.Join(" ", args.Select(Quote)),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            var process = new Process
            {
                StartInfo = psi,
                EnableRaisingEvents = true
            };

            process.OutputDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    LauncherLogService.Write("MC-OUT", e.Data);
                }
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    LauncherLogService.Write("MC-ERR", e.Data);
                }
            };

            process.Exited += (sender, e) =>
            {
                var pid = process.Id;
                var code = process.ExitCode;
                LauncherLogService.Info("Minecraft завершился. Код: " + code);
                var exited = MinecraftProcessExited;
                if (exited != null)
                {
                    try
                    {
                        exited(pid, code);
                    }
                    catch
                    {
                    }
                }
                UntrackProcess(process);
                
                // Clean up hosts file when Minecraft process exits
                try
                {
                    RemoveHostsRedirects();
                }
                catch (Exception ex)
                {
                    LauncherLogService.Exception("Ошибка при очистке hosts файла после завершения игры", ex);
                }
            };

            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException("Не удалось запустить Java процесс.");
            }

            RunningMinecraftProcesses[process.Id] = process;
            var started = MinecraftProcessStarted;
            if (started != null)
            {
                try
                {
                    started(process.Id);
                }
                catch
                {
                }
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            LauncherLogService.Info("Minecraft запущен. PID=" + process.Id);
            LauncherLogService.Info("Java окно скрыто, вывод перенаправлен в консоль лаунчера.");
        }

        private static void UntrackProcess(Process process)
        {
            if (process == null)
            {
                return;
            }

            Process removed;
            if (RunningMinecraftProcesses.TryRemove(process.Id, out removed))
            {
                try
                {
                    removed.Dispose();
                }
                catch
                {
                }
            }
            else
            {
                try
                {
                    process.Dispose();
                }
                catch
                {
                }
            }
        }

        private static void ReportStatus(IProgress<string> status, string text)
        {
            status?.Report(text);
            LauncherLogService.Info(text);
        }

        private async Task DownloadIfNeededAsync(string url, string path, string sha1, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                throw new InvalidOperationException("URL загрузки пустой.");
            }

            if (File.Exists(path) && (string.IsNullOrWhiteSpace(sha1) || Sha1Match(path, sha1)))
            {
                return;
            }

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var temp = path + ".download";
            TryDelete(temp);

            using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    var errorText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (errorText == null)
                    {
                        errorText = string.Empty;
                    }

                    errorText = errorText.Replace('\r', ' ').Replace('\n', ' ').Trim();
                    if (errorText.Length > 280)
                    {
                        errorText = errorText.Substring(0, 280) + "...";
                    }

                    throw new HttpRequestException(
                        "HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase
                        + " при загрузке: " + url
                        + (string.IsNullOrWhiteSpace(errorText) ? string.Empty : ". " + errorText));
                }

                using (var src = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var dst = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await src.CopyToAsync(dst).ConfigureAwait(false);
                }
            }

            if (!string.IsNullOrWhiteSpace(sha1) && !Sha1Match(temp, sha1))
            {
                TryDelete(temp);
                throw new InvalidOperationException("SHA1 не совпал для файла: " + path);
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }

            File.Move(temp, path);
        }

        private static async Task<Dictionary<string, object>> GetJsonObjectAsync(string url, CancellationToken ct)
        {
            using (var response = await Http.GetAsync(url, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var obj = Json.DeserializeObject(text) as Dictionary<string, object>;
                if (obj == null)
                {
                    throw new InvalidOperationException("Не удалось разобрать JSON: " + url);
                }

                return obj;
            }
        }

        private static async Task<object[]> GetJsonArrayAsync(string url, CancellationToken ct)
        {
            using (var response = await Http.GetAsync(url, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var arr = Json.DeserializeObject(text) as object[];
                if (arr == null)
                {
                    throw new InvalidOperationException("Не удалось разобрать JSON-массив: " + url);
                }

                return arr;
            }
        }

        private static int GetRequiredJavaMajor(Dictionary<string, object> metadata, string versionId)
        {
            var javaVersion = Dict(Obj(metadata, "javaVersion"));
            var major = ToInt(javaVersion != null ? Obj(javaVersion, "majorVersion") : null);
            if (major > 0)
            {
                return major;
            }

            return GetRequiredJavaMajorByGameVersion(versionId);
        }

        private static int GetRequiredJavaMajorByGameVersion(string versionId)
        {
            if (string.IsNullOrWhiteSpace(versionId))
            {
                return 8;
            }

            if (versionId.StartsWith("1.21", StringComparison.OrdinalIgnoreCase)
                || versionId.StartsWith("1.20.5", StringComparison.OrdinalIgnoreCase)
                || versionId.StartsWith("1.20.6", StringComparison.OrdinalIgnoreCase))
            {
                return 21;
            }

            if (versionId.StartsWith("1.18", StringComparison.OrdinalIgnoreCase)
                || versionId.StartsWith("1.19", StringComparison.OrdinalIgnoreCase)
                || versionId.StartsWith("1.20", StringComparison.OrdinalIgnoreCase))
            {
                return 17;
            }

            if (versionId.StartsWith("1.17", StringComparison.OrdinalIgnoreCase))
            {
                return 16;
            }

            return 8;
        }

        private static bool SupportsForgeInstaller(string gameVersion)
        {
            int major;
            int minor;
            if (!TryParseGameVersion(gameVersion, out major, out minor))
            {
                return false;
            }

            // The automatic installer flow is reliable for modern Forge branches.
            return major > 1 || (major == 1 && minor >= 12);
        }

        private static bool TryParseGameVersion(string version, out int major, out int minor)
        {
            major = 0;
            minor = 0;

            if (string.IsNullOrWhiteSpace(version))
            {
                return false;
            }

            var parts = version.Split('.');
            if (parts.Length < 2)
            {
                return false;
            }

            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out major))
            {
                return false;
            }

            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out minor))
            {
                return false;
            }

            return true;
        }

        private static bool IsForgeVersionEntry(MinecraftVersionEntry version)
        {
            if (version == null)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(version.Type)
                && version.Type.IndexOf("forge", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(version.Id))
            {
                return false;
            }

            return version.Id.IndexOf("-forge-", StringComparison.OrdinalIgnoreCase) >= 0
                || version.Id.StartsWith("forge-", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildForgeVersionId(string gameVersion, string forgeVersion)
        {
            return (gameVersion ?? string.Empty).Trim() + "-forge-" + (forgeVersion ?? string.Empty).Trim();
        }

        private static bool TryParseForgeVersionId(string versionId, out string gameVersion, out string forgeVersion)
        {
            gameVersion = string.Empty;
            forgeVersion = string.Empty;

            if (string.IsNullOrWhiteSpace(versionId))
            {
                return false;
            }

            var markerIndex = versionId.IndexOf("-forge-", StringComparison.OrdinalIgnoreCase);
            if (markerIndex > 0 && markerIndex < versionId.Length - "-forge-".Length)
            {
                gameVersion = versionId.Substring(0, markerIndex).Trim();
                forgeVersion = versionId.Substring(markerIndex + "-forge-".Length).Trim();
                return !string.IsNullOrWhiteSpace(gameVersion) && !string.IsNullOrWhiteSpace(forgeVersion);
            }

            if (versionId.StartsWith("forge-", StringComparison.OrdinalIgnoreCase))
            {
                var tail = versionId.Substring("forge-".Length);
                var splitIndex = tail.IndexOf('-');
                if (splitIndex > 0 && splitIndex < tail.Length - 1)
                {
                    gameVersion = tail.Substring(0, splitIndex).Trim();
                    forgeVersion = tail.Substring(splitIndex + 1).Trim();
                    return !string.IsNullOrWhiteSpace(gameVersion) && !string.IsNullOrWhiteSpace(forgeVersion);
                }
            }

            return false;
        }

        private static bool TryParseForgeDescriptor(MinecraftVersionEntry version, out string gameVersion, out string forgeVersion)
        {
            return TryParseForgeVersionId(version != null ? version.Id : null, out gameVersion, out forgeVersion);
        }

        private bool HasLocalVersionMetadata(string versionId)
        {
            if (string.IsNullOrWhiteSpace(versionId))
            {
                return false;
            }

            var metadataPath = Path.Combine(_versions, versionId, versionId + ".json");
            return File.Exists(metadataPath);
        }

        private string TryReadForgeInstallerVersionId(string installerPath)
        {
            if (!File.Exists(installerPath))
            {
                return string.Empty;
            }

            try
            {
                using (var zip = ZipFile.OpenRead(installerPath))
                {
                    var installProfileEntry = zip.GetEntry("install_profile.json");
                    if (installProfileEntry != null)
                    {
                        using (var stream = installProfileEntry.Open())
                        using (var reader = new StreamReader(stream, Encoding.UTF8))
                        {
                            var json = reader.ReadToEnd();
                            var profile = Json.DeserializeObject(json) as Dictionary<string, object>;
                            var version = Str(profile, "version");
                            if (!string.IsNullOrWhiteSpace(version))
                            {
                                return version;
                            }
                        }
                    }

                    var versionJsonEntry = zip.GetEntry("version.json");
                    if (versionJsonEntry != null)
                    {
                        using (var stream = versionJsonEntry.Open())
                        using (var reader = new StreamReader(stream, Encoding.UTF8))
                        {
                            var json = reader.ReadToEnd();
                            var versionObj = Json.DeserializeObject(json) as Dictionary<string, object>;
                            var id = Str(versionObj, "id");
                            if (!string.IsNullOrWhiteSpace(id))
                            {
                                return id;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LauncherLogService.Exception("Не удалось прочитать install_profile Forge installer.", ex);
            }

            return string.Empty;
        }

        private Dictionary<string, object> TryReadForgeInstallProfile(string installerPath)
        {
            if (!File.Exists(installerPath))
            {
                return null;
            }

            try
            {
                using (var zip = ZipFile.OpenRead(installerPath))
                {
                    var installProfileEntry = zip.GetEntry("install_profile.json");
                    if (installProfileEntry == null)
                    {
                        return null;
                    }

                    using (var stream = installProfileEntry.Open())
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        var json = reader.ReadToEnd();
                        return Json.DeserializeObject(json) as Dictionary<string, object>;
                    }
                }
            }
            catch (Exception ex)
            {
                LauncherLogService.Exception("Не удалось прочитать install_profile Forge installer.", ex);
                return null;
            }
        }

        private async Task EnsureForgeInstallerLibrariesAsync(
            Dictionary<string, object> installProfile,
            IProgress<string> status,
            CancellationToken ct)
        {
            if (installProfile == null)
            {
                return;
            }

            var libraries = new List<Dictionary<string, object>>();
            libraries.AddRange(Arr(installProfile, "libraries").Select(Dict).Where(d => d != null));

            var versionInfo = Dict(Obj(installProfile, "versionInfo"));
            if (versionInfo != null)
            {
                libraries.AddRange(Arr(versionInfo, "libraries").Select(Dict).Where(d => d != null));
            }

            var uniqueByName = libraries
                .Where(l => !string.IsNullOrWhiteSpace(Str(l, "name")))
                .GroupBy(l => Str(l, "name"), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            var total = uniqueByName.Count;
            for (var i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();

                var lib = uniqueByName[i];
                var name = Str(lib, "name");
                var relativePath = BuildLibraryPath(name);
                if (string.IsNullOrWhiteSpace(relativePath))
                {
                    continue;
                }

                if (i % 20 == 0)
                {
                    ReportStatus(status, $"Подготовка Forge библиотек... ({i + 1}/{total})");
                }

                var targetPath = Path.Combine(_libraries, relativePath);
                if (File.Exists(targetPath))
                {
                    continue;
                }

                var sha1 = FirstSha1FromChecksums(lib);
                var downloaded = false;

                foreach (var baseUrl in BuildForgeLibraryBaseUrls(lib, name))
                {
                    ct.ThrowIfCancellationRequested();
                    var url = baseUrl.TrimEnd('/') + "/" + relativePath.Replace("\\", "/");

                    try
                    {
                        await DownloadIfNeededAsync(url, targetPath, sha1, ct).ConfigureAwait(false);
                        downloaded = true;
                        break;
                    }
                    catch
                    {
                    }
                }

                if (!downloaded && !File.Exists(targetPath))
                {
                    LauncherLogService.Warn("Не удалось предзагрузить Forge библиотеку: " + name);
                }
            }
        }

        private static IEnumerable<string> BuildForgeLibraryBaseUrls(Dictionary<string, object> lib, string libraryName)
        {
            var urls = new List<string>();
            var explicitUrl = Str(lib, "url");
            if (!string.IsNullOrWhiteSpace(explicitUrl))
            {
                urls.Add(NormalizeRepositoryUrl(explicitUrl));
            }

            urls.Add("https://maven.minecraftforge.net");
            urls.Add("https://libraries.minecraft.net");
            urls.Add("https://repo1.maven.org/maven2");
            urls.Add("https://maven.creeperhost.net");

            if (!string.IsNullOrWhiteSpace(libraryName)
                && libraryName.StartsWith("net.minecraftforge", StringComparison.OrdinalIgnoreCase))
            {
                urls.Insert(0, "https://maven.minecraftforge.net");
            }

            return urls
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string NormalizeRepositoryUrl(string url)
        {
            var value = (url ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                return "https://libraries.minecraft.net";
            }

            if (value.StartsWith("//", StringComparison.Ordinal))
            {
                value = "https:" + value;
            }

            if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                value = "https://" + value;
            }

            // Reject unsupported URI schemes to avoid HttpClient invalid URI crashes.
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri))
            {
                return "https://libraries.minecraft.net";
            }

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return "https://libraries.minecraft.net";
            }

            return uri.ToString().TrimEnd('/');
        }

        private static string FirstSha1FromChecksums(Dictionary<string, object> lib)
        {
            var checksums = Obj(lib, "checksums") as object[];
            if (checksums == null || checksums.Length == 0)
            {
                return null;
            }

            foreach (var item in checksums)
            {
                var text = Convert.ToString(item, CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var trimmed = text.Trim();
                if (trimmed.Length == 40 && trimmed.All(Uri.IsHexDigit))
                {
                    return trimmed.ToLowerInvariant();
                }
            }

            return null;
        }

        private async Task DownloadLibraryWithFallbackAsync(
            Dictionary<string, object> lib,
            string relativePath,
            string primaryUrl,
            string targetPath,
            string sha1,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                throw new ArgumentException("Путь библиотеки пустой.", nameof(relativePath));
            }

            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(primaryUrl))
            {
                candidates.Add(primaryUrl.Trim());
            }

            var normalizedRelative = relativePath.Replace("\\", "/");
            foreach (var baseUrl in BuildForgeLibraryBaseUrls(lib, Str(lib, "name")))
            {
                candidates.Add(baseUrl.TrimEnd('/') + "/" + normalizedRelative);
            }

            var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Exception lastError = null;

            foreach (var candidate in candidates)
            {
                ct.ThrowIfCancellationRequested();

                var normalizedCandidate = NormalizeAbsoluteUrl(candidate);
                if (string.IsNullOrWhiteSpace(normalizedCandidate) || !tried.Add(normalizedCandidate))
                {
                    continue;
                }

                try
                {
                    await DownloadIfNeededAsync(normalizedCandidate, targetPath, sha1, ct).ConfigureAwait(false);
                    return;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
            }

            if (lastError != null)
            {
                throw new InvalidOperationException(
                    "Не удалось скачать библиотеку после fallback-попыток: " + relativePath
                    + ". Последняя ошибка: " + lastError.Message,
                    lastError);
            }

            throw new InvalidOperationException("Не удалось скачать библиотеку: " + relativePath);
        }

        private static string NormalizeAbsoluteUrl(string url)
        {
            var value = (url ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            if (value.StartsWith("//", StringComparison.Ordinal))
            {
                value = "https:" + value;
            }

            if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                value = "https://" + value;
            }

            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri))
            {
                return string.Empty;
            }

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return uri.ToString();
        }

        private string FindInstalledForgeVersionId(string gameVersion, string forgeVersion)
        {
            if (!Directory.Exists(_versions))
            {
                return string.Empty;
            }

            var expected = BuildForgeVersionId(gameVersion, forgeVersion);
            if (HasLocalVersionMetadata(expected))
            {
                return expected;
            }

            var fallback = string.Empty;
            var fallbackTime = DateTime.MinValue;

            foreach (var dir in Directory.GetDirectories(_versions))
            {
                var id = Path.GetFileName(dir);
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                if (id.IndexOf("forge", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                var metadataPath = Path.Combine(dir, id + ".json");
                if (!File.Exists(metadataPath))
                {
                    continue;
                }

                if (id.IndexOf(gameVersion ?? string.Empty, StringComparison.OrdinalIgnoreCase) < 0
                    || id.IndexOf(forgeVersion ?? string.Empty, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                var fileTime = File.GetLastWriteTimeUtc(metadataPath);
                if (fileTime > fallbackTime)
                {
                    fallbackTime = fileTime;
                    fallback = id;
                }
            }

            return fallback;
        }

        private void EnsureForgeLauncherProfile()
        {
            try
            {
                Directory.CreateDirectory(_root);

                var profileJson = BuildDefaultLauncherProfileJson();
                var profilePath = Path.Combine(_root, "launcher_profiles.json");
                EnsureJsonFile(profilePath, profileJson);

                var msStoreProfilePath = Path.Combine(_root, "launcher_profiles_microsoft_store.json");
                EnsureJsonFile(msStoreProfilePath, profileJson);
            }
            catch (Exception ex)
            {
                LauncherLogService.Exception("Не удалось подготовить launcher_profiles.json для Forge installer.", ex);
            }
        }

        private static void EnsureJsonFile(string path, string fallbackJson)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                if (File.Exists(path))
                {
                    var existing = File.ReadAllText(path, Encoding.UTF8);
                    if (!string.IsNullOrWhiteSpace(existing))
                    {
                        Json.DeserializeObject(existing);
                        return;
                    }
                }
            }
            catch
            {
            }

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(path, fallbackJson, Encoding.UTF8);
        }

        private static string BuildDefaultLauncherProfileJson()
        {
            var stamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

            var profile = new Dictionary<string, object>
            {
                {
                    "profiles",
                    new Dictionary<string, object>
                    {
                        {
                            "AsetLauncher",
                            new Dictionary<string, object>
                            {
                                { "name", "AsetLauncher" },
                                { "type", "custom" },
                                { "created", stamp },
                                { "lastUsed", stamp },
                                { "icon", "Grass" }
                            }
                        }
                    }
                },
                { "selectedProfile", "AsetLauncher" },
                { "clientToken", "00000000000000000000000000000000" },
                { "authenticationDatabase", new Dictionary<string, object>() },
                { "settings", new Dictionary<string, object>() },
                { "version", 3 }
            };

            return Json.Serialize(profile);
        }

        private static async Task RunLoggedProcessAsync(
            string fileName,
            string arguments,
            string workingDirectory,
            string logTag,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                throw new ArgumentException("Путь к исполняемому файлу пустой.", nameof(fileName));
            }

            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments ?? string.Empty,
                WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? Environment.CurrentDirectory : workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using (var process = new Process { StartInfo = psi, EnableRaisingEvents = true })
            {
                var exitCodeTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                var outputTail = new Queue<string>(20);
                var errorTail = new Queue<string>(20);

                process.OutputDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        LauncherLogService.Write(logTag + "-OUT", e.Data);
                        EnqueueTail(outputTail, e.Data);
                    }
                };

                process.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        LauncherLogService.Write(logTag + "-ERR", e.Data);
                        EnqueueTail(errorTail, e.Data);
                    }
                };

                process.Exited += (s, e) => exitCodeTcs.TrySetResult(process.ExitCode);

                if (!process.Start())
                {
                    throw new InvalidOperationException("Не удалось запустить процесс: " + fileName);
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                using (ct.Register(() =>
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill();
                        }
                    }
                    catch
                    {
                    }
                }))
                {
                    var exitCode = await exitCodeTcs.Task.ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();

                    if (exitCode != 0)
                    {
                        var outputText = outputTail.Count > 0
                            ? string.Join(" | ", outputTail)
                            : "<empty>";
                        var errorText = errorTail.Count > 0
                            ? string.Join(" | ", errorTail)
                            : "<empty>";
                        throw new InvalidOperationException(
                            "Процесс завершился с ошибкой. Код: " + exitCode
                            + ". STDOUT: " + outputText
                            + ". STDERR: " + errorText);
                    }
                }
            }
        }

        private static void EnqueueTail(Queue<string> queue, string line)
        {
            if (queue == null || string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            if (queue.Count >= 20)
            {
                queue.Dequeue();
            }

            queue.Enqueue(line.Trim());
        }

        private static string BuildLibraryPath(string name)
        {
            return BuildLibraryPath(name, null);
        }

        private static string BuildLibraryPath(string name, string classifierOverride)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var p = name.Split(':');
            if (p.Length < 3)
            {
                return null;
            }

            var group = p[0].Replace('.', '/');
            var artifact = p[1];
            var version = p[2];
            var classifier = !string.IsNullOrWhiteSpace(classifierOverride)
                ? classifierOverride
                : (p.Length >= 4 ? p[3] : null);
            var file = artifact + "-" + version + (string.IsNullOrWhiteSpace(classifier) ? string.Empty : "-" + classifier) + ".jar";
            return Norm(group + "/" + artifact + "/" + version + "/" + file);
        }

        private static void ExtractNatives(string jarPath, string nativesDir)
        {
            Directory.CreateDirectory(nativesDir);
            using (var zip = ZipFile.OpenRead(jarPath))
            {
                foreach (var e in zip.Entries)
                {
                    if (string.IsNullOrWhiteSpace(e.Name) || e.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var outPath = Path.Combine(nativesDir, e.FullName.Replace('/', Path.DirectorySeparatorChar));
                    var dir = Path.GetDirectoryName(outPath);
                    if (!string.IsNullOrWhiteSpace(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    e.ExtractToFile(outPath, true);
                }
            }
        }

        private static bool Sha1Match(string path, string expected)
        {
            if (!File.Exists(path) || string.IsNullOrWhiteSpace(expected))
            {
                return false;
            }

            using (var file = File.OpenRead(path))
            using (var sha1 = SHA1.Create())
            {
                var hash = sha1.ComputeHash(file);
                var current = BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
                return current == expected.Trim().ToLowerInvariant();
            }
        }

        private static string FindJavaExe(string root)
        {
            if (!Directory.Exists(root))
            {
                return null;
            }

            return Directory.GetFiles(root, "java.exe", SearchOption.AllDirectories)
                .FirstOrDefault(p => p.IndexOf("\\bin\\", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string ReplaceVars(string value, Dictionary<string, string> vars)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            foreach (var v in vars)
            {
                value = value.Replace("${" + v.Key + "}", v.Value ?? string.Empty);
            }

            return value;
        }

        private static List<string> SplitLegacy(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return new List<string>();
            }

            var tokens = Regex.Matches(raw, "[\"].+?[\"]|[^ ]+");
            var result = new List<string>(tokens.Count);
            foreach (Match m in tokens)
            {
                var v = m.Value.Trim();
                if (v.StartsWith("\"", StringComparison.Ordinal) && v.EndsWith("\"", StringComparison.Ordinal) && v.Length >= 2)
                {
                    v = v.Substring(1, v.Length - 2);
                }

                if (!string.IsNullOrWhiteSpace(v))
                {
                    result.Add(v);
                }
            }

            return result;
        }

        private static string Quote(string arg)
        {
            if (string.IsNullOrEmpty(arg))
            {
                return "\"\"";
            }

            if (!arg.Any(c => char.IsWhiteSpace(c) || c == '"'))
            {
                return arg;
            }

            return "\"" + arg.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static string OfflineUuid(string name)
        {
            using (var md5 = MD5.Create())
            {
                return new Guid(md5.ComputeHash(Encoding.UTF8.GetBytes("OfflinePlayer:" + name))).ToString("N");
            }
        }

        private static string NormalizeUuid(string uuid)
        {
            if (string.IsNullOrWhiteSpace(uuid))
            {
                return string.Empty;
            }

            return uuid.Replace("-", string.Empty).Trim().ToLowerInvariant();
        }

        private static void RecreateDirectory(string path)
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }

            Directory.CreateDirectory(path);
        }

        private static void TryDelete(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        private static void TryMigrateLegacyRoot(string legacyRoot, string newRoot)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(legacyRoot) || string.IsNullOrWhiteSpace(newRoot))
                {
                    return;
                }

                if (!Directory.Exists(legacyRoot) || Directory.Exists(newRoot))
                {
                    return;
                }

                var newRootParent = Path.GetDirectoryName(newRoot);
                if (!string.IsNullOrWhiteSpace(newRootParent))
                {
                    Directory.CreateDirectory(newRootParent);
                }

                Directory.Move(legacyRoot, newRoot);
            }
            catch
            {
            }
        }

        private static HttpClient CreateHttpClient()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
            return new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        }

        private static Dictionary<string, object> Dict(object value) => value as Dictionary<string, object>;
        private static object Obj(Dictionary<string, object> map, string key) => map != null && key != null && map.ContainsKey(key) ? map[key] : null;
        private static object[] Arr(Dictionary<string, object> map, string key) => Obj(map, key) as object[] ?? Array.Empty<object>();
        private static string Str(Dictionary<string, object> map, string key, string def = "") => Convert.ToString(Obj(map, key), CultureInfo.InvariantCulture) ?? def;
        private static bool ToBool(object value)
        {
            if (value is bool flag)
            {
                return flag;
            }

            var text = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(text, "1", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }
        private static int ToInt(object value) { int i; return int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out i) ? i : 0; }
        private static int NormalizeRamMb(int ramMb)
        {
            if (ramMb < 512)
            {
                return 512;
            }

            if (ramMb > 32768)
            {
                return 32768;
            }

            return ramMb;
        }
        private static string Norm(string p) => string.IsNullOrWhiteSpace(p) ? p : p.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

        #region Server list management

        private enum NbtTagType : byte
        {
            End = 0,
            Byte = 1,
            Short = 2,
            Int = 3,
            Long = 4,
            Float = 5,
            Double = 6,
            ByteArray = 7,
            String = 8,
            List = 9,
            Compound = 10,
            IntArray = 11,
            LongArray = 12
        }

        private sealed class NbtNode
        {
            public NbtTagType Type;
            public object Value;
        }

        private sealed class NbtCompoundValue
        {
            public readonly Dictionary<string, NbtNode> Tags =
                new Dictionary<string, NbtNode>(StringComparer.Ordinal);
        }

        private sealed class NbtListValue
        {
            public NbtTagType ElementType;
            public readonly List<NbtNode> Items = new List<NbtNode>();
        }

        private void EnsureFeaturedServerInListSafe()
        {
            try
            {
                EnsureFeaturedServerInList(FeaturedServerName, FeaturedServerAddress);
            }
            catch (Exception ex)
            {
                LauncherLogService.Exception("Не удалось обновить список серверов Minecraft.", ex);
            }
        }

        private void EnsureFeaturedServerInList(string serverName, string serverAddress)
        {
            if (string.IsNullOrWhiteSpace(serverAddress))
            {
                return;
            }

            var path = Path.Combine(_root, "servers.dat");
            NbtCompoundValue root;
            bool wasCompressed;

            if (!TryReadServersDat(path, out root, out wasCompressed))
            {
                root = new NbtCompoundValue();
                wasCompressed = false;
            }

            var servers = GetOrCreateServersList(root);
            var normalizedTargetAddress = NormalizeServerAddressForCompare(serverAddress);
            var found = false;

            foreach (var item in servers.Items)
            {
                if (item == null || item.Type != NbtTagType.Compound)
                {
                    continue;
                }

                var entry = item.Value as NbtCompoundValue;
                if (entry == null)
                {
                    continue;
                }

                NbtNode ipNode;
                if (!entry.Tags.TryGetValue("ip", out ipNode)
                    || ipNode == null
                    || ipNode.Type != NbtTagType.String)
                {
                    continue;
                }

                var ip = Convert.ToString(ipNode.Value, CultureInfo.InvariantCulture) ?? string.Empty;
                if (!string.Equals(
                        NormalizeServerAddressForCompare(ip),
                        normalizedTargetAddress,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                found = true;

                NbtNode nameNode;
                if (!entry.Tags.TryGetValue("name", out nameNode)
                    || nameNode == null
                    || nameNode.Type != NbtTagType.String
                    || string.IsNullOrWhiteSpace(Convert.ToString(nameNode.Value, CultureInfo.InvariantCulture)))
                {
                    entry.Tags["name"] = new NbtNode
                    {
                        Type = NbtTagType.String,
                        Value = serverName
                    };
                }

                break;
            }

            if (!found)
            {
                var newEntry = new NbtCompoundValue();
                newEntry.Tags["name"] = new NbtNode
                {
                    Type = NbtTagType.String,
                    Value = serverName
                };
                newEntry.Tags["ip"] = new NbtNode
                {
                    Type = NbtTagType.String,
                    Value = serverAddress.Trim()
                };

                servers.Items.Insert(0, new NbtNode
                {
                    Type = NbtTagType.Compound,
                    Value = newEntry
                });

                LauncherLogService.Info("В список серверов добавлен сервер: " + serverAddress.Trim());
            }

            WriteServersDat(path, root, wasCompressed);
        }

        private static NbtListValue GetOrCreateServersList(NbtCompoundValue root)
        {
            NbtNode serversNode;
            if (root != null
                && root.Tags.TryGetValue("servers", out serversNode)
                && serversNode != null
                && serversNode.Type == NbtTagType.List
                && serversNode.Value is NbtListValue existingList
                && existingList.ElementType == NbtTagType.Compound)
            {
                return existingList;
            }

            var created = new NbtListValue
            {
                ElementType = NbtTagType.Compound
            };

            if (root != null)
            {
                root.Tags["servers"] = new NbtNode
                {
                    Type = NbtTagType.List,
                    Value = created
                };
            }

            return created;
        }

        private static string NormalizeServerAddressForCompare(string address)
        {
            var value = (address ?? string.Empty).Trim();
            if (value.Length == 0)
            {
                return string.Empty;
            }

            if (value.StartsWith("minecraft://", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring("minecraft://".Length);
            }
            else if (value.StartsWith("mc://", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring("mc://".Length);
            }

            value = value.TrimEnd('/');

            if (value.EndsWith(":25565", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(0, value.Length - 6);
            }

            return value.ToLowerInvariant();
        }

        private static bool TryReadServersDat(string path, out NbtCompoundValue root, out bool wasCompressed)
        {
            root = null;
            wasCompressed = false;

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return false;
            }

            var raw = File.ReadAllBytes(path);
            if (TryReadNbtBinary(raw, out root))
            {
                wasCompressed = false;
                return true;
            }

            try
            {
                using (var input = new MemoryStream(raw))
                using (var gzip = new GZipStream(input, CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    gzip.CopyTo(output);
                    var decompressed = output.ToArray();
                    if (TryReadNbtBinary(decompressed, out root))
                    {
                        wasCompressed = true;
                        return true;
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private static bool TryReadNbtBinary(byte[] data, out NbtCompoundValue root)
        {
            root = null;
            if (data == null || data.Length < 3)
            {
                return false;
            }

            try
            {
                using (var stream = new MemoryStream(data))
                using (var reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    var rootType = (NbtTagType)reader.ReadByte();
                    if (rootType != NbtTagType.Compound)
                    {
                        return false;
                    }

                    ReadNbtString(reader); // root name (usually empty)
                    var node = ReadNbtPayload(reader, NbtTagType.Compound);
                    if (node == null || node.Type != NbtTagType.Compound)
                    {
                        return false;
                    }

                    root = node.Value as NbtCompoundValue;
                    return root != null;
                }
            }
            catch
            {
                return false;
            }
        }

        private static NbtNode ReadNbtPayload(BinaryReader reader, NbtTagType type)
        {
            switch (type)
            {
                case NbtTagType.Byte:
                    return new NbtNode { Type = type, Value = reader.ReadByte() };

                case NbtTagType.Short:
                    return new NbtNode { Type = type, Value = ReadInt16BigEndian(reader) };

                case NbtTagType.Int:
                    return new NbtNode { Type = type, Value = ReadInt32BigEndian(reader) };

                case NbtTagType.Long:
                    return new NbtNode { Type = type, Value = ReadInt64BigEndian(reader) };

                case NbtTagType.Float:
                    return new NbtNode
                    {
                        Type = type,
                        Value = BitConverter.ToSingle(
                            BitConverter.GetBytes(ReadInt32BigEndian(reader)),
                            0)
                    };

                case NbtTagType.Double:
                    return new NbtNode
                    {
                        Type = type,
                        Value = BitConverter.Int64BitsToDouble(ReadInt64BigEndian(reader))
                    };

                case NbtTagType.ByteArray:
                {
                    var len = ReadInt32BigEndian(reader);
                    if (len < 0)
                    {
                        throw new InvalidDataException("Invalid NBT byte array length.");
                    }

                    return new NbtNode { Type = type, Value = ReadBytesExact(reader, len) };
                }

                case NbtTagType.String:
                    return new NbtNode { Type = type, Value = ReadNbtString(reader) };

                case NbtTagType.List:
                {
                    var itemType = (NbtTagType)reader.ReadByte();
                    var len = ReadInt32BigEndian(reader);
                    if (len < 0)
                    {
                        throw new InvalidDataException("Invalid NBT list length.");
                    }

                    var list = new NbtListValue
                    {
                        ElementType = itemType
                    };

                    for (var i = 0; i < len; i++)
                    {
                        list.Items.Add(ReadNbtPayload(reader, itemType));
                    }

                    return new NbtNode { Type = type, Value = list };
                }

                case NbtTagType.Compound:
                {
                    var compound = new NbtCompoundValue();
                    while (true)
                    {
                        var innerType = (NbtTagType)reader.ReadByte();
                        if (innerType == NbtTagType.End)
                        {
                            break;
                        }

                        var name = ReadNbtString(reader);
                        compound.Tags[name] = ReadNbtPayload(reader, innerType);
                    }

                    return new NbtNode { Type = type, Value = compound };
                }

                case NbtTagType.IntArray:
                {
                    var len = ReadInt32BigEndian(reader);
                    if (len < 0)
                    {
                        throw new InvalidDataException("Invalid NBT int array length.");
                    }

                    var values = new int[len];
                    for (var i = 0; i < len; i++)
                    {
                        values[i] = ReadInt32BigEndian(reader);
                    }

                    return new NbtNode { Type = type, Value = values };
                }

                case NbtTagType.LongArray:
                {
                    var len = ReadInt32BigEndian(reader);
                    if (len < 0)
                    {
                        throw new InvalidDataException("Invalid NBT long array length.");
                    }

                    var values = new long[len];
                    for (var i = 0; i < len; i++)
                    {
                        values[i] = ReadInt64BigEndian(reader);
                    }

                    return new NbtNode { Type = type, Value = values };
                }

                default:
                    throw new InvalidDataException("Unsupported NBT tag type: " + (byte)type);
            }
        }

        private static void WriteServersDat(string path, NbtCompoundValue root, bool compress)
        {
            if (string.IsNullOrWhiteSpace(path) || root == null)
            {
                return;
            }

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            byte[] nbtData;
            using (var output = new MemoryStream())
            using (var writer = new BinaryWriter(output, Encoding.UTF8))
            {
                writer.Write((byte)NbtTagType.Compound);
                WriteNbtString(writer, string.Empty); // root name
                WriteNbtPayload(writer, new NbtNode { Type = NbtTagType.Compound, Value = root });
                writer.Flush();
                nbtData = output.ToArray();
            }

            var tempPath = path + ".tmp";
            TryDelete(tempPath);

            if (compress)
            {
                using (var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var gzip = new GZipStream(file, CompressionMode.Compress))
                {
                    gzip.Write(nbtData, 0, nbtData.Length);
                }
            }
            else
            {
                File.WriteAllBytes(tempPath, nbtData);
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }

            File.Move(tempPath, path);
        }

        private static void WriteNbtPayload(BinaryWriter writer, NbtNode node)
        {
            if (writer == null || node == null)
            {
                return;
            }

            switch (node.Type)
            {
                case NbtTagType.Byte:
                    writer.Write(Convert.ToByte(node.Value, CultureInfo.InvariantCulture));
                    break;

                case NbtTagType.Short:
                    WriteInt16BigEndian(writer, Convert.ToInt16(node.Value, CultureInfo.InvariantCulture));
                    break;

                case NbtTagType.Int:
                    WriteInt32BigEndian(writer, Convert.ToInt32(node.Value, CultureInfo.InvariantCulture));
                    break;

                case NbtTagType.Long:
                    WriteInt64BigEndian(writer, Convert.ToInt64(node.Value, CultureInfo.InvariantCulture));
                    break;

                case NbtTagType.Float:
                    WriteInt32BigEndian(
                        writer,
                        BitConverter.ToInt32(
                            BitConverter.GetBytes(Convert.ToSingle(node.Value, CultureInfo.InvariantCulture)),
                            0));
                    break;

                case NbtTagType.Double:
                    WriteInt64BigEndian(
                        writer,
                        BitConverter.DoubleToInt64Bits(Convert.ToDouble(node.Value, CultureInfo.InvariantCulture)));
                    break;

                case NbtTagType.ByteArray:
                {
                    var arr = node.Value as byte[] ?? Array.Empty<byte>();
                    WriteInt32BigEndian(writer, arr.Length);
                    writer.Write(arr);
                    break;
                }

                case NbtTagType.String:
                    WriteNbtString(writer, Convert.ToString(node.Value, CultureInfo.InvariantCulture) ?? string.Empty);
                    break;

                case NbtTagType.List:
                {
                    var list = node.Value as NbtListValue ?? new NbtListValue { ElementType = NbtTagType.End };
                    writer.Write((byte)list.ElementType);
                    WriteInt32BigEndian(writer, list.Items.Count);
                    foreach (var item in list.Items)
                    {
                        WriteNbtPayload(writer, item);
                    }
                    break;
                }

                case NbtTagType.Compound:
                {
                    var compound = node.Value as NbtCompoundValue ?? new NbtCompoundValue();
                    foreach (var pair in compound.Tags)
                    {
                        var name = pair.Key ?? string.Empty;
                        var value = pair.Value;
                        if (value == null)
                        {
                            continue;
                        }

                        writer.Write((byte)value.Type);
                        WriteNbtString(writer, name);
                        WriteNbtPayload(writer, value);
                    }

                    writer.Write((byte)NbtTagType.End);
                    break;
                }

                case NbtTagType.IntArray:
                {
                    var arr = node.Value as int[] ?? Array.Empty<int>();
                    WriteInt32BigEndian(writer, arr.Length);
                    for (var i = 0; i < arr.Length; i++)
                    {
                        WriteInt32BigEndian(writer, arr[i]);
                    }
                    break;
                }

                case NbtTagType.LongArray:
                {
                    var arr = node.Value as long[] ?? Array.Empty<long>();
                    WriteInt32BigEndian(writer, arr.Length);
                    for (var i = 0; i < arr.Length; i++)
                    {
                        WriteInt64BigEndian(writer, arr[i]);
                    }
                    break;
                }

                default:
                    throw new InvalidDataException("Unsupported NBT tag type for writing: " + (byte)node.Type);
            }
        }

        private static byte[] ReadBytesExact(BinaryReader reader, int length)
        {
            var bytes = reader.ReadBytes(length);
            if (bytes.Length != length)
            {
                throw new EndOfStreamException("Unexpected end of NBT stream.");
            }

            return bytes;
        }

        private static short ReadInt16BigEndian(BinaryReader reader)
        {
            var bytes = ReadBytesExact(reader, 2);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }

            return BitConverter.ToInt16(bytes, 0);
        }

        private static int ReadInt32BigEndian(BinaryReader reader)
        {
            var bytes = ReadBytesExact(reader, 4);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }

            return BitConverter.ToInt32(bytes, 0);
        }

        private static long ReadInt64BigEndian(BinaryReader reader)
        {
            var bytes = ReadBytesExact(reader, 8);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }

            return BitConverter.ToInt64(bytes, 0);
        }

        private static string ReadNbtString(BinaryReader reader)
        {
            var length = (ushort)ReadInt16BigEndian(reader);
            if (length == 0)
            {
                return string.Empty;
            }

            var bytes = ReadBytesExact(reader, length);
            return Encoding.UTF8.GetString(bytes);
        }

        private static void WriteInt16BigEndian(BinaryWriter writer, short value)
        {
            var bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }

            writer.Write(bytes);
        }

        private static void WriteInt32BigEndian(BinaryWriter writer, int value)
        {
            var bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }

            writer.Write(bytes);
        }

        private static void WriteInt64BigEndian(BinaryWriter writer, long value)
        {
            var bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }

            writer.Write(bytes);
        }

        private static void WriteNbtString(BinaryWriter writer, string value)
        {
            var text = value ?? string.Empty;
            var bytes = Encoding.UTF8.GetBytes(text);
            if (bytes.Length > ushort.MaxValue)
            {
                throw new InvalidDataException("NBT string is too long.");
            }

            WriteInt16BigEndian(writer, (short)bytes.Length);
            writer.Write(bytes);
        }

        #endregion

        #region Hosts file management methods

        /// <summary>
        /// Проверяет, запущено ли приложение с правами администратора
        /// </summary>
        private static bool IsAdmin()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    WindowsPrincipal principal = new WindowsPrincipal(identity);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Добавляет редиректы в файл hosts
        /// </summary>
        private static void AddHostsRedirects()
        {
            if (!IsAdmin())
            {
                LauncherLogService.Warning("Для изменения hosts-файла нужны права администратора!");
                return;
            }

            try
            {
                // Проверяем существование файла
                if (!File.Exists(HostsPath))
                {
                    LauncherLogService.Error($"Файл hosts не найден по пути: {HostsPath}");
                    return;
                }

                string content = File.ReadAllText(HostsPath, Encoding.UTF8);
                bool needUpdate = false;
                StringBuilder newContent = new StringBuilder(content);

                foreach (string redirect in HostsRedirects)
                {
                    if (!content.Contains(redirect))
                    {
                        // Добавляем новую строку, если её нет
                        if (!newContent.ToString().EndsWith("\n"))
                            newContent.AppendLine();
                        newContent.AppendLine(redirect);
                        needUpdate = true;
                    }
                }

                if (needUpdate)
                {
                    File.WriteAllText(HostsPath, newContent.ToString(), Encoding.UTF8);
                    LauncherLogService.Info("Строки для обхода авторизации добавлены в hosts-файл.");
                }
            }
            catch (Exception ex)
            {
                LauncherLogService.Exception($"Ошибка при изменении hosts-файла: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Удаляет редиректы из файла hosts
        /// </summary>
        private static void RemoveHostsRedirects()
        {
            if (!IsAdmin())
            {
                LauncherLogService.Warning("Для изменения hosts-файла нужны права администратора!");
                return;
            }

            try
            {
                if (!File.Exists(HostsPath))
                    return;

                var lines = File.ReadAllLines(HostsPath, Encoding.UTF8);
                var filteredLines = lines.Where(line => 
                    !HostsDomainsToCheck.Any(domain => line.Contains(domain))
                ).ToArray();

                File.WriteAllLines(HostsPath, filteredLines, Encoding.UTF8);
                LauncherLogService.Info("Строки для обхода авторизации удалены из hosts-файла.");
            }
            catch (Exception ex)
            {
                LauncherLogService.Exception($"Ошибка при изменении hosts-файла: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Извлекает версию Minecraft из строки версии
        /// </summary>
        private static string ExtractMinecraftVersion(string versionId)
        {
            if (string.IsNullOrWhiteSpace(versionId))
                return null;

            // Для Forge версий извлекаем базовую версию
            string baseVersion = versionId;
            if (versionId.Contains("-forge-"))
            {
                baseVersion = versionId.Split(new[] { "-forge-" }, StringSplitOptions.None)[0];
            }
            else if (versionId.StartsWith("forge-"))
            {
                var parts = versionId.Split('-');
                if (parts.Length >= 2)
                    baseVersion = parts[1];
            }
            else if (versionId.Contains("fabric-loader"))
            {
                var parts = versionId.Split('-');
                if (parts.Length >= 4)
                    baseVersion = parts[3];
            }

            // Проверяем формат версии (например, "1.16.5" или "1.16")
            if (System.Text.RegularExpressions.Regex.IsMatch(baseVersion, @"^\d+\.\d+(\.\d+)?$"))
                return baseVersion;

            return null;
        }

        /// <summary>
        /// Разбирает версию Minecraft на компоненты
        /// </summary>
        private static int[] ParseMinecraftVersionParts(string version)
        {
            try
            {
                var parts = version.Split('.');
                if (parts.Length >= 2)
                {
                    int major = int.Parse(parts[0]);
                    int minor = int.Parse(parts[1]);
                    int patch = parts.Length > 2 ? int.Parse(parts[2]) : 0;
                    return new[] { major, minor, patch };
                }
            }
            catch
            {
                return null;
            }
            return null;
        }

        /// <summary>
        /// Синхронизирует редиректы в зависимости от версии Minecraft
        /// </summary>
        /// <param name="versionId">Идентификатор версии Minecraft</param>
        private void SyncAuthHostsRedirects(string versionId)
        {
            try
            {
                string minecraftVersion = ExtractMinecraftVersion(versionId);
                if (string.IsNullOrEmpty(minecraftVersion))
                {
                    RemoveHostsRedirects();
                    return;
                }

                int[] parts = ParseMinecraftVersionParts(minecraftVersion);
                if (parts == null)
                {
                    RemoveHostsRedirects();
                    return;
                }

                // Включаем редиректы только для версий 1.16.0-1.16.5
                if (parts[0] == 1 && parts[1] == 16 && parts[2] <= 5)
                {
                    AddHostsRedirects();
                }
                else
                {
                    RemoveHostsRedirects();
                }
            }
            catch (Exception ex)
            {
                LauncherLogService.Exception($"Failed to sync auth hosts redirects: {ex.Message}", ex);
            }
        }

        #endregion
    }
}

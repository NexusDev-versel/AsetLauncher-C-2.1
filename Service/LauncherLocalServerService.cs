using AsetLauncher.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace AsetLauncher.Services
{
    public sealed class LauncherLocalServerService
    {
        private const string ForgePromotionsUrl = "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json";
        private const string ForgeMavenBaseUrl = "https://maven.minecraftforge.net/net/minecraftforge/forge";
        private const string PurpurVersionsUrl = "https://api.purpurmc.org/v2/purpur";
        private const string VelocityVersionsUrl = "https://api.papermc.io/v2/projects/velocity";
        private const string BungeeCordBuildsUrl = "https://ci.md-5.net/job/BungeeCord/api/json?tree=builds[number,result]";

        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer
        {
            MaxJsonLength = int.MaxValue,
            RecursionLimit = 128
        };

        private static readonly HttpClient Http = CreateHttpClient();

        public static string GetServersRootPath()
        {
            var root = MinecraftLauncherService.GetMinecraftRootPath();
            var serversRoot = Path.Combine(root, "local-servers");
            Directory.CreateDirectory(serversRoot);
            return serversRoot;
        }

        public static string GetServersConfigPath()
        {
            return Path.Combine(GetServersRootPath(), "servers.json");
        }

        public IReadOnlyList<LocalServerProfile> Load()
        {
            try
            {
                var path = GetServersConfigPath();
                if (!File.Exists(path))
                {
                    return new List<LocalServerProfile>();
                }

                var jsonText = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(jsonText))
                {
                    return new List<LocalServerProfile>();
                }

                var storage = Json.Deserialize<LocalServerStorage>(jsonText) ?? new LocalServerStorage();
                return NormalizeAll(storage.Servers);
            }
            catch (Exception ex)
            {
                LauncherLogService.Warn("Не удалось загрузить список локальных серверов: " + ex.Message);
                return new List<LocalServerProfile>();
            }
        }

        public void Save(IEnumerable<LocalServerProfile> servers)
        {
            var normalized = NormalizeAll(servers);
            var storage = new LocalServerStorage { Servers = normalized.ToList() };
            File.WriteAllText(GetServersConfigPath(), Json.Serialize(storage), Encoding.UTF8);
        }

        public LocalServerProfile CreateServer(string name, string core, string version)
        {
            var safeName = string.IsNullOrWhiteSpace(name) ? "MyServer" : name.Trim();
            var safeCore = NormalizeCore(core);
            var safeVersion = string.IsNullOrWhiteSpace(version) ? GetDefaultVersionForCore(safeCore) : version.Trim();

            var id = Guid.NewGuid().ToString("N");
            var folderName = SanitizeFolderName(safeName) + "_" + id.Substring(0, 8);
            var folderPath = Path.Combine(GetServersRootPath(), folderName);

            Directory.CreateDirectory(folderPath);
            Directory.CreateDirectory(Path.Combine(folderPath, "logs"));

            var profile = new LocalServerProfile
            {
                Id = id,
                Name = safeName,
                Core = safeCore,
                Version = safeVersion,
                RamMb = 2048,
                ExtraJavaArgs = string.Empty,
                FolderPath = folderPath,
                JarFileName = GetDefaultJarFileName(safeCore),
                InstalledCoreVersion = string.Empty,
                CreatedAtUtc = DateTime.UtcNow.ToString("o")
            };

            WriteBootstrapFiles(profile);

            var servers = Load().ToList();
            servers.Add(profile);
            Save(servers);
            return profile;
        }

        public async Task InstallServerCoreAsync(LocalServerProfile server, IProgress<string> status, CancellationToken ct)
        {
            if (server == null)
            {
                throw new ArgumentNullException(nameof(server));
            }

            var core = NormalizeCore(server.Core);
            Directory.CreateDirectory(server.FolderPath);
            ReportStatus(status, "Установка ядра " + core + "...");

            if (string.Equals(core, "Forge", StringComparison.OrdinalIgnoreCase))
            {
                await InstallForgeServerAsync(server, status, ct).ConfigureAwait(false);
            }
            else if (string.Equals(core, "Purpur", StringComparison.OrdinalIgnoreCase))
            {
                await InstallPurpurServerAsync(server, status, ct).ConfigureAwait(false);
            }
            else if (string.Equals(core, "Velocity", StringComparison.OrdinalIgnoreCase))
            {
                await InstallVelocityServerAsync(server, status, ct).ConfigureAwait(false);
            }
            else if (string.Equals(core, "Bungee_Cord", StringComparison.OrdinalIgnoreCase))
            {
                await InstallBungeeServerAsync(server, status, ct).ConfigureAwait(false);
            }
            else
            {
                throw new InvalidOperationException("Неподдерживаемое ядро: " + core);
            }

            EnsureEulaAccepted(server.FolderPath);
            server.InstalledCoreVersion = BuildInstallSignature(server);
            WriteBootstrapFiles(server);
            SaveOrUpdateServer(server);
            ReportStatus(status, "Установка завершена.");
        }

        public async Task<IReadOnlyList<string>> GetAvailableVersionsAsync(string core, CancellationToken ct)
        {
            var normalizedCore = NormalizeCore(core);
            try
            {
                List<string> versions;
                if (string.Equals(normalizedCore, "Forge", StringComparison.OrdinalIgnoreCase))
                {
                    versions = await GetForgeVersionsAsync(ct).ConfigureAwait(false);
                }
                else if (string.Equals(normalizedCore, "Purpur", StringComparison.OrdinalIgnoreCase))
                {
                    versions = await GetPurpurVersionsAsync(ct).ConfigureAwait(false);
                }
                else if (string.Equals(normalizedCore, "Velocity", StringComparison.OrdinalIgnoreCase))
                {
                    versions = await GetVelocityVersionsAsync(ct).ConfigureAwait(false);
                }
                else if (string.Equals(normalizedCore, "Bungee_Cord", StringComparison.OrdinalIgnoreCase))
                {
                    versions = await GetBungeeCordVersionsAsync(ct).ConfigureAwait(false);
                }
                else
                {
                    versions = new List<string>();
                }

                return versions.Count > 0 ? versions : GetFallbackVersions(normalizedCore);
            }
            catch (Exception ex)
            {
                LauncherLogService.Warn("Не удалось загрузить список версий ядра " + normalizedCore + ": " + ex.Message);
                return GetFallbackVersions(normalizedCore);
            }
        }

        public static bool IsServerCoreInstalled(LocalServerProfile server)
        {
            if (server == null || string.IsNullOrWhiteSpace(server.FolderPath) || !Directory.Exists(server.FolderPath))
            {
                return false;
            }

            var expectedSignature = BuildInstallSignature(server);
            if (!string.Equals(expectedSignature, (server.InstalledCoreVersion ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var core = NormalizeCore(server.Core);
            if (string.Equals(core, "Forge", StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(Path.Combine(server.FolderPath, "run.bat")))
                {
                    return true;
                }

                return Directory.GetFiles(server.FolderPath, "forge-*.jar", SearchOption.TopDirectoryOnly).Length > 0;
            }

            var preferred = Path.Combine(server.FolderPath, string.IsNullOrWhiteSpace(server.JarFileName) ? GetDefaultJarFileName(core) : server.JarFileName);
            return File.Exists(preferred) || !string.IsNullOrWhiteSpace(ResolveJarPath(server));
        }

        public static string ResolveJavaPathForServer(LocalServerProfile server)
        {
            var required = GetRequiredJavaMajor(server);
            var runtimeRoot = Path.Combine(MinecraftLauncherService.GetMinecraftRootPath(), "runtime");

            foreach (var major in BuildJavaMajorFallbackOrder(required))
            {
                var javaPath = FindJavaInRuntime(runtimeRoot, major);
                if (!string.IsNullOrWhiteSpace(javaPath))
                {
                    return javaPath;
                }
            }

            return "java";
        }

        public static string BuildServerLaunchArguments(LocalServerProfile server, string jarFilePath)
        {
            var ramMb = Math.Max(512, server != null ? server.RamMb : 2048);
            var extraArgs = server != null ? (server.ExtraJavaArgs ?? string.Empty).Trim() : string.Empty;
            var jarName = Path.GetFileName(jarFilePath ?? string.Empty);
            if (string.IsNullOrWhiteSpace(jarName))
            {
                jarName = "server.jar";
            }

            var core = NormalizeCore(server != null ? server.Core : string.Empty);
            var sb = new StringBuilder();
            sb.Append("-Xms512M -Xmx").Append(ramMb).Append("M ");
            if (!string.IsNullOrWhiteSpace(extraArgs))
            {
                sb.Append(extraArgs).Append(" ");
            }

            sb.Append("-jar \"").Append(jarName).Append("\"");
            if (!string.Equals(core, "Velocity", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(core, "Bungee_Cord", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append(" nogui");
            }

            return sb.ToString();
        }

        public static string ResolveJarPath(LocalServerProfile server)
        {
            if (server == null || string.IsNullOrWhiteSpace(server.FolderPath) || !Directory.Exists(server.FolderPath))
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(server.JarFileName))
            {
                var preferred = Path.Combine(server.FolderPath, server.JarFileName);
                if (File.Exists(preferred))
                {
                    return preferred;
                }
            }

            var files = Directory.GetFiles(server.FolderPath, "*.jar", SearchOption.TopDirectoryOnly);
            return files.Length > 0 ? files[0] : string.Empty;
        }

        public static string NormalizeCore(string core)
        {
            var value = (core ?? string.Empty).Trim();
            if (string.Equals(value, "Forge", StringComparison.OrdinalIgnoreCase)) return "Forge";
            if (string.Equals(value, "Purpur", StringComparison.OrdinalIgnoreCase)) return "Purpur";
            if (string.Equals(value, "Velocity", StringComparison.OrdinalIgnoreCase)) return "Velocity";
            if (string.Equals(value, "Bungee_Cord", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "BungeeCord", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "Bungee", StringComparison.OrdinalIgnoreCase)) return "Bungee_Cord";
            return "Purpur";
        }

        public static string GetDefaultVersionForCore(string core)
        {
            var normalized = NormalizeCore(core);
            if (string.Equals(normalized, "Forge", StringComparison.OrdinalIgnoreCase)) return "1.20.1-47.3.0";
            if (string.Equals(normalized, "Purpur", StringComparison.OrdinalIgnoreCase)) return "1.20.6";
            if (string.Equals(normalized, "Velocity", StringComparison.OrdinalIgnoreCase)) return "3.3.0";
            return "latest";
        }
        private async Task InstallPurpurServerAsync(LocalServerProfile server, IProgress<string> status, CancellationToken ct)
        {
            var version = (server.Version ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(version)) throw new InvalidOperationException("Не выбрана версия Purpur.");

            var url = "https://api.purpurmc.org/v2/purpur/" + Uri.EscapeDataString(version) + "/latest/download";
            var targetPath = Path.Combine(server.FolderPath, "purpur.jar");

            ReportStatus(status, "Скачивание Purpur " + version + "...");
            await DownloadFileAsync(url, targetPath, ct, true).ConfigureAwait(false);
            server.JarFileName = "purpur.jar";
        }

        private async Task InstallBungeeServerAsync(LocalServerProfile server, IProgress<string> status, CancellationToken ct)
        {
            var version = (server.Version ?? string.Empty).Trim();
            var buildSegment = "lastSuccessfulBuild";
            if (!string.IsNullOrWhiteSpace(version) && version.StartsWith("build-", StringComparison.OrdinalIgnoreCase))
            {
                buildSegment = version.Substring("build-".Length);
            }

            if (string.IsNullOrWhiteSpace(buildSegment)) buildSegment = "lastSuccessfulBuild";

            var url = "https://ci.md-5.net/job/BungeeCord/" + buildSegment + "/artifact/bootstrap/target/BungeeCord.jar";
            var targetPath = Path.Combine(server.FolderPath, "bungeecord.jar");

            ReportStatus(status, "Скачивание BungeeCord " + version + "...");
            await DownloadFileAsync(url, targetPath, ct, true).ConfigureAwait(false);
            server.JarFileName = "bungeecord.jar";
        }

        private async Task InstallVelocityServerAsync(LocalServerProfile server, IProgress<string> status, CancellationToken ct)
        {
            var version = (server.Version ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(version)) throw new InvalidOperationException("Не выбрана версия Velocity.");

            ReportStatus(status, "Получение build версии Velocity " + version + "...");
            var versionMeta = await GetJsonObjectAsync("https://api.papermc.io/v2/projects/velocity/versions/" + Uri.EscapeDataString(version), ct).ConfigureAwait(false);
            var builds = Arr(versionMeta, "builds");
            if (builds == null || builds.Count == 0) throw new InvalidOperationException("Для версии Velocity " + version + " не найдены сборки.");

            var build = builds
                .Select(x => Convert.ToString(x, CultureInfo.InvariantCulture))
                .Select(x => { int v; return int.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : -1; })
                .Where(x => x > 0)
                .DefaultIfEmpty(-1)
                .Max();

            if (build <= 0) throw new InvalidOperationException("Не удалось определить build Velocity.");

            var buildMeta = await GetJsonObjectAsync(
                "https://api.papermc.io/v2/projects/velocity/versions/" + Uri.EscapeDataString(version) + "/builds/" + build.ToString(CultureInfo.InvariantCulture),
                ct).ConfigureAwait(false);

            var downloads = Dict(Obj(buildMeta, "downloads"));
            if (downloads == null || downloads.Count == 0) throw new InvalidOperationException("Не найден файл загрузки Velocity для build " + build + ".");

            var downloadName = downloads.Keys.FirstOrDefault(k => !string.IsNullOrWhiteSpace(k) && k.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                ?? downloads.Keys.FirstOrDefault();

            if (string.IsNullOrWhiteSpace(downloadName)) throw new InvalidOperationException("Не удалось определить имя jar для Velocity.");

            var url = "https://api.papermc.io/v2/projects/velocity/versions/"
                + Uri.EscapeDataString(version)
                + "/builds/"
                + build.ToString(CultureInfo.InvariantCulture)
                + "/downloads/"
                + Uri.EscapeDataString(downloadName);

            var targetPath = Path.Combine(server.FolderPath, "velocity.jar");
            ReportStatus(status, "Скачивание Velocity " + version + " build " + build + "...");
            await DownloadFileAsync(url, targetPath, ct, true).ConfigureAwait(false);
            server.JarFileName = "velocity.jar";
        }

        private async Task InstallForgeServerAsync(LocalServerProfile server, IProgress<string> status, CancellationToken ct)
        {
            var forgeVersion = (server.Version ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(forgeVersion) || !forgeVersion.Contains("-"))
            {
                throw new InvalidOperationException("Некорректная версия Forge. Формат: 1.16.5-36.2.42");
            }

            var escaped = Uri.EscapeDataString(forgeVersion);
            var installerUrl = ForgeMavenBaseUrl + "/" + escaped + "/forge-" + escaped + "-installer.jar";
            var installerPath = Path.Combine(server.FolderPath, "forge-" + SanitizeFileName(forgeVersion) + "-installer.jar");

            ReportStatus(status, "Скачивание Forge installer " + forgeVersion + "...");
            await DownloadFileAsync(installerUrl, installerPath, ct, true).ConfigureAwait(false);

            var javaPath = ResolveJavaPathForServer(server);
            var args = "-jar \"" + installerPath + "\" --installServer";
            ReportStatus(status, "Запуск Forge installer...");

            var result = await RunProcessAndCaptureAsync(javaPath, args, server.FolderPath, status, ct, "[FORGE-INSTALL] ").ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "Forge installer завершился с ошибкой. Код: " + result.ExitCode
                    + (string.IsNullOrWhiteSpace(result.ErrorTail) ? string.Empty : " | " + result.ErrorTail));
            }

            if (!File.Exists(Path.Combine(server.FolderPath, "run.bat")))
            {
                var forgeJar = Directory.GetFiles(server.FolderPath, "forge-*.jar", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (string.IsNullOrWhiteSpace(forgeJar))
                {
                    throw new InvalidOperationException("Forge установлен, но jar/run.bat не найден в папке сервера.");
                }

                server.JarFileName = Path.GetFileName(forgeJar);
            }
            else
            {
                var preferredJar = Directory.GetFiles(server.FolderPath, "forge-*.jar", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(preferredJar))
                {
                    server.JarFileName = Path.GetFileName(preferredJar);
                }
            }
        }

        private async Task<List<string>> GetForgeVersionsAsync(CancellationToken ct)
        {
            var root = await GetJsonObjectAsync(ForgePromotionsUrl, ct).ConfigureAwait(false);
            var promos = Dict(Obj(root, "promos"));
            if (promos == null || promos.Count == 0) return new List<string>();

            var selected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in promos)
            {
                ct.ThrowIfCancellationRequested();

                var promoKey = pair.Key ?? string.Empty;
                var forgeVersion = Convert.ToString(pair.Value, CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(forgeVersion)) continue;

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

                if (string.IsNullOrWhiteSpace(gameVersion) || !SupportsForgeVersion(gameVersion)) continue;
                if (isLatest || !selected.ContainsKey(gameVersion)) selected[gameVersion] = forgeVersion.Trim();
            }

            return selected
                .Select(x => x.Key + "-" + x.Value)
                .OrderByDescending(v => v, Comparer<string>.Create(CompareVersionLike))
                .Take(120)
                .ToList();
        }

        private async Task<List<string>> GetPurpurVersionsAsync(CancellationToken ct)
        {
            var root = await GetJsonObjectAsync(PurpurVersionsUrl, ct).ConfigureAwait(false);
            var versions = Arr(root, "versions");
            if (versions == null) return new List<string>();

            return versions
                .Select(v => Convert.ToString(v, CultureInfo.InvariantCulture))
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(v => v, Comparer<string>.Create(CompareVersionLike))
                .Take(120)
                .ToList();
        }

        private async Task<List<string>> GetVelocityVersionsAsync(CancellationToken ct)
        {
            var root = await GetJsonObjectAsync(VelocityVersionsUrl, ct).ConfigureAwait(false);
            var versions = Arr(root, "versions");
            if (versions == null) return new List<string>();

            return versions
                .Select(v => Convert.ToString(v, CultureInfo.InvariantCulture))
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(v => v, Comparer<string>.Create(CompareVersionLike))
                .Take(120)
                .ToList();
        }

        private async Task<List<string>> GetBungeeCordVersionsAsync(CancellationToken ct)
        {
            var root = await GetJsonObjectAsync(BungeeCordBuildsUrl, ct).ConfigureAwait(false);
            var builds = Arr(root, "builds");
            if (builds == null || builds.Count == 0) return new List<string> { "latest" };

            var result = new List<string> { "latest" };
            foreach (var item in builds)
            {
                var obj = Dict(item);
                if (obj == null) continue;

                var number = Convert.ToString(Obj(obj, "number"), CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(number)) continue;

                var status = Convert.ToString(Obj(obj, "result"), CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(status)
                    && !string.Equals(status, "SUCCESS", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(status, "null", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                result.Add("build-" + number.Trim());
            }

            return result.Distinct(StringComparer.OrdinalIgnoreCase).Take(120).ToList();
        }
        private static List<string> GetFallbackVersions(string core)
        {
            var normalized = NormalizeCore(core);
            if (string.Equals(normalized, "Forge", StringComparison.OrdinalIgnoreCase))
            {
                return new List<string> { "1.20.1-47.3.0", "1.18.2-40.2.21", "1.16.5-36.2.42", "1.12.2-14.23.5.2860" };
            }

            if (string.Equals(normalized, "Purpur", StringComparison.OrdinalIgnoreCase))
            {
                return new List<string> { "1.20.6", "1.20.4", "1.19.4", "1.16.5" };
            }

            if (string.Equals(normalized, "Velocity", StringComparison.OrdinalIgnoreCase))
            {
                return new List<string> { "3.3.0", "3.2.0" };
            }

            return new List<string> { "latest" };
        }

        private static bool SupportsForgeVersion(string gameVersion)
        {
            var parts = ParseVersionParts(gameVersion);
            if (parts == null || parts.Length < 2) return false;
            return parts[0] > 1 || (parts[0] == 1 && parts[1] >= 12);
        }

        private static int CompareVersionLike(string left, string right)
        {
            var l = (left ?? string.Empty).Trim();
            var r = (right ?? string.Empty).Trim();
            var lt = Regex.Matches(l, "\\d+|[A-Za-z]+")
                .Cast<Match>()
                .Select(m => m.Value)
                .ToList();
            var rt = Regex.Matches(r, "\\d+|[A-Za-z]+")
                .Cast<Match>()
                .Select(m => m.Value)
                .ToList();

            var max = Math.Max(lt.Count, rt.Count);
            for (var i = 0; i < max; i++)
            {
                if (i >= lt.Count) return -1;
                if (i >= rt.Count) return 1;

                var leftToken = lt[i];
                var rightToken = rt[i];

                int leftNum;
                int rightNum;
                var leftIsNum = int.TryParse(leftToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out leftNum);
                var rightIsNum = int.TryParse(rightToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out rightNum);

                int cmp;
                if (leftIsNum && rightIsNum)
                {
                    cmp = leftNum.CompareTo(rightNum);
                }
                else
                {
                    cmp = string.Compare(leftToken, rightToken, StringComparison.OrdinalIgnoreCase);
                }

                if (cmp != 0) return cmp;
            }

            return string.Compare(l, r, StringComparison.OrdinalIgnoreCase);
        }

        private static int[] ParseVersionParts(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return null;

            var raw = version.Trim();
            var dash = raw.IndexOf('-');
            if (dash >= 0) raw = raw.Substring(0, dash);

            var parts = raw.Split('.');
            if (parts.Length < 2) return null;

            var result = new List<int>();
            foreach (var part in parts)
            {
                int value;
                if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return null;
                result.Add(value);
            }

            return result.ToArray();
        }

        private static int GetRequiredJavaMajor(LocalServerProfile server)
        {
            var core = NormalizeCore(server != null ? server.Core : string.Empty);
            var version = server != null ? (server.Version ?? string.Empty) : string.Empty;
            var mcVersion = version;

            if (string.Equals(core, "Forge", StringComparison.OrdinalIgnoreCase))
            {
                var idx = version.IndexOf('-');
                if (idx > 0) mcVersion = version.Substring(0, idx);
            }

            var parts = ParseVersionParts(mcVersion);
            if (parts == null || parts.Length < 2)
            {
                if (string.Equals(core, "Velocity", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(core, "Bungee_Cord", StringComparison.OrdinalIgnoreCase)) return 17;
                return 17;
            }

            if (parts[0] > 1) return 21;

            var minor = parts[1];
            var patch = parts.Length > 2 ? parts[2] : 0;
            if (minor > 20 || (minor == 20 && patch >= 5)) return 21;
            if (minor >= 17) return 17;
            return 8;
        }

        private static IEnumerable<int> BuildJavaMajorFallbackOrder(int requiredMajor)
        {
            var list = new List<int> { requiredMajor };
            if (!list.Contains(21)) list.Add(21);
            if (!list.Contains(17)) list.Add(17);
            if (!list.Contains(8)) list.Add(8);
            return list;
        }

        private static string FindJavaInRuntime(string runtimeRoot, int major)
        {
            if (string.IsNullOrWhiteSpace(runtimeRoot) || !Directory.Exists(runtimeRoot)) return string.Empty;

            var majorFolder = Path.Combine(runtimeRoot, "java-" + major.ToString(CultureInfo.InvariantCulture));
            if (!Directory.Exists(majorFolder)) return string.Empty;

            try
            {
                var files = Directory.GetFiles(majorFolder, "java.exe", SearchOption.AllDirectories);
                return files.FirstOrDefault() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static void EnsureEulaAccepted(string serverFolder)
        {
            if (string.IsNullOrWhiteSpace(serverFolder)) return;

            var eulaPath = Path.Combine(serverFolder, "eula.txt");
            var text = "# EULA accepted by AsetLauncher" + Environment.NewLine + "eula=true" + Environment.NewLine;
            File.WriteAllText(eulaPath, text, Encoding.UTF8);
        }

        private static string BuildInstallSignature(LocalServerProfile server)
        {
            var core = NormalizeCore(server != null ? server.Core : string.Empty);
            var version = server != null ? (server.Version ?? string.Empty).Trim() : string.Empty;
            return core + "|" + version;
        }

        private void SaveOrUpdateServer(LocalServerProfile server)
        {
            var all = Load().ToList();
            var index = all.FindIndex(s => string.Equals(s.Id, server.Id, StringComparison.OrdinalIgnoreCase));
            if (index < 0) all.Add(server);
            else all[index] = server;
            Save(all);
        }

        private static void ReportStatus(IProgress<string> status, string message)
        {
            if (status != null && !string.IsNullOrWhiteSpace(message)) status.Report(message);
            if (!string.IsNullOrWhiteSpace(message)) LauncherLogService.Info("[LocalServer] " + message);
        }

        private async Task DownloadFileAsync(string url, string targetPath, CancellationToken ct, bool overwrite)
        {
            if (string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException("Пустой URL загрузки.");

            var dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

            if (!overwrite && File.Exists(targetPath) && new FileInfo(targetPath).Length > 0) return;

            using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var output = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await input.CopyToAsync(output).ConfigureAwait(false);
                }
            }
        }

        private sealed class ProcessResult
        {
            public int ExitCode { get; set; }
            public string ErrorTail { get; set; }
        }

        private async Task<ProcessResult> RunProcessAndCaptureAsync(string fileName, string args, string workDir, IProgress<string> status, CancellationToken ct, string linePrefix)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                WorkingDirectory = workDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var errorLines = new List<string>();

            process.OutputDataReceived += (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) ReportStatus(status, (linePrefix ?? string.Empty) + e.Data);
            };

            process.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    ReportStatus(status, (linePrefix ?? string.Empty) + e.Data);
                    lock (errorLines)
                    {
                        errorLines.Add(e.Data);
                        if (errorLines.Count > 30) errorLines.RemoveAt(0);
                    }
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using (ct.Register(() =>
            {
                try
                {
                    if (!process.HasExited) process.Kill();
                }
                catch
                {
                }
            }))
            {
                await Task.Run(() => process.WaitForExit(), ct).ConfigureAwait(false);
            }

            process.CancelOutputRead();
            process.CancelErrorRead();

            string tail;
            lock (errorLines)
            {
                tail = string.Join(" | ", errorLines);
            }

            return new ProcessResult { ExitCode = process.ExitCode, ErrorTail = tail };
        }

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(4) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AsetLauncher/1.0");
            return client;
        }

        private async Task<Dictionary<string, object>> GetJsonObjectAsync(string url, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(url)) return new Dictionary<string, object>();

            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            using (var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(content)) return new Dictionary<string, object>();

                var parsed = Json.DeserializeObject(content);
                return Dict(parsed) ?? new Dictionary<string, object>();
            }
        }

        private static Dictionary<string, object> Dict(object value)
        {
            return value as Dictionary<string, object>;
        }

        private static List<object> Arr(Dictionary<string, object> dict, string key)
        {
            return Obj(dict, key) as List<object>;
        }

        private static object Obj(Dictionary<string, object> dict, string key)
        {
            if (dict == null || key == null) return null;
            object value;
            return dict.TryGetValue(key, out value) ? value : null;
        }
        private static List<LocalServerProfile> NormalizeAll(IEnumerable<LocalServerProfile> servers)
        {
            var result = new List<LocalServerProfile>();
            if (servers == null) return result;

            foreach (var source in servers)
            {
                var normalized = Normalize(source);
                if (normalized != null) result.Add(normalized);
            }

            return result
                .GroupBy(s => s.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static LocalServerProfile Normalize(LocalServerProfile source)
        {
            if (source == null) return null;

            source.Id = string.IsNullOrWhiteSpace(source.Id) ? Guid.NewGuid().ToString("N") : source.Id.Trim();
            source.Name = string.IsNullOrWhiteSpace(source.Name) ? "MyServer" : source.Name.Trim();
            source.Core = NormalizeCore(source.Core);
            source.Version = string.IsNullOrWhiteSpace(source.Version) ? GetDefaultVersionForCore(source.Core) : source.Version.Trim();
            source.RamMb = ClampRam(source.RamMb);
            source.ExtraJavaArgs = (source.ExtraJavaArgs ?? string.Empty).Trim();
            source.FolderPath = (source.FolderPath ?? string.Empty).Trim();
            source.JarFileName = string.IsNullOrWhiteSpace(source.JarFileName) ? GetDefaultJarFileName(source.Core) : source.JarFileName.Trim();
            source.InstalledCoreVersion = (source.InstalledCoreVersion ?? string.Empty).Trim();
            source.CreatedAtUtc = string.IsNullOrWhiteSpace(source.CreatedAtUtc) ? DateTime.UtcNow.ToString("o") : source.CreatedAtUtc.Trim();
            return source;
        }

        private static int ClampRam(int ramMb)
        {
            if (ramMb < 512) return 512;
            if (ramMb > 65536) return 65536;
            return ramMb;
        }

        private static string GetDefaultJarFileName(string core)
        {
            var normalized = NormalizeCore(core);
            if (string.Equals(normalized, "Forge", StringComparison.OrdinalIgnoreCase)) return "forge.jar";
            if (string.Equals(normalized, "Bungee_Cord", StringComparison.OrdinalIgnoreCase)) return "bungeecord.jar";
            if (string.Equals(normalized, "Velocity", StringComparison.OrdinalIgnoreCase)) return "velocity.jar";
            return "purpur.jar";
        }

        private static string SanitizeFolderName(string name)
        {
            var raw = (name ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(raw)) return "Server";

            var invalid = Path.GetInvalidFileNameChars();
            var chars = raw.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
            var cleaned = new string(chars).Trim();
            return string.IsNullOrWhiteSpace(cleaned) ? "Server" : cleaned;
        }

        private static string SanitizeFileName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = (value ?? string.Empty).Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
            var result = new string(chars).Trim();
            return string.IsNullOrWhiteSpace(result) ? "file" : result;
        }

        private static void WriteBootstrapFiles(LocalServerProfile server)
        {
            var serverRoot = server.FolderPath;
            Directory.CreateDirectory(serverRoot);

            EnsureEulaAccepted(serverRoot);

            var propertiesPath = Path.Combine(serverRoot, "server.properties");
            if (!File.Exists(propertiesPath))
            {
                var propertiesText =
                    "motd=" + (server.Name ?? "AsetLauncher Server") + Environment.NewLine +
                    "online-mode=true" + Environment.NewLine +
                    "enable-command-block=true" + Environment.NewLine +
                    "max-players=20" + Environment.NewLine +
                    "server-port=25565" + Environment.NewLine;
                File.WriteAllText(propertiesPath, propertiesText, Encoding.UTF8);
            }

            var startBatPath = Path.Combine(serverRoot, "start.bat");
            if (!File.Exists(startBatPath))
            {
                var args = BuildServerLaunchArguments(server, server.JarFileName);
                var bat =
                    "@echo off" + Environment.NewLine +
                    "title " + (server.Name ?? "Minecraft Server") + Environment.NewLine +
                    "java " + args + Environment.NewLine +
                    "pause" + Environment.NewLine;
                File.WriteAllText(startBatPath, bat, Encoding.UTF8);
            }

            var readmePath = Path.Combine(serverRoot, "README_AsetLauncher.txt");
            var readme =
                "Сервер создан в AsetLauncher." + Environment.NewLine +
                "Ядро: " + server.Core + Environment.NewLine +
                "Версия: " + server.Version + Environment.NewLine +
                Environment.NewLine +
                "Ядро устанавливается автоматически при создании/первом запуске." + Environment.NewLine +
                "Кнопка Старт запускает сервер без ручного копирования jar." + Environment.NewLine;
            File.WriteAllText(readmePath, readme, Encoding.UTF8);
        }
    }
}

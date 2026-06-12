using AsetLauncher.Models;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace AsetLauncher.Services
{
    public sealed class LauncherSettingsService
    {
        private const string DefaultBackendBaseUrl = "https://asetlauncher.ru";

        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer
        {
            MaxJsonLength = int.MaxValue,
            RecursionLimit = 64
        };

        public static string GetSettingsPath()
        {
            var root = MinecraftLauncherService.GetMinecraftRootPath();
            Directory.CreateDirectory(root);
            return Path.Combine(root, "launcher-settings.json");
        }

        public LauncherSettings Load()
        {
            try
            {
                var path = GetSettingsPath();
                if (!File.Exists(path))
                {
                    return Normalize(new LauncherSettings());
                }

                var jsonText = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(jsonText))
                {
                    return Normalize(new LauncherSettings());
                }

                var settings = Json.Deserialize<LauncherSettings>(jsonText);
                var hasFabricFlag = jsonText.IndexOf("\"ShowFabricVersions\"", StringComparison.OrdinalIgnoreCase) >= 0;
                var hasForgeFlag = jsonText.IndexOf("\"ShowForgeVersions\"", StringComparison.OrdinalIgnoreCase) >= 0;
                var hasMusicEnabled = jsonText.IndexOf("\"MusicEnabled\"", StringComparison.OrdinalIgnoreCase) >= 0;
                var hasMusicVolume = jsonText.IndexOf("\"MusicVolume\"", StringComparison.OrdinalIgnoreCase) >= 0;
                var hasMusicTrack = jsonText.IndexOf("\"MusicTrackId\"", StringComparison.OrdinalIgnoreCase) >= 0;
                if (settings != null)
                {
                    if (!hasFabricFlag)
                    {
                        settings.ShowFabricVersions = true;
                    }

                    if (!hasForgeFlag)
                    {
                        settings.ShowForgeVersions = true;
                    }

                    if (!hasMusicEnabled)
                    {
                        settings.MusicEnabled = true;
                    }

                    if (!hasMusicVolume)
                    {
                        settings.MusicVolume = 35;
                    }

                    if (!hasMusicTrack)
                    {
                        settings.MusicTrackId = string.Empty;
                    }
                }
                var normalized = Normalize(settings);
                LauncherLogService.Info("Настройки загружены из: " + path);
                return normalized;
            }
            catch
            {
                LauncherLogService.Warn("Не удалось загрузить настройки, применены значения по умолчанию.");
                return Normalize(new LauncherSettings());
            }
        }

        public void Save(LauncherSettings settings)
        {
            var normalized = Normalize(settings);
            var jsonText = Json.Serialize(normalized);
            var path = GetSettingsPath();
            File.WriteAllText(path, jsonText, Encoding.UTF8);
            LauncherLogService.Info("Настройки сохранены в: " + path);
        }

        public static LauncherAccount GetSelectedAccount(LauncherSettings settings)
        {
            if (settings == null || settings.Accounts == null || settings.Accounts.Count == 0)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(settings.SelectedAccountId))
            {
                var selected = settings.Accounts.FirstOrDefault(a =>
                    string.Equals(a.Id, settings.SelectedAccountId, StringComparison.OrdinalIgnoreCase));
                if (selected != null)
                {
                    return selected;
                }
            }

            return settings.Accounts[0];
        }

        private static LauncherSettings Normalize(LauncherSettings settings)
        {
            if (settings == null)
            {
                settings = new LauncherSettings();
            }

            if (settings.MaxRamMb < 512)
            {
                settings.MaxRamMb = 512;
            }

            if (settings.MaxRamMb > 32768)
            {
                settings.MaxRamMb = 32768;
            }

            if (string.IsNullOrWhiteSpace(settings.ThemeId))
            {
                settings.ThemeId = "default";
            }
            else
            {
                settings.ThemeId = settings.ThemeId.Trim();
            }

            if (settings.MusicVolume < 0)
            {
                settings.MusicVolume = 0;
            }

            if (settings.MusicVolume > 100)
            {
                settings.MusicVolume = 100;
            }

            settings.MusicTrackId = (settings.MusicTrackId ?? string.Empty)
                .Trim()
                .Replace('\\', '/');

            settings.BackendBaseUrl = NormalizeBackendBaseUrl(settings.BackendBaseUrl);
            settings.PlayerNickname = (settings.PlayerNickname ?? string.Empty).Trim();
            settings.PlayerAvatarPath = (settings.PlayerAvatarPath ?? string.Empty).Trim();

            if (settings.Accounts == null)
            {
                settings.Accounts = new System.Collections.Generic.List<LauncherAccount>();
            }

            for (var i = 0; i < settings.Accounts.Count; i++)
            {
                settings.Accounts[i] = NormalizeAccount(settings.Accounts[i]);
            }

            settings.Accounts = settings.Accounts
                .Where(a => a != null)
                .GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            if (settings.Accounts.Count == 0)
            {
                var migratedNickname = string.IsNullOrWhiteSpace(settings.PlayerNickname)
                    ? Environment.UserName
                    : settings.PlayerNickname;

                var migrated = new LauncherAccount
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Type = "offline",
                    Nickname = migratedNickname.Trim(),
                    Uuid = OfflineUuid(migratedNickname),
                    AccessToken = "0",
                    ClientToken = string.Empty,
                    UserType = "legacy",
                    AvatarPath = settings.PlayerAvatarPath,
                    CreatedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
                };

                settings.Accounts.Add(NormalizeAccount(migrated));
            }

            if (string.IsNullOrWhiteSpace(settings.SelectedAccountId)
                || !settings.Accounts.Any(a => string.Equals(a.Id, settings.SelectedAccountId, StringComparison.OrdinalIgnoreCase)))
            {
                settings.SelectedAccountId = settings.Accounts[0].Id;
            }
            else
            {
                settings.SelectedAccountId = settings.SelectedAccountId.Trim();
            }

            var selectedAccount = GetSelectedAccount(settings);
            if (selectedAccount != null)
            {
                settings.PlayerNickname = selectedAccount.Nickname ?? Environment.UserName;
                settings.PlayerAvatarPath = selectedAccount.AvatarPath ?? string.Empty;
            }

            return settings;
        }

        private static LauncherAccount NormalizeAccount(LauncherAccount account)
        {
            if (account == null)
            {
                account = new LauncherAccount();
            }

            account.Id = string.IsNullOrWhiteSpace(account.Id)
                ? Guid.NewGuid().ToString("N")
                : account.Id.Trim();

            account.Type = string.Equals(account.Type, "authorized", StringComparison.OrdinalIgnoreCase)
                ? "authorized"
                : "offline";

            account.Nickname = (account.Nickname ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(account.Nickname))
            {
                account.Nickname = Environment.UserName;
            }

            account.Uuid = NormalizeUuid(account.Uuid);
            if (string.IsNullOrWhiteSpace(account.Uuid) && account.Type == "offline")
            {
                account.Uuid = OfflineUuid(account.Nickname);
            }

            account.AccessToken = (account.AccessToken ?? string.Empty).Trim();
            account.ClientToken = (account.ClientToken ?? string.Empty).Trim();
            account.UserType = (account.UserType ?? string.Empty).Trim();
            account.AvatarPath = (account.AvatarPath ?? string.Empty).Trim();
            account.CreatedAtUtc = string.IsNullOrWhiteSpace(account.CreatedAtUtc)
                ? DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
                : account.CreatedAtUtc.Trim();

            if (string.IsNullOrWhiteSpace(account.UserType))
            {
                account.UserType = account.Type == "authorized" ? "mojang" : "legacy";
            }

            return account;
        }

        private static string NormalizeBackendBaseUrl(string url)
        {
            // Backend address is fixed for launcher users.
            // Old persisted custom/local values are ignored.
            return DefaultBackendBaseUrl;
        }

        private static string NormalizeUuid(string uuid)
        {
            if (string.IsNullOrWhiteSpace(uuid))
            {
                return string.Empty;
            }

            return uuid.Replace("-", string.Empty).Trim().ToLowerInvariant();
        }

        private static string OfflineUuid(string name)
        {
            using (var md5 = MD5.Create())
            {
                return new Guid(md5.ComputeHash(Encoding.UTF8.GetBytes("OfflinePlayer:" + (name ?? string.Empty))))
                    .ToString("N");
            }
        }
    }
}

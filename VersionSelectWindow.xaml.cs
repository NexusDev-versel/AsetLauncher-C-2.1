using AsetLauncher.Models;
using AsetLauncher.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace AsetLauncher
{
    public partial class VersionSelectWindow : Window
    {
        private readonly MinecraftLauncherService _launcherService = new MinecraftLauncherService();
        private readonly LauncherSettingsService _settingsService = new LauncherSettingsService();

        private LauncherSettings _settings;
        private CancellationTokenSource _loadCts;
        private readonly List<MinecraftVersionEntry> _manifestCache = new List<MinecraftVersionEntry>();
        private List<MinecraftVersionEntry> _sourceVersions = new List<MinecraftVersionEntry>();
        private bool _showInstalledOnly;
        public MinecraftVersionEntry SelectedVersion { get; private set; }

        public VersionSelectWindow()
        {
            InitializeComponent();
            Loaded += VersionSelectWindow_Loaded;
            ThemeService.ThemeChanged += ThemeService_ThemeChanged;
            UpdateFilterButtonText();
        }

        private async void VersionSelectWindow_Loaded(object sender, RoutedEventArgs e)
        {
            LauncherLogService.Info("Открыто окно выбора версии.");
            ApplyTheme();
            _settings = _settingsService.Load();
            await ReloadVersionsAsync();
        }

        private void ThemeService_ThemeChanged(LauncherTheme theme)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(ApplyTheme));
                return;
            }

            ApplyTheme();
        }

        private void ApplyTheme()
        {
            var activeTheme = ThemeService.CurrentTheme;
            VersionWindowBackgroundBrush.ImageSource = ThemeService.CreateImageSourceOrFallback(
                activeTheme != null ? activeTheme.MainImagePath : null,
                "Assets/main-menu-bg-mc.png");
        }

        private async Task ReloadVersionsAsync()
        {
            try
            {
                _loadCts?.Cancel();
                _loadCts = new CancellationTokenSource();

                _settings = _settingsService.Load();

                if (_showInstalledOnly)
                {
                    SetBusy(true, string.Empty);
                    _sourceVersions = GetInstalledVersions()
                        .Where(IsEntryVisibleBySettings)
                        .ToList();
                    LauncherLogService.Info("Показаны установленные версии.");
                }
                else
                {
                    SetBusy(true, string.Empty);
                    if (_manifestCache.Count == 0)
                    {
                        _manifestCache.AddRange(await _launcherService.GetVanillaAndFabricVersionsAsync(_loadCts.Token));
                        LauncherLogService.Info("Загружен список версий Minecraft (Vanilla + Fabric + Forge).");
                    }

                    _sourceVersions = FilterManifestBySettings(_manifestCache);
                    LauncherLogService.Info("Показаны версии из онлайн-списка.");
                }

                ApplySearchFilter();
                UpdateFilterButtonText();

                if (VersionComboBox.Items.Count == 0)
                {
                    StatusTextBlock.Text = "Версии не найдены.";
                }
                else if (StatusTextBlock.Text == "Версии не найдены.")
                {
                    StatusTextBlock.Text = string.Empty;
                }
            }
            catch (OperationCanceledException)
            {
                StatusTextBlock.Text = string.Empty;
            }
            catch (Exception ex)
            {
                LauncherLogService.Exception("Ошибка загрузки списка версий", ex);
                StatusTextBlock.Text = "Ошибка загрузки списка версий.";
                MessageBox.Show(
                    "Не удалось загрузить список версий Minecraft:\n\n" + ex.Message,
                    "AsetLauncher",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false, StatusTextBlock.Text);
            }
        }

        private List<MinecraftVersionEntry> FilterManifestBySettings(IEnumerable<MinecraftVersionEntry> manifest)
        {
            return manifest
                .Where(IsEntryVisibleBySettings)
                .ToList();
        }

        private bool IsEntryVisibleBySettings(MinecraftVersionEntry entry)
        {
            if (entry == null)
            {
                return false;
            }

            if (_settings != null)
            {
                if (!_settings.ShowFabricVersions && (IsFabricType(entry.Type) || IsFabricId(entry.Id)))
                {
                    return false;
                }

                if (!_settings.ShowForgeVersions && (IsForgeType(entry.Type) || IsForgeId(entry.Id)))
                {
                    return false;
                }
            }

            if (IsSnapshotType(entry.Type))
            {
                return _settings != null && _settings.ShowSnapshots;
            }

            return true;
        }

        private List<MinecraftVersionEntry> GetInstalledVersions()
        {
            var versionsDir = Path.Combine(MinecraftLauncherService.GetMinecraftRootPath(), "versions");
            if (!Directory.Exists(versionsDir))
            {
                return new List<MinecraftVersionEntry>();
            }

            var manifestById = _manifestCache
                .GroupBy(v => v.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToDictionary(v => v.Id, v => v, StringComparer.OrdinalIgnoreCase);

            var installed = new List<MinecraftVersionEntry>();

            foreach (var versionDir in Directory.GetDirectories(versionsDir))
            {
                var versionId = Path.GetFileName(versionDir);
                if (string.IsNullOrWhiteSpace(versionId))
                {
                    continue;
                }

                var metadataPath = Path.Combine(versionDir, versionId + ".json");
                if (!File.Exists(metadataPath))
                {
                    continue;
                }

                MinecraftVersionEntry manifestEntry;
                manifestById.TryGetValue(versionId, out manifestEntry);

                var type = manifestEntry != null
                    ? manifestEntry.Type
                    : TryReadLocalVersionType(metadataPath);

                if (string.IsNullOrWhiteSpace(type))
                {
                    type = "installed";
                }

                var displayName = manifestEntry != null
                    ? manifestEntry.DisplayName
                    : BuildDisplayNameFromLocalId(versionId, type);

                if (type == "release" || type == "snapshot")
                {
                    string forgeGameVersion;
                    string forgeLoaderVersion;
                    if (TryParseForgeVersionId(versionId, out forgeGameVersion, out forgeLoaderVersion))
                    {
                        type = "forge-release";
                    }

                    string fabricGameVersion;
                    string fabricLoaderVersion;
                    if (TryParseFabricVersionId(versionId, out fabricGameVersion, out fabricLoaderVersion))
                    {
                        type = IsSnapshotType(fabricGameVersion) ? "fabric-snapshot" : "fabric-release";
                    }
                }

                if (!_settings.ShowSnapshots && IsSnapshotType(type))
                {
                    continue;
                }

                var releaseTime = manifestEntry != null
                    ? manifestEntry.ReleaseTime
                    : File.GetLastWriteTimeUtc(metadataPath);

                installed.Add(new MinecraftVersionEntry
                {
                    Id = versionId,
                    Type = type,
                    ReleaseTime = releaseTime,
                    MetadataUrl = manifestEntry != null ? manifestEntry.MetadataUrl : string.Empty,
                    DisplayName = displayName
                });
            }

            return installed
                .OrderByDescending(v => v.ReleaseTime)
                .ThenByDescending(v => v.Id)
                .ToList();
        }

        private static string TryReadLocalVersionType(string metadataPath)
        {
            try
            {
                var json = File.ReadAllText(metadataPath);
                var markerIndex = json.IndexOf("\"type\"", StringComparison.OrdinalIgnoreCase);
                if (markerIndex < 0)
                {
                    return "installed";
                }

                var colonIndex = json.IndexOf(':', markerIndex);
                if (colonIndex < 0)
                {
                    return "installed";
                }

                var quoteStart = json.IndexOf('"', colonIndex + 1);
                if (quoteStart < 0)
                {
                    return "installed";
                }

                var quoteEnd = json.IndexOf('"', quoteStart + 1);
                if (quoteEnd < 0)
                {
                    return "installed";
                }

                var value = json.Substring(quoteStart + 1, quoteEnd - quoteStart - 1).Trim();
                return string.IsNullOrWhiteSpace(value) ? "installed" : value;
            }
            catch
            {
                return "installed";
            }
        }

        private void ApplySearchFilter()
        {
            var query = SearchTextBox.Text?.Trim();
            IEnumerable<MinecraftVersionEntry> filtered = _sourceVersions;

            if (!string.IsNullOrWhiteSpace(query))
            {
                filtered = filtered.Where(v =>
                {
                    var idMatch = !string.IsNullOrWhiteSpace(v.Id)
                        && v.Id.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (idMatch)
                    {
                        return true;
                    }

                    var display = string.IsNullOrWhiteSpace(v.DisplayName) ? v.Id : v.DisplayName;
                    return !string.IsNullOrWhiteSpace(display)
                        && display.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
                });
            }

            var list = filtered.ToList();
            VersionComboBox.ItemsSource = list;
            VersionComboBox.SelectedItem = list.FirstOrDefault(v => !IsSnapshotType(v.Type)) ?? list.FirstOrDefault();
        }

        private async void InstalledFilterButton_Click(object sender, RoutedEventArgs e)
        {
            _showInstalledOnly = !_showInstalledOnly;
            LauncherLogService.Info(_showInstalledOnly
                ? "Переключено на установленные версии."
                : "Переключено на версии из манифеста.");
            await ReloadVersionsAsync();
        }

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplySearchFilter();

            if (VersionComboBox.Items.Count > 0 && StatusTextBlock.Text == "Версии не найдены.")
            {
                StatusTextBlock.Text = string.Empty;
            }
            else if (VersionComboBox.Items.Count == 0 && !string.IsNullOrWhiteSpace(SearchTextBox.Text))
            {
                StatusTextBlock.Text = "Версии не найдены.";
            }
        }

        private void UpdateFilterButtonText()
        {
            InstalledFilterButton.Content = _showInstalledOnly
                ? "Показать манифест"
                : "Показать установленные";
        }

        private void LaunchButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = VersionComboBox.SelectedItem as MinecraftVersionEntry;
            if (selected == null)
            {
                MessageBox.Show(
                    "Сначала выберите версию Minecraft.",
                    "AsetLauncher",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            SelectedVersion = selected;
            LauncherLogService.Info("Выбрана версия для запуска: " + selected.Id);
            DialogResult = true;
            Close();
        }

        private void SetBusy(bool isBusy, string statusText)
        {
            LaunchButton.IsEnabled = !isBusy;
            InstalledFilterButton.IsEnabled = !isBusy;
            VersionComboBox.IsEnabled = !isBusy;
            SearchTextBox.IsEnabled = !isBusy;
            BusyProgressBar.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;

            if (!string.IsNullOrWhiteSpace(statusText))
            {
                StatusTextBlock.Text = statusText;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            ThemeService.ThemeChanged -= ThemeService_ThemeChanged;
            _loadCts?.Cancel();
            _loadCts?.Dispose();
            base.OnClosed(e);
        }

        private static bool IsSnapshotType(string type)
        {
            if (string.IsNullOrWhiteSpace(type))
            {
                return false;
            }

            return type.IndexOf("snapshot", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsFabricType(string type)
        {
            return !string.IsNullOrWhiteSpace(type)
                && type.IndexOf("fabric", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsForgeType(string type)
        {
            return !string.IsNullOrWhiteSpace(type)
                && type.IndexOf("forge", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsFabricId(string versionId)
        {
            return !string.IsNullOrWhiteSpace(versionId)
                && versionId.StartsWith("fabric-loader-", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsForgeId(string versionId)
        {
            if (string.IsNullOrWhiteSpace(versionId))
            {
                return false;
            }

            return versionId.StartsWith("forge-", StringComparison.OrdinalIgnoreCase)
                || versionId.IndexOf("-forge-", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string BuildDisplayNameFromLocalId(string versionId, string type)
        {
            string forgeGameVersion;
            string forgeLoaderVersion;
            if (TryParseForgeVersionId(versionId, out forgeGameVersion, out forgeLoaderVersion))
            {
                return forgeGameVersion + " (Forge)";
            }

            string gameVersion;
            string loaderVersion;
            if (TryParseFabricVersionId(versionId, out gameVersion, out loaderVersion))
            {
                return gameVersion + " (Fabric)";
            }

            return versionId;
        }

        private static bool TryParseFabricVersionId(string versionId, out string gameVersion, out string loaderVersion)
        {
            gameVersion = string.Empty;
            loaderVersion = string.Empty;

            const string prefix = "fabric-loader-";
            if (string.IsNullOrWhiteSpace(versionId)
                || !versionId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var tail = versionId.Substring(prefix.Length);
            var separatorIndex = tail.IndexOf('-');
            if (separatorIndex <= 0 || separatorIndex >= tail.Length - 1)
            {
                return false;
            }

            loaderVersion = tail.Substring(0, separatorIndex);
            gameVersion = tail.Substring(separatorIndex + 1);
            return !string.IsNullOrWhiteSpace(gameVersion);
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
    }
}

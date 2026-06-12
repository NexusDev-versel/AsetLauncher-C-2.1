using AsetLauncher.Models;
using AsetLauncher.Services;
using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AsetLauncher
{
    public partial class SettingsWindow : Window
    {
        private enum SettingsPage
        {
            Launcher,
            Minecraft,
            Servers
        }

        private static readonly Color ActiveNavBackground = Color.FromRgb(0x2D, 0x4E, 0x74);
        private static readonly Color InactiveNavBackground = Color.FromRgb(0x23, 0x32, 0x45);
        private static readonly Color ActiveNavBorder = Color.FromRgb(0x55, 0x88, 0xBF);
        private static readonly Color InactiveNavBorder = Color.FromRgb(0x4A, 0x5F, 0x79);
        private static readonly TimeSpan PanelFadeDuration = TimeSpan.FromMilliseconds(210);
        private static readonly TimeSpan NavColorDuration = TimeSpan.FromMilliseconds(230);

        private readonly LauncherSettingsService _settingsService = new LauncherSettingsService();
        private static ConsoleWindow _consoleWindow;
        private static ServerManagerWindow _serverManagerWindow;
        private LauncherTheme[] _availableThemes = Array.Empty<LauncherTheme>();
        private LauncherMusicTrack[] _availableTracks = Array.Empty<LauncherMusicTrack>();
        private SettingsPage _currentPage = SettingsPage.Launcher;
        private int _pageTransitionToken;
        private bool _suppressMusicUiEvents;

        public LauncherSettings CurrentSettings { get; private set; }

        public SettingsWindow()
        {
            InitializeComponent();
            LoadSettings();
            SelectPage(SettingsPage.Launcher, false);
            LauncherLogService.Info("Открыто окно настроек лаунчера.");
        }

        private void LoadSettings()
        {
            CurrentSettings = _settingsService.Load();
            LauncherLogService.Info("Настройки лаунчера загружены.");

            RamGbTextBox.Text = FormatRamGb(CurrentSettings.MaxRamMb);
            SnapshotsCheckBox.IsChecked = CurrentSettings.ShowSnapshots;
            ShowFabricCheckBox.IsChecked = CurrentSettings.ShowFabricVersions;
            ShowForgeCheckBox.IsChecked = CurrentSettings.ShowForgeVersions;

            _availableThemes = ThemeService.GetAvailableThemes().ToArray();
            ThemeComboBox.ItemsSource = _availableThemes;
            ThemeComboBox.DisplayMemberPath = "Name";

            var selectedTheme = _availableThemes.FirstOrDefault(t =>
                t.Id.Equals(CurrentSettings.ThemeId ?? "default", StringComparison.OrdinalIgnoreCase))
                ?? _availableThemes.FirstOrDefault();

            ThemeComboBox.SelectedItem = selectedTheme;
            LoadTrackOptions();
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            double ramGb;
            if (!TryParseRamGb(RamGbTextBox.Text, out ramGb))
            {
                MessageBox.Show(
                    "Введите корректное число ГБ для оперативной памяти.",
                    "AsetLauncher",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var ramMb = (int)Math.Round(ramGb * 1024.0, MidpointRounding.AwayFromZero);
            if (ramMb < 512)
            {
                ramMb = 512;
            }

            if (ramMb > 32768)
            {
                ramMb = 32768;
            }

            if (CurrentSettings == null)
            {
                CurrentSettings = _settingsService.Load();
            }

            CurrentSettings.MaxRamMb = ramMb;
            CurrentSettings.ShowSnapshots = SnapshotsCheckBox.IsChecked == true;
            CurrentSettings.ShowFabricVersions = ShowFabricCheckBox.IsChecked != false;
            CurrentSettings.ShowForgeVersions = ShowForgeCheckBox.IsChecked != false;
            CurrentSettings.ThemeId = ((ThemeComboBox.SelectedItem as LauncherTheme)?.Id) ?? "default";
            CurrentSettings.MusicVolume = ClampVolume((int)Math.Round(MusicVolumeSlider.Value, MidpointRounding.AwayFromZero));
            CurrentSettings.MusicTrackId = NormalizeTrackId((TrackComboBox.SelectedItem as LauncherMusicTrack)?.Id);
            CurrentSettings.MusicEnabled = CurrentSettings.MusicEnabled && _availableTracks.Length > 0;

            _settingsService.Save(CurrentSettings);
            ThemeService.ApplyTheme(CurrentSettings.ThemeId);
            LauncherMusicService.ApplySettings(CurrentSettings);
            LauncherLogService.Info("Настройки лаунчера сохранены.");
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var persisted = _settingsService.Load();
                LauncherMusicService.ApplySettings(persisted);
            }
            catch (Exception ex)
            {
                LauncherLogService.Warn("Не удалось восстановить музыкальные настройки после отмены: " + ex.Message);
            }

            Close();
        }

        private void ConsoleButton_Click(object sender, RoutedEventArgs e)
        {
            if (_consoleWindow == null)
            {
                LauncherLogService.Info("Открытие окна консоли.");
                _consoleWindow = new ConsoleWindow
                {
                    Owner = Owner as Window
                };

                _consoleWindow.Closed += (s, args) => _consoleWindow = null;
                _consoleWindow.Show();
                _consoleWindow.Activate();
                return;
            }

            if (_consoleWindow.WindowState == WindowState.Minimized)
            {
                _consoleWindow.WindowState = WindowState.Normal;
            }

            LauncherLogService.Info("Активация уже открытого окна консоли.");
            _consoleWindow.Activate();
        }

        private void OpenServerManagerButton_Click(object sender, RoutedEventArgs e)
        {
            if (_serverManagerWindow == null)
            {
                _serverManagerWindow = new ServerManagerWindow();

                _serverManagerWindow.Closed += (s, args) => _serverManagerWindow = null;
                _serverManagerWindow.Show();
                _serverManagerWindow.Activate();
                return;
            }

            if (_serverManagerWindow.WindowState == WindowState.Minimized)
            {
                _serverManagerWindow.WindowState = WindowState.Normal;
            }

            _serverManagerWindow.Activate();
        }

        private void TrackComboBox_DropDownOpened(object sender, EventArgs e)
        {
            RefreshTrackList(true);
        }

        private void TrackComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressMusicUiEvents || CurrentSettings == null)
            {
                return;
            }

            var selected = TrackComboBox.SelectedItem as LauncherMusicTrack;
            CurrentSettings.MusicTrackId = NormalizeTrackId(selected != null ? selected.Id : string.Empty);
            ApplyMusicPreview();
        }

        private void MusicToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (CurrentSettings == null)
            {
                return;
            }

            CurrentSettings.MusicEnabled = !CurrentSettings.MusicEnabled;
            UpdateMusicToggleButton();
            ApplyMusicPreview();
        }

        private void MusicVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressMusicUiEvents || CurrentSettings == null)
            {
                return;
            }

            CurrentSettings.MusicVolume = ClampVolume((int)Math.Round(MusicVolumeSlider.Value, MidpointRounding.AwayFromZero));
            UpdateVolumeText();
            ApplyMusicPreview();
        }

        private void LoadTrackOptions()
        {
            if (CurrentSettings == null)
            {
                return;
            }

            _suppressMusicUiEvents = true;

            RefreshTrackList(false);

            CurrentSettings.MusicVolume = ClampVolume(CurrentSettings.MusicVolume);
            MusicVolumeSlider.Value = CurrentSettings.MusicVolume;
            UpdateVolumeText();

            if (_availableTracks.Length == 0)
            {
                CurrentSettings.MusicEnabled = false;
            }

            UpdateMusicToggleButton();
            _suppressMusicUiEvents = false;
            ApplyMusicPreview();
        }

        private void RefreshTrackList(bool preserveSelection)
        {
            var previousTrackId = preserveSelection
                ? NormalizeTrackId((TrackComboBox.SelectedItem as LauncherMusicTrack)?.Id)
                : NormalizeTrackId(CurrentSettings != null ? CurrentSettings.MusicTrackId : string.Empty);

            _availableTracks = LauncherMusicService.GetAvailableTracks().ToArray();
            TrackComboBox.ItemsSource = _availableTracks;
            TrackComboBox.DisplayMemberPath = "Name";

            LauncherMusicTrack selected = null;
            if (!string.IsNullOrWhiteSpace(previousTrackId))
            {
                selected = _availableTracks.FirstOrDefault(t =>
                    string.Equals(NormalizeTrackId(t.Id), previousTrackId, StringComparison.OrdinalIgnoreCase));
            }

            if (selected == null)
            {
                selected = _availableTracks.FirstOrDefault();
            }

            TrackComboBox.SelectedItem = selected;
            TrackComboBox.IsEnabled = _availableTracks.Length > 0;
            MusicVolumeSlider.IsEnabled = _availableTracks.Length > 0;

            if (CurrentSettings != null)
            {
                CurrentSettings.MusicTrackId = NormalizeTrackId(selected != null ? selected.Id : string.Empty);
                if (_availableTracks.Length == 0)
                {
                    CurrentSettings.MusicEnabled = false;
                }
            }

            MusicHintTextBlock.Text = _availableTracks.Length == 0
                ? "В папке Assets/Sounds нет MP3 треков."
                : "Треки берутся из папки Assets/Sounds. Поддерживаются MP3.";

            UpdateMusicToggleButton();
        }

        private void UpdateMusicToggleButton()
        {
            var hasTracks = _availableTracks.Length > 0;
            var enabled = hasTracks && CurrentSettings != null && CurrentSettings.MusicEnabled;

            MusicToggleButton.Content = enabled ? "Музыка: Вкл" : "Музыка: Выкл";
            MusicToggleButton.IsEnabled = hasTracks;
            MusicToggleButton.Background = enabled
                ? new SolidColorBrush(Color.FromRgb(0x2D, 0x4E, 0x74))
                : new SolidColorBrush(Color.FromRgb(0x3F, 0x4F, 0x64));
            MusicToggleButton.BorderBrush = enabled
                ? new SolidColorBrush(Color.FromRgb(0x55, 0x88, 0xBF))
                : new SolidColorBrush(Color.FromRgb(0x70, 0x88, 0xA7));
        }

        private void UpdateVolumeText()
        {
            var volume = CurrentSettings != null ? ClampVolume(CurrentSettings.MusicVolume) : 0;
            MusicVolumeTextBlock.Text = volume.ToString(CultureInfo.InvariantCulture) + "%";
        }

        private void ApplyMusicPreview()
        {
            if (_suppressMusicUiEvents || CurrentSettings == null)
            {
                return;
            }

            LauncherMusicService.ApplySettings(CurrentSettings);
        }

        private void LauncherPageButton_Click(object sender, RoutedEventArgs e)
        {
            _ = SelectPageAsync(SettingsPage.Launcher);
        }

        private void MinecraftPageButton_Click(object sender, RoutedEventArgs e)
        {
            _ = SelectPageAsync(SettingsPage.Minecraft);
        }

        private void ServersPageButton_Click(object sender, RoutedEventArgs e)
        {
            _ = SelectPageAsync(SettingsPage.Servers);
        }

        private void SelectPage(SettingsPage page, bool animate)
        {
            if (!animate)
            {
                LauncherPagePanel.Visibility = page == SettingsPage.Launcher ? Visibility.Visible : Visibility.Collapsed;
                MinecraftPagePanel.Visibility = page == SettingsPage.Minecraft ? Visibility.Visible : Visibility.Collapsed;
                ServersPagePanel.Visibility = page == SettingsPage.Servers ? Visibility.Visible : Visibility.Collapsed;

                LauncherPagePanel.Opacity = page == SettingsPage.Launcher ? 1 : 0;
                MinecraftPagePanel.Opacity = page == SettingsPage.Minecraft ? 1 : 0;
                ServersPagePanel.Opacity = page == SettingsPage.Servers ? 1 : 0;
            }

            ApplyNavButtonState(LauncherPageButton, page == SettingsPage.Launcher, animate);
            ApplyNavButtonState(MinecraftPageButton, page == SettingsPage.Minecraft, animate);
            ApplyNavButtonState(ServersPageButton, page == SettingsPage.Servers, animate);

            _currentPage = page;
            LauncherLogService.Info("Открыт раздел настроек: " + GetPageName(page));
        }

        private async Task SelectPageAsync(SettingsPage page)
        {
            if (page == _currentPage)
            {
                ApplyNavButtonState(LauncherPageButton, page == SettingsPage.Launcher, true);
                ApplyNavButtonState(MinecraftPageButton, page == SettingsPage.Minecraft, true);
                ApplyNavButtonState(ServersPageButton, page == SettingsPage.Servers, true);
                return;
            }

            var token = ++_pageTransitionToken;
            var previousPage = _currentPage;
            _currentPage = page;

            ApplyNavButtonState(LauncherPageButton, page == SettingsPage.Launcher, true);
            ApplyNavButtonState(MinecraftPageButton, page == SettingsPage.Minecraft, true);
            ApplyNavButtonState(ServersPageButton, page == SettingsPage.Servers, true);

            var oldPanel = GetPagePanel(previousPage);
            var newPanel = GetPagePanel(page);

            if (oldPanel != null && oldPanel != newPanel && oldPanel.Visibility == Visibility.Visible)
            {
                await AnimateOpacityAsync(oldPanel, 0, PanelFadeDuration).ConfigureAwait(true);
                if (token != _pageTransitionToken)
                {
                    return;
                }

                oldPanel.Visibility = Visibility.Collapsed;
            }

            if (newPanel == null)
            {
                return;
            }

            newPanel.Visibility = Visibility.Visible;
            newPanel.Opacity = 0;
            await AnimateOpacityAsync(newPanel, 1, PanelFadeDuration).ConfigureAwait(true);

            if (token == _pageTransitionToken)
            {
                LauncherLogService.Info("Открыт раздел настроек: " + GetPageName(page));
            }
        }

        private FrameworkElement GetPagePanel(SettingsPage page)
        {
            switch (page)
            {
                case SettingsPage.Launcher:
                    return LauncherPagePanel;
                case SettingsPage.Minecraft:
                    return MinecraftPagePanel;
                case SettingsPage.Servers:
                    return ServersPagePanel;
                default:
                    return LauncherPagePanel;
            }
        }

        private void ApplyNavButtonState(Button button, bool isActive, bool animate)
        {
            if (button == null)
            {
                return;
            }

            var targetBackground = isActive ? ActiveNavBackground : InactiveNavBackground;
            var targetBorder = isActive ? ActiveNavBorder : InactiveNavBorder;

            var backgroundBrush = EnsureSolidColorBrush(button.Background, InactiveNavBackground);
            var borderBrush = EnsureSolidColorBrush(button.BorderBrush, InactiveNavBorder);
            button.Background = backgroundBrush;
            button.BorderBrush = borderBrush;

            if (!animate)
            {
                backgroundBrush.Color = targetBackground;
                borderBrush.Color = targetBorder;
                return;
            }

            var colorEase = new CubicEase { EasingMode = EasingMode.EaseInOut };
            backgroundBrush.BeginAnimation(
                SolidColorBrush.ColorProperty,
                new ColorAnimation
                {
                    To = targetBackground,
                    Duration = NavColorDuration,
                    EasingFunction = colorEase
                });

            borderBrush.BeginAnimation(
                SolidColorBrush.ColorProperty,
                new ColorAnimation
                {
                    To = targetBorder,
                    Duration = NavColorDuration,
                    EasingFunction = colorEase
                });
        }

        private static SolidColorBrush EnsureSolidColorBrush(Brush brush, Color fallback)
        {
            var solid = brush as SolidColorBrush;
            if (solid == null || solid.IsFrozen)
            {
                return new SolidColorBrush(fallback);
            }

            return solid;
        }

        private static Task AnimateOpacityAsync(UIElement element, double to, TimeSpan duration)
        {
            if (element == null)
            {
                return Task.CompletedTask;
            }

            var tcs = new TaskCompletionSource<bool>();
            var animation = new DoubleAnimation
            {
                To = to,
                Duration = duration,
                EasingFunction = new CubicEase
                {
                    EasingMode = EasingMode.EaseInOut
                }
            };

            animation.Completed += (s, e) => tcs.TrySetResult(true);
            element.BeginAnimation(UIElement.OpacityProperty, animation);
            return tcs.Task;
        }

        private static string GetPageName(SettingsPage page)
        {
            switch (page)
            {
                case SettingsPage.Launcher:
                    return "Настройки лаунчера";
                case SettingsPage.Minecraft:
                    return "Майнкрафт";
                case SettingsPage.Servers:
                    return "Сервера";
                default:
                    return "Неизвестно";
            }
        }

        private static bool TryParseRamGb(string raw, out double value)
        {
            value = 0;
            var text = (raw ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            text = text.Replace(',', '.');
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                return false;
            }

            return value > 0;
        }

        private static string FormatRamGb(int ramMb)
        {
            var gb = Math.Max(0.5, ramMb / 1024.0);
            if (Math.Abs(gb - Math.Round(gb)) < 0.001)
            {
                return Math.Round(gb).ToString("0", CultureInfo.InvariantCulture);
            }

            return gb.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static int ClampVolume(int value)
        {
            if (value < 0)
            {
                return 0;
            }

            if (value > 100)
            {
                return 100;
            }

            return value;
        }

        private static string NormalizeTrackId(string trackId)
        {
            return (trackId ?? string.Empty)
                .Trim()
                .Replace('\\', '/');
        }
    }
}

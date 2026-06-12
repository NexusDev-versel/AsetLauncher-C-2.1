using AsetLauncher.Models;
using AsetLauncher.Services;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AsetLauncher
{
    public partial class MainWindow : Window
    {
        private const double MenuOpenX = 0;
        private const double MenuClosedX = -170;
        private const double ArrowOpenAngle = 180;
        private const double ArrowClosedAngle = 0;

        private static readonly Regex ProgressRegex = new Regex(@"\((\d+)\s*/\s*(\d+)\)", RegexOptions.Compiled);

        private readonly MinecraftLauncherService _launcherService = new MinecraftLauncherService();
        private readonly LauncherSettingsService _settingsService = new LauncherSettingsService();

        private CancellationTokenSource _launchCts;
        private bool _isMenuOpen;
        private bool _isLaunchInProgress;
        private string _lastLaunchStatusText = string.Empty;
        private DateTime _lastLaunchStatusUiUpdateUtc = DateTime.MinValue;

        public MainWindow()
        {
            InitializeComponent();
            SlideMenuTransform.X = MenuClosedX;
            MenuArrowRotate.Angle = ArrowClosedAngle;

            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed;
            LauncherLogService.Info("Главное окно открыто.");
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            ThemeService.ThemeChanged += ThemeService_ThemeChanged;
            ApplyTheme();
        }

        private void MainWindow_Closed(object sender, EventArgs e)
        {
            ThemeService.ThemeChanged -= ThemeService_ThemeChanged;

            try
            {
                _launchCts?.Cancel();
                _launchCts?.Dispose();
            }
            catch
            {
            }
            finally
            {
                _launchCts = null;
            }
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

            MainRootGrid.Background = new ImageBrush
            {
                ImageSource = ThemeService.CreateImageSourceOrFallback(
                    activeTheme != null ? activeTheme.MainImagePath : null,
                    "Assets/main-menu-bg-mc.png"),
                Stretch = Stretch.UniformToFill
            };

            LauncherLogoImage.Source = ThemeService.CreateImageSourceOrFallback(
                activeTheme != null ? activeTheme.LogoImagePath : null,
                "Assets/logo.png");
        }

        private void MenuToggleButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleMenu(!_isMenuOpen);
        }

        private void ToggleMenu(bool open)
        {
            _isMenuOpen = open;
            SlideMenuPanel.IsHitTestVisible = open;
            LauncherLogService.Info(open ? "Меню открыто." : "Меню закрыто.");

            var slideAnimation = new DoubleAnimation
            {
                To = open ? MenuOpenX : MenuClosedX,
                Duration = TimeSpan.FromMilliseconds(220),
                EasingFunction = new CubicEase
                {
                    EasingMode = EasingMode.EaseOut
                }
            };

            var arrowAnimation = new DoubleAnimation
            {
                To = open ? ArrowOpenAngle : ArrowClosedAngle,
                Duration = TimeSpan.FromMilliseconds(220),
                EasingFunction = new CubicEase
                {
                    EasingMode = EasingMode.EaseOut
                }
            };

            SlideMenuTransform.BeginAnimation(TranslateTransform.XProperty, slideAnimation);
            MenuArrowRotate.BeginAnimation(RotateTransform.AngleProperty, arrowAnimation);
        }

        private void OpenMinecraftFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var minecraftFolder = MinecraftLauncherService.GetMinecraftRootPath();
                Directory.CreateDirectory(minecraftFolder);
                LauncherLogService.Info("Открытие папки Minecraft: " + minecraftFolder);

                Process.Start(new ProcessStartInfo
                {
                    FileName = minecraftFolder,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                LauncherLogService.Exception("Ошибка открытия папки Minecraft", ex);
                MessageBox.Show(
                    "Не удалось открыть папку minecraft:\n\n" + ex.Message,
                    "AsetLauncher",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void LauncherSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                LauncherLogService.Info("Открытие окна настроек.");
                var settingsWindow = new SettingsWindow
                {
                    Owner = this
                };

                settingsWindow.ShowDialog();
            }
            catch (Exception ex)
            {
                LauncherLogService.Exception("Ошибка открытия настроек", ex);
                MessageBox.Show(
                    "Не удалось открыть настройки лаунчера:\n\n" + ex.Message,
                    "AsetLauncher",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void ProfileButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                LauncherLogService.Info("Открытие окна профиля пользователя.");
                var profileWindow = new ProfileWindow
                {
                    Owner = this
                };

                profileWindow.ShowDialog();
            }
            catch (Exception ex)
            {
                LauncherLogService.Exception("Ошибка открытия окна профиля", ex);
                MessageBox.Show(
                    "Не удалось открыть окно профиля:\n\n" + ex.Message,
                    "AsetLauncher",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void DiscordButton_Click(object sender, RoutedEventArgs e)
        {
            OpenSocialLink("https://discord.com/invite/ZzbDzq88Hj", "Discord");
        }

        private void TelegramButton_Click(object sender, RoutedEventArgs e)
        {
            OpenSocialLink("https://t.me/asetmc", "Telegram");
        }

        private static void OpenSocialLink(string url, string caption)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
                LauncherLogService.Info("Открыта ссылка " + caption + ": " + url);
            }
            catch (Exception ex)
            {
                LauncherLogService.Exception("Ошибка открытия ссылки " + caption, ex);
                MessageBox.Show(
                    "Не удалось открыть ссылку " + caption + ":\n\n" + ex.Message,
                    "AsetLauncher",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private async void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isLaunchInProgress)
            {
                MessageBox.Show(
                    "Подождите, уже идет подготовка или запуск Minecraft.",
                    "AsetLauncher",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            try
            {
                LauncherLogService.Info("Открытие окна выбора версии.");
                var versionSelectWindow = new VersionSelectWindow
                {
                    Owner = this
                };

                var dialogResult = versionSelectWindow.ShowDialog();
                if (dialogResult != true || versionSelectWindow.SelectedVersion == null)
                {
                    return;
                }

                await LaunchSelectedVersionAsync(versionSelectWindow.SelectedVersion);
            }
            catch (Exception ex)
            {
                LauncherLogService.Exception("Ошибка открытия окна выбора версии", ex);
                MessageBox.Show(
                    "Не удалось открыть окно выбора версии:\n\n" + ex.Message,
                    "AsetLauncher",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private async Task LaunchSelectedVersionAsync(MinecraftVersionEntry selectedVersion)
        {
            if (selectedVersion == null)
            {
                return;
            }

            try
            {
                _launchCts?.Cancel();
                _launchCts?.Dispose();
                _launchCts = new CancellationTokenSource();
                _isLaunchInProgress = true;

                SetLaunchProgressVisible(true);
                UpdateLaunchProgress("Подготовка к запуску...");

                var settings = _settingsService.Load();
                var selectedAccount = LauncherSettingsService.GetSelectedAccount(settings);
                if (selectedAccount == null)
                {
                    throw new InvalidOperationException("Не найден выбранный аккаунт. Откройте профиль и выберите аккаунт.");
                }

                LauncherLogService.Info("Запуск версии: " + selectedVersion.Id);
                LauncherLogService.Info("Используется аккаунт: " + selectedAccount.Nickname + " (" + selectedAccount.Type + ")");

                var statusProgress = new Progress<string>(UpdateLaunchProgress);

                // Run install/launch pipeline on a worker thread to keep launcher UI responsive.
                await Task.Run(
                    async () => await _launcherService.InstallAndLaunchAsync(
                        selectedVersion,
                        selectedAccount,
                        settings,
                        statusProgress,
                        _launchCts.Token).ConfigureAwait(false),
                    _launchCts.Token).ConfigureAwait(true);

                UpdateLaunchProgress("Minecraft запущен.");
            }
            catch (OperationCanceledException)
            {
                LauncherLogService.Warn("Операция установки/запуска была отменена.");
            }
            catch (Exception ex)
            {
                LauncherLogService.Exception("Ошибка установки или запуска версии", ex);
                MessageBox.Show(
                    "Не удалось установить или запустить выбранную версию:\n\n" + ex.Message,
                    "AsetLauncher",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                _isLaunchInProgress = false;
                SetLaunchProgressVisible(false);
            }
        }

        private void SetLaunchProgressVisible(bool visible)
        {
            LaunchProgressContainer.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            PlayButton.IsEnabled = !visible;

            if (visible)
            {
                LaunchProgressBar.IsIndeterminate = true;
                LaunchProgressBar.Value = 0;
                _lastLaunchStatusText = string.Empty;
                _lastLaunchStatusUiUpdateUtc = DateTime.MinValue;
            }
        }

        private void UpdateLaunchProgress(string statusText)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => UpdateLaunchProgress(statusText)));
                return;
            }

            if (string.IsNullOrWhiteSpace(statusText))
            {
                return;
            }

            var now = DateTime.UtcNow;
            if (string.Equals(_lastLaunchStatusText, statusText, StringComparison.Ordinal)
                && (now - _lastLaunchStatusUiUpdateUtc).TotalMilliseconds < 120)
            {
                return;
            }

            _lastLaunchStatusText = statusText;
            _lastLaunchStatusUiUpdateUtc = now;

            LaunchProgressStatusText.Text = statusText;

            int current;
            int total;
            if (TryExtractProgress(statusText, out current, out total))
            {
                LaunchProgressBar.IsIndeterminate = false;
                LaunchProgressBar.Minimum = 0;
                LaunchProgressBar.Maximum = total;
                LaunchProgressBar.Value = Math.Min(current, total);
                return;
            }

            LaunchProgressBar.IsIndeterminate = true;
            LaunchProgressBar.Value = 0;
        }

        private static bool TryExtractProgress(string text, out int current, out int total)
        {
            current = 0;
            total = 0;

            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var match = ProgressRegex.Match(text);
            if (!match.Success || match.Groups.Count < 3)
            {
                return false;
            }

            if (!int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out current))
            {
                return false;
            }

            if (!int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out total))
            {
                return false;
            }

            return total > 0 && current >= 0;
        }
    }
}

using AsetLauncher.Models;
using AsetLauncher.Services;
using System;
using System.Windows;

namespace AsetLauncher
{
    public partial class ProfileWindow : Window
    {
        private readonly LauncherSettingsService _settingsService = new LauncherSettingsService();
        private LauncherSettings _settings;

        public ProfileWindow()
        {
            InitializeComponent();
            Loaded += ProfileWindow_Loaded;
            Closed += ProfileWindow_Closed;
        }

        private void ProfileWindow_Loaded(object sender, RoutedEventArgs e)
        {
            ThemeService.ThemeChanged += ThemeService_ThemeChanged;
            LoadProfile();
            ApplyTheme();
            LauncherLogService.Info("Открыто окно профиля.");
        }

        private void ProfileWindow_Closed(object sender, EventArgs e)
        {
            ThemeService.ThemeChanged -= ThemeService_ThemeChanged;
        }

        private void ThemeService_ThemeChanged(LauncherTheme theme)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(ApplyTheme));
                return;
            }

            ApplyTheme();
            LoadProfile();
        }

        private void LoadProfile()
        {
            _settings = _settingsService.Load();
            var account = LauncherSettingsService.GetSelectedAccount(_settings);

            if (account == null)
            {
                NicknameTextBlock.Text = Environment.UserName;
                AccountTypeTextBlock.Text = "Оффлайн";
                AvatarImage.Source = ThemeService.CreateImageSourceOrFallback(null, "Assets/logo.png");
                return;
            }

            NicknameTextBlock.Text = string.IsNullOrWhiteSpace(account.Nickname)
                ? Environment.UserName
                : account.Nickname;

            AccountTypeTextBlock.Text = string.Equals(account.Type, "authorized", StringComparison.OrdinalIgnoreCase)
                ? "Авторизованный аккаунт"
                : "Оффлайн аккаунт";

            ApplyAvatar(account);
        }

        private void ApplyAvatar(LauncherAccount account)
        {
            var themeAvatar = ThemeService.CurrentTheme != null
                ? ThemeService.CurrentTheme.LogoImagePath
                : null;

            var selectedAvatar = !string.IsNullOrWhiteSpace(account != null ? account.AvatarPath : null)
                ? account.AvatarPath
                : themeAvatar;

            AvatarImage.Source = ThemeService.CreateImageSourceOrFallback(
                selectedAvatar,
                "Assets/logo.png");
        }

        private void ApplyTheme()
        {
            var activeTheme = ThemeService.CurrentTheme;
            ProfileBackgroundBrush.ImageSource = ThemeService.CreateImageSourceOrFallback(
                activeTheme != null ? activeTheme.MainImagePath : null,
                "Assets/main-menu-bg-mc.png");
        }

        private void ChooseProfileButton_Click(object sender, RoutedEventArgs e)
        {
            var selector = new AccountSelectWindow
            {
                Owner = this
            };

            var result = selector.ShowDialog();
            if (result == true)
            {
                LauncherLogService.Info("Профиль выбран: " + (selector.SelectedAccount != null ? selector.SelectedAccount.Nickname : "<none>"));
            }

            LoadProfile();
        }
    }
}

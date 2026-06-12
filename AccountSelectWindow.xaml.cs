using AsetLauncher.Models;
using AsetLauncher.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AsetLauncher
{
    public partial class AccountSelectWindow : Window
    {
        private enum AddAccountType
        {
            None,
            Offline,
            Authorized
        }

        private sealed class AccountListItem
        {
            public LauncherAccount Account { get; set; }
            public string Nickname { get; set; }
            public string TypeLabel { get; set; }
            public string Uuid { get; set; }
        }

        private static readonly Random Random = new Random();

        private readonly LauncherSettingsService _settingsService = new LauncherSettingsService();
        private readonly LauncherDeviceAuthService _deviceAuthService = new LauncherDeviceAuthService();

        private LauncherSettings _settings;
        private CancellationTokenSource _authCts;

        public LauncherAccount SelectedAccount { get; private set; }

        public AccountSelectWindow()
        {
            InitializeComponent();
            Loaded += AccountSelectWindow_Loaded;
            Closed += AccountSelectWindow_Closed;
        }

        private void AccountSelectWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _settings = _settingsService.Load();
            RefreshAccountsList();
            LauncherLogService.Info("Открыто окно выбора аккаунта.");
        }

        private void AccountSelectWindow_Closed(object sender, EventArgs e)
        {
            CancelAuthorization();
        }

        private void RefreshAccountsList()
        {
            _settings = _settingsService.Load();
            var selectedId = _settings.SelectedAccountId;

            var items = new List<AccountListItem>();
            foreach (var account in _settings.Accounts)
            {
                items.Add(new AccountListItem
                {
                    Account = account,
                    Nickname = account.Nickname,
                    TypeLabel = string.Equals(account.Type, "authorized", StringComparison.OrdinalIgnoreCase)
                        ? "Авторизованный"
                        : "Оффлайн",
                    Uuid = account.Uuid
                });
            }

            AccountsListView.ItemsSource = items;
            var selected = items.FirstOrDefault(i =>
                string.Equals(i.Account.Id, selectedId, StringComparison.OrdinalIgnoreCase));
            if (selected != null)
            {
                AccountsListView.SelectedItem = selected;
            }
            else if (items.Count > 0)
            {
                AccountsListView.SelectedIndex = 0;
            }
        }

        private async void AddAccountButton_Click(object sender, RoutedEventArgs e)
        {
            var addType = PromptAddAccountType(this);
            if (addType == AddAccountType.None)
            {
                return;
            }

            if (addType == AddAccountType.Offline)
            {
                AddOfflineAccount();
                return;
            }

            await AddAuthorizedAccountAsync();
        }

        private void AddOfflineAccount()
        {
            _settings = _settingsService.Load();

            var nickname = GenerateOfflineNickname();
            var account = new LauncherAccount
            {
                Id = Guid.NewGuid().ToString("N"),
                Type = "offline",
                Nickname = nickname,
                Uuid = CreateOfflineUuid(nickname),
                AccessToken = "0",
                ClientToken = string.Empty,
                UserType = "legacy",
                AvatarPath = string.Empty,
                CreatedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            };

            _settings.Accounts.Add(account);
            _settings.SelectedAccountId = account.Id;
            _settingsService.Save(_settings);

            LauncherLogService.Info("Добавлен оффлайн аккаунт: " + account.Nickname);
            StatusTextBlock.Text = "Добавлен оффлайн аккаунт: " + account.Nickname;
            RefreshAccountsList();
        }

        private async Task AddAuthorizedAccountAsync()
        {
            if (_authCts != null)
            {
                return;
            }

            try
            {
                _settings = _settingsService.Load();
                _authCts = new CancellationTokenSource();
                SetBusy(true);
                StatusTextBlock.Text = "Запрос device-кода...";

                var start = await _deviceAuthService.StartDeviceAsync(null, _authCts.Token);
                LauncherLogService.Info("Создан device-code для авторизации.");

                Process.Start(new ProcessStartInfo
                {
                    FileName = start.VerificationUri,
                    UseShellExecute = true
                });

                var intervalSeconds = Math.Max(1, start.IntervalSeconds);
                var expiresAtUtc = DateTime.UtcNow.AddSeconds(Math.Max(15, start.ExpiresInSeconds));
                StatusTextBlock.Text = "Подтвердите вход в браузере, затем дождитесь завершения.";

                while (DateTime.UtcNow < expiresAtUtc)
                {
                    _authCts.Token.ThrowIfCancellationRequested();

                    var poll = await _deviceAuthService.PollDeviceAsync(
                        start.BackendBaseUrl,
                        start.DeviceCode,
                        _authCts.Token);

                    if (!poll.IsPending)
                    {
                        SaveAuthorizedAccount(poll);
                        return;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), _authCts.Token);
                }

                StatusTextBlock.Text = "Время подтверждения истекло.";
                LauncherLogService.Warn("Авторизация через device flow не подтверждена вовремя.");
            }
            catch (OperationCanceledException)
            {
                StatusTextBlock.Text = "Авторизация отменена.";
                LauncherLogService.Warn("Авторизация отменена пользователем.");
            }
            catch (Exception ex)
            {
                LauncherLogService.Exception("Ошибка device-авторизации", ex);
                StatusTextBlock.Text = "Ошибка авторизации.";
                var details = (ex.Message ?? string.Empty).Replace(" | ", Environment.NewLine);
                if (string.IsNullOrWhiteSpace(details))
                {
                    details = ex.ToString();
                }
                MessageBox.Show(
                    this,
                    "Не удалось добавить авторизованный аккаунт:\n\n" + details,
                    "AsetLauncher",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
                CancelAuthorization();
            }
        }

        private static AddAccountType PromptAddAccountType(Window owner)
        {
            var result = AddAccountType.None;

            var titleText = new TextBlock
            {
                Text = "Какой аккаунт добавить?",
                Margin = new Thickness(0, 0, 0, 14),
                FontSize = 14
            };

            var offlineButton = new Button
            {
                Content = "Оффлайн",
                Width = 120,
                Height = 34,
                Margin = new Thickness(0, 0, 8, 0)
            };

            var authorizedButton = new Button
            {
                Content = "Авторизованный",
                Width = 140,
                Height = 34,
                Margin = new Thickness(0, 0, 8, 0)
            };

            var cancelButton = new Button
            {
                Content = "Отмена",
                Width = 96,
                Height = 34
            };

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            buttons.Children.Add(offlineButton);
            buttons.Children.Add(authorizedButton);
            buttons.Children.Add(cancelButton);

            var panel = new StackPanel
            {
                Margin = new Thickness(14)
            };

            panel.Children.Add(titleText);
            panel.Children.Add(buttons);

            var dialog = new Window
            {
                Title = "Добавить аккаунт",
                Width = 430,
                Height = 155,
                MinWidth = 410,
                MinHeight = 150,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = owner,
                Content = panel
            };

            offlineButton.Click += (s, e) =>
            {
                result = AddAccountType.Offline;
                dialog.DialogResult = true;
                dialog.Close();
            };

            authorizedButton.Click += (s, e) =>
            {
                result = AddAccountType.Authorized;
                dialog.DialogResult = true;
                dialog.Close();
            };

            cancelButton.Click += (s, e) =>
            {
                dialog.DialogResult = false;
                dialog.Close();
            };

            dialog.ShowDialog();
            return result;
        }

        private void SaveAuthorizedAccount(DevicePollResult poll)
        {
            _settings = _settingsService.Load();
            var normalizedUuid = NormalizeUuid(poll.Uuid);

            var existing = _settings.Accounts.FirstOrDefault(a =>
                string.Equals(a.Type, "authorized", StringComparison.OrdinalIgnoreCase)
                && string.Equals(NormalizeUuid(a.Uuid), normalizedUuid, StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                existing = new LauncherAccount
                {
                    Id = Guid.NewGuid().ToString("N"),
                    CreatedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
                };
                _settings.Accounts.Add(existing);
            }

            existing.Type = "authorized";
            existing.Nickname = string.IsNullOrWhiteSpace(poll.Username) ? "Authorized" : poll.Username.Trim();
            existing.Uuid = normalizedUuid;
            existing.AccessToken = poll.AccessToken ?? string.Empty;
            existing.ClientToken = poll.ClientToken ?? string.Empty;
            existing.UserType = "mojang";

            _settings.SelectedAccountId = existing.Id;
            _settingsService.Save(_settings);

            LauncherLogService.Info("Авторизованный аккаунт добавлен: " + existing.Nickname);
            StatusTextBlock.Text = "Добавлен аккаунт: " + existing.Nickname;
            RefreshAccountsList();
        }

        private void RemoveAccountButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = AccountsListView.SelectedItem as AccountListItem;
            if (selected == null || selected.Account == null)
            {
                return;
            }

            _settings = _settingsService.Load();
            if (_settings.Accounts.Count <= 1)
            {
                MessageBox.Show(
                    this,
                    "Нельзя удалить единственный аккаунт.",
                    "AsetLauncher",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            _settings.Accounts.RemoveAll(a => string.Equals(a.Id, selected.Account.Id, StringComparison.OrdinalIgnoreCase));
            if (string.Equals(_settings.SelectedAccountId, selected.Account.Id, StringComparison.OrdinalIgnoreCase))
            {
                _settings.SelectedAccountId = _settings.Accounts[0].Id;
            }

            _settingsService.Save(_settings);
            LauncherLogService.Info("Аккаунт удален: " + selected.Nickname);
            StatusTextBlock.Text = "Аккаунт удален: " + selected.Nickname;
            RefreshAccountsList();
        }

        private void SelectButton_Click(object sender, RoutedEventArgs e)
        {
            SelectCurrentAccount();
        }

        private void AccountsListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (AccountsListView.SelectedItem != null)
            {
                SelectCurrentAccount();
            }
        }

        private void SelectCurrentAccount()
        {
            var selected = AccountsListView.SelectedItem as AccountListItem;
            if (selected == null || selected.Account == null)
            {
                MessageBox.Show(
                    this,
                    "Выберите аккаунт из списка.",
                    "AsetLauncher",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            _settings = _settingsService.Load();
            _settings.SelectedAccountId = selected.Account.Id;
            _settingsService.Save(_settings);

            SelectedAccount = selected.Account;
            LauncherLogService.Info("Выбран аккаунт: " + selected.Account.Nickname);
            DialogResult = true;
            Close();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void SetBusy(bool isBusy)
        {
            AddAccountButton.IsEnabled = !isBusy;
            RemoveAccountButton.IsEnabled = !isBusy;
            SelectButton.IsEnabled = !isBusy;
            AccountsListView.IsEnabled = !isBusy;
        }

        private void CancelAuthorization()
        {
            if (_authCts == null)
            {
                return;
            }

            try
            {
                _authCts.Cancel();
                _authCts.Dispose();
            }
            catch
            {
            }
            finally
            {
                _authCts = null;
            }
        }

        private static string GenerateOfflineNickname()
        {
            lock (Random)
            {
                return "Player" + Random.Next(1000, 9999).ToString(CultureInfo.InvariantCulture);
            }
        }

        private static string CreateOfflineUuid(string nickname)
        {
            using (var md5 = MD5.Create())
            {
                return new Guid(md5.ComputeHash(Encoding.UTF8.GetBytes("OfflinePlayer:" + (nickname ?? string.Empty))))
                    .ToString("N");
            }
        }

        private static string NormalizeUuid(string uuid)
        {
            return string.IsNullOrWhiteSpace(uuid)
                ? string.Empty
                : uuid.Replace("-", string.Empty).Trim().ToLowerInvariant();
        }
    }
}


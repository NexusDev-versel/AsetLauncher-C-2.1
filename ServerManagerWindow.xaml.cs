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
using System.Windows.Input;
using System.Windows.Media;

namespace AsetLauncher
{
    public partial class ServerManagerWindow : Window
    {
        private static readonly Dictionary<string, ServerConsoleWindow> OpenedServerWindows =
            new Dictionary<string, ServerConsoleWindow>(StringComparer.OrdinalIgnoreCase);

        private readonly LauncherLocalServerService _localServerService = new LauncherLocalServerService();
        private LocalServerProfile[] _servers = Array.Empty<LocalServerProfile>();
        private CancellationTokenSource _versionsCts;

        public ServerManagerWindow()
        {
            InitializeComponent();
            InitializeCreateForm();
            RefreshServersList(string.Empty);
            _ = ReloadVersionsForSelectedCoreAsync();
            UpdateButtons();
        }

        private void InitializeCreateForm()
        {
            ServerCreateCoreComboBox.ItemsSource = new[] { "Forge", "Purpur", "Bungee_Cord", "Velocity" };
            ServerCreateCoreComboBox.SelectedItem = "Purpur";
            ServerCreateNameTextBox.Text = "MyServer";
            ServerCreateStatusTextBlock.Text = string.Empty;
        }

        private async Task ReloadVersionsForSelectedCoreAsync()
        {
            var selectedCore = Convert.ToString(ServerCreateCoreComboBox.SelectedItem ?? "Purpur");
            if (string.IsNullOrWhiteSpace(selectedCore))
            {
                selectedCore = "Purpur";
            }

            if (_versionsCts != null)
            {
                _versionsCts.Cancel();
                _versionsCts.Dispose();
            }

            _versionsCts = new CancellationTokenSource();
            var token = _versionsCts.Token;

            ServerCreateVersionComboBox.ItemsSource = null;
            ServerCreateVersionComboBox.IsEnabled = false;
            CreateServerButton.IsEnabled = false;
            VersionLoadingTextBlock.Text = "Загрузка...";

            try
            {
                var versions = await _localServerService.GetAvailableVersionsAsync(selectedCore, token).ConfigureAwait(true);
                var versionList = versions == null ? new List<string>() : versions.ToList();

                if (token.IsCancellationRequested)
                {
                    return;
                }

                ServerCreateVersionComboBox.ItemsSource = versionList;
                ServerCreateVersionComboBox.SelectedItem = versionList.FirstOrDefault();
                ServerCreateVersionComboBox.IsEnabled = versionList.Count > 0;
                CreateServerButton.IsEnabled = versionList.Count > 0;
                VersionLoadingTextBlock.Text = versionList.Count > 0
                    ? "Версий: " + versionList.Count
                    : "Нет доступных версий";
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                ServerCreateVersionComboBox.ItemsSource = new[] { LauncherLocalServerService.GetDefaultVersionForCore(selectedCore) };
                ServerCreateVersionComboBox.SelectedIndex = 0;
                ServerCreateVersionComboBox.IsEnabled = true;
                CreateServerButton.IsEnabled = true;
                VersionLoadingTextBlock.Text = "Ошибка загрузки версий";
                ServerCreateStatusTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xA3, 0xA3));
                ServerCreateStatusTextBlock.Text = "Использован резервный список: " + ex.Message;
            }
        }

        private async void ServerCreateCoreComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            await ReloadVersionsForSelectedCoreAsync().ConfigureAwait(true);
        }

        private async void CreateServerButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var name = (ServerCreateNameTextBox.Text ?? string.Empty).Trim();
                var core = Convert.ToString(ServerCreateCoreComboBox.SelectedItem ?? "Purpur");
                var version = Convert.ToString(ServerCreateVersionComboBox.SelectedItem ?? string.Empty);

                if (string.IsNullOrWhiteSpace(name))
                {
                    throw new InvalidOperationException("Укажите название сервера.");
                }

                if (string.IsNullOrWhiteSpace(version))
                {
                    throw new InvalidOperationException("Выберите доступную версию сервера.");
                }

                SetCreateControlsEnabled(false);
                ServerCreateStatusTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(0xB7, 0xD9, 0xFF));
                ServerCreateStatusTextBlock.Text = "Создание сервера...";
                var created = _localServerService.CreateServer(name, core, version);

                var progress = new Progress<string>(msg =>
                {
                    if (!string.IsNullOrWhiteSpace(msg))
                    {
                        ServerCreateStatusTextBlock.Text = msg;
                    }
                });

                await _localServerService.InstallServerCoreAsync(created, progress, CancellationToken.None).ConfigureAwait(true);
                ServerCreateStatusTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(0x7F, 0xE3, 0xA2));
                ServerCreateStatusTextBlock.Text = "Сервер создан и полностью установлен!";
                RefreshServersList(created.Id);
            }
            catch (Exception ex)
            {
                ServerCreateStatusTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xA3, 0xA3));
                ServerCreateStatusTextBlock.Text = "Ошибка: " + ex.Message;
            }
            finally
            {
                SetCreateControlsEnabled(true);
                CreateServerButton.IsEnabled = ServerCreateVersionComboBox.Items.Count > 0;
            }
        }

        private void RefreshServersList(string selectedServerId)
        {
            _servers = _localServerService.Load().ToArray();
            ServersListView.ItemsSource = _servers;

            var selected = _servers.FirstOrDefault(s => string.Equals(s.Id, selectedServerId, StringComparison.OrdinalIgnoreCase));
            if (selected == null)
            {
                selected = _servers.FirstOrDefault();
            }

            ServersListView.SelectedItem = selected;
            UpdateButtons();
        }

        private void ServersListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateButtons();
        }

        private void ServersListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            OpenSelectedServer();
        }

        private void OpenSelectedServerButton_Click(object sender, RoutedEventArgs e)
        {
            OpenSelectedServer();
        }

        private void RefreshServersButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = ServersListView.SelectedItem as LocalServerProfile;
            RefreshServersList(selected != null ? selected.Id : string.Empty);
        }

        private void DeleteServerButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = ServersListView.SelectedItem as LocalServerProfile;
            if (selected == null)
            {
                return;
            }

            var answer = MessageBox.Show(
                "Удалить выбранный сервер из списка и его папку?\n\n" + (selected.Name ?? ""),
                "Удаление сервера",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (answer != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                ServerConsoleWindow opened;
                if (OpenedServerWindows.TryGetValue(selected.Id ?? string.Empty, out opened) && opened != null)
                {
                    opened.Close();
                }

                var all = _localServerService.Load().ToList();
                all.RemoveAll(s => string.Equals(s.Id, selected.Id, StringComparison.OrdinalIgnoreCase));
                _localServerService.Save(all);

                var root = LauncherLocalServerService.GetServersRootPath();
                var path = selected.FolderPath ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path) && IsPathInsideRoot(path, root))
                {
                    Directory.Delete(path, true);
                }

                ServerCreateStatusTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(0xB7, 0xD9, 0xFF));
                ServerCreateStatusTextBlock.Text = "Сервер удалён.";
                RefreshServersList(string.Empty);
            }
            catch (Exception ex)
            {
                ServerCreateStatusTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xA3, 0xA3));
                ServerCreateStatusTextBlock.Text = "Ошибка удаления: " + ex.Message;
            }
        }

        private void OpenSelectedServer()
        {
            var selected = ServersListView.SelectedItem as LocalServerProfile;
            if (selected == null)
            {
                return;
            }

            ServerConsoleWindow existing;
            var key = selected.Id ?? string.Empty;
            if (OpenedServerWindows.TryGetValue(key, out existing) && existing != null)
            {
                if (existing.WindowState == WindowState.Minimized)
                {
                    existing.WindowState = WindowState.Normal;
                }

                existing.Activate();
                return;
            }

            var fresh = _localServerService.Load().FirstOrDefault(s => string.Equals(s.Id, key, StringComparison.OrdinalIgnoreCase)) ?? selected;
            var window = new ServerConsoleWindow(fresh);

            OpenedServerWindows[key] = window;
            window.Closed += (s, e) =>
            {
                OpenedServerWindows.Remove(key);
                if (!IsLoaded)
                {
                    return;
                }

                RefreshServersList(key);
            };

            window.Show();
            window.Activate();
        }

        private static bool IsPathInsideRoot(string path, string root)
        {
            try
            {
                var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private void UpdateButtons()
        {
            var hasSelection = ServersListView.SelectedItem as LocalServerProfile != null;
            OpenSelectedServerButton.IsEnabled = hasSelection;
            DeleteServerButton.IsEnabled = hasSelection;
        }

        private void SetCreateControlsEnabled(bool enabled)
        {
            ServerCreateNameTextBox.IsEnabled = enabled;
            ServerCreateCoreComboBox.IsEnabled = enabled;
            ServerCreateVersionComboBox.IsEnabled = enabled;
            CreateServerButton.IsEnabled = enabled;
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            if (_versionsCts != null)
            {
                _versionsCts.Cancel();
                _versionsCts.Dispose();
                _versionsCts = null;
            }
        }
    }
}

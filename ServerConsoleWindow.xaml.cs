using AsetLauncher.Models;
using AsetLauncher.Services;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AsetLauncher
{
    public partial class ServerConsoleWindow : Window
    {
        private sealed class ServerFileEntry
        {
            public string Name { get; set; }
            public string Type { get; set; }
            public string SizeText { get; set; }
            public string FullPath { get; set; }
            public bool IsDirectory { get; set; }
        }

        private readonly LauncherLocalServerService _localServerService = new LauncherLocalServerService();
        private const string InternalFileEntryDropFormat = "AsetLauncher.InternalFileEntryPath";
        private LocalServerProfile _server;
        private Process _serverProcess;
        private string _currentDirectory = string.Empty;
        private readonly List<string> _commandHistory = new List<string>();
        private int _commandHistoryIndex = -1;
        private Point _fileDragStartPoint;
        private ServerFileEntry _fileDragEntry;
        private string _openedEditorFilePath = string.Empty;
        private Encoding _openedEditorEncoding = new UTF8Encoding(false);
        private bool _isEditorTextDirty;
        private bool _isEditorUpdating;
        private bool _isStarting;
        private bool _isStopping;
        private bool _isWindowClosing;

        public string ServerId
        {
            get { return _server != null ? (_server.Id ?? string.Empty) : string.Empty; }
        }

        public ServerConsoleWindow(LocalServerProfile server)
        {
            if (server == null)
            {
                throw new ArgumentNullException(nameof(server));
            }

            _server = server;
            InitializeComponent();
            LoadServerData();
            AppendConsoleLine("Окно управления сервером открыто.");
        }

        private void LoadServerData()
        {
            Title = "Сервер: " + (_server.Name ?? "Unknown");
            ServerTitleTextBlock.Text = (_server.Name ?? "Server") + " [" + (_server.Core ?? "-") + " " + (_server.Version ?? "-") + "]";

            ServerSettingsNameTextBox.Text = _server.Name ?? string.Empty;
            ServerSettingsCoreTextBlock.Text = _server.Core ?? "-";
            ServerSettingsVersionTextBox.Text = _server.Version ?? string.Empty;
            ServerSettingsRamGbTextBox.Text = FormatRamGb(_server.RamMb);
            ServerSettingsArgsTextBox.Text = _server.ExtraJavaArgs ?? string.Empty;

            _currentDirectory = _server.FolderPath ?? string.Empty;
            RefreshFileList();
            CloseFileEditor();
            UpdateServerStateText("Сервер остановлен.");
            UpdateButtons();
        }

        private async void ServerStartButton_Click(object sender, RoutedEventArgs e)
        {
            await StartServerAsync().ConfigureAwait(true);
        }

        private async void ServerRestartButton_Click(object sender, RoutedEventArgs e)
        {
            await StopServerAsync().ConfigureAwait(true);
            await StartServerAsync().ConfigureAwait(true);
        }

        private async void ServerStopButton_Click(object sender, RoutedEventArgs e)
        {
            await StopServerAsync().ConfigureAwait(true);
        }

        private void ServerSendCommandButton_Click(object sender, RoutedEventArgs e)
        {
            SendCurrentServerCommand();
        }

        private void ServerCommandTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                SendCurrentServerCommand();
                return;
            }

            if (e.Key == Key.Up)
            {
                e.Handled = true;
                NavigateCommandHistory(-1);
                return;
            }

            if (e.Key == Key.Down)
            {
                e.Handled = true;
                NavigateCommandHistory(1);
            }
        }

        private async Task StartServerAsync()
        {
            if (_server == null)
            {
                return;
            }

            if (_isStarting || _isStopping || _isWindowClosing)
            {
                return;
            }

            if (IsServerRunning())
            {
                MessageBox.Show("Сервер уже запущен.", "Сервер", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _isStarting = true;
            UpdateButtons();
            try
            {
                if (!LauncherLocalServerService.IsServerCoreInstalled(_server))
                {
                    AppendConsoleLine("Ядро не установлено или изменена версия. Запускаю автоматическую установку...");
                    var progress = new Progress<string>(msg =>
                    {
                        if (!string.IsNullOrWhiteSpace(msg))
                        {
                            AppendConsoleLine(msg);
                        }
                    });

                    try
                    {
                        await _localServerService.InstallServerCoreAsync(_server, progress, CancellationToken.None).ConfigureAwait(true);
                        AppendConsoleLine("Установка ядра завершена.");
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(
                            "Не удалось автоматически установить ядро сервера:\n\n" + ex.Message,
                            "Сервер",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                        return;
                    }
                }

                var normalizedCore = LauncherLocalServerService.NormalizeCore(_server.Core);
                var runBatPath = Path.Combine(_server.FolderPath ?? string.Empty, "run.bat");
                var useForgeRunBat = string.Equals(normalizedCore, "Forge", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(runBatPath);

                var startInfo = new ProcessStartInfo();
                if (useForgeRunBat)
                {
                    startInfo.FileName = "cmd.exe";
                    startInfo.Arguments = "/c \"run.bat\"";
                }
                else
                {
                    var jarPath = LauncherLocalServerService.ResolveJarPath(_server);
                    if (string.IsNullOrWhiteSpace(jarPath) || !File.Exists(jarPath))
                    {
                        MessageBox.Show(
                            "После установки не найден jar-файл ядра сервера.",
                            "Сервер",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                        return;
                    }

                    startInfo.FileName = LauncherLocalServerService.ResolveJavaPathForServer(_server);
                    startInfo.Arguments = LauncherLocalServerService.BuildServerLaunchArguments(_server, jarPath);
                }

                startInfo.WorkingDirectory = _server.FolderPath;
                startInfo.UseShellExecute = false;
                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;
                startInfo.RedirectStandardInput = true;
                startInfo.CreateNoWindow = true;
                startInfo.StandardOutputEncoding = Encoding.UTF8;
                startInfo.StandardErrorEncoding = Encoding.UTF8;

                var process = new Process
                {
                    StartInfo = startInfo,
                    EnableRaisingEvents = true
                };

                process.OutputDataReceived += (s, e) =>
                {
                    SafeAppendFromWorker("[OUT] ", e.Data);
                };

                process.ErrorDataReceived += (s, e) =>
                {
                    SafeAppendFromWorker("[ERR] ", e.Data);
                };

                process.Exited += (s, e) =>
                {
                    SafeUi(() =>
                    {
                        var exitCodeText = "unknown";
                        try
                        {
                            exitCodeText = process.ExitCode.ToString(CultureInfo.InvariantCulture);
                        }
                        catch
                        {
                        }

                        AppendConsoleLine("Процесс сервера завершился. Код: " + exitCodeText);
                        _serverProcess = null;
                        UpdateServerStateText("Сервер остановлен.");
                        UpdateButtons();
                    });

                    try
                    {
                        process.Dispose();
                    }
                    catch
                    {
                    }
                };

                process.Start();
                _serverProcess = process;
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                AppendConsoleLine("Запуск сервера: " + (_server.Name ?? "-"));
                UpdateServerStateText("Сервер запущен. PID=" + process.Id);
                UpdateButtons();
                await Task.CompletedTask;
            }
            finally
            {
                _isStarting = false;
                UpdateButtons();
            }
        }

        private async Task StopServerAsync()
        {
            if (_isStopping || _isWindowClosing)
            {
                return;
            }

            if (!IsServerRunning())
            {
                return;
            }

            _isStopping = true;
            UpdateButtons();

            var process = _serverProcess;
            try
            {
                try
                {
                    process.StandardInput.WriteLine("stop");
                    process.StandardInput.Flush();
                }
                catch
                {
                }

                var exited = await Task.Run(() =>
                {
                    try
                    {
                        return process.WaitForExit(10000);
                    }
                    catch
                    {
                        return true;
                    }
                }).ConfigureAwait(true);

                if (!exited)
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

                    await Task.Run(() =>
                    {
                        try
                        {
                            process.WaitForExit(5000);
                        }
                        catch
                        {
                        }
                    }).ConfigureAwait(true);
                }
            }
            catch (Exception ex)
            {
                AppendConsoleLine("Ошибка остановки: " + ex.Message);
            }
            finally
            {
                _isStopping = false;
                _serverProcess = null;
                UpdateServerStateText("Сервер остановлен.");
                UpdateButtons();
            }
        }

        private void SafeAppendFromWorker(string prefix, string data)
        {
            if (string.IsNullOrWhiteSpace(data))
            {
                return;
            }

            SafeUi(() => AppendConsoleLine((prefix ?? string.Empty) + data));
        }

        private void SafeUi(Action action)
        {
            if (action == null)
            {
                return;
            }

            try
            {
                if (Dispatcher == null || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                {
                    return;
                }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_isWindowClosing)
                    {
                        return;
                    }

                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        LauncherLogService.Warn("Ошибка обновления UI окна сервера: " + ex.Message);
                    }
                }));
            }
            catch (Exception ex)
            {
                LauncherLogService.Warn("Ошибка вызова UI окна сервера: " + ex.Message);
            }
        }

        private void ServerUpDirectoryButton_Click(object sender, RoutedEventArgs e)
        {
            if (_server == null)
            {
                return;
            }

            var root = Path.GetFullPath(_server.FolderPath ?? string.Empty);
            var current = Path.GetFullPath(_currentDirectory ?? root);
            var parent = Directory.GetParent(current);
            if (parent == null)
            {
                return;
            }

            var parentPath = parent.FullName;
            if (!IsPathInsideRoot(parentPath, root))
            {
                parentPath = root;
            }

            _currentDirectory = parentPath;
            RefreshFileList();
        }

        private void ServerFilesListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            OpenSelectedEntry();
        }

        private void ServerFilesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var entry = ServerFilesListView.SelectedItem as ServerFileEntry;
            ServerDeleteEntryButton.IsEnabled = entry != null;
            ServerOpenEntryButton.IsEnabled = CanOpenEntry(entry);
        }

        private void ServerFilesListView_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
            {
                return;
            }

            OpenSelectedEntry();
            e.Handled = true;
        }

        private void ServerFilesListView_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _fileDragStartPoint = e.GetPosition(ServerFilesListView);
            _fileDragEntry = GetEntryFromEventSource(e.OriginalSource as DependencyObject);
        }

        private void ServerFilesListView_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _fileDragEntry == null)
            {
                return;
            }

            var currentPoint = e.GetPosition(ServerFilesListView);
            var deltaX = Math.Abs(currentPoint.X - _fileDragStartPoint.X);
            var deltaY = Math.Abs(currentPoint.Y - _fileDragStartPoint.Y);
            if (deltaX < SystemParameters.MinimumHorizontalDragDistance &&
                deltaY < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            var data = new DataObject();
            data.SetData(InternalFileEntryDropFormat, _fileDragEntry.FullPath);
            DragDrop.DoDragDrop(ServerFilesListView, data, DragDropEffects.Move);
            _fileDragEntry = null;
        }

        private void ServerFilesListView_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data == null)
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else if (e.Data.GetDataPresent(InternalFileEntryDropFormat))
            {
                e.Effects = DragDropEffects.Move;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }

            e.Handled = true;
        }

        private void ServerFilesListView_Drop(object sender, DragEventArgs e)
        {
            if (_server == null || e.Data == null)
            {
                return;
            }

            var destinationFolder = ResolveDropDestinationFolder(e);
            if (string.IsNullOrWhiteSpace(destinationFolder))
            {
                return;
            }

            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var dropped = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (dropped != null && dropped.Length > 0)
                {
                    CopyExternalEntriesIntoFolder(dropped, destinationFolder);
                }
                return;
            }

            if (e.Data.GetDataPresent(InternalFileEntryDropFormat))
            {
                var sourcePath = e.Data.GetData(InternalFileEntryDropFormat) as string;
                if (!string.IsNullOrWhiteSpace(sourcePath))
                {
                    MoveInternalEntryIntoFolder(sourcePath, destinationFolder);
                }
            }
        }

        private void ServerMoveFilesButton_Click(object sender, RoutedEventArgs e)
        {
            if (_server == null)
            {
                return;
            }

            var dlg = new OpenFileDialog
            {
                Title = "Выберите файлы для добавления",
                Multiselect = true,
                CheckFileExists = true
            };

            if (dlg.ShowDialog(this) != true)
            {
                return;
            }

            CopyExternalEntriesIntoFolder(dlg.FileNames, _currentDirectory);
        }

        private void CopyExternalEntriesIntoFolder(IEnumerable<string> paths, string destinationFolder)
        {
            if (_server == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(destinationFolder) || !Directory.Exists(destinationFolder))
            {
                destinationFolder = _currentDirectory;
            }

            if (string.IsNullOrWhiteSpace(destinationFolder))
            {
                return;
            }

            var copiedFiles = 0;
            var copiedFolders = 0;
            foreach (var sourcePath in paths ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(sourcePath))
                {
                    continue;
                }

                try
                {
                    var destinationPath = Path.Combine(destinationFolder, Path.GetFileName(sourcePath));

                    if (File.Exists(sourcePath))
                    {
                        if (File.Exists(destinationPath))
                        {
                            File.Delete(destinationPath);
                        }
                        else if (Directory.Exists(destinationPath))
                        {
                            Directory.Delete(destinationPath, true);
                        }

                        File.Copy(sourcePath, destinationPath, true);
                        copiedFiles++;
                        continue;
                    }

                    if (Directory.Exists(sourcePath))
                    {
                        if (Directory.Exists(destinationPath))
                        {
                            Directory.Delete(destinationPath, true);
                        }
                        else if (File.Exists(destinationPath))
                        {
                            File.Delete(destinationPath);
                        }

                        CopyDirectoryRecursive(sourcePath, destinationPath);
                        copiedFolders++;
                    }
                }
                catch (Exception ex)
                {
                    AppendConsoleLine("Не удалось скопировать элемент: " + ex.Message);
                }
            }

            if (copiedFiles > 0 || copiedFolders > 0)
            {
                AppendConsoleLine("Скопировано: файлов " + copiedFiles + ", папок " + copiedFolders + ".");
            }

            RefreshFileList();
        }

        private void MoveInternalEntryIntoFolder(string sourcePath, string destinationFolder)
        {
            if (_server == null || string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(destinationFolder))
            {
                return;
            }

            var root = Path.GetFullPath(_server.FolderPath ?? string.Empty);
            var fullSourcePath = Path.GetFullPath(sourcePath);
            var fullDestinationFolder = Path.GetFullPath(destinationFolder);
            if (!IsPathInsideRoot(fullSourcePath, root) || !IsPathInsideRoot(fullDestinationFolder, root))
            {
                return;
            }

            var sourceParent = Path.GetDirectoryName(fullSourcePath) ?? string.Empty;
            if (string.Equals(sourceParent, fullDestinationFolder, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (Directory.Exists(fullSourcePath) && IsPathInsideRoot(fullDestinationFolder, fullSourcePath))
            {
                AppendConsoleLine("Нельзя переместить папку внутрь самой себя.");
                return;
            }

            var destinationPath = Path.Combine(fullDestinationFolder, Path.GetFileName(fullSourcePath));
            try
            {
                if (File.Exists(fullSourcePath))
                {
                    if (File.Exists(destinationPath))
                    {
                        File.Delete(destinationPath);
                    }
                    else if (Directory.Exists(destinationPath))
                    {
                        Directory.Delete(destinationPath, true);
                    }

                    File.Move(fullSourcePath, destinationPath);
                    AppendConsoleLine("Файл перемещён: " + Path.GetFileName(fullSourcePath));
                }
                else if (Directory.Exists(fullSourcePath))
                {
                    if (Directory.Exists(destinationPath))
                    {
                        Directory.Delete(destinationPath, true);
                    }
                    else if (File.Exists(destinationPath))
                    {
                        File.Delete(destinationPath);
                    }

                    Directory.Move(fullSourcePath, destinationPath);
                    AppendConsoleLine("Папка перемещена: " + Path.GetFileName(fullSourcePath));
                }
            }
            catch (Exception ex)
            {
                AppendConsoleLine("Не удалось переместить элемент: " + ex.Message);
            }

            RefreshFileList();
        }

        private string ResolveDropDestinationFolder(DragEventArgs e)
        {
            if (_server == null)
            {
                return string.Empty;
            }

            var root = Path.GetFullPath(_server.FolderPath ?? string.Empty);
            var destinationFolder = _currentDirectory;

            var entry = GetEntryFromEventSource(e.OriginalSource as DependencyObject);
            if (entry != null && entry.IsDirectory && Directory.Exists(entry.FullPath))
            {
                destinationFolder = entry.FullPath;
            }

            if (string.IsNullOrWhiteSpace(destinationFolder))
            {
                destinationFolder = root;
            }

            try
            {
                destinationFolder = Path.GetFullPath(destinationFolder);
            }
            catch
            {
                destinationFolder = root;
            }

            if (!IsPathInsideRoot(destinationFolder, root))
            {
                destinationFolder = root;
            }

            return destinationFolder;
        }

        private ServerFileEntry GetEntryFromEventSource(DependencyObject source)
        {
            while (source != null && !(source is ListViewItem))
            {
                source = VisualTreeHelper.GetParent(source);
            }

            var item = source as ListViewItem;
            return item != null ? item.DataContext as ServerFileEntry : null;
        }

        private static void CopyDirectoryRecursive(string sourceDir, string destinationDir)
        {
            if (!Directory.Exists(sourceDir))
            {
                return;
            }

            Directory.CreateDirectory(destinationDir);

            foreach (var file in Directory.GetFiles(sourceDir))
            {
                var targetFile = Path.Combine(destinationDir, Path.GetFileName(file));
                File.Copy(file, targetFile, true);
            }

            foreach (var directory in Directory.GetDirectories(sourceDir))
            {
                var targetDirectory = Path.Combine(destinationDir, Path.GetFileName(directory));
                CopyDirectoryRecursive(directory, targetDirectory);
            }
        }

        private void ServerDeleteEntryButton_Click(object sender, RoutedEventArgs e)
        {
            var entry = ServerFilesListView.SelectedItem as ServerFileEntry;
            if (entry == null)
            {
                return;
            }

            try
            {
                if (entry.IsDirectory)
                {
                    Directory.Delete(entry.FullPath, true);
                }
                else if (File.Exists(entry.FullPath))
                {
                    File.Delete(entry.FullPath);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Не удалось удалить элемент:\n\n" + ex.Message, "Сервер", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            RefreshFileList();
        }

        private void ServerCreateFolderButton_Click(object sender, RoutedEventArgs e)
        {
            if (_server == null)
            {
                return;
            }

            var index = 1;
            string path;
            do
            {
                path = Path.Combine(_currentDirectory, "Новая папка " + index);
                index++;
            } while (Directory.Exists(path));

            Directory.CreateDirectory(path);
            RefreshFileList();
        }

        private void ServerRefreshFilesButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshFileList();
        }

        private void ServerOpenEntryButton_Click(object sender, RoutedEventArgs e)
        {
            OpenSelectedEntry();
        }

        private void ServerFileSaveButton_Click(object sender, RoutedEventArgs e)
        {
            SaveOpenedEditorFile();
        }

        private void ServerFileCloseButton_Click(object sender, RoutedEventArgs e)
        {
            CloseFileEditor();
        }

        private void ServerFileEditorTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isEditorUpdating)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(_openedEditorFilePath))
            {
                return;
            }

            _isEditorTextDirty = true;
            UpdateEditorHeaderText();
        }

        private void ServerSaveSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_server == null)
            {
                return;
            }

            try
            {
                var name = (ServerSettingsNameTextBox.Text ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    throw new InvalidOperationException("Укажите название сервера.");
                }

                var version = (ServerSettingsVersionTextBox.Text ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(version))
                {
                    throw new InvalidOperationException("Укажите версию сервера.");
                }

                double ramGb;
                var ramRaw = (ServerSettingsRamGbTextBox.Text ?? string.Empty).Trim().Replace(',', '.');
                if (!double.TryParse(ramRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out ramGb) || ramGb <= 0)
                {
                    throw new InvalidOperationException("Некорректное значение ОЗУ.");
                }

                _server.Name = name;
                var oldVersion = _server.Version ?? string.Empty;
                _server.Version = version;
                _server.RamMb = ClampRamMb((int)Math.Round(ramGb * 1024.0, MidpointRounding.AwayFromZero));
                _server.ExtraJavaArgs = (ServerSettingsArgsTextBox.Text ?? string.Empty).Trim();
                if (!string.Equals(oldVersion, _server.Version, StringComparison.OrdinalIgnoreCase))
                {
                    _server.InstalledCoreVersion = string.Empty;
                }

                PersistServerChanges();

                Title = "Сервер: " + _server.Name;
                ServerTitleTextBlock.Text = (_server.Name ?? "Server") + " [" + (_server.Core ?? "-") + " " + (_server.Version ?? "-") + "]";
                ServerSettingsStatusTextBlock.Text = "Настройки сервера сохранены.";
            }
            catch (Exception ex)
            {
                ServerSettingsStatusTextBlock.Text = "Ошибка: " + ex.Message;
            }
        }

        private void PersistServerChanges()
        {
            var all = _localServerService.Load().ToList();
            var index = all.FindIndex(s => string.Equals(s.Id, _server.Id, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                all.Add(_server);
            }
            else
            {
                all[index] = _server;
            }

            _localServerService.Save(all);
        }

        private void RefreshFileList()
        {
            if (_server == null)
            {
                ServerFilesListView.ItemsSource = null;
                return;
            }

            var root = Path.GetFullPath(_server.FolderPath ?? string.Empty);
            if (!Directory.Exists(root))
            {
                Directory.CreateDirectory(root);
            }

            var target = string.IsNullOrWhiteSpace(_currentDirectory) ? root : Path.GetFullPath(_currentDirectory);
            if (!IsPathInsideRoot(target, root))
            {
                target = root;
            }

            _currentDirectory = target;
            ServerCurrentPathTextBlock.Text = "Папка: " + target;

            var entries = new List<ServerFileEntry>();
            foreach (var dir in Directory.GetDirectories(target).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                entries.Add(new ServerFileEntry
                {
                    Name = Path.GetFileName(dir),
                    Type = "Папка",
                    SizeText = "-",
                    FullPath = dir,
                    IsDirectory = true
                });
            }

            foreach (var file in Directory.GetFiles(target).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var fileInfo = new FileInfo(file);
                entries.Add(new ServerFileEntry
                {
                    Name = Path.GetFileName(file),
                    Type = "Файл",
                    SizeText = FormatSize(fileInfo.Length),
                    FullPath = file,
                    IsDirectory = false
                });
            }

            ServerFilesListView.ItemsSource = entries;
            ServerDeleteEntryButton.IsEnabled = false;
            ServerOpenEntryButton.IsEnabled = false;
            if (!string.IsNullOrWhiteSpace(_openedEditorFilePath) && !File.Exists(_openedEditorFilePath))
            {
                CloseFileEditor();
            }
        }

        private void OpenSelectedEntry()
        {
            var entry = ServerFilesListView.SelectedItem as ServerFileEntry;
            if (entry == null)
            {
                return;
            }

            if (entry.IsDirectory)
            {
                _currentDirectory = entry.FullPath;
                RefreshFileList();
                return;
            }

            OpenFileInEditor(entry.FullPath);
        }

        private static bool CanOpenEntry(ServerFileEntry entry)
        {
            return entry != null;
        }

        private void OpenFileInEditor(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return;
            }

            try
            {
                Encoding encoding;
                string text;
                using (var reader = new StreamReader(filePath, Encoding.UTF8, true))
                {
                    text = reader.ReadToEnd();
                    encoding = reader.CurrentEncoding;
                }

                _openedEditorFilePath = filePath;
                _openedEditorEncoding = encoding ?? new UTF8Encoding(false);
                _isEditorTextDirty = false;

                _isEditorUpdating = true;
                ServerFileEditorTextBox.Text = text;
                _isEditorUpdating = false;

                ServerFilesListView.Visibility = Visibility.Collapsed;
                ServerFilesActionsPanel.Visibility = Visibility.Collapsed;
                ServerFileEditorPanel.Visibility = Visibility.Visible;
                ServerFileEditorTextBox.Focus();
                ServerFileEditorTextBox.SelectionStart = ServerFileEditorTextBox.Text.Length;
                UpdateEditorHeaderText();
            }
            catch (Exception ex)
            {
                _isEditorUpdating = false;
                MessageBox.Show("Не удалось открыть файл:\n\n" + ex.Message, "Сервер", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveOpenedEditorFile()
        {
            if (string.IsNullOrWhiteSpace(_openedEditorFilePath))
            {
                return;
            }

            try
            {
                File.WriteAllText(_openedEditorFilePath, ServerFileEditorTextBox.Text ?? string.Empty, _openedEditorEncoding ?? new UTF8Encoding(false));
                _isEditorTextDirty = false;
                UpdateEditorHeaderText();
                AppendConsoleLine("Файл сохранён: " + Path.GetFileName(_openedEditorFilePath));
                RefreshFileList();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Не удалось сохранить файл:\n\n" + ex.Message, "Сервер", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CloseFileEditor()
        {
            _openedEditorFilePath = string.Empty;
            _openedEditorEncoding = new UTF8Encoding(false);
            _isEditorTextDirty = false;
            _isEditorUpdating = true;
            ServerFileEditorTextBox.Text = string.Empty;
            _isEditorUpdating = false;
            ServerFileEditorPathTextBlock.Text = "Файл: -";
            ServerFileEditorPanel.Visibility = Visibility.Collapsed;
            ServerFilesListView.Visibility = Visibility.Visible;
            ServerFilesActionsPanel.Visibility = Visibility.Visible;
        }

        private void UpdateEditorHeaderText()
        {
            if (string.IsNullOrWhiteSpace(_openedEditorFilePath))
            {
                ServerFileEditorPathTextBlock.Text = "Файл: -";
                return;
            }

            var mark = _isEditorTextDirty ? " *" : string.Empty;
            ServerFileEditorPathTextBlock.Text = "Файл: " + _openedEditorFilePath + mark;
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024)
            {
                return bytes.ToString(CultureInfo.InvariantCulture) + " Б";
            }

            if (bytes < 1024 * 1024)
            {
                return (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " КБ";
            }

            return (bytes / 1024.0 / 1024.0).ToString("0.00", CultureInfo.InvariantCulture) + " МБ";
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

        private static string FormatRamGb(int ramMb)
        {
            var gb = Math.Max(0.5, ramMb / 1024.0);
            if (Math.Abs(gb - Math.Round(gb)) < 0.001)
            {
                return Math.Round(gb).ToString("0", CultureInfo.InvariantCulture);
            }

            return gb.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static int ClampRamMb(int ramMb)
        {
            if (ramMb < 512)
            {
                return 512;
            }

            if (ramMb > 65536)
            {
                return 65536;
            }

            return ramMb;
        }

        private bool IsServerRunning()
        {
            try
            {
                return _serverProcess != null && !_serverProcess.HasExited;
            }
            catch
            {
                return false;
            }
        }

        private void UpdateServerStateText(string text)
        {
            ServerStateTextBlock.Text = text;
        }

        private void UpdateButtons()
        {
            var running = IsServerRunning();
            var canStart = !running && !_isStarting && !_isStopping && !_isWindowClosing;
            var canStop = running && !_isStarting && !_isStopping && !_isWindowClosing;
            ServerStartButton.IsEnabled = canStart;
            ServerRestartButton.IsEnabled = canStop;
            ServerStopButton.IsEnabled = canStop;
            ServerCommandTextBox.IsEnabled = canStop;
            ServerSendCommandButton.IsEnabled = canStop;
        }

        private void AppendConsoleLine(string line)
        {
            var text = "[" + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "] " + line + Environment.NewLine;
            ServerConsoleTextBox.AppendText(text);
            ServerConsoleTextBox.ScrollToEnd();
        }

        private void SendCurrentServerCommand()
        {
            var command = (ServerCommandTextBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(command))
            {
                return;
            }

            if (!IsServerRunning() || _serverProcess == null)
            {
                AppendConsoleLine("[CMD] Сервер не запущен.");
                return;
            }

            try
            {
                _serverProcess.StandardInput.WriteLine(command);
                _serverProcess.StandardInput.Flush();
                AppendConsoleLine("[CMD] " + command);
                PushCommandToHistory(command);
                ServerCommandTextBox.Clear();
                _commandHistoryIndex = _commandHistory.Count;
            }
            catch (Exception ex)
            {
                AppendConsoleLine("[CMD-ERR] " + ex.Message);
            }
        }

        private void PushCommandToHistory(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return;
            }

            if (_commandHistory.Count > 0 &&
                string.Equals(_commandHistory[_commandHistory.Count - 1], command, StringComparison.Ordinal))
            {
                return;
            }

            _commandHistory.Add(command);
            if (_commandHistory.Count > 200)
            {
                _commandHistory.RemoveAt(0);
            }
        }

        private void NavigateCommandHistory(int step)
        {
            if (_commandHistory.Count == 0)
            {
                return;
            }

            if (_commandHistoryIndex < 0 || _commandHistoryIndex > _commandHistory.Count)
            {
                _commandHistoryIndex = _commandHistory.Count;
            }

            _commandHistoryIndex += step;

            if (_commandHistoryIndex < 0)
            {
                _commandHistoryIndex = 0;
            }

            if (_commandHistoryIndex > _commandHistory.Count)
            {
                _commandHistoryIndex = _commandHistory.Count;
            }

            if (_commandHistoryIndex == _commandHistory.Count)
            {
                ServerCommandTextBox.Clear();
                return;
            }

            ServerCommandTextBox.Text = _commandHistory[_commandHistoryIndex];
            ServerCommandTextBox.SelectionStart = ServerCommandTextBox.Text.Length;
        }

        protected override void OnClosed(EventArgs e)
        {
            _isWindowClosing = true;

            var process = _serverProcess;
            _serverProcess = null;

            base.OnClosed(e);

            if (process == null)
            {
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    if (process.HasExited)
                    {
                        return;
                    }

                    try
                    {
                        process.StandardInput.WriteLine("stop");
                        process.StandardInput.Flush();
                        if (!process.WaitForExit(2000))
                        {
                            process.Kill();
                        }
                    }
                    catch
                    {
                        if (!process.HasExited)
                        {
                            process.Kill();
                        }
                    }
                }
                catch
                {
                }
                finally
                {
                    try
                    {
                        process.Dispose();
                    }
                    catch
                    {
                    }
                }
            });
        }
    }
}

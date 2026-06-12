using AsetLauncher.Services;
using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace AsetLauncher
{
    public partial class ConsoleWindow : Window
    {
        private readonly object _pendingLock = new object();
        private readonly Queue<string> _pendingLines = new Queue<string>();
        private readonly DispatcherTimer _flushTimer;

        public ConsoleWindow()
        {
            InitializeComponent();

            _flushTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(80)
            };
            _flushTimer.Tick += FlushTimer_Tick;

            Loaded += ConsoleWindow_Loaded;
            Closed += ConsoleWindow_Closed;
        }

        private void ConsoleWindow_Loaded(object sender, RoutedEventArgs e)
        {
            LogTextBox.Text = LauncherLogService.GetSnapshotText();
            if (!string.IsNullOrEmpty(LogTextBox.Text))
            {
                LogTextBox.ScrollToEnd();
            }

            LauncherLogService.LineAppended += LauncherLogService_LineAppended;
            LauncherLogService.LogsCleared += LauncherLogService_LogsCleared;
        }

        private void ConsoleWindow_Closed(object sender, EventArgs e)
        {
            LauncherLogService.LineAppended -= LauncherLogService_LineAppended;
            LauncherLogService.LogsCleared -= LauncherLogService_LogsCleared;
            _flushTimer.Stop();
        }

        private void LauncherLogService_LineAppended(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            lock (_pendingLock)
            {
                _pendingLines.Enqueue(line);
            }

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(EnsureFlushRunning));
                return;
            }

            EnsureFlushRunning();
        }

        private void LauncherLogService_LogsCleared()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(LauncherLogService_LogsCleared));
                return;
            }

            lock (_pendingLock)
            {
                _pendingLines.Clear();
            }

            LogTextBox.Clear();
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            LauncherLogService.Clear();
            LauncherLogService.Info("Логи очищены пользователем.");
        }

        private void EnsureFlushRunning()
        {
            if (!_flushTimer.IsEnabled)
            {
                _flushTimer.Start();
            }
        }

        private void FlushTimer_Tick(object sender, EventArgs e)
        {
            string[] linesToAppend;
            lock (_pendingLock)
            {
                if (_pendingLines.Count == 0)
                {
                    _flushTimer.Stop();
                    return;
                }

                linesToAppend = _pendingLines.ToArray();
                _pendingLines.Clear();
            }

            var builder = new StringBuilder();
            if (!string.IsNullOrEmpty(LogTextBox.Text))
            {
                builder.AppendLine();
            }

            for (var i = 0; i < linesToAppend.Length; i++)
            {
                if (i > 0)
                {
                    builder.AppendLine();
                }

                builder.Append(linesToAppend[i]);
            }

            LogTextBox.AppendText(builder.ToString());
            LogTextBox.ScrollToEnd();
        }
    }
}

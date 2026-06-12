using AsetLauncher.Services;
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace AsetLauncher
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

            LauncherLogService.Info("AsetLauncher запущен.");

            try
            {
                var settings = new LauncherSettingsService().Load();
                ThemeService.ApplyTheme(settings.ThemeId);
                LauncherMusicService.ApplySettings(settings);
            }
            catch (Exception ex)
            {
                LauncherLogService.Exception("Не удалось применить настройки при старте", ex);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            LauncherLogService.Info("AsetLauncher завершает работу.");

            DispatcherUnhandledException -= App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException -= CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException -= TaskScheduler_UnobservedTaskException;
            LauncherMusicService.Stop();

            base.OnExit(e);
        }

        private static void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            LauncherLogService.Exception("Необработанная ошибка UI-потока", e.Exception);
            e.Handled = true;
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            LauncherLogService.Error("Необработанная ошибка AppDomain. IsTerminating=" + e.IsTerminating);
            LauncherLogService.Error(Convert.ToString(e.ExceptionObject));
        }

        private static void TaskScheduler_UnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            LauncherLogService.Exception("Необработанная ошибка Task", e.Exception);
            e.SetObserved();
        }
    }
}

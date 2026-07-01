using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace ArchiveFixer
{
    public partial class App : Application
    {
        public static string AppBaseDirectory { get; private set; } = AppContext.BaseDirectory;

        public static string SettingsFilePath =>
            Path.Combine(AppBaseDirectory, "appsettings.json");

        public static string LogsDirectory =>
            Path.Combine(AppBaseDirectory, "logs");

        public static string TempDirectory =>
            Path.Combine(AppBaseDirectory, "temp");

        public static string ToolsDirectory =>
            Path.Combine(AppBaseDirectory, "tools");

        public static string SevenZipDirectory =>
            Path.Combine(ToolsDirectory, "7zip");

        public static string SevenZipExePath =>
            Path.Combine(SevenZipDirectory, "7z.exe");

        public static string SevenZipDllPath =>
            Path.Combine(SevenZipDirectory, "7z.dll");

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            AppBaseDirectory = AppContext.BaseDirectory;

            EnsureApplicationDirectories();

            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
            TaskSchedulerUnobservedExceptionHelper.Register();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            base.OnExit(e);
        }

        private static void EnsureApplicationDirectories()
        {
            SafeCreateDirectory(LogsDirectory);
            SafeCreateDirectory(TempDirectory);
            SafeCreateDirectory(ToolsDirectory);
            SafeCreateDirectory(SevenZipDirectory);
        }

        private static void SafeCreateDirectory(string path)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                }
            }
            catch
            {
                // 这里不弹窗，避免程序启动阶段因为目录权限问题直接崩溃。
                // 后续 LogService / ExtractService 会给出更明确的错误。
            }
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            try
            {
                WriteFatalErrorLog(e.Exception);

                MessageBox.Show(
                    "程序发生未处理的界面异常，但已尝试记录日志。\n\n" +
                    e.Exception.Message,
                    "ArchiveFixer 异常",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch
            {
                // 防止异常处理本身再次异常。
            }

            e.Handled = true;
        }

        private static void OnCurrentDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                if (e.ExceptionObject is Exception ex)
                {
                    WriteFatalErrorLog(ex);
                }
            }
            catch
            {
                // 忽略异常处理过程中的异常。
            }
        }

        private static void WriteFatalErrorLog(Exception ex)
        {
            try
            {
                SafeCreateDirectory(LogsDirectory);

                string logFile = Path.Combine(
                    LogsDirectory,
                    $"fatal_{DateTime.Now:yyyyMMdd}.log");

                string text =
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [FATAL]\r\n" +
                    ex + "\r\n\r\n";

                File.AppendAllText(logFile, text);
            }
            catch
            {
                // 不能因为写 fatal 日志失败导致程序再次崩溃。
            }
        }
    }

    internal static class TaskSchedulerUnobservedExceptionHelper
    {
        private static bool _registered;

        public static void Register()
        {
            if (_registered)
            {
                return;
            }

            _registered = true;

            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                try
                {
                    string logDir = App.LogsDirectory;

                    if (!Directory.Exists(logDir))
                    {
                        Directory.CreateDirectory(logDir);
                    }

                    string logFile = Path.Combine(
                        logDir,
                        $"fatal_{DateTime.Now:yyyyMMdd}.log");

                    string text =
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [UNOBSERVED_TASK_EXCEPTION]\r\n" +
                        e.Exception + "\r\n\r\n";

                    File.AppendAllText(logFile, text);
                }
                catch
                {
                    // 忽略异常处理过程中的异常。
                }

                e.SetObserved();
            };
        }
    }
}

using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using ArchiveFixer.Services;

namespace ArchiveFixer
{
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
                    LogService.Current?.Exception(
                        "未观察任务异常",
                        e.Exception,
                        errorCode: DiagnosticCodes.Unhandled,
                        component: "TaskScheduler");
                }
                catch
                {
                }

                try
                {
                    string logDir = string.IsNullOrWhiteSpace(LogService.Current?.CurrentOperationDirectory) ? App.LogsDirectory : LogService.Current.CurrentOperationDirectory;

                    if (!Directory.Exists(logDir))
                    {
                        Directory.CreateDirectory(logDir);
                    }

                    string logFile = Path.Combine(
                        logDir,
                        $"fatal_{ArchiveFixer.Helpers.Clock.Now:yyyyMMdd}.log");

                    string text =
                        $"[{ArchiveFixer.Helpers.Clock.Now:yyyy-MM-dd HH:mm:ss}] [UNOBSERVED_TASK_EXCEPTION]\r\n" +
                        ArchiveFixer.Password.PasswordMasker.Sanitize(e.Exception.ToString()) + "\r\n\r\n";

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


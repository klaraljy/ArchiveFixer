using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace ArchiveFixer
{
    public partial class App : Application
    {
        public static string AppBaseDirectory { get; private set; } = AppContext.BaseDirectory;

        /// <summary>
        /// 用户数据目录（<c>&lt;程序目录&gt;\data</c>）。
        /// 配置、日志、临时文件、工作区都放在这里 —— 绿色软件跟着安装位置走，
        /// 缓存绝不写进 C 盘的 %AppData%（用户明确要求）。
        /// </summary>
        public static string DataRootDirectory { get; private set; } =
            Path.Combine(AppContext.BaseDirectory, "data");

        public static string SettingsFilePath =>
            Path.Combine(DataRootDirectory, "appsettings.json");

        public static string LogsDirectory =>
            Path.Combine(DataRootDirectory, "logs");

        public static string TempDirectory =>
            Path.Combine(DataRootDirectory, "temp");

        /// <summary>递归解压的工作区根目录（中间产物，不写用户最终目录）。</summary>
        public static string WorkDirectory =>
            Path.Combine(DataRootDirectory, "work");

        public static string ToolsDirectory =>
            Path.Combine(AppBaseDirectory, "tools");

        /// <summary>
        /// 内置 7-Zip 目录。**只用来在启动时把目录建出来**，路径本身由
        /// <see cref="ArchiveFixer.Engines.ToolLocator.BundledDirectory"/> 提供 —— 7z 路径只允许有一个来源。
        ///
        /// 历史：这里曾经还有 SevenZipExePath / SevenZipDllPath 两个属性，与 ToolLocator 里那份拼装重复
        /// （当时都没有调用方）。一旦被谁顺手用起来，"到底用的哪个 7z"就再也说不清了，故删除。
        /// </summary>
        public static string SevenZipDirectory =>
            ArchiveFixer.Engines.ToolLocator.Default.BundledDirectory;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            AppBaseDirectory = AppContext.BaseDirectory;

            /*
             * 整机被拖死的问题：批量解压会连着读几百 MB、写几百 MB，再交给 7-Zip 解一层，
             * 期间磁盘与 Defender 实时扫描同时被打满，桌面会卡到连任务栏都点不动。
             *
             * 降到 BelowNormal：本程序让出优先级给前台程序，用户还能正常操作电脑。
             * 子进程（7z.exe）继承这个优先级，所以解压也不再和桌面抢资源。
             * 代价是纯后台跑时慢一点，这个取舍对"批量工具"是对的。
             */
            try
            {
                System.Diagnostics.Process.GetCurrentProcess().PriorityClass =
                    System.Diagnostics.ProcessPriorityClass.BelowNormal;
            }
            catch
            {
                // 改优先级失败不影响功能，最多是抢资源。
            }

            MigrateLegacyData();
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
            SafeCreateDirectory(DataRootDirectory);
            SafeCreateDirectory(LogsDirectory);
            SafeCreateDirectory(TempDirectory);
            SafeCreateDirectory(WorkDirectory);
            SafeCreateDirectory(WorkDirectory);
            SafeCreateDirectory(ToolsDirectory);
            SafeCreateDirectory(SevenZipDirectory);
        }

        /// <summary>
        /// 一次性迁移：把旧版本放在程序目录的 appsettings.json 复制到用户数据目录。
        /// 仅在用户数据目录还没有配置文件时执行。
        /// </summary>
        private static void MigrateLegacyData()
        {
            try
            {
                // 两个历史位置都要看一眼：
                //   1) 最老的一版放在程序目录；
                //   2) 8 月那版放在 %AppData%\ArchiveFixer（C 盘，已被用户否掉）。
                // 找到就先搬过来，别让用户重新配一遍。
                string[] legacyPaths =
                {
                    Path.Combine(AppBaseDirectory, "appsettings.json"),
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "ArchiveFixer",
                        "appsettings.json")
                };

                string legacySettingsPath = legacyPaths.FirstOrDefault(File.Exists) ?? legacyPaths[0];

                if (File.Exists(legacySettingsPath) && !File.Exists(SettingsFilePath))
                {
                    SafeCreateDirectory(DataRootDirectory);
                    File.Copy(legacySettingsPath, SettingsFilePath, overwrite: false);
                }
            }
            catch
            {
                // 迁移失败不影响程序启动，后续会使用默认设置。
            }
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
                // 后续 LogService / 归档引擎会给出更明确的错误。
            }
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            try
            {
                WriteFatalErrorLog(e.Exception);
                ShowExceptionDialog(e.Exception);
            }
            catch
            {
                // 防止异常处理本身再次异常。
            }

            e.Handled = true;
        }

        /// <summary>
        /// 界面未处理异常的统一出口：走**自绘**对话框（<see cref="DialogService.ShowException"/>），
        /// 不再用系统 <c>MessageBox</c>。
        ///
        /// <para>
        /// 为什么换掉 MessageBox（用户点名批评过）：灰底、字挤、正文不能复制，
        /// 用户想把异常文本发回来时只能手抄；自绘对话框能选中复制，观感也与程序其它提示一致。
        /// </para>
        /// <para>
        /// 三条兜底（异常处理器本身**绝不允许**再抛，否则一次异常会变成崩溃循环）：
        /// ① 启动早期（主窗口还没显示）与正在关闭时**只写日志、不弹窗** ——
        ///    这两个时刻没有可用的消息泵，弹窗要么没人点得掉、要么把关闭流程卡住；
        /// ② 没有 UI 宿主时不动弹窗（<see cref="ArchiveFixer.Services.DialogService"/> 内部本来就会降级，
        ///    这里再挡一道，免得在无界面环境里创建窗口）；
        /// ③ 整段包在 try 里，任何失败都吞掉 —— 日志里已经有 FATAL 记录，用户不会丢信息。
        /// </para>
        /// <para>
        /// 脱敏：异常文本可能夹带命令行（7z 的 <c>-p&lt;密码&gt;</c> 就在其中），
        /// 所以对话框与日志两条路都过 <c>PasswordMasker.Sanitize</c>（AGENTS.md §6 第 5 条）。
        /// </para>
        /// </summary>
        private static void ShowExceptionDialog(Exception? ex)
        {
            try
            {
                Application? app = Application.Current;

                if (app?.Dispatcher == null
                    || app.Dispatcher.HasShutdownStarted
                    || app.Dispatcher.HasShutdownFinished)
                {
                    // 启动早期 / 正在关闭：没有可靠的消息泵，只留日志。
                    return;
                }

                Window? main = app.MainWindow;

                if (main == null || !main.IsVisible)
                {
                    // 主窗口还没显示出来：此刻弹模态框会盖在启动画面上，用户只会更慌。
                    return;
                }

                new ArchiveFixer.Services.DialogService().ShowException(
                    ex ?? new Exception("未知异常"),
                    "程序发生未处理的界面异常（已记录日志）");
            }
            catch
            {
                // 见方法注释第 ③ 条：兜底就是"日志里已经有 FATAL"。
            }
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
                    ArchiveFixer.Password.PasswordMasker.Sanitize(ex.ToString()) + "\r\n\r\n";

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

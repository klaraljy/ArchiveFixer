using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Storage;

namespace ArchiveFixer.ViewModels;

/// <summary>把设置里引擎/工作区/日志相关项推到运行时。</summary>
internal static class EngineSettingsApplier
{
    internal static void Apply(MainViewModel vm)
    {
        /*
         * 一次调用把三件事推给引擎层：两条外部工具路径 + 引擎优先级 + 保留受损文件。
         *
         * 为什么必须调它（这是上一批交付里**唯一**没接上的那一环）：
         * EngineRuntimeSettings 的默认值虽然"正确可用"，但用户改了优先级之后，
         * 引擎层要能从运行时设置里读到新顺序 —— 不推的话，「先是 UnRAR、再是 7-Zip」
         * 就只是一句写在设置界面上的话，真正干活的仍然是写死的那个引擎。
         * 设置窗口保存时也会推一次（两条路幂等，谁先谁后都不会打架）。
         */
        EngineRuntimeSettings.Apply(vm.Settings);

        /*
         * 数据根**固定**是 %LOCALAPPDATA%\ArchiveFixer（用户 2026-10-29：与安装目录解耦）。
         *
         * ⛔ 这里刻意**不写** `vm.PathService.DataRootDirectory = ...`：
         * 它本来就是 <see cref="PathService.DefaultDataRootDirectory"/>，而测试装配时会把
         * DataRootDirectory 指到临时目录 —— 在这里"顺手复位成默认值"会把测试的数据根
         * 顶回程序目录（真实后果：用例往测试输出目录里写日志 / 设置）。
         * 数据根只有一个出口：PathService 自己的默认值。
         */

        /*
         * 工作区根（用户 2026-09-30：**唯一来源 = 这一单的目标目录**）。
         *
         * ⚠ 真正"落在目标目录里"的解析发生在**每批开工前**
         * （ExtractionCoordinator.ApplyBatchWorkspaceRoot）—— 只有那时才知道这一批会处理哪些包、
         * 目标目录在哪。启动 / 保存设置时根本还没有这一批的任务，所以这里只能取"当前生效的那个根"
         * （本次会话用过的，见 WorkspaceRootIndex.SessionRoots；没有就是老位置 <数据根>\work）。
         *
         * ⚠ 这里**只服务于 ③ 页的残留扫描与启动日志**，不是这一批真正会用的根。
         * ⛔ 批首解析不出来时整批停手（绝不在这里替它挑一个盘）。
         *
         * ⚠ 递归核心的工作区根是进程级静态（它自己会再挂一层 "recursive"），必须跟着一起换 ——
         * 不换的话递归那几百 MB 又会回到程序盘（那正是早先要改掉的老行为）。
         */
        vm.PathService.WorkDirectory = ResolveStartupWorkspaceRoot(vm);
        RecursiveExtractor.ConfiguredWorkspaceRoot = vm.PathService.WorkDirectory;

        /*
         * 「启用日志文件」真正生效的地方：关掉之后只留屏幕日志，不再往 data\logs 追加。
         * 之前这个开关在设置窗口里躺着，勾不勾都不影响写盘 —— 属于"改了没用"的开关。
         * LogService 内部对写失败也会自行降级（置 false），这里只管用户的选择。
         */
        vm.LogService.EnableFileLog = vm.Settings?.EnableLog ?? true;

        // 密码本侧车文件跟着同一个根走，否则"导入记住了"和"启动读取"会看两个目录。
        vm.PasswordService.DataRootDirectory = vm.PathService.DataRootDirectory;
        DurationHistory.DataRootDirectory = vm.PathService.DataRootDirectory;
        ToolLocator.Default.Invalidate();
    }

    private static string ResolveStartupWorkspaceRoot(MainViewModel vm)
    {
        foreach (string remembered in WorkspaceRootIndex.SessionRoots)
        {
            if (!string.IsNullOrWhiteSpace(remembered))
            {
                return remembered;
            }
        }

        return vm.PathService.WorkDirectory;
    }
}


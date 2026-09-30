using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ArchiveFixer.Helpers;

namespace ArchiveFixer.Storage
{
    /// <summary>上一次被中断留下的空壳，这一次跑的时候怎么处置了。</summary>
    public enum WorkspaceLeftoverShellAction
    {
        /// <summary>
        /// 这一层目录里**没有我们的脚印**（那个工作区目录根本不在）、或者它压根不存在
        /// —— 什么都没做，也没什么可说的（⛔ 空目录本身不构成"是我们建的"的理由）。
        /// </summary>
        NoFootprint = 0,

        /// <summary>收掉了：空的工作区目录先删，再删那个已经空掉的目标目录壳。</summary>
        Reclaimed = 1,

        /// <summary>**发现上次没跑完的残留，但一个字节都没动**（里面还有别的东西 / 工作区不是空的）。</summary>
        LeftIntact = 2
    }

    /// <summary>一次"上次没跑完留下的空壳"的处置结论（只描述结论，不碰界面 —— <c>Storage</c> 不引用 WPF）。</summary>
    public sealed class WorkspaceLeftoverShellOutcome
    {
        /// <summary>这一次任务的目标目录（判据只在这一层看，⛔ 不往下递归、也不看别的目录）。</summary>
        public string TargetDirectory { get; init; } = string.Empty;

        /// <summary>那个工作区目录（<c>&lt;目标目录&gt;\.ArchiveFixer.work</c>）；没找到时为空串。</summary>
        public string WorkspaceDirectory { get; init; } = string.Empty;

        /// <summary>怎么处置的。</summary>
        public WorkspaceLeftoverShellAction Action { get; init; }

        /// <summary>这一条要不要写进日志（没脚印那一档一律不写：没有事实可说，写了只是噪音）。</summary>
        public bool ShouldReport => Action != WorkspaceLeftoverShellAction.NoFootprint;

        /// <summary>
        /// 日志级别：**收掉了 = INFO**（顺手活，正常）；
        /// **发现了但没清理 = WARN**（用户可能想自己看一眼，不该埋进 INFO 里）。
        /// </summary>
        public string LogLevel => Action == WorkspaceLeftoverShellAction.LeftIntact ? "WARN" : "INFO";

        /// <summary>给日志的一句话（含真实路径与"为什么没清理"）。没脚印时为空串。</summary>
        public string Message { get; init; } = string.Empty;
    }

    /// <summary>
    /// **上一次跑到一半被中断 / 被掐掉，留下的那个空壳 —— 下一次跑的时候顺手收掉**
    /// （用户 2026-09-30 亲口点了"推荐"）。
    ///
    /// <para><b>现场</b>：正常跑完的收尾本来就会连壳一起删
    /// （<c>ExtractionCoordinator.RemoveEmptyWorkspaceShells</c> + <c>RemoveEmptyTargetShellIfWeCreatedIt</c>），
    /// 只有**中断**这一档会残留 —— 真机上 <c>H:\成果\1111\</c> 里就只剩一个空的 <c>.ArchiveFixer.work</c>，
    /// 一个成品都没有，而用户翻自己的目录时会看到这个"凭空多出来的文件夹"。</para>
    ///
    /// <para><b>判据只有一条，而且极窄（全项目唯一出口，⛔ 调用点不许各写一遍）</b>：
    /// 那个目标目录里**除了我们那个空的工作区目录之外什么都没有**（0 个文件、0 个子目录）
    /// ⇒ 先删空的工作区、再删那个空壳目录（两次都 <c>recursive: false</c>）；
    /// 只要里面有**任何一个**别的文件或目录（哪怕 0 字节）⇒ **一个字节都不动**，只写一条 WARN 说清
    /// "发现了上次没跑完的残留、没有清理、路径是…"。</para>
    ///
    /// <para><b>为什么判据是"工作区目录在不在"而不是"目录是不是空的"</b>：
    /// 空目录本身不是证据 —— 用户自己建的空文件夹、别的程序留下的空文件夹，长得一模一样。
    /// 只有 <c>.ArchiveFixer.work</c> 这个约定名（<see cref="WorkspaceTree.IsWorkspaceDirectoryName"/>，
    /// 名字只允许在 <see cref="WorkspaceRootResolver.DefaultWorkspaceDirectoryName"/> 出现一次）
    /// 才是"我们确实在这儿干过活"的事实。⛔ 光凭"名字像包名"不删任何东西。</para>
    ///
    /// <para><b>四条红线（⛔ 一条都不许松）</b>：</para>
    /// <list type="number">
    /// <item><description>**只删空的**：现枚举，且两次删除都用 <c>recursive: false</c> ——
    /// 非递归删除本身就是最后一道闸门（枚举与删除之间若冒出来任何条目，删除会直接失败而不是连它一起删）。</description></item>
    /// <item><description>**只看目标目录那一层**：不往下递归、不扫兄弟目录、⛔ 更不做全盘扫描
    /// （用户会把它读成"程序乱动我盘"）。要查哪些目录由调用方（本批的落点）给。</description></item>
    /// <item><description>**兜底落在"什么都不做"那一档**：读不出来 / 同名的是个文件 / 工作区是个链接（联接点）
    /// ⇒ 一律不删，只如实报告。</description></item>
    /// <item><description>**只碰工作区与那个空壳**：源包、成品、其余物一个字节都不动
    /// （工作区里还有东西时连工作区都不删 —— 那可能是用户唯一的一份产物线索）。</description></item>
    /// </list>
    ///
    /// <para>⚠ <b>调用时机是硬要求</b>：必须排在 <see cref="WorkspaceRootResolver.Resolve"/> **之前**。
    /// 那一句会按"解析之前它在不在"如实记下 <c>TargetDirectoryCreated</c> 并把目标目录与工作区都建出来 ——
    /// 先建后收会把**刚建出来的工作区**当成残留删掉。</para>
    /// </summary>
    public static class WorkspaceLeftoverShellCleaner
    {
        /// <summary>
        /// 收掉一个目标目录里"上次没跑完留下的空壳"（判据见类注释；<b>唯一的判据出口</b>）。
        /// </summary>
        /// <param name="targetDirectory">这一次任务的目标目录（只查它这一层）。</param>
        /// <returns>处置结论；<see cref="WorkspaceLeftoverShellOutcome.ShouldReport"/> 为 false 时没有日志要写。</returns>
        public static WorkspaceLeftoverShellOutcome Reclaim(string? targetDirectory)
        {
            string target = SafeFullPath(targetDirectory);

            if (target.Length == 0 || !SafeDirectoryExists(target))
            {
                return new WorkspaceLeftoverShellOutcome { Action = WorkspaceLeftoverShellAction.NoFootprint };
            }

            string[] entries;

            try
            {
                entries = Directory.GetFileSystemEntries(target);
            }
            catch
            {
                // 读不出来就什么都判不出来 → 按"什么都不做"办（⛔ 不猜、不删）。
                return new WorkspaceLeftoverShellOutcome
                {
                    TargetDirectory = target,
                    Action = WorkspaceLeftoverShellAction.NoFootprint
                };
            }

            string? workspace = null;

            foreach (string entry in entries)
            {
                if (WorkspaceTree.IsWorkspaceDirectoryName(entry))
                {
                    workspace = entry;
                    break;
                }
            }

            if (workspace == null)
            {
                // 没有我们的脚印：空目录 / 装满了用户自己的东西 —— 都不是删它的理由。
                return new WorkspaceLeftoverShellOutcome
                {
                    TargetDirectory = target,
                    Action = WorkspaceLeftoverShellAction.NoFootprint
                };
            }

            var outcome = new WorkspaceLeftoverShellOutcome
            {
                TargetDirectory = target,
                WorkspaceDirectory = workspace,
                Action = WorkspaceLeftoverShellAction.LeftIntact
            };

            /*
             * ① 同名的是个**文件**（不是我们建的工作区目录）：那不是工作区，⛔ 不许删。
             * 这一档必须排在最前面 —— 后面的"空不空"对文件根本不成立。
             */
            if (!SafeDirectoryExists(workspace))
            {
                return WithMessage(
                    outcome,
                    string.Format(
                        CultureInfo.CurrentCulture,
                        "发现上次没跑完的残留，没有清理：{0} 里有一个叫 {1} 的同名文件，"
                        + "那不是我们建的工作区目录（只有工作区目录才可能是可以收的空壳），一个字节都没动。",
                        target,
                        Path.GetFileName(workspace)));
            }

            /*
             * ② 目标目录里除了它还有别的东西（哪怕 0 字节的文件 / 一个空子目录）：
             * **一个字节都不动** —— 那条"只删空壳"的判据本来就是"除了它什么都没有"。
             */
            if (entries.Length != 1)
            {
                return WithMessage(
                    outcome,
                    string.Format(
                        CultureInfo.CurrentCulture,
                        "发现上次没跑完的残留，没有清理：{0} 里除了工作区目录 {1} 还有 {2} 个别的文件或目录"
                        + "（判据是「除了那个空工作区之外什么都没有」才收，所以一个字节都没动，"
                        + "要清的话请自己确认后再删）。",
                        target,
                        Path.GetFileName(workspace),
                        entries.Length - 1));
            }

            /*
             * ③ 工作区里还有东西（哪怕只有一个 0 字节的文件）：不是空壳，**连工作区都不删** ——
             * 里面可能是用户唯一的一份产物线索（与 §6 不变量 12 的"失败保留现场"同一口径）。
             */
            string[] workspaceEntries;

            try
            {
                workspaceEntries = Directory.GetFileSystemEntries(workspace);
            }
            catch (Exception ex)
            {
                return WithMessage(
                    outcome,
                    string.Format(
                        CultureInfo.CurrentCulture,
                        "发现上次没跑完的残留，没有清理：工作区目录 {0} 读不出来（{1}），一个字节都没动。",
                        workspace,
                        ex.Message));
            }

            if (workspaceEntries.Length != 0)
            {
                return WithMessage(
                    outcome,
                    string.Format(
                        CultureInfo.CurrentCulture,
                        "发现上次没跑完的残留，没有清理：工作区目录 {0} 里还有 {1} 个条目，不是空壳，"
                        + "一个字节都没动（里面可能是还没搬出去的产物）。",
                        workspace,
                        workspaceEntries.Length));
            }

            /*
             * ④ 目录联接点 / 符号链接不当空壳处理：它的"空"说的是链接那一头，删它意味着什么
             * 我们不打算靠猜 —— 如实报告，什么都不动。
             */
            if (SafeIsReparsePoint(workspace))
            {
                return WithMessage(
                    outcome,
                    string.Format(
                        CultureInfo.CurrentCulture,
                        "发现上次没跑完的残留，没有清理：工作区目录 {0} 是个链接（目录联接点 / 符号链接），"
                        + "不当空壳处理，一个字节都没动。",
                        workspace));
            }

            /*
             * ⑤ 两条判据都成立 → 真的收掉。顺序不能反：先收里面那个空工作区，目标目录才可能空。
             *
             * 两次都 recursive: false：非递归删除在"目录其实不空"时会直接抛错，
             * 于是"枚举之后又冒出来东西"这个窗口**删不掉任何东西**，而不是被连坐删掉。
             */
            try
            {
                Directory.Delete(workspace, recursive: false);
            }
            catch (Exception ex)
            {
                return WithMessage(
                    outcome,
                    string.Format(
                        CultureInfo.CurrentCulture,
                        "发现上次没跑完的残留，没清掉（{0}）：{1}（一个字节都没动）。",
                        ex.Message,
                        workspace));
            }

            try
            {
                Directory.Delete(target, recursive: false);
            }
            catch (Exception ex)
            {
                return WithMessage(
                    outcome,
                    string.Format(
                        CultureInfo.CurrentCulture,
                        "上次没跑完留下的空工作区已收掉（{0}），但那个空壳目录没收掉（{1}）：{2}。",
                        workspace,
                        ex.Message,
                        target));
            }

            return new WorkspaceLeftoverShellOutcome
            {
                TargetDirectory = target,
                WorkspaceDirectory = workspace,
                Action = WorkspaceLeftoverShellAction.Reclaimed,
                Message = string.Format(
                    CultureInfo.CurrentCulture,
                    "收掉了上次没跑完留下的空壳（里面只有一个空的工作区目录）：{0}（连 {1} 一起删了）。",
                    target,
                    Path.GetFileName(workspace))
            };
        }

        /// <summary>
        /// 收掉**一批**目标目录里的残留空壳（本批落点由唯一实现算出来后原样传进来）。
        ///
        /// <para>只返回**有话要说的**那些结论（收掉了 / 发现了但没清理）—— 没有脚印的目录不产生日志。
        /// 重复的目录只查一次，且**保持调用方给的顺序**（日志读起来就是处理顺序）。</para>
        /// </summary>
        public static IReadOnlyList<WorkspaceLeftoverShellOutcome> ReclaimAll(
            IEnumerable<string>? targetDirectories)
        {
            var results = new List<WorkspaceLeftoverShellOutcome>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string? directory in targetDirectories ?? Array.Empty<string>())
            {
                string full = SafeFullPath(directory);

                if (full.Length == 0 || !seen.Add(full))
                {
                    continue;
                }

                WorkspaceLeftoverShellOutcome outcome = Reclaim(full);

                if (outcome.ShouldReport)
                {
                    results.Add(outcome);
                }
            }

            return results;
        }

        private static WorkspaceLeftoverShellOutcome WithMessage(
            WorkspaceLeftoverShellOutcome source,
            string message)
            => new()
            {
                TargetDirectory = source.TargetDirectory,
                WorkspaceDirectory = source.WorkspaceDirectory,
                Action = WorkspaceLeftoverShellAction.LeftIntact,
                Message = message
            };

        private static string SafeFullPath(string? path)
        {
            try
            {
                // TrimEndingDirectorySeparator 而不是 TrimEnd：盘根（"E:\"）不能被削成 "E:"。
                return string.IsNullOrWhiteSpace(path)
                    ? string.Empty
                    : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool SafeDirectoryExists(string? path)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);
            }
            catch
            {
                return false;
            }
        }

        private static bool SafeIsReparsePoint(string path)
        {
            try
            {
                return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
            }
            catch
            {
                // 属性都读不出来 ⇒ 当成"不能确定"，按不删办（返回 true 会让调用方原样报告、什么都不动）。
                return true;
            }
        }
    }
}

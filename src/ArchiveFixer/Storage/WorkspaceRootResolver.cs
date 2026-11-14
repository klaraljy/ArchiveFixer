using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ArchiveFixer.Storage;

/// <summary>
/// **工作区根只有一个来源：这一单的目标位置**（用户 2026-09-30 明确指示）。
///
/// <para><b>用户原话（三条诉求 + 最后一句"彻底取消"）</b>：① 「我不喜欢现在这样的模式就是专门在一个盘里面
/// 去开一个工作区……我这个 H 盘总共才三个最顶层文件夹，但是你这个第四个就会占一个」；
/// ② 「要么是在解压的地方临时创一个，然后弄完就直接全部清除什么都不留，原地就在原地创，
/// 指定就在指定创，不要有固定一个位置的思想，因为用户可能忘了固定，可能固定到了 D 盘，
/// 甚至危险操作固定到了 C 盘」；③ 「就弄到工作区，而且是隐形的，什么都不要创，或者要创的话就隐秘文件」；
/// ④（2026-09-30 收口）「**这个彻底取消，用户没有定工作区的权力，就是在解压的地方设立隐形的工作区，
/// 这就完全不存在跨盘的操作**」。</para>
///
/// <para><b>于是本类只有一条规则</b>：工作区 = <c>&lt;这一批第一个算得出落点的目标目录&gt;\.ArchiveFixer.work</c>。
/// 中间产物与成品落在**同一棵树**里 → 天然同卷，定稿永远是同卷改名，**不存在跨盘搬运**；
/// 目录名点开头 + <see cref="FileAttributes.Hidden"/>，用户翻自己的目录时看不见它。</para>
///
/// <list type="number">
/// <item><description>⛔ **没有任何"用户指定"的入口**：设置项 <c>CacheRootDirectory</c> 已删除，
/// 参数表里也不再接受任何覆盖值 —— 想改工作区位置，只能改这一批的落点。</description></item>
/// <item><description>目标目录还不存在就**先建它**（用户原话"原地就在原地创"）；建不出来就换下一个
/// 算得出落点的任务继续试（那一批可能只是某个任务落点无效，不该拖垮整批的工作区）。</description></item>
/// <item><description>⛔ 所有候选都不行（没有任务算得出落点 / 建不出目录）→
/// <see cref="WorkspaceRootOrigin.TargetUnusable"/>：**报错并说清是哪个目标位置不行**，
/// <b>绝不</b>悄悄回落到 C 盘 / 程序目录 / 任何别的盘。</description></item>
/// </list>
///
/// <para>⚠ <b>落进目标目录之后必须排除自己那棵树</b>：那边有唯一出口
/// <see cref="WorkspaceTree.ShouldSkipEntry"/> / <see cref="WorkspaceTree.EnumerateFiles"/>，
/// 凡"扫产物 / 数内容物 / 找内层包 / 校验落点"的判据都要走它。</para>
///
/// <para><b>目录名是点开头的 <c>.ArchiveFixer.work</c></b>（常量 <see cref="DefaultWorkspaceDirectoryName"/>，
/// 只允许在这里出现一次）。名字、位置都不许在别处再拼一遍 —— 落点唯一实现仍是
/// <c>OutputPlacement.ResolveDestinationDirectory</c>，本类只消费**它算出来的落点**。</para>
/// </summary>
public static class WorkspaceRootResolver
{
    /// <summary>
    /// 工作区目录名（**点开头 = Windows 默认隐藏**，用户翻盘时不至于把它当成自己的东西）。
    /// 全项目只在这里出现一次；改名要连测试与文档一起改。
    /// </summary>
    public const string DefaultWorkspaceDirectoryName = ".ArchiveFixer.work";

    /// <summary>
    /// 解析本批的工作区根（**唯一入口**，无从外部指定任何位置）。
    /// </summary>
    /// <param name="batchDestinationDirectories">
    /// 本批**按处理顺序**排好的目标目录（由调用方走唯一实现
    /// <c>PathService.ResolveOutputPlacement</c> 算出来，本类不自己拼路径）。
    /// 算不出落点的任务由调用方跳过 —— 这里只认"能算出目标目录"的那些。
    /// </param>
    /// <param name="driveOf">
    /// "一个目标目录在哪块盘上"（收到的是目录，返回盘根如 <c>D:\</c>）。
    /// 抽成参数是为了让测试不依赖本机真有 D 盘（默认 = <see cref="Path.GetPathRoot(string)"/>）；
    /// 它只影响"跨了几个盘"这句**描述**，不影响工作区落在哪。
    /// </param>
    /// <param name="ensureDirectory">按需建目录（默认 = <c>SafePathHelper.EnsureDirectoryExists</c>）。</param>
    public static WorkspaceRootResolution Resolve(
        IReadOnlyList<string>? batchDestinationDirectories,
        Func<string, string>? driveOf = null,
        Func<string, bool>? ensureDirectory = null)
    {
        Func<string, bool> ensure = ensureDirectory ?? SafePathHelper.EnsureDirectoryExists;

        List<string> drives = CollectDrives(batchDestinationDirectories, driveOf);
        string? firstFailure = null;

        foreach (string? destination in batchDestinationDirectories ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(destination))
            {
                continue;
            }

            string full = SafePathHelper.GetFullPathSafe(destination);

            if (string.IsNullOrWhiteSpace(full))
            {
                firstFailure ??= string.Format(
                    CultureInfo.CurrentCulture,
                    StatusText.WorkspaceTargetInvalidShapeFormat,
                    destination);
                continue;
            }

            // 建之前先如实记下"它本来在不在"：收尾那边要靠这个事实决定能不能收掉自己造的壳。
            bool targetExisted = Directory.Exists(full);

            /*
             * 目标目录还不存在就**先建它**（用户原话"原地就在原地创"）：工作区要落在它里面，
             * 连它都没有就无从谈"落在目标目录里"。建不出来说明那个位置不可写，
             * 换下一个能算出落点的任务继续试。
             */
            if (!ensure(full))
            {
                firstFailure ??= string.Format(
                    CultureInfo.CurrentCulture,
                    StatusText.WorkspaceTargetNotWritableFormat,
                    full);
                continue;
            }

            string root = Path.Combine(full, DefaultWorkspaceDirectoryName);

            if (!ensure(root))
            {
                firstFailure ??= string.Format(
                    CultureInfo.CurrentCulture,
                    StatusText.WorkspaceUnderTargetNotWritableFormat,
                    root);
                continue;
            }

            // 建完立刻标隐藏（用户诉求③）。设不上只当没发生，绝不影响这一批能不能开工。
            WorkspaceTree.MarkHidden(root);

            return new WorkspaceRootResolution
            {
                RootDirectory = root,
                Origin = WorkspaceRootOrigin.OutputDirectory,
                TargetDirectory = full,
                TargetDirectoryCreated = !targetExisted,
                BatchDrives = drives,
                Reason = BuildReason(full, root, drives, driveOf)
            };
        }

        /*
         * 所有目标位置都不可用 → **报错，不回落任何位置**（用户 2026-09-30 点名的红线）。
         *
         * 老实现这里回落到 <程序目录>\data\work 并写一条 WARN"整批照常开工"——
         * 用户明确否掉了那个方向：他可能把输出位置固定到了只剩 40G 的 D 盘、甚至固定到 C 盘，
         * 程序"悄悄换个盘"写中间产物正是他说"危险"的那件事。
         * 所以这一档 RootDirectory 留空，由调用方停手并如实报出**是哪个目标位置不行**。
         */
        return new WorkspaceRootResolution
        {
            RootDirectory = string.Empty,
            Origin = WorkspaceRootOrigin.TargetUnusable,
            BatchDrives = drives,
            Reason = string.Format(
                CultureInfo.CurrentCulture,
                StatusText.WorkspaceTargetUnusableFormat,
                firstFailure ?? StatusText.WorkspaceNoUsableTargetReason)
        };
    }

    /// <summary>跟着目标目录那条日志正文（跨盘时如实补一句）。</summary>
    private static string BuildReason(
        string targetDirectory,
        string root,
        IReadOnlyList<string> drives,
        Func<string, string>? driveOf)
    {
        var text = new StringBuilder();

        text.Append(string.Format(
            CultureInfo.CurrentCulture,
            StatusText.WorkspaceRootInOutputDirectoryFormat,
            targetDirectory,
            root));

        if (drives.Count > 1)
        {
            string ownDrive = (driveOf ?? DefaultDriveOf)(targetDirectory);

            text.Append(string.Format(
                CultureInfo.CurrentCulture,
                StatusText.WorkspaceRootCrossDriveFormat,
                drives.Count,
                string.Join("、", drives),
                ownDrive.TrimEnd('\\', '/')));
        }

        return text.ToString();
    }

    /// <summary>本批落点涉及到的盘（去重、保持首次出现的顺序）—— 跨盘说明读它。</summary>
    private static List<string> CollectDrives(
        IReadOnlyList<string>? destinations,
        Func<string, string>? driveOf)
    {
        var drives = new List<string>();

        if (destinations == null)
        {
            return drives;
        }

        Func<string, string> resolveDrive = driveOf ?? DefaultDriveOf;

        foreach (string? destination in destinations)
        {
            if (string.IsNullOrWhiteSpace(destination))
            {
                continue;
            }

            string drive = resolveDrive(SafePathHelper.GetFullPathSafe(destination));

            if (string.IsNullOrWhiteSpace(drive))
            {
                continue;
            }

            bool seen = false;

            foreach (string existing in drives)
            {
                if (string.Equals(existing, drive, StringComparison.OrdinalIgnoreCase))
                {
                    seen = true;
                    break;
                }
            }

            if (!seen)
            {
                drives.Add(drive);
            }
        }

        return drives;
    }

    /// <summary>默认的"这个落点在哪块盘上"：<c>Path.GetPathRoot</c>（UNC 路径也会给出 <c>\\server\share</c>）。</summary>
    private static string DefaultDriveOf(string path)
    {
        try
        {
            return Path.GetPathRoot(path) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}

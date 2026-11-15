using ArchiveFixer.BindingModels;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Services;

/// <summary>
/// **准入**：把识别结论落成任务上的状态与用户可见句子（原文 <c>ArchiveDetectService.ApplyDetectResultAsync</c>
/// 那一层，判据一条没动）。
///
/// <para>它回答三个问题：</para>
/// <list type="number">
/// <item><description>这一份**进不进归档流程**（<see cref="ArchiveTask.IsArchive"/>）—— 是则「已识别 / 等待处理」；</description></item>
/// <item><description>不是归档时**到底是什么情况** —— "只是分卷组的后续卷，缺第 1 卷"与"格式未知"是两句完全不同的话；</description></item>
/// <item><description>后缀**该不该动**（正常 / 缺失 / 不匹配 / 多重伪装 / 分卷后缀 / 内嵌归档）。</description></item>
/// </list>
///
/// <para>⛔ 格式结论本身（<see cref="DetectResult.Format"/>）**不在这里改**：后续卷仍然是 Unknown，
/// 那才是识别层的诚实结论；这里只把状态与那句话换对。加密判据也不在这里另写一套 ——
/// 多卷 7z 的那一次重判走的仍是 <see cref="ArchiveEncryptionVerdict.Apply"/>。</para>
/// </summary>
internal static class ArchiveAdmissionEvaluator
{
    /// <summary>
    /// 识别并应用到任务（公开 API 的实现在这一层）。
    /// </summary>
    internal static async Task<DetectResult> ApplyAsync(
        ArchiveTask task,
        ArchiveContentIdentifier identifier,
        CancellationToken cancellationToken = default)
    {
        if (task == null)
        {
            return DetectResultFactory.CreateUnknown("任务为空");
        }

        task.Operation = StatusText.OpScan;
        task.Status = StatusText.Scanning;
        task.ProgressText = StatusText.ProgressProcessing;
        task.ErrorMessage = string.Empty;
        task.LastUpdatedTime = Clock.Now;

        DetectResult result = await identifier.IdentifyAsync(task.CurrentPath, cancellationToken);

        string currentExtension = ExtensionHelper.GetLastExtensionDisplay(task.CurrentPath);

        task.FileName = Path.GetFileName(task.CurrentPath);
        task.DirectoryPath = Path.GetDirectoryName(task.CurrentPath) ?? string.Empty;
        task.CurrentExtension = currentExtension;

        task.DetectedFormat = result.Format;
        task.SuggestedExtension = result.SuggestedExtension;
        task.ExtensionStatus = GetExtensionStatus(
            task.FileName,
            currentExtension,
            result);

        task.IsArchive = result.IsArchive;

        /*
         * 加密结论 = **第 1 卷（= task.CurrentPath）那一份的结论**，多卷 RAR 上也是这样。
         *
         * 为什么必须说清这一条：RAR 的主头与第一个文件头都在**第 1 卷**里，后续卷是数据的中段，
         * 直接读它必然得不出结论（RAR4 的续卷连主头都读不像、RAR5 的续卷是第一卷头链的续写）。
         * 所以判据只问 `task.CurrentPath`（分卷归组保证了它就是第一卷），
         * ⛔ **绝不去遍历 VolumePaths 再要求"每一卷都报加密"** ——
         * 那会把"续卷读不出来"当成"没加密"，整组加密包当场判否（用户 2026-09-29 明确点名的坑）。
         *
         * ===== 多卷 7z：只有它需要卷组信息（用户 2026-09-30）=====
         * 7z 分卷是**原样切**的（<c>-v100k</c> 切出来的每一卷就是整包的一段连续字节），
         * 元数据在**最后一卷** —— 所以"只看第 1 卷"读不出 7z 的加密（实测 vol-p.7z 的第 1 卷里
         * <c>32 + NextHeaderOffset + NextHeaderSize</c> 直接超出卷长，判据器如实报"不知道"）。
         *
         * 卷组信息**这时候已经填好了**（`FileScanService.ScanPathsAsync` 里
         * `VolumeGroupingService.ApplyVolumeGrouping` 在归组，`ScanCoordinator.ScanTasksAsync`
         * 之后才调识别），所以这里带卷组**再走一遍同一个出口** —— ⛔ 不为多卷另写一套判据，
         * 出口仍然只有 <see cref="ArchiveEncryptionVerdict.Apply"/> 一个方法。
         *
         * 单文件任务（卷组只有它自己）不重算：识别那一遍问的就是同一个问题，
         * 结果必然一样，白读一次没有意义。
         */
        if (result.IsArchive && task.VolumePaths.Count > 1)
        {
            result = ArchiveEncryptionVerdict.Apply(result, task.CurrentPath, 0, task.VolumePaths);
        }

        task.IsEncrypted = result.IsProbablyEncrypted;

        // 内嵌归档偏移必须落到任务上：解压管线靠它决定"要不要先抠出来"。
        // 终点（EOCD + 22 + 注释长度）同样要落：真实文件在 EOCD 之后还有十几 KB 正常数据，
        // 抠取按终点截断，不能拿"文件末尾"当终点。
        // 直读探查结论一并落下来：**只用于空间核算**（直读时那笔抠取副本不预留）。
        task.EmbeddedArchiveOffset = result.EmbeddedArchiveOffset;
        task.EmbeddedArchiveEnd = result.EmbeddedArchiveEnd;
        task.EmbeddedDirectReadSupported = result.EmbeddedDirectReadSupported;
        task.EmbeddedDirectReadReason = result.EmbeddedDirectReadReason ?? string.Empty;

        /*
         * 「修复分卷名并重试」那颗建议（用户 2026-09-25 第 41 条；**2026-09-26 审计改口径**）。
         *
         * 以前这里是无条件清空（那建议是"上一轮解压"的产物，重扫之后可能已经改好了），
         * 后果是那颗按钮**要先跑一次失败的解压才可能亮** —— 用户从没见过它亮过。
         *
         * 现在改成**扫描时就从文件系统事实算一遍**（判据是 VolumeNameRepair.Plan 那六条：
         * 源文件在 / 名字带卷号且是第 1 卷 / 同目录有后续卷 / 推得出标准名 / 现名不是标准名 /
         * 目标名没被占）——"名字被改坏的第一卷"这六条当场就成立，于是**扫完按钮就是亮的**；
         * 名字本来就标准、或者是后续卷、或者目标名被占时，算出来是空，按钮照旧熄灭
         * （问题解决了它自己灭，这条语义一个字没变）。
         */
        task.VolumeRenameSuggestion = ResolveVolumeRenameSuggestion(task.CurrentPath);

        if (result.IsArchive)
        {
            task.Status = StatusText.Recognized;
            task.Operation = StatusText.OpWaiting;
            task.ProgressText = StatusText.ProgressCompleted;
            task.ErrorMessage = string.Empty;
        }
        else
        {
            /*
             * 「列表里只有分卷组的后续卷」这一档（用户 2026-09-26 拍板："可以"）。
             *
             * 现场（真机代跑时撞到）：一组分卷的**第 1 卷没被加进来**（或名字被改坏、当时不在目录里），
             * 于是 `set.7z.002` 自己成了一个任务 —— 它的文件头里**没有归档魔数**（那只是数据流的中段），
             * 识别必然报「格式未知 / 未识别为支持的压缩格式」，用户看到会以为文件坏了；
             * 可它其实是一组分卷的后续卷，该做的是"把第 1 卷也加进来"。
             *
             * ⛔ 判据全在**名字**上（分卷号 ≥ 2 + 推得出标准首卷名），**不改格式结论**
             * （DetectedFormat 仍然是 Unknown —— 那是识别层的诚实结论），只把状态与那句话换对。
             */
            string laterVolumeNote = BuildLaterVolumeOnlyNote(task.CurrentPath);

            if (!string.IsNullOrWhiteSpace(laterVolumeNote))
            {
                task.Status = StatusText.VolumeMissing;
                task.Reason = TaskReason.VolumeMissing;
                task.Operation = StatusText.OpWaiting;
                task.ProgressText = StatusText.ProgressCompleted;
                task.ErrorMessage = laterVolumeNote;
            }
            else
            {
                task.Status = StatusText.UnknownFormat;
                task.Operation = StatusText.OpWaiting;
                task.ProgressText = StatusText.ProgressCompleted;
                task.ErrorMessage = result.Message;
            }
        }

        task.LastUpdatedTime = Clock.Now;

        return result;
    }

    /// <summary>
    /// 获取后缀状态。
    ///
    /// 规则：
    /// 如果是内嵌归档（尾部藏着 ZIP）：
    ///     内嵌归档（**这一条最先判**：这种文件不该改名）
    /// 如果 Unknown：
    ///     格式未知
    /// 如果没有后缀：
    ///     后缀缺失
    /// 如果最后后缀等于建议后缀：
    ///     后缀正常
    /// 如果存在多个后缀，并且其中一个后缀等于建议后缀，但最后后缀不等于建议后缀：
    ///     多重后缀疑似伪装
    /// 否则：
    ///     后缀不匹配
    /// </summary>
    internal static string GetExtensionStatus(
        string fileName,
        string currentExtension,
        DetectResult result)
    {
        /*
         * 内嵌归档排在最前面判，而且**优先于一切后缀结论**。
         *
         * 理由：这种文件"没有可修正的后缀"。它的 ZIP 内部偏移相对它自己，前置数据又远超
         * 7-Zip 的容忍上限（实测 8 MiB），所以改成 .zip 之后 7z 依然打不开；
         * 报成"后缀缺失 / 不匹配 / 多重伪装"都会把用户引向改名这条路 —— 那是一条死路。
         * 它该看到的是"内嵌归档：需要按偏移取出"，所以这一条必须在其它判断之前返回。
         */
        if (result != null && result.EmbeddedArchiveOffset > 0)
        {
            return StatusText.ExtensionEmbedded;
        }

        /*
         * ⛔ 跨盘 ZIP 的成员**永远不参与“按检测格式自动改后缀”**：
         * 首片必须保持 .z01 语义、末片才是 .zip；一旦把首片也改成 .zip，
         * 一组里会出现两个末片，组分析只能 Unknown（真机 940 批 7777/16161616）。
         * 槽位由第二大步组算法按内容/盘号统一命名，这里只负责“别动它”。
         */
        if (result != null
            && string.Equals(result.Format, "ZIP_SPANNED", StringComparison.OrdinalIgnoreCase))
        {
            return StatusText.ExtensionVolume;
        }

        /*
         * 分卷文件的"后缀"是 .001 / .z01 / .part1 这类卷标记，不是伪装，也不是漏写后缀。
         * 设计.md §七 要求把它单独归一类；报成"多重后缀疑似伪装"会误导用户去"修正"它，
         * 而修正分卷名会直接切断分卷链。
         *
         * ⚠ 这一条排在"格式未知"**之前**（用户 2026-09-26 拍板）：后续卷（`set.7z.002`）的文件头里
         * 没有归档魔数，识别必然说 Unknown —— 可它的**后缀**一点都不神秘，就是分卷标记。
         * 报成"格式未知"会把"缺首卷"这件事藏起来（状态那一列现在会说清，见 ApplyAsync）。
         */
        if (LegacyVolumeNames.IsVolumePart(fileName))
        {
            return StatusText.ExtensionVolume;
        }

        if (result == null || !result.IsArchive || !result.IsKnownFormat)
        {
            return StatusText.UnknownFormat;
        }

        string suggestedExtension = ExtensionHelper.NormalizeExtension(result.SuggestedExtension);

        if (string.IsNullOrWhiteSpace(suggestedExtension))
        {
            return StatusText.UnknownFormat;
        }

        if (string.IsNullOrWhiteSpace(currentExtension) ||
            currentExtension == "无")
        {
            return StatusText.ExtensionMissing;
        }

        currentExtension = ExtensionHelper.NormalizeExtension(currentExtension);

        /*
         * 本身就是 ZIP 容器的合法后缀（.apk / .jar / .docx / .xlsx / .epub …）：
         * 内容是 ZIP，但后缀本来就是对的 —— 不许判成"后缀不匹配"。
         * 否则「智能修正后缀」会把 xxx.apk 改成 xxx.zip，把安装包变成打不开的压缩包。
         */
        if (ArchiveFormatProbe.IsZipFamily(result.Format) &&
            ExtensionHelper.IsZipContainerExtension(currentExtension))
        {
            return StatusText.ExtensionNormal;
        }

        if (string.Equals(
                currentExtension,
                suggestedExtension,
                StringComparison.OrdinalIgnoreCase))
        {
            return StatusText.ExtensionNormal;
        }

        if (IsMultiExtensionSuspicious(fileName, suggestedExtension))
        {
            return StatusText.ExtensionMultiFake;
        }

        return StatusText.ExtensionMismatch;
    }

    /// <summary>
    /// 判断是否是多重后缀疑似伪装。
    /// 例如：
    /// 111.7z.jpg 检测为 7Z，则 true。
    /// 111.rar.pdf.jpg 检测为 RAR，则 true。
    /// </summary>
    internal static bool IsMultiExtensionSuspicious(
        string fileName,
        string suggestedExtension)
    {
        return FileNameHelper.IsMultiExtensionSuspicious(fileName, suggestedExtension);
    }

    /// <summary>
    /// 根据格式获取建议后缀。
    /// </summary>
    internal static string GetSuggestedExtension(string format)
    {
        return ExtensionHelper.GetSuggestedExtensionByFormat(format);
    }

    /// <summary>
    /// 「这一份只是分卷组的后续卷」时给用户的那句话（不是这种形状就返回空串）。
    ///
    /// <para>判据（全部来自名字 + 一次"同目录里在不在"的检查）：</para>
    /// <list type="number">
    /// <item><description>名字里带卷号、而且**不是第 1 卷**（第 1 卷没有魔数也一样能开，正是被这条排除的）；</description></item>
    /// <item><description>从它推得出**标准首卷名**（推不出就不猜，退回"格式未知"）。</description></item>
    /// </list>
    ///
    /// <para>句子分两档：首卷**就在同目录里** → 让用户把它也加进来；同目录也没有 → 如实说没找到。
    /// ⛔ 不说"文件损坏"，也不说"格式不支持" —— 那两句都会把人引到错的方向。</para>
    /// </summary>
    private static string BuildLaterVolumeOnlyNote(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            string fileName = Path.GetFileName(path);
            int? index = LegacyVolumeNames.TryGetIndex(fileName);

            if (index == null || index.Value < 2)
            {
                return string.Empty;
            }

            string? firstVolumeName = LegacyVolumeNames.TryGetFirstVolumeName(fileName);

            if (string.IsNullOrWhiteSpace(firstVolumeName))
            {
                return string.Empty;
            }

            string directory = Path.GetDirectoryName(path) ?? string.Empty;
            bool firstVolumeIsNextToIt = !string.IsNullOrWhiteSpace(directory)
                                         && File.Exists(Path.Combine(directory, firstVolumeName!));

            return string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.LaterVolumeOnlyFormat,
                fileName,
                index.Value,
                firstVolumeName,
                firstVolumeIsNextToIt
                    ? StatusText.LaterVolumeOnlyFirstVolumeHere
                    : StatusText.LaterVolumeOnlyFirstVolumeMissing);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 扫描期算一次"这一卷的名字要不要修"（见 <see cref="ApplyAsync"/> 里那段注释）。
    ///
    /// <para>只有"第 1 卷"才可能修，所以先用卷号挡一道（省掉绝大多数任务的目录枚举）；
    /// 任何 IO 意外都落成空串（= 按钮不亮），⛔ 绝不抛。</para>
    /// </summary>
    private static string ResolveVolumeRenameSuggestion(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            string fileName = Path.GetFileName(path);

            if (LegacyVolumeNames.TryGetIndex(fileName) != 1)
            {
                return string.Empty;
            }

            VolumeNameRepairPlan plan = VolumeNameRepair.Plan(
                path,
                VolumeNameRepair.EnumerateFileNamesInDirectory(path));

            return plan.CanRepair ? plan.SuggestedFileName : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}

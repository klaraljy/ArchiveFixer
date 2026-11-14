using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Engines.SevenZip;

/// <summary>
/// 7-Zip 的**错误分类与结果成形**这一块：退出码 + 两路输出 → <see cref="ArchiveOperationResult"/>，
/// 以及"测试 / 解压"两种操作在结果上的最终修饰。
///
/// <para>这里的判定顺序是既有口径，不要重排：先判成功（只有退出码 0），再判错误类型
/// （<see cref="SevenZipOutputParser"/>，纯文本层），最后做需要文件系统的二次判定
/// （缺卷 / 缺首卷，见 <see cref="ResolveVolumeMissingErrorType"/>）。</para>
///
/// <para>⛔ 7-Zip 文本解析、退出码映射、错误类型字符串只允许待在本命名空间；
/// 本类不拼参数、不起进程、不读管道。</para>
/// </summary>
internal static class SevenZipResultClassifier
{
    internal static ArchiveOperationResult AnalyzeResult(int exitCode, string output, string error)
    {
        return AnalyzeResult(exitCode, output, error, string.Empty, TimeSpan.Zero);
    }

    internal static ArchiveOperationResult AnalyzeResult(
        int exitCode,
        string output,
        string error,
        string usedPassword,
        TimeSpan elapsed)
    {
        return AnalyzeResult(exitCode, output, error, usedPassword, elapsed, archivePath: null);
    }

    /// <summary>
    /// 分析一次 7z 调用的结果。
    ///
    /// <paramref name="archivePath"/> 非空时，分卷缺失会被判得更准（见
    /// <see cref="ResolveVolumeMissingErrorType"/>）：只给 <c>xxx.7z.002</c> 时 7-Zip 报的是
    /// "Cannot open the file as archive"，字面与"这不是归档"一样，只能靠文件名与目录内容区分。
    ///
    /// <paramref name="operation"/> 说明这次跑的是哪个命令（l / t / x）：<b>只有列目录</b>才允许
    /// 下"加密了文件名"的结论（见 <see cref="SevenZipOutputParser.LooksLikeEncryptedHeaders"/>）。
    /// </summary>
    internal static ArchiveOperationResult AnalyzeResult(
        int exitCode,
        string output,
        string error,
        string usedPassword,
        TimeSpan elapsed,
        string? archivePath,
        EngineOperation? operation = null,
        IEnumerable<string>? arguments = null)
    {
        output = PasswordMasker.Sanitize(output);
        error = PasswordMasker.Sanitize(error);

        string combined = SevenZipOutputParser.CombineOutput(output, error);
        bool success = SevenZipOutputParser.LooksLikeSuccess(exitCode, output, error);

        string errorType = success
            ? "None"
            : SevenZipOutputParser.DetectSevenZipErrorType(exitCode, output, error, archivePath, operation);

        errorType = ResolveVolumeMissingErrorType(errorType, archivePath);

        string status = SevenZipOutputParser.ErrorTypeToTaskStatus(errorType);

        string message = success
            ? "操作成功"
            : errorType switch
            {
                SevenZipOutputParser.MissingFirstVolumeErrorType => SevenZipOutputParser.ErrorTypeToMessage(errorType),
                "VolumeMissing" => BuildVolumeMissingMessage(archivePath),
                _ => SevenZipOutputParser.ErrorTypeToMessage(errorType, combined)
            };

        return new ArchiveOperationResult
        {
            Success = success,
            ExitCode = exitCode,
            StandardOutput = output,
            StandardError = error,
            Status = status,
            Message = message,
            DetectedErrorType = errorType,
            UsedPasswordMasked = PasswordMasker.Mask(usedPassword),
            Elapsed = elapsed,
            CommandSummary = SevenZipCommandLine.BuildCommandSummary(arguments),

            /*
             * 「哪些条目坏了」与"引擎自报坏了几个" —— 部分完成发布那块功能的地基（见
             * ArchiveOperationResult.FailedEntryNames 的说明）。原文已经脱敏，解析在引擎目录内完成。
             */
            FailedEntryNames = SevenZipOutputParser.ExtractFailedEntryNames(combined),
            ReportedSubItemErrors = SevenZipOutputParser.ExtractSubItemErrorCount(combined)
        };
    }

    /// <summary>工具不存在时的结果（路径由 <see cref="ToolLocator"/> 解析，⛔ 不在这里拼、不猜安装目录）。</summary>
    internal static ArchiveOperationResult CreateEngineMissingResult(string sevenZipPath)
    {
        return new ArchiveOperationResult
        {
            Success = false,
            ExitCode = -1,
            StandardOutput = string.Empty,
            StandardError = string.Empty,
            Status = StatusText.SevenZipMissing,
            Message = $"未找到 7-Zip 程序：{sevenZipPath}",
            DetectedErrorType = "SevenZipMissing",
            UsedPasswordMasked = string.Empty,
            Elapsed = TimeSpan.Zero
        };
    }

    /// <summary><c>Process.Start()</c> 返回 false 时的结果。</summary>
    internal static ArchiveOperationResult CreateStartFailureResult(TimeSpan elapsed, string usedPassword)
    {
        return ArchiveOperationResult.CreateFailure(
            -1,
            string.Empty,
            string.Empty,
            StatusText.ExtractFailed,
            "无法启动 7-Zip",
            "UnknownError",
            elapsed,
            PasswordMasker.Mask(usedPassword));
    }

    /// <summary>
    /// 运行器自己抛出异常时的结果：把异常文本并进 stderr，再按**同一个分类出口**归类。
    /// <paramref name="output"/> / <paramref name="errorText"/> 传进来时已经脱敏（两路缓冲区读出的就是脱敏后的）。
    /// </summary>
    internal static ArchiveOperationResult CreateExceptionResult(
        int exitCode,
        string output,
        string errorText,
        string usedPassword,
        TimeSpan elapsed)
    {
        string combined = SevenZipOutputParser.CombineOutput(output, errorText);
        string detectedErrorType = SevenZipOutputParser.DetectSevenZipErrorType(exitCode, output, errorText);

        return new ArchiveOperationResult
        {
            Success = false,
            ExitCode = exitCode,
            StandardOutput = PasswordMasker.Sanitize(output),
            StandardError = PasswordMasker.Sanitize(errorText),
            Status = SevenZipOutputParser.ErrorTypeToTaskStatus(detectedErrorType),
            Message = SevenZipOutputParser.ErrorTypeToMessage(detectedErrorType, combined),
            DetectedErrorType = detectedErrorType,
            UsedPasswordMasked = PasswordMasker.Mask(usedPassword),
            Elapsed = elapsed
        };
    }

    /// <summary>
    /// 测试操作在通用结果上的最终修饰（成功文案、错误类型二次判定、缺卷清单）。
    ///
    /// 这是 <c>TestArchiveAsync</c> 的既有口径：<c>ExtractSuccess</c> 映射成"测试通过"，
    /// <c>ExtractFailed</c> 映射成"测试失败"，其余状态原样透传。
    /// </summary>
    internal static void ApplyTestOutcome(
        ArchiveOperationResult result,
        string archivePath,
        IEnumerable<string> arguments)
    {
        if (result.Success)
        {
            result.Status = StatusText.TestPassed;
            result.Message = StatusText.TestPassed;
            result.DetectedErrorType = "None";
            return;
        }

        result.DetectedErrorType = ResolveVolumeMissingErrorType(
            result.DetectedErrorType,
            archivePath,
            arguments);

        string mappedStatus = SevenZipOutputParser.ErrorTypeToTaskStatus(result.DetectedErrorType);

        result.Status = mappedStatus switch
        {
            StatusText.ExtractSuccess => StatusText.TestPassed,
            StatusText.ExtractFailed => StatusText.TestFailed,
            _ => mappedStatus
        };

        if (result.DetectedErrorType == "VolumeMissing")
        {
            /*
             * 这个结果不是"AnalyzeResult 直接产出"的（上面刚改过分类），
             * 所以"缺哪几卷"要在这里补上，否则用户看到的还是那句笼统的"缺少必要分卷"。
             */
            result.Message = BuildVolumeMissingMessage(archivePath);
        }
        else if (string.IsNullOrWhiteSpace(result.Message) ||
            result.Message == "操作失败" ||
            result.Message == "未知错误，请查看日志")
        {
            result.Message = SevenZipOutputParser.ErrorTypeToMessage(
                result.DetectedErrorType,
                result.CombinedOutput);
        }
    }

    /// <summary>解压操作在通用结果上的最终修饰（成功文案 / 错误状态 / 兜底文案）。</summary>
    internal static void ApplyExtractOutcome(ArchiveOperationResult result)
    {
        if (result.Success)
        {
            result.Status = StatusText.ExtractSuccess;
            result.Message = StatusText.ExtractSuccess;
            result.DetectedErrorType = "None";
            return;
        }

        result.Status = SevenZipOutputParser.ErrorTypeToTaskStatus(result.DetectedErrorType);

        if (string.IsNullOrWhiteSpace(result.Message) ||
            result.Message == "操作失败" ||
            result.Message == "未知错误，请查看日志")
        {
            result.Message = SevenZipOutputParser.ErrorTypeToMessage(
                result.DetectedErrorType,
                result.CombinedOutput);
        }
    }

    /// <summary>
    /// 把"字段上像'非归档'"的结果修正成缺卷/缺首卷（不变量 7）。
    ///
    /// 为什么需要二次判定：7-Zip 对"只给非首卷"和"给了一个纯文本文件"报的是同一句
    /// <c>Cannot open the file as archive</c>（26.01 实测，退出码 2），
    /// 纯关键字分类一定会把缺卷误报成"不支持该格式"，用户就会以为文件坏了。
    /// 唯一可靠的判据是**文件名 + 同目录里有没有这一组的其它卷**：
    /// 目录里数出缺号 → 缺卷；数不出缺号但文件本身就叫 .001/.002/… → 缺首卷。
    ///
    /// <para>⚠ 2026-10-05：入口那一档已经**在纯文本分类里就分好**了
    /// （<see cref="SevenZipOutputParser.LooksLikeMissingVolumePart"/> 只认卷号 ≥ 2；
    /// 卷号 == 1 由分类器直接返 <c>"VolumeMissing"</c>，真机 `HK.7z.001` 报"缺少首卷"就是说反了）。
    /// 本方法因此**照旧自洽**：<c>"VolumeMissing"</c> 在第一行原样返回，
    /// <c>MissingFirstVolume</c> 仍走下面的"数缺号"二次判定；两个码对上层是同一类
    /// （<c>ExtractionCoordinator.IsVolumeMissingErrorType</c> 两个都收）。</para>
    /// </summary>
    internal static string ResolveVolumeMissingErrorType(
        string errorType,
        string? archivePath,
        IEnumerable<string>? arguments = null)
    {
        if (string.Equals(errorType, "VolumeMissing", StringComparison.Ordinal))
        {
            /*
             * 2026-11-05 真机 70 份：中间卷被截断，文件名仍是完整组（.001/.002/.003 都在），
             * 7-Zip 报“Cannot open the file as [7z] archive”，文本分类先命中了
             * “Can not open file + 路径里的 .001” ⇒ 误判“分卷缺失”。
             *
             * 只有第 1 卷、同目录同基名的分卷片至少 2 片、且数不出缺号时，
             * 才能确认“组齐了但打不开” ⇒ 落损坏，不再让用户去补不存在的卷。
             * 卷号 ≥2、缺号非空、没兄弟卷都保持原分类。
             */
            string? firstPath = ResolveArchivePath(archivePath, arguments);

            /*
             * 单文件、文件名也没有任何分卷槽，内容卡却证明它是 SingleComplete /
             * ProvablyNotVolumeMember ⇒ 引擎文字里的 VolumeMissing 不成立，
             * 真原因是数据层失败（ZIP CRC 损坏现场：7z 报 CRC Failed，但文本分类先
             * 命中了 VolumeMissing）。⛔ 只否证“内容已证明不是分卷成员”这一种；
             * 真分卷片 / 判不出内容卡时保持原分类。
             */
            if (!string.IsNullOrWhiteSpace(firstPath))
            {
                FileNatureCard card = FileNature.Inspect(firstPath);

                /*
                 * 以**内容卡**为准，不再先看文件名：ZIP 入口/跨盘末片本身就叫 .zip，
                 * 名字槽可能也返回一个卷号；唯一可用的是内容层的明确否证：
                 * “可证不是分卷组的一员” ⇒ 引擎文字里的 VolumeMissing 不成立。
                 */
                if (card.Membership == VolumeNumberFromContent.VolumeMembershipVerdict.ProvablyNotVolumeMember)
                {
                    return "CorruptedArchive";
                }
            }

            if (IsCompleteVolumeGroup(firstPath))
            {
                return "CorruptedArchive";
            }

            return errorType;
        }

        /*
         * 纯文本分类（没有文件系统可用）只会说"这是分卷，少了首卷"。
         * 走到这里说明我们在运行器里、磁盘就在手边 —— 那就去数一遍缺哪几卷，
         * 把结论落到上层认识的 VolumeMissing 上（不变量 7 要的是"缺哪几个"）。
         */
        if (string.Equals(errorType, SevenZipOutputParser.MissingFirstVolumeErrorType, StringComparison.Ordinal))
        {
            string? volumePath = ResolveArchivePath(archivePath, arguments);

            return string.IsNullOrWhiteSpace(volumePath) || FindMissingVolumeParts(volumePath).Count == 0
                ? errorType
                : "VolumeMissing";
        }

        /*
         * 只在"看起来像打不开这个文件"的分类上做二次判定。
         * 密码错误、权限不足、磁盘满这些各有明确原因，不能被这条吞掉。
         */
        if (!string.Equals(errorType, "UnsupportedFormat", StringComparison.Ordinal) &&
            !string.Equals(errorType, "CorruptedArchive", StringComparison.Ordinal) &&
            !string.Equals(errorType, "UnknownError", StringComparison.Ordinal))
        {
            return errorType;
        }

        string? path = ResolveArchivePath(archivePath, arguments);

        if (string.IsNullOrWhiteSpace(path))
        {
            return errorType;
        }

        string fileName = Path.GetFileName(path);

        if (LegacyVolumeNames.TryGetIndex(fileName) == null)
        {
            // 文件名不是分卷 → 原来的分类是对的（纯文本改名成 .7z 走的就是这一支）。
            return errorType;
        }

        /*
         * 这个文件本身就是分卷名：
         * 同目录里能数出缺号 → 缺卷（消息里会列出缺哪几个）；
         * 数不出缺号 → 首卷不在同目录（用户的真实现场：两个 mp4 各藏一段分卷，解到两个目录里去了），
         * 消息里点名首卷名 —— 分卷总数在命名里没有答案，不编造。
         *
         * 对外统一报 VolumeMissing：它已经映射到"分卷缺失"状态与正确的用户动作，
         * 不新造一个上层代码不认识的状态码。MissingFirstVolume 只留给
         * 纯文本分类（DetectSevenZipErrorType）那一条没有文件系统可查的路径。
         */
        return "VolumeMissing";
    }

    /// <summary>
    /// "缺哪几个卷"是用户在缺卷时唯一能行动的信息（不变量 7），所以这句提示要去磁盘上数一遍：
    /// 已经找到哪几卷、按命名推出来的缺号是哪些。数不出来时退回通用文案，绝不编造名字。
    /// </summary>
    private static string BuildVolumeMissingMessage(string? archivePath)
    {
        List<string> missing = FindMissingVolumeParts(archivePath);

        if (missing.Count == 0)
        {
            return SevenZipOutputParser.ErrorTypeToMessage("VolumeMissing");
        }

        return $"分卷压缩包缺少必要分卷，缺少：{string.Join('、', missing)}" +
               "。请把同一组分卷放在同一目录后重试。";
    }

    /// <summary>
    /// 拿这次调用真正用的归档路径：优先用调用方直接给的，没有才去参数表里翻。
    /// </summary>
    private static string? ResolveArchivePath(string? archivePath, IEnumerable<string>? arguments)
    {
        return string.IsNullOrWhiteSpace(archivePath)
            ? SevenZipCommandLine.FindArchiveArgument(arguments)
            : archivePath;
    }

    private static bool IsCompleteVolumeGroup(string? archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath)
            || LegacyVolumeNames.TryGetIndex(Path.GetFileName(archivePath)) != 1)
        {
            return false;
        }

        try
        {
            string? directory = Path.GetDirectoryName(archivePath);

            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return false;
            }

            string baseName = FileNameHelper.SecondStepBaseName(Path.GetFileName(archivePath));
            int siblings = 0;

            foreach (string file in Directory.EnumerateFiles(directory))
            {
                string name = Path.GetFileName(file);

                if (LegacyVolumeNames.TryGetIndex(name) == null)
                {
                    continue;
                }

                if (string.Equals(
                        FileNameHelper.SecondStepBaseName(name),
                        baseName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    siblings++;
                }
            }

            if (siblings < 2)
            {
                return false;
            }

            /*
             * 7z 首卷自述“整包多少片”时，片数是比“缺号清单”更强的证据：
             * - 实到片数 < 自述片数 ⇒ 真缺卷（例如缺最后一卷，名字清单看不出缺号）；
             * - 实到片数 == 自述片数 ⇒ 组齐了但字节数/内容对不上 ⇒ 损坏。
             * 自述片数读不到时，退回原来的“无缺号”判据。
             */
            FileNatureCard card = FileNature.Inspect(archivePath);

            if (card.DeclaredVolumeCount > 0)
            {
                return siblings >= card.DeclaredVolumeCount;
            }

            if (card.DeclaredTotalBytes > 0)
            {
                string[] pieces = Directory
                    .EnumerateFiles(directory)
                    .Where(file => string.Equals(
                        FileNameHelper.SecondStepBaseName(Path.GetFileName(file)),
                        baseName,
                        StringComparison.OrdinalIgnoreCase))
                    .Where(file => LegacyVolumeNames.TryGetIndex(Path.GetFileName(file)) != null)
                    .OrderBy(file => LegacyVolumeNames.TryGetIndex(Path.GetFileName(file)) ?? int.MaxValue)
                    .ToArray();

                long sum = pieces.Sum(file => new FileInfo(file).Length);
                long firstSize = new FileInfo(pieces[0]).Length;
                long lastSize = new FileInfo(pieces[^1]).Length;

                if (sum >= card.DeclaredTotalBytes)
                {
                    // 字节数对得上 ⇒ 组齐了；打不开就是内容损坏。
                    return true;
                }

                /*
                 * 字节数不够：末片仍是满片大小 ⇒ 后面还有没到齐的卷 ⇒ 缺卷；
                 * 末片已经小于首片 ⇒ 这是正常的末片被截断 ⇒ 组齐了但内容损坏。
                 */
                return lastSize < firstSize;
            }

            return FindMissingVolumeParts(archivePath).Count == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 在归档所在目录里找分卷缺号。
    ///
    /// 这些命名（.001 / .z01 / .r00 / .partN）**都没有"共几卷"这个信息**，
    /// 所以只能报 [第 1 卷, 命中过的最大卷号] 之间的缺号；最大卷号之后再有没有卷，
    /// 命名里没有答案，不猜（与 VolumeGroupDetector 的口径一致）。
    /// </summary>
    private static List<string> FindMissingVolumeParts(string? archivePath)
    {
        var missing = new List<string>();

        if (string.IsNullOrWhiteSpace(archivePath))
        {
            return missing;
        }

        try
        {
            string directory = Path.GetDirectoryName(archivePath) ?? string.Empty;

            if (directory.Length == 0 || !Directory.Exists(directory))
            {
                return missing;
            }

            // 只扫同目录，不递归、不跟链接：与"一组分卷 = 一个任务"的口径一致。
            var candidates = new List<VolumeCandidate>();
            var foundInline = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string archiveFileName = Path.GetFileName(archivePath);

            foreach (string file in Directory.EnumerateFiles(directory))
            {
                candidates.Add(new VolumeCandidate { Path = file });
            }

            foreach (VolumeGroup group in VolumeGroupDetector.Group(candidates))
            {
                if (!group.Volumes.Any(volume => SafePathHelper.PathEquals(volume.Path, archivePath)))
                {
                    continue;
                }

                /*
                 * 首卷不在时**必须单独点名**，而且名字要跟用户手上的文件同风格：
                 * 用 VolumeGroupDetector 按"基名 + 卷号"补出来的名字是 volume.001，
                 * 而用户磁盘上那一组叫 volume.7z.001 —— 报一个用户找不到的名字等于没报。
                 * 所以缺第 1 卷时，名字取"当前这个分卷的文件名 + 推出来的首卷名"。
                 */
                if (!string.Equals(
                        SafePathHelper.GetFullPathSafe(group.FirstVolumePath),
                        SafePathHelper.GetFullPathSafe(archivePath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    string? firstVolumeName = LegacyVolumeNames.TryGetFirstVolumeName(archiveFileName);

                    if (!string.IsNullOrWhiteSpace(firstVolumeName))
                    {
                        int suffixIndex = archiveFileName.Length - Path.GetExtension(archiveFileName).Length;
                        string styledName = string.Equals(
                                Path.GetExtension(firstVolumeName),
                                Path.GetExtension(archiveFileName),
                                StringComparison.OrdinalIgnoreCase)
                            ? firstVolumeName
                            : (suffixIndex > 0 ? archiveFileName[..suffixIndex] : archiveFileName) + firstVolumeName;

                        if (foundInline.Add(styledName))
                        {
                            missing.Add(styledName);
                        }
                    }
                }

                foreach (string name in group.MissingVolumeNames)
                {
                    if (foundInline.Add(name))
                    {
                        missing.Add(name);
                    }
                }

                break;
            }
        }
        catch
        {
            // 数不出来就退回通用文案：报"缺卷"这件事本身已经做对了，清单是加分项。
        }

        return missing;
    }
}

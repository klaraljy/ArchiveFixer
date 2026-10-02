using ArchiveFixer.Models;
using ArchiveFixer.Password;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ArchiveFixer.Engines
{
    /// <summary>
    /// 引擎操作的统一结果（成功/失败、可读原因、错误分类、耗时）。
    ///
    /// 为什么放在 Engines 而不是 Models：
    /// 它是"引擎调用"的返回类型，GUI / 调度 / 递归只应该看到这个，而不是 7-Zip 的私有结果对象。
    /// 新增引擎时复用同一个结果类型，核心模块就不用改。
    ///
    /// 注意：<see cref="UsedPasswordMasked"/> 只允许放脱敏后的占位符，**绝不能放明文密码**。
    /// </summary>
    public class ArchiveOperationResult
    {
        public bool Success { get; set; }

        public int ExitCode { get; set; }

        public string StandardOutput { get; set; } = string.Empty;

        public string StandardError { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public string Message { get; set; } = string.Empty;

        public string DetectedErrorType { get; set; } = "None";

        public string UsedPasswordMasked { get; set; } = string.Empty;

        /// <summary>
        /// **这次到底是哪个引擎干的活**（不变量 14：结果必须能追到具体任务与具体引擎）。
        ///
        /// <para>
        /// 为什么要落在结果对象上，而不是"报告时再去问一遍注册表"：
        /// 报告那一刻的注册表可能已经变了（用户改了优先级、卸载了 WinRAR、换了自选路径），
        /// 而"这个包是被谁解开的"是**已经发生的事实**。由真正执行的那个引擎在返回前盖戳，
        /// 才是可信的溯源；失败清单与正常路径读的是同一份信息。
        /// </para>
        ///
        /// <para>两个字段由引擎自己填（见 <c>SevenZipEngine</c> / <c>UnRarEngine</c>），
        /// 手写 <c>ArchiveOperationResult</c> 的地方（含测试的假引擎）留空即为"未知引擎"。</para>
        /// </summary>
        public string EngineId { get; set; } = string.Empty;

        /// <summary>执行这次操作的引擎版本；取不到时是 <c>unknown</c>，**不编造**。</summary>
        public string EngineVersion { get; set; } = string.Empty;

        /// <summary>执行这次操作的引擎显示名（报告里用；为空时不显示名字）。</summary>
        public string EngineDisplayName { get; set; } = string.Empty;

        /// <summary>把三个引擎字段整理成报告口径的 <see cref="EngineIdentity"/>。</summary>
        public EngineIdentity ToEngineIdentity()
        {
            if (string.IsNullOrWhiteSpace(EngineId))
            {
                return EngineIdentityResolver.Unavailable;
            }

            return new EngineIdentity
            {
                EngineId = EngineId,
                DisplayName = EngineDisplayName ?? string.Empty,
                Version = EngineVersion ?? string.Empty,
                IsAvailable = true
            };
        }

        /// <summary>把"这次是哪个引擎干的"盖到结果上（引擎在返回前调用一次）。</summary>
        public ArchiveOperationResult StampEngine(string? engineId, string? displayName, string? version)
        {
            EngineId = engineId ?? string.Empty;
            EngineDisplayName = displayName ?? string.Empty;
            EngineVersion = version ?? string.Empty;

            return this;
        }

        public TimeSpan Elapsed { get; set; } = TimeSpan.Zero;

        public string CombinedOutput
        {
            get
            {
                if (string.IsNullOrWhiteSpace(StandardOutput))
                {
                    return StandardError ?? string.Empty;
                }

                if (string.IsNullOrWhiteSpace(StandardError))
                {
                    return StandardOutput ?? string.Empty;
                }

                return StandardOutput + Environment.NewLine + StandardError;
            }
        }

        public bool IsWrongPassword => DetectedErrorType == "WrongPassword";

        /// <summary>
        /// **这次引擎调用跑了什么命令**（已脱敏的一句摘要，例：<c>7z x -y -sccUTF-8 -p****** &lt;归档&gt;</c>）。
        ///
        /// <para>为什么要有它（用户 2026-09-27：「开了更详细的日志选项怎么还是这么简单」）：
        /// 详细日志要能回答"程序到底拿什么参数去调的引擎"，而参数表只有运行器见过 ——
        /// 它是一次调用的**事实**，不该在别处再拼一遍（§9.5 同一件事只有一个出口）。</para>
        ///
        /// <para>⚠ 只允许放**脱敏后**的文本：<c>-p&lt;明文&gt;</c> 必须已经是 <c>-p******</c>
        /// （不变量 5：密码进进程命令行是 7z 的固有限制，但进日志是绝对禁止的）。</para>
        /// </summary>
        public string CommandSummary { get; set; } = string.Empty;

        /// <summary>日志里"引擎原话"那一行的前缀（例：<c>7-Zip 原话</c>）。</summary>
        public string EngineOutputLabel => EngineId switch
        {
            EngineIds.SevenZip => "7-Zip 原话",
            EngineIds.WinRar => "UnRAR 原话",
            _ => "引擎原话"
        };

        /// <summary>
        /// 失败时挑出来的**引擎原话**（最多 <see cref="KeyOutputMaxLines"/> 行，每行截断
        /// <see cref="KeyOutputMaxLineLength"/> 字符，已脱敏）。
        ///
        /// <para><b>这是"引擎原话落日志"的唯一出口</b>：单层路径（<c>ExtractionCoordinator</c>）
        /// 与递归路径（<c>RecursiveExtractor</c>）都只调这一个方法，⛔ 谁都不许自己再挑一遍。
        /// 挑行规则在 <see cref="EngineOutputKeywords"/>（与结论里那几行**同一份**排序）。</para>
        ///
        /// <para>⛔ 绝不返回整段 stdout：那是噪声，而且可能带用户名路径。</para>
        /// </summary>
        public IReadOnlyList<string> KeyOutputLines() => BuildKeyOutputLines(CombinedOutput, EngineId);

        /// <summary>同一个出口的静态形态（测试与"结果对象还没造出来"的场合用）。</summary>
        public static IReadOnlyList<string> BuildKeyOutputLines(string? combinedOutput, string? engineId)
        {
            string[][] buckets = engineId switch
            {
                EngineIds.WinRar => UnRarKeywords.Buckets,
                _ => SevenZipKeywords.Buckets
            };

            return EngineOutputKeywords
                .PickImportantLines(PasswordMasker.Sanitize(combinedOutput), KeyOutputMaxLines, buckets)
                .Select(TruncateKeyLine)
                .ToList();
        }

        /// <summary>原话那一行最多留多少个字符（超了截断并加省略号 —— 一行日志不该上千字符）。</summary>
        public const int KeyOutputMaxLineLength = 300;

        /// <summary>原话最多留几行。</summary>
        public const int KeyOutputMaxLines = 3;

        /// <summary>
        /// 日志里那几行原话用什么连。
        /// ⚠ 必须与结论里那几行（<c>SevenZipOutputParser.ImportantMessageSeparator</c> /
        /// <c>UnRarOutputParser.ImportantMessageSeparator</c>）**同一个字符**：
        /// 同一件事在日志与结论里长得不一样，用户会以为是两件事。
        /// </summary>
        public const string KeyOutputLineSeparator = " ｜ ";

        private static string TruncateKeyLine(string line)
        {
            if (line.Length <= KeyOutputMaxLineLength)
            {
                return line;
            }

            return line[..KeyOutputMaxLineLength] + "…";
        }

        /// <summary>
        /// 这次是"部分完成"：解出来了一部分，但绝不是成功（AGENTS.md §6 第 6 条）。
        ///
        /// 7-Zip 在"一部分文件解出来、一部分失败"时给的是退出码 1（非致命错误），
        /// 这一条就是它落到调用方手里的判定口 —— 界面上"成功"的那一格永远不许吃到它。
        /// 判定用的是状态字符串（不是 7z 的错误类型名）：这样换引擎也不会漏判。
        /// 退出码 → 错误类型 → 状态的映射只在 <c>Engines/SevenZip/</c> 里做（§3.1 禁止项②的边界）。
        /// </summary>
        public bool IsPartiallyCompleted => !Success && Status == StatusText.PartiallyCompleted;

        public bool IsNeedPassword => DetectedErrorType == "NeedPassword";

        public bool IsCorrupted => DetectedErrorType == "CorruptedArchive";

        /// <summary>
        /// **两义那一档**：引擎同一句话里既说"密码不对"又说"数据坏了"（RAR 1.5–4.x 的加密包）。
        ///
        /// <para>候选循环见到它必须**继续试下一个候选**（与 <see cref="IsWrongPassword"/> 同一处置，
        /// 与 7-Zip 侧那句 <c>CRC Failed in encrypted file. Wrong password?</c> 同一口径），
        /// ⛔ 绝不像 <see cref="IsCorrupted"/> 那样当场停下（那会让正确的密码候选永远没机会被试）。</para>
        /// </summary>
        public bool IsPasswordOrCorrupted => DetectedErrorType == "PasswordOrCorrupted";

        public bool IsSevenZipMissing => DetectedErrorType == "SevenZipMissing";

        public bool IsCancelled => DetectedErrorType == "Cancelled";

        public bool IsTimedOut => DetectedErrorType == "TimedOut";

        /// <summary>
        /// 引擎**点名**的坏条目（包内相对路径；点不出名时是空集合）。
        ///
        /// <para><b>为什么要结构化这一份</b>（「部分完成也把已解出的内容放进目标目录」那块功能的地基）：
        /// 引擎解一个包解到一半失败时，退出码只说"有错"，**说清"哪一个条目坏了"的只有它打的那几行字**；
        /// 而"哪些文件敢发布"必须逐条回答，所以要在引擎目录内（⛔ 只有那里允许解析引擎文本）把它读成数据。
        /// 实测形状（本机 7-Zip 26.03 / UnRAR 7.23 真样本，见两个解析器的注释）：
        /// <c>ERROR: Data Error : good.bin</c>、<c>bad.bin  -  checksum error</c>。</para>
        ///
        /// <para>⛔ <b>不许拿它单独当"可以发布"的依据</b>：中文版 UnRAR 打的是中文，这里会是空的 ——
        /// 调用方必须拿"清单 vs 盘上实际"逐条对账（缺 / 大小不符），并用
        /// <see cref="ReportedSubItemErrors"/> 做闸门（自报有错却点不出名 ⇒ 什么都不发布）。</para>
        ///
        /// <para>默认空集合：别的引擎与手写的结果对象不填这一位，行为与从前逐字相同。</para>
        /// </summary>
        public IReadOnlyList<string> FailedEntryNames { get; set; } = Array.Empty<string>();

        /// <summary>
        /// 引擎**自报**的"出错的子项数"（7-Zip 的 <c>Sub items Errors: N</c> / UnRAR 的 <c>Total errors: N</c>）。
        ///
        /// <para>它是**闸门不是清单**：拿不到某几个坏条目的名字时，靠它知道"确实还有坏东西没点名"
        /// ⇒ 那一档一律**不发布**（判不出 ⇒ 什么都不做）。<c>0</c> = 没自报或确实没有错误。</para>
        /// </summary>
        public int ReportedSubItemErrors { get; set; }

        public static ArchiveOperationResult CreateSuccess(
            int exitCode,
            string output,
            string error,
            TimeSpan elapsed,
            string usedPasswordMasked = "")
        {
            return new ArchiveOperationResult
            {
                Success = true,
                ExitCode = exitCode,
                StandardOutput = output ?? string.Empty,
                StandardError = error ?? string.Empty,
                Status = StatusText.Success,
                Message = "操作成功",
                DetectedErrorType = "None",
                UsedPasswordMasked = usedPasswordMasked ?? string.Empty,
                Elapsed = elapsed
            };
        }

        public static ArchiveOperationResult CreateFailure(
            int exitCode,
            string output,
            string error,
            string status,
            string message,
            string detectedErrorType,
            TimeSpan elapsed,
            string usedPasswordMasked = "")
        {
            return new ArchiveOperationResult
            {
                Success = false,
                ExitCode = exitCode,
                StandardOutput = output ?? string.Empty,
                StandardError = error ?? string.Empty,
                Status = string.IsNullOrWhiteSpace(status) ? StatusText.ProgressFailed : status,
                Message = message ?? string.Empty,
                DetectedErrorType = string.IsNullOrWhiteSpace(detectedErrorType)
                    ? "UnknownError"
                    : detectedErrorType,
                UsedPasswordMasked = usedPasswordMasked ?? string.Empty,
                Elapsed = elapsed
            };
        }

        public static ArchiveOperationResult CreateEngineMissing(string path)
        {
            return new ArchiveOperationResult
            {
                Success = false,
                ExitCode = -1,
                StandardOutput = string.Empty,
                StandardError = string.Empty,
                Status = StatusText.SevenZipMissing,
                Message = $"未找到 7-Zip 程序：{path}",
                DetectedErrorType = "SevenZipMissing",
                UsedPasswordMasked = string.Empty,
                Elapsed = TimeSpan.Zero
            };
        }

        public static ArchiveOperationResult CreateCancelled(TimeSpan elapsed)
        {
            return new ArchiveOperationResult
            {
                Success = false,
                ExitCode = 255,
                StandardOutput = string.Empty,
                StandardError = string.Empty,
                Status = StatusText.Cancelled,
                Message = "用户取消当前任务",
                DetectedErrorType = "Cancelled",
                UsedPasswordMasked = string.Empty,
                Elapsed = elapsed
            };
        }

        public static ArchiveOperationResult CreateTimedOut(TimeSpan elapsed)
        {
            return new ArchiveOperationResult
            {
                Success = false,
                ExitCode = -3,
                StandardOutput = string.Empty,
                StandardError = string.Empty,
                Status = StatusText.UnknownError,
                Message = "7-Zip 执行超时",
                DetectedErrorType = "TimedOut",
                UsedPasswordMasked = string.Empty,
                Elapsed = elapsed
            };
        }

        public override string ToString()
        {
            return $"{Status}，ExitCode={ExitCode}，ErrorType={DetectedErrorType}，Elapsed={Elapsed:hh\\:mm\\:ss}";
        }
    }
}

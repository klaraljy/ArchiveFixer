using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Extraction
{
    public sealed partial class RecursiveExtractor
    {
        private IReadOnlyList<string> BuildPasswordCandidates(string archivePath)
        {
            IReadOnlyList<string> provided;

            try
            {
                provided = _passwordProvider(archivePath) ?? Array.Empty<string>();
            }
            catch
            {
                // 密码来源出问题（密码本损坏等）不该让递归核心炸掉：当成"没有候选"处理，
                // 后面会以 WrongPassword 收场并如实报告。
                return Array.Empty<string>();
            }

            var candidates = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (string? candidate in provided)
            {
                if (candidate == null)
                {
                    continue;
                }

                // 去重：调用方的候选顺序里常常"最近成功的密码"和"密码本命中"是同一个值，
                // 重复试同一个密码只会白烧一次密码尝试次数。
                if (seen.Add(candidate))
                {
                    candidates.Add(candidate);
                }
            }

            return candidates;
        }

        /// <summary>
        /// 这一次尝试在**这一层的产物目录**里留下了多少字节。
        ///
        /// <para>用途只有一处：判定"哪一个候选的失败更值得当结论"—— 留下过产物的那一次必然比
        /// "连门都没进去"的那些更有信息量（用户 2026-09-30 真机：正确密码那次解了 13 分钟 /
        /// 17.7 GiB 才失败，却被后面 50 毫秒就失败的错候选盖掉）。</para>
        ///
        /// <para>读不到（目录不在 / 被占用 / 权限）一律返回 0：那只会让结论退回老口径
        /// （用最后一次失败），⛔ 不会把"什么都没留下"误判成"留下了东西"。</para>
        /// </summary>
        private static long ProducedBytesInLayerOutput(WorkItem item)
        {
            try
            {
                string path = item.Layer.OutputPath;

                if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                {
                    return 0;
                }

                long total = 0;

                foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        total += new FileInfo(file).Length;
                    }
                    catch
                    {
                        // 量不出单个文件不影响"有没有留下东西"这个结论。
                    }
                }

                return total;
            }
            catch
            {
                return 0;
            }
        }

        private RecursionLayerReport BuildLayerReport(
            WorkItem item,
            ArchiveOperationResult? result,
            string? succeededPassword,
            IReadOnlyList<(string Path, long Size)>? manifestEntries = null)
        {
            return BuildLayerReport(
                item,
                result,
                succeededPassword,
                PasswordMasker.Sanitize(result?.Message),
                manifestEntries);
        }

        /// <summary>
        /// 带"失败原因覆盖"的层报告：预检/落点校验这类**不是引擎给出**的失败，
        /// 原因来自安全检查，必须原样写进报告（Message 会被 PasswordMasker 再洗一遍，防密码泄漏）。
        ///
        /// <para><paramref name="overrideStatus"/> 同理：像「文件名已加密」这种结论，
        /// 引擎最后一次给的往往是"密码错误"那一档（那是候选循环的驱动信号），
        /// 直接照抄会让层报告与结论自相矛盾。</para>
        /// </summary>
        internal RecursionLayerReport BuildLayerReport(
            WorkItem item,
            ArchiveOperationResult? result,
            string? succeededPassword,
            string? overrideMessage,
            IReadOnlyList<(string Path, long Size)>? manifestEntries = null,
            string? overrideStatus = null)
        {
            string message = overrideMessage
                ?? (result == null ? "引擎没有返回结果" : PasswordMasker.Sanitize(result.Message));

            return new RecursionLayerReport
            {
                Depth = item.Depth,
                ArchivePath = item.ArchivePath,
                OutputPath = item.Layer.OutputPath,
                Success = false,
                Status = overrideStatus ?? result?.Status ?? StatusText.ExtractFailed,
                Message = PasswordMasker.Sanitize(message),
                InnerArchives = Array.Empty<string>(),
                OutputFileCount = 0,
                OutputSize = 0,

                // 失败层没有可信清单（它压根没解开）：L3 不会拿它当预期，原因如实写。
                Manifest = LayerManifest.Unavailable("这一层没有成功解压，没有可信清单"),

                /*
                 * 但"逐条清单"要带上（部分完成发布的输入）：它是**解压前**列出来的那一份，
                 * 与"这一层解开了没有"无关 —— 密码对、坏在个别条目上时，它正是逐条对账唯一能用的尺子。
                 */
                ManifestEntries = manifestEntries ?? Array.Empty<(string, long)>(),
                FailedEntryNames = result?.FailedEntryNames ?? Array.Empty<string>(),
                ReportedSubItemErrors = result?.ReportedSubItemErrors ?? 0,
                DetectedErrorType = result?.DetectedErrorType ?? string.Empty,
                CandidatesProducedIdenticalResults = item.CandidatesProducedIdenticalResults,
                TriedCandidateCount = item.TriedCandidateCount,
                UsedPasswordMasked = succeededPassword == null ? string.Empty : PasswordMasker.Mask(succeededPassword)
            };
        }

        private static ExtractionWorkspace CreateWorkspace(ArchiveTask task)
        {
            string sourceName = string.IsNullOrWhiteSpace(task.CurrentPath)
                ? "未命名"
                : FileNameHelper.GetArchiveBaseName(task.CurrentPath);

            /*
             * taskId = 包基名 + 时间戳 + 随机码。
             * 为什么不能只用包名：同一批任务里常有两个同名包（C:\t\a\pack.zip 与 C:\t\b\pack.zip），
             * 共用一个工作区目录会让第二次解压撞上第一次的产物。
             * 为什么不能只用时间戳：并发解压时两个任务可能落在同一毫秒。
             *
             * 这里**不需要**自己做文件名清洗：ExtractionWorkspace 构造时会再清洗一遍，
             * 而且它才是"路径安全"的唯一责任方（清洗两次不冲突，但不能靠这里兜底）。
             */
            string taskId = $"{sourceName}_{ArchiveFixer.Helpers.Clock.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}";

            return new ExtractionWorkspace(WorkspaceRootDirectory, taskId);
        }

        /// <summary>
        /// 结论里点名"是哪个引擎报的密码错"（换引擎兜底那一档要用）。
        /// 优先取**引擎自己盖的戳**（不变量 14），取不到才退回注入的那个引擎实例的显示名。
        /// </summary>
        internal string ResolveFailedEngineName(ArchiveOperationResult? result)
        {
            if (!string.IsNullOrWhiteSpace(result?.EngineDisplayName))
            {
                return result!.EngineDisplayName;
            }

            return string.IsNullOrWhiteSpace(_engine.DisplayName) ? EngineIds.SevenZip : _engine.DisplayName;
        }

        /// <summary>
        /// 上一次运行里"换引擎兜底"留下的那句话（"已换 WinRAR 再试过一遍" / "没找到 WinRAR，所以没换引擎再试"）。
        ///
        /// <para>为什么要实例字段：结论（<see cref="RecursionResult.Summary"/>）是在
        /// <see cref="BuildResult"/> 里拼的，而现场（候选循环那一层）在 <see cref="ExtractLayerAsync"/> 里 ——
        /// 中间隔着好几层返回值。兜底只可能发生在**整条链停下来的那一层**，所以一个字段就够，
        /// 每次运行开始时清空（见 <see cref="ExtractAsync"/>）。</para>
        /// </summary>
        private string _passwordFallbackNote = string.Empty;

        /// <summary>
        /// 递归工作区的根目录，由调用方在启动时指定（批首 <c>ApplyBatchWorkspaceRoot</c> 会设成
        /// <c>&lt;目标目录&gt;\.ArchiveFixer.work</c>；<c>MainViewModel</c> 构造时设成 <c>PathService.WorkDirectory</c>）。
        /// </summary>
        public static string? ConfiguredWorkspaceRoot { get; set; }

        private static string WorkspaceRootDirectory =>
            ResolveWorkspaceRootDirectory(ConfiguredWorkspaceRoot);

        /// <summary>
        /// **没人配过根时怎么走** —— 唯一出口（<see cref="WorkspaceRootDirectory"/> 转调它）。
        /// 拿不到根 ⇒ 抛异常，⛔ 不回落程序目录 / C 盘 / <c>%TEMP%</c>。
        /// </summary>
        internal static string ResolveWorkspaceRootDirectory(string? configuredRoot)
        {
            if (string.IsNullOrWhiteSpace(configuredRoot))
            {
                throw new InvalidOperationException(
                    "递归工作区根目录没有配置：批首会把这一批的工作区定在「本次目标目录里的 "
                    + ".ArchiveFixer.work」，手动路径由 MainViewModel 构造时设。按不变量 12，"
                    + "拿不到根时「不回落程序目录 / C 盘 / %TEMP%」—— 这一单不解，"
                    + "请确认目标目录可用后重试。");
            }

            string root = Path.Combine(configuredRoot, "recursive");

            // 工作区那棵树一律带隐藏属性（用户 2026-09-30 诉求③：单看着看不出来）。
            WorkspaceTree.EnsureHiddenDirectory(root);

            return root;
        }

    }
}
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Engines.WinRar
{
    /// <summary>
    /// RAR 侧的第二引擎：RARLAB <c>UnRAR.exe</c>（免费件，只解压）。
    ///
    /// <para><b>为什么要有它</b>（用户 2026-09-22 指示："先是 winrar、7z、然后就是后面的引擎"）：
    /// RAR 是 7-Zip 的"外来"格式，它靠 UnRAR 源码的解码器支持，出问题时给用户的结论往往不如
    /// 原厂工具准。实测确认 UnRAR 更强的两处（原始输出见 `_tmp\ArchiveFixer\unrar-samples\`）：
    /// ① **缺分卷时它直接点名**：<c>Cannot find volume …\vol.part3.rar</c> + 退出码 6，
    ///    而 7-Zip 报的是 "Cannot open the file as archive"（字面与"这不是归档"一样，
    ///    现有代码只能靠文件名去猜 —— 见 <c>SevenZipProcessRunner.ResolveVolumeMissingErrorType</c>）；
    /// ② **加密文件名有显式标志**：<c>Details: RAR 5, encrypted headers</c>，不用从错误文本猜。
    /// ⚠ 诚实交代：验收要求里"7-Zip 解不开 -hp"这一条在 7-Zip 26.01 上**不成立** ——
    /// 实测它带正确密码能解 RAR4/RAR5 的 <c>-hp</c>（含分卷），证据写在报告里。第二引擎的价值
    /// 因此落在"分类与报错更准 + 将来 RAR 7.x 新格式"上，不在"7-Zip 完全不能"。 </para>
    ///
    /// <para><b>能力位如实</b>（AGENTS.md §3.1 / 用户指示"不要声称能处理 zip/7z"）：
    /// 只登记 <c>RAR</c> / <c>RAR4</c> / <c>RAR5</c>；zip / 7z / tar 等**一个都不登记**，
    /// 于是 <see cref="EngineSelector"/> 会把这些格式自动交给 7-Zip。</para>
    /// </summary>
    public sealed class UnRarEngine : IArchiveEngine
    {
        private readonly UnRarProcessRunner _runner;
        private readonly ToolLocator _tools;
        private EngineCapabilities? _capabilities;

        public UnRarEngine()
            : this(new UnRarProcessRunner())
        {
        }

        public UnRarEngine(UnRarProcessRunner runner)
        {
            _runner = runner ?? new UnRarProcessRunner();
            _tools = _runner.Tools;
        }

        /// <summary>
        /// 机器标识。用 <c>EngineIds.WinRar</c>（= <c>"winrar"</c>）：
        /// 它就写在用户的 <see cref="AppSettings.EnginePriority"/> 里，两处必须是同一个字面量。
        /// </summary>
        public string Id => EngineIds.WinRar;

        /// <summary>
        /// 给人看的名字。措辞只做**事实性说明**（AGENTS.md §3.1 / docs/引擎与外部工具.md §3）：
        /// 说清它是 RARLAB 的解压工具，不借用 WinRAR 的品牌做宣传性表述。
        /// </summary>
        public string DisplayName => "UnRAR 命令行（RARLAB 解压工具）";

        public string Version => _tools.UnRarVersion;

        public bool IsAvailable => _tools.UnRarExists;

        public EngineCapabilities Capabilities => _capabilities ??= BuildCapabilities();

        /// <summary>
        /// 引擎级探测：UnRAR **没有**"只读文件头就能判断格式"的能力 ——
        /// <c>-hp</c> 的包连文件列表都要密码才读得出来。廉价格式识别在 Detection 层的魔数识别里，
        /// 这里照 7-Zip 引擎的口径如实回答 <c>CanProbe = false</c>，不假装能探测。
        /// </summary>
        public Task<ArchiveProbeResult> ProbeAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
        {
            if (!IsAvailable)
            {
                return Task.FromResult(new ArchiveProbeResult
                {
                    IsArchive = false,
                    Format = "Unknown",
                    Confidence = "Unknown",
                    Message = $"引擎不可用：{_tools.DescribeUnRarResolution()}"
                });
            }

            return Task.FromResult(new ArchiveProbeResult
            {
                IsArchive = true,
                Format = "Unknown",
                Confidence = "Unknown",
                Message = "UnRAR 无法只凭文件头判断格式；请使用 Detection 层的魔数识别，必要时调用 ListAsync。"
            });
        }

        /// <summary>
        /// 列出条目：<c>unrar lt -scf</c>，解析交给 <see cref="UnRarListParser"/>。
        ///
        /// 两处与 7-Zip 不同的处理：
        /// ① 加密文件名（<c>-hp</c>）在无密码 / 错密码时 UnRAR 会给 <c>encrypted headers</c> 字样，
        ///    于是可以落成既有的 <c>StatusText.EncryptedHeaders</c>（不是"密码错误"、更不是"文件损坏"）；
        /// ② 从**后续卷**启动时 UnRAR 会"成功"地列出 0 个文件（实测 volume 2 就是这样），
        ///    那不是"空归档"而是"起点不对"，必须明确报出来（不变量 7 / AGENTS.md §9.3 只从第一卷启动）。
        /// </summary>
        public async Task<ArchiveListResult> ListAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
        {
            if (!IsAvailable)
            {
                return ArchiveListResult.Failure(
                    EngineErrorTypes.EngineUnavailable,
                    $"引擎不可用：{_tools.DescribeUnRarResolution()}",
                    Id,
                    Version);
            }

            if (string.IsNullOrWhiteSpace(request.ArchivePath))
            {
                return ArchiveListResult.Failure(EngineErrorTypes.UnknownError, "归档路径为空", Id, Version);
            }

            List<string> arguments = _runner.BuildListArguments(request.ArchivePath, request.Password);

            ArchiveOperationResult result = await _runner
                .RunAsync(arguments, request.Password, cancellationToken)
                .ConfigureAwait(false);

            if (!result.Success)
            {
                return ArchiveListResult.Failure(
                    string.IsNullOrWhiteSpace(result.DetectedErrorType) ? EngineErrorTypes.UnknownError : result.DetectedErrorType,
                    string.IsNullOrWhiteSpace(result.Message) ? "列出归档内容失败" : result.Message,
                    Id,
                    Version);
            }

            UnRarListParser.ParsedListing parsed = UnRarListParser.Parse(result.StandardOutput, request.ArchivePath);

            ArchiveListResult listing = new()
            {
                Success = true,
                Entries = parsed.Entries,
                TotalUncompressedSize = parsed.TotalUncompressedSize,
                FileCount = parsed.FileCount,
                DirectoryCount = parsed.DirectoryCount,
                IsEncrypted = parsed.HeaderEncrypted || parsed.AnyEntryEncrypted,
                IsMultiVolume = parsed.IsMultiVolume,
                EngineId = Id,
                EngineVersion = Version
            };

            /*
             * 从后续卷启动：UnRAR 对 "xxx.part2.rar" 会给出"成功 + 0 个条目"。
             * 那不是空包，而是**起点不对** —— 报出来让调用方去找第一卷，
             * 免得下游把它当成"这个包里什么都没有"（分卷相关的坑见 AGENTS.md §9.3）。
             */
            if (listing.Entries.Count == 0 && parsed.VolumeIndex > 1)
            {
                return ArchiveListResult.Failure(
                    EngineErrorTypes.MissingFirstVolume,
                    $"这是分卷压缩包的第 {parsed.VolumeIndex} 卷，不是第一卷；请从第一卷（.rar / .part1.rar）开始处理",
                    Id,
                    Version);
            }

            return listing;
        }

        public async Task<ArchiveOperationResult> TestAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
        {
            ArchiveOperationResult result = await _runner.TestArchiveAsync(
                request.ArchivePath,
                request.Password,
                cancellationToken).ConfigureAwait(false);

            // 结果可追溯（不变量 14）：报告读的是**这次真正执行的那个引擎**，不是报告时刻的注册表。
            return result.StampEngine(Id, DisplayName, Version);
        }

        public async Task<ArchiveOperationResult> ExtractAsync(
            ArchiveRequest request,
            ExtractOptions options,
            CancellationToken cancellationToken = default)
        {
            ArchiveOperationResult result = await _runner.ExtractArchiveAsync(
                request.ArchivePath,
                request.OutputPath ?? string.Empty,
                request.Password,
                options,
                EngineRuntimeSettings.KeepBrokenFiles,
                cancellationToken).ConfigureAwait(false);

            return result.StampEngine(Id, DisplayName, Version);
        }

        private EngineCapabilities BuildCapabilities()
        {
            return new EngineCapabilities
            {
                CanProbe = false, // 见 ProbeAsync 的说明：没有廉价的文件头探测能力
                CanList = true,
                CanTest = true,
                CanExtract = true,
                SupportsPassword = true,
                SupportsMultiVolume = true,
                SupportsStreaming = false,
                SupportsLargeFiles = true,

                // 实测：加 -scf 后中文条目名能正确列出与解出（unrar x 中文名.txt → OK）。
                PreservesUnicodeNames = true,

                /*
                 * ⚠ 白名单：这个引擎**只**认 RAR 系列。
                 * 不开这一条时，未登记的 zip / 7z / tar 会退回"总体能力位"而被路由到它身上
                 * （验收第 3 条实测踩到：ZIP → winrar），本来能打开的包反而打不开。
                 */
                FormatsAreWhitelist = true,

                Formats = BuildFormatCapabilities(),
                VersionInfo = new EngineVersionInfo
                {
                    EngineId = EngineIds.WinRar,
                    DisplayName = DisplayName,
                    Version = Version,
                    VerifiedOn = "2026-09-22 于 _tmp\\ArchiveFixer\\unrar-samples（真样本：普通 RAR5/RAR4、数据加密 -p、加密文件名 -hp、"
                                 + "分卷 -v（partN 与 R4 的 .r00 两种命名）、截断损坏、缺卷、中文名条目；"
                                 + "逐条命令与原始输出见交付报告 fix-unrar.md）",
                    KnownIssues = new[]
                    {
                        "只支持 RAR 系列（RAR/RAR4/RAR5）：zip / 7z / tar 等一律不支持，由 EngineSelector 交给 7-Zip",
                        "覆盖档 AutoRenameExisting 没有对应开关，落到 -o-（不覆盖），与 7-Zip 的 -aot 行为不同",
                        "密码通过 -p<明文> 出现在进程命令行，属命令行固有限制，日志层只能事后脱敏（与 7-Zip 侧同一限制）",
                        "只解压（读）：绝不接建包/修包/改包命令（许可第 4 条禁止用其重建 RAR 压缩算法）",
                        "加密文件名（-hp）的包只有给对密码才能列出内容；无密码时列目录返回加密头状态"
                    }
                }
            };
        }

        /// <summary>
        /// 只登记 RAR 三种写法（Detection 层给出的就是 RAR4 / RAR5 两个值，RAR 是历史别名）。
        /// ⛔ 一个 zip / 7z 都不许出现在这里 —— 声称能处理它们会直接把包路由到解不开的引擎上。
        /// </summary>
        private static List<EngineFormatCapability> BuildFormatCapabilities()
        {
            return new List<EngineFormatCapability>
            {
                new()
                {
                    Format = "RAR5",
                    CanList = true,
                    CanTest = true,
                    CanExtract = true,
                    SupportsPassword = true,
                    SupportsAes = true,
                    SupportsMultiVolume = true,
                    SupportsLargeFiles = true,
                    PreservesUnicodeNames = true
                },
                new()
                {
                    Format = "RAR4",
                    CanList = true,
                    CanTest = true,
                    CanExtract = true,
                    SupportsPassword = true,
                    SupportsAes = true,
                    SupportsMultiVolume = true,
                    SupportsLargeFiles = true,
                    PreservesUnicodeNames = true
                },
                new()
                {
                    Format = "RAR",
                    CanList = true,
                    CanTest = true,
                    CanExtract = true,
                    SupportsPassword = true,
                    SupportsAes = true,
                    SupportsMultiVolume = true,
                    SupportsLargeFiles = true,
                    PreservesUnicodeNames = true
                }
            };
        }
    }
}

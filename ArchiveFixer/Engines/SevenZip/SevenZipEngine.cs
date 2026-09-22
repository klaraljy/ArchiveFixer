using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Engines.SevenZip
{
    /// <summary>
    /// 7-Zip 命令行的引擎实现。
    ///
    /// 分工：
    /// - <see cref="SevenZipProcessRunner"/> 负责"怎么起进程"（参数、超时、取消、收尸）；
    /// - <see cref="SevenZipOutputParser"/> 负责"输出是什么意思"（错误分类、成功判定）；
    /// - 本类负责"引擎该回答什么"（能力、列目录、测试、解压）。
    ///
    /// 这三块都待在 <c>Engines/SevenZip/</c> 里 —— 之外的地方不许再出现 7z 参数、7z 文本解析、7z 错误字符串。
    /// </summary>
    public sealed class SevenZipEngine : IArchiveEngine
    {
        /// <summary>机器标识取自全仓唯一词表（<see cref="EngineIds"/>），别处不许再写字面量。</summary>
        private const string EngineId = EngineIds.SevenZip;

        private readonly SevenZipProcessRunner _runner;
        private readonly ToolLocator _tools;
        private EngineCapabilities? _capabilities;

        public SevenZipEngine()
            : this(new SevenZipProcessRunner())
        {
        }

        public SevenZipEngine(SevenZipProcessRunner runner)
        {
            _runner = runner ?? new SevenZipProcessRunner();
            _tools = _runner.Tools;
        }

        public string Id => EngineId;

        public string DisplayName => "7-Zip 命令行";

        public string Version => _tools.SevenZipVersion;

        public bool IsAvailable => _tools.SevenZipExists;

        public EngineCapabilities Capabilities => _capabilities ??= BuildCapabilities();

        /// <summary>
        /// 引擎级探测。
        ///
        /// 注意：**7-Zip 没有"只读文件头就能判断格式"的能力** —— 它要么完整打开归档，要么什么都说不出。
        /// 真正便宜的格式识别在 Detection 层的魔数识别里（M2.3 起由 <c>Detection</c> 统一负责），
        /// 这里只回答"引擎本身能不能干活"，不做昂贵解析。
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
                    Message = $"引擎不可用：{_tools.DescribeResolution()}"
                });
            }

            return Task.FromResult(new ArchiveProbeResult
            {
                IsArchive = true,
                Format = "Unknown",
                Confidence = "Unknown",
                Message = "7-Zip 无法只凭文件头判断格式；请使用 Detection 层的魔数识别，必要时调用 ListAsync。"
            });
        }

        /// <summary>
        /// 列出条目。用 <c>7z l -slt</c>（稳定键值格式），解析交给 <see cref="SevenZipListParser"/>。
        /// M5 的资源预算与"解压前路径预检"都要靠它，所以这里失败时必须给出可读原因，不能只返回空列表。
        /// </summary>
        public async Task<ArchiveListResult> ListAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
        {
            if (!IsAvailable)
            {
                return ArchiveListResult.Failure(
                    "EngineUnavailable",
                    $"引擎不可用：{_tools.DescribeResolution()}",
                    Id,
                    Version);
            }

            if (string.IsNullOrWhiteSpace(request.ArchivePath))
            {
                return ArchiveListResult.Failure("UnknownError", "归档路径为空", Id, Version);
            }

            List<string> arguments = BuildListArguments(request.ArchivePath, request.Password);
            ArchiveOperationResult result = await _runner
                .RunSevenZipAsync(arguments, request.Password ?? string.Empty, cancellationToken)
                .ConfigureAwait(false);

            if (!result.Success)
            {
                return ArchiveListResult.Failure(
                    result.DetectedErrorType,
                    string.IsNullOrWhiteSpace(result.Message) ? "列出归档内容失败" : result.Message,
                    Id,
                    Version);
            }

            return SevenZipListParser.Parse(result.StandardOutput, request.ArchivePath, Id, Version);
        }

        public async Task<ArchiveOperationResult> TestAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
        {
            /*
             * 引擎层一律 ConfigureAwait(false)（见 SevenZipProcessRunner 的同名注释）：
             * 我们不需要回到调用方的同步上下文，续体回到 UI 线程反而会把
             * "谁在等谁"变成一条隐形的依赖 —— 历史卡死就是这么来的。
             *
             * StampEngine 是"结果可追溯"的落点（不变量 14）：报告与失败清单读的是
             * **这次真正执行的那个引擎**，而不是报告时刻注册表里排第一的引擎。
             */
            ArchiveOperationResult result = await _runner.TestArchiveAsync(
                request.ArchivePath,
                request.Password ?? string.Empty,
                cancellationToken,
                EngineProgressContext.From(request)).ConfigureAwait(false);

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
                request.Password ?? string.Empty,
                options,
                cancellationToken,
                EngineProgressContext.From(request)).ConfigureAwait(false);

            return result.StampEngine(Id, DisplayName, Version);
        }

        /// <summary>
        /// 列目录参数只在这里拼。
        /// 用 <c>-slt</c> 拿稳定键值；<c>-bd</c> 关进度条（免得日志里全是百分比刷屏）；
        /// 密码只在本层拼进参数，且**不写进任何日志**。
        ///
        /// <c>-sccUTF-8</c> 是必须的（实测缺陷）：7-Zip 的控制台输出默认跟随系统 OEM 代码页
        /// （中文系统 = 936/GBK），而 <see cref="SevenZipProcessRunner"/> 按 UTF-8 解码输出流，
        /// 不加这个开关，中文路径与中文条目名会全部变成 <c>01_��ͨ</c> 这种乱码 ——
        /// 路径预检摘要、超限文件名、失败清单都会跟着不可核对（26.01 实测：加上即正常）。
        /// </summary>
        private static List<string> BuildListArguments(string archivePath, string? password)
        {
            var args = new List<string>
            {
                "l",
                "-slt",
                "-bd",
                "-sccUTF-8",
                "-y",
                archivePath
            };

            args.Add("-p" + (password ?? string.Empty));

            return args;
        }

        private EngineCapabilities BuildCapabilities()
        {
            return new EngineCapabilities
            {
                CanProbe = false, // 见 ProbeAsync 的说明：7-Zip 不具备廉价探测能力
                CanList = true,
                CanTest = true,
                CanExtract = true,
                SupportsPassword = true,
                SupportsMultiVolume = true,
                SupportsStreaming = false,
                SupportsLargeFiles = true,
                PreservesUnicodeNames = true,
                Formats = BuildFormatCapabilities(),
                VersionInfo = new EngineVersionInfo
                {
                    EngineId = EngineId,
                    DisplayName = DisplayName,
                    Version = Version,
                    VerifiedOn = "2026-09-21 于 samples/generated（合成样本集：普通/加密/分卷/伪装后缀/损坏/非归档/复合后缀）",
                    KnownIssues = new[]
                    {
                        "加密状态无法只靠文件头判断，只能以 7z 实际测试为准（设计.md §六 已承认）",
                        "密码通过 -p 明文出现在进程命令行，属 7-Zip 命令行固有限制，日志层只能事后脱敏",
                        "RAR 只能解不能建；测试用真 RAR 样本需用户自备（见 samples/MANIFEST.md）",
                        "无法预知解压后总大小时，预算只能退化为运行时统计（设计.md §二十六）"
                    }
                }
            };
        }

        private static List<EngineFormatCapability> BuildFormatCapabilities()
        {
            return new List<EngineFormatCapability>
            {
                new() { Format = "ZIP", CanList = true, CanTest = true, CanExtract = true, SupportsPassword = true, SupportsAes = true, SupportsMultiVolume = true },
                new() { Format = "ZIP_EMPTY", CanList = true, CanTest = true, CanExtract = true },
                new() { Format = "ZIP_SPANNED", CanList = true, CanTest = true, CanExtract = true, SupportsMultiVolume = true },
                new() { Format = "7Z", CanList = true, CanTest = true, CanExtract = true, SupportsPassword = true, SupportsAes = true, SupportsMultiVolume = true },
                new() { Format = "RAR4", CanList = true, CanTest = true, CanExtract = true, SupportsPassword = true, SupportsMultiVolume = true },
                new() { Format = "RAR5", CanList = true, CanTest = true, CanExtract = true, SupportsPassword = true, SupportsAes = true, SupportsMultiVolume = true },
                new() { Format = "RAR", CanList = true, CanTest = true, CanExtract = true, SupportsPassword = true, SupportsMultiVolume = true },
                new() { Format = "GZIP", CanList = true, CanTest = true, CanExtract = true, SupportsStreaming = true },
                new() { Format = "BZIP2", CanList = true, CanTest = true, CanExtract = true, SupportsStreaming = true },
                new() { Format = "XZ", CanList = true, CanTest = true, CanExtract = true, SupportsStreaming = true },
                new() { Format = "TAR", CanList = true, CanTest = true, CanExtract = true, SupportsStreaming = true },
                new() { Format = "CAB", CanList = true, CanTest = true, CanExtract = true },
                new() { Format = "ARJ", CanList = true, CanTest = true, CanExtract = true, SupportsPassword = true, SupportsMultiVolume = true },
                new() { Format = "LZH", CanList = true, CanTest = true, CanExtract = true },
                new() { Format = "Z", CanList = true, CanTest = true, CanExtract = true, SupportsStreaming = true },
                new() { Format = "ZSTD", CanList = true, CanTest = true, CanExtract = true, SupportsStreaming = true },
                new() { Format = "LZ4", CanList = true, CanTest = true, CanExtract = true, SupportsStreaming = true },
                new() { Format = "RPM", CanList = true, CanTest = true, CanExtract = true },
                new() { Format = "ISO", CanList = true, CanTest = true, CanExtract = true }
            };
        }
    }
}

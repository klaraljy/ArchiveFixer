using System.Collections.Generic;
using System.Linq;

namespace ArchiveFixer.Engines
{
    /// <summary>
    /// 某个引擎对某个格式的**细粒度**能力。
    ///
    /// 为什么不能只写"支持 ZIP"（设计.md §十八）：
    /// 同一个格式不同引擎、甚至同一引擎不同版本，能力都不一样 ——
    /// 比如"能列出但解不开 AES 加密"、"支持大文件但不支持分卷"。
    /// 只写一个 bool 会让引擎选择变成一个说不清的猜谜。
    /// </summary>
    public sealed class EngineFormatCapability
    {
        /// <summary>格式名，与 <c>DetectResult.Format</c> 同一套口径：ZIP / 7Z / RAR4 / RAR5 / TAR ...</summary>
        public string Format { get; init; } = string.Empty;

        public bool CanList { get; init; } = true;

        public bool CanTest { get; init; } = true;

        public bool CanExtract { get; init; } = true;

        public bool SupportsPassword { get; init; }

        public bool SupportsAes { get; init; }

        public bool SupportsMultiVolume { get; init; }

        public bool SupportsLargeFiles { get; init; } = true;

        public bool PreservesUnicodeNames { get; init; } = true;

        public bool SupportsStreaming { get; init; }
    }

    /// <summary>引擎的版本记录：能力是"哪个版本上验过的"，出问题时能对上号。</summary>
    public sealed class EngineVersionInfo
    {
        public string EngineId { get; init; } = string.Empty;

        public string DisplayName { get; init; } = string.Empty;

        public string Version { get; init; } = "unknown";

        /// <summary>这套能力是在哪一天、用什么样本验过的（人写的备注，不是自动推断）。</summary>
        public string VerifiedOn { get; init; } = string.Empty;

        public IReadOnlyList<string> KnownIssues { get; init; } = new List<string>();
    }

    /// <summary>引擎的总体能力位（设计.md §十八 的 9 个字段）。</summary>
    public sealed class EngineCapabilities
    {
        public bool CanProbe { get; init; }

        public bool CanList { get; init; }

        public bool CanTest { get; init; }

        public bool CanExtract { get; init; }

        public bool SupportsPassword { get; init; }

        public bool SupportsMultiVolume { get; init; }

        public bool SupportsStreaming { get; init; }

        public bool SupportsLargeFiles { get; init; }

        public bool PreservesUnicodeNames { get; init; }

        /// <summary>按格式的细粒度能力；没有列出的格式按"不支持"处理，不要猜。</summary>
        public IReadOnlyList<EngineFormatCapability> Formats { get; init; } = new List<EngineFormatCapability>();

        /// <summary>
        /// <see cref="Formats"/> 是不是**白名单**（未登记的格式一律"不支持"）。
        ///
        /// <para>
        /// 为什么必须有这个开关（实测踩到的坑，验收第 3 条）：选择器原来的兜底是
        /// "没登记具体格式时退回总体能力位，宁可让引擎去试一把"。
        /// 这条对**通用引擎**（7-Zip）是对的 —— 它确实还能处理不少没逐条登记的格式；
        /// 但对**专用引擎**（RARLAB UnRAR，只认 RAR 系列）就是灾难：
        /// 实测 zip / 7z / tar **全被路由到了 UnRAR 上**（它没登记这些格式，
        /// 于是"退回总体 CanExtract = true"），结果是"本来能打开的包反而打不开"。
        /// </para>
        ///
        /// <para>所以：<b>专用引擎必须打开它</b>（<c>UnRarEngine</c> 打开），
        /// 通用引擎保持 false（<c>SevenZipEngine</c> 不打开，行为与以前完全一致）。</para>
        /// </summary>
        public bool FormatsAreWhitelist { get; init; }

        public EngineVersionInfo VersionInfo { get; init; } = new EngineVersionInfo();

        /// <summary>取某个格式的能力；没有登记就返回 null（调用方据此判定"这个引擎干不了"）。</summary>
        public EngineFormatCapability? ForFormat(string? format)
        {
            if (string.IsNullOrWhiteSpace(format))
            {
                return null;
            }

            return Formats.FirstOrDefault(
                f => string.Equals(f.Format, format, System.StringComparison.OrdinalIgnoreCase));
        }

        public bool CanExtractFormat(string? format)
        {
            EngineFormatCapability? capability = ForFormat(format);

            if (capability != null)
            {
                return capability.CanExtract;
            }

            // 白名单引擎：没登记就是"干不了"，不许退回总体能力位。
            if (FormatsAreWhitelist)
            {
                return false;
            }

            // 没登记具体格式时，退回总体能力位：宁可让引擎去试一把，也不要因为"没登记"就拒绝。
            return CanExtract;
        }

        /// <summary>给人看的一句话（写进日志与任务报告）。</summary>
        public string Describe()
        {
            var parts = new List<string>();

            if (CanProbe) parts.Add("探测");
            if (CanList) parts.Add("列目录");
            if (CanTest) parts.Add("测试");
            if (CanExtract) parts.Add("解压");
            if (SupportsPassword) parts.Add("密码");
            if (SupportsMultiVolume) parts.Add("分卷");
            if (SupportsStreaming) parts.Add("流式");
            if (SupportsLargeFiles) parts.Add("大文件");
            if (PreservesUnicodeNames) parts.Add("Unicode 名称");

            return string.Join(" / ", parts);
        }
    }
}

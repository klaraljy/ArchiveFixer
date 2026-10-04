using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace ArchiveFixer.Detection
{
    /// <summary>
    /// 「手上这几卷加起来够不够整包」的三档（方案《分卷族系统化识别》§4 阶段 B 的 (d)）。
    ///
    /// <para>⛔ <see cref="Unknown"/> 是**默认档**：读不出起始头就什么都不断言（⛔ 不拿"差不多"当结论）。</para>
    /// </summary>
    public enum SevenZipByteBudgetVerdict
    {
        /// <summary>读不出起始头（没有魔数 / 读不到 / 布局不符）⇒ 这一条证据今天用不上。</summary>
        Unknown,

        /// <summary>手上的字节数**少于**整包自述的字节数 ⇒ 缺卷，缺多少也说得出来。</summary>
        Short,

        /// <summary>**正好相等** ⇒ 字节数这一条硬证据成立（与顺序无关），可以当"整组齐了"。</summary>
        Exact,

        /// <summary>手上的字节数**多于**整包自述的字节数 ⇒ 候选池里混进了不该算的文件。</summary>
        Excess
    }

    /// <summary>
    /// 7z 起始头自述的"整包应当有多少字节"与"手上这几份加起来有多少字节"的一次比对读数。
    ///
    /// <para>它只描述事实，**不含任何动作**：消费方最多拿它决定"值不值得试开一次 / 怎么排序 / 这句话怎么说"。</para>
    /// </summary>
    public readonly struct SevenZipByteBudget
    {
        /// <summary>读出了起始头（<see cref="ExpectedBytes"/> 可信）。</summary>
        public bool Known { get; init; }

        /// <summary>整包自述的字节数 = <c>32 + NextHeaderOffset + NextHeaderSize</c>。</summary>
        public long ExpectedBytes { get; init; }

        /// <summary>手上这几份加起来的字节数。</summary>
        public long ActualBytes { get; init; }

        /// <summary>三档结论。</summary>
        public SevenZipByteBudgetVerdict Verdict { get; init; }

        /// <summary>差的字节数（<see cref="SevenZipByteBudgetVerdict.Short"/> = 缺多少、<see cref="SevenZipByteBudgetVerdict.Excess"/> = 多多少；其余档为 0）。</summary>
        public long DifferenceBytes { get; init; }

        /// <summary>给人看的一句话（判不出时为空 —— 没有可说的结构化事实）。</summary>
        public string Describe()
        {
            if (!Known)
            {
                return string.Empty;
            }

            return Verdict switch
            {
                SevenZipByteBudgetVerdict.Short =>
                    $"7z 起始头自述这一包应当有 {ExpectedBytes} 字节，手上这几卷加起来只有 {ActualBytes} 字节 —— 还差 {DifferenceBytes} 字节",
                SevenZipByteBudgetVerdict.Exact =>
                    $"7z 起始头自述这一包应当有 {ExpectedBytes} 字节，手上这几卷加起来正好 {ActualBytes} 字节（字节数这一条证据成立）",
                SevenZipByteBudgetVerdict.Excess =>
                    $"7z 起始头自述这一包应当有 {ExpectedBytes} 字节，手上这几卷加起来却有 {ActualBytes} 字节 —— 多出 {DifferenceBytes} 字节",
                _ => string.Empty
            };
        }
    }

    /// <summary>
    /// **只读** 7z 起始头：给出"整包应当有多少字节"。
    ///
    /// <para><b>为什么这是一条硬证据</b>（方案 §1 表格 ①）：7z 的起始头 32 字节是**明文**（<c>-mhe</c> 也不加密），
    /// 里面写着 <c>NextHeaderOffset</c>（相对偏移 32 起算）与 <c>NextHeaderSize</c>，而 7z 的布局是
    /// "起始头 + 打包流 + next header" 首尾相接 —— 所以
    /// <c>32 + NextHeaderOffset + NextHeaderSize</c> **就是整包应当有的字节数**（与卷序无关，四份单文件真样本逐字节核过）。</para>
    ///
    /// <para><b>⛔ 它只准当只读判据</b>（识别 / 定序 / 能不能改名）：⛔ 不许拿它去改"删源 / 搬其余物"的结论
    /// （那条路上的判据是完整性与检验等级，见 <c>AGENTS.md</c> §11.6 与 <c>docs/检验等级.md</c>）。
    /// ⛔ 也不拿它猜"哪一卷是第几卷" —— 中间片的内容里没有任何身份信息，那件事只有名字与试开能回答。</para>
    ///
    /// <para>本类只读 32 字节、只做整数加法，不建目录、不改名字、不调引擎。</para>
    /// </summary>
    public static class SevenZipStartHeader
    {
        /// <summary>
        /// 读 <paramref name="firstVolumePath"/> 的起始头，算出"整包应当有多少字节"。
        ///
        /// <para>任何读不到（不是 7z / 文件不在 / 被占 / 不足 32 字节 / <c>NextHeaderSize</c> 为 0 /
        /// 数值越界）一律返回 false，**绝不抛** —— 调用方那时照旧走老路（不看这条证据）。</para>
        /// </summary>
        public static bool TryReadExpectedTotalBytes(string? firstVolumePath, out long expectedBytes)
        {
            expectedBytes = 0;

            if (string.IsNullOrWhiteSpace(firstVolumePath)
                || VolumeContentInference.SniffFormat(firstVolumePath) != VolumeContentFormat.SevenZip)
            {
                return false;
            }

            try
            {
                var head = new byte[SevenZipEncryptionReader.StartHeaderLength];

                using (var stream = new FileStream(
                    firstVolumePath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    int filled = 0;

                    while (filled < head.Length)
                    {
                        int read = stream.Read(head, filled, head.Length - filled);

                        if (read <= 0)
                        {
                            return false;
                        }

                        filled += read;
                    }
                }

                ulong nextHeaderOffset = BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(12, 8));
                ulong nextHeaderSize = BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(20, 8));

                if (nextHeaderSize == 0
                    || nextHeaderOffset > long.MaxValue - SevenZipEncryptionReader.StartHeaderLength
                    || nextHeaderSize > long.MaxValue)
                {
                    return false;
                }

                long total = SevenZipEncryptionReader.StartHeaderLength + (long)nextHeaderOffset;

                if ((long)nextHeaderSize > long.MaxValue - total)
                {
                    return false;
                }

                expectedBytes = total + (long)nextHeaderSize;

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 把"整包自述的字节数"与"手上这几份的字节数之和"比一次。
        ///
        /// <para>读不出起始头 ⇒ <see cref="SevenZipByteBudgetVerdict.Unknown"/>（这一条证据缺席，
        /// 调用方照旧按老路走）；卷长为负数 / 读不到 ⇒ 同样不动结论。</para>
        /// </summary>
        public static SevenZipByteBudget Measure(string? firstVolumePath, IEnumerable<string?>? volumePaths)
        {
            if (!TryReadExpectedTotalBytes(firstVolumePath, out long expected))
            {
                return new SevenZipByteBudget { Known = false, Verdict = SevenZipByteBudgetVerdict.Unknown };
            }

            long actual = 0;

            if (volumePaths != null)
            {
                foreach (string? path in volumePaths)
                {
                    long length = LengthOf(path);

                    if (length < 0)
                    {
                        return new SevenZipByteBudget { Known = false, Verdict = SevenZipByteBudgetVerdict.Unknown };
                    }

                    actual += length;
                }
            }

            if (actual == expected)
            {
                return new SevenZipByteBudget
                {
                    Known = true,
                    ExpectedBytes = expected,
                    ActualBytes = actual,
                    Verdict = SevenZipByteBudgetVerdict.Exact
                };
            }

            return new SevenZipByteBudget
            {
                Known = true,
                ExpectedBytes = expected,
                ActualBytes = actual,
                Verdict = actual < expected ? SevenZipByteBudgetVerdict.Short : SevenZipByteBudgetVerdict.Excess,
                DifferenceBytes = Math.Abs(expected - actual)
            };
        }

        /// <summary>文件长度；读不到（不在 / 被占 / 路径不合法）返回 -1（= "这一份说不清"，调用方按"不判"处理）。</summary>
        private static long LengthOf(string? path)
        {
            try
            {
                return string.IsNullOrWhiteSpace(path) ? -1 : new FileInfo(path!).Length;
            }
            catch
            {
                return -1;
            }
        }
    }
}

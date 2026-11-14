using System;
using System.Collections.Generic;

namespace ArchiveFixer.Detection;


/// <summary>
/// **内容级卷号识别**（用户 2026-09-29 任务：RAR / ZIP 分卷不靠名字，靠内容里的卷号）。
///
/// <para><b>为什么 RAR / ZIP 能做、7z 不能</b>：7z 的 <c>-v</c> 分卷内容里**没有卷号**
/// （只有第一卷有魔数，之后是裸字节流，见 <see cref="VolumeContentInference"/>），
/// 所以 7z 只能"猜顺序 + 试开验证"。RAR 与跨盘 ZIP 不一样：**卷号写在内容里**——</para>
/// <list type="bullet">
/// <item><description><b>RAR5</b>：主归档头（headerType=1）的 archiveFlags 带 <c>0x0001</c> = 这是分卷组的一员；
/// 带 <c>0x0002</c> = 后面还有卷号字段（**第 1 卷没有这个字段**），字段值 v → 卷号 = v + 1。</description></item>
/// <item><description><b>RAR 1.5–4.x</b>（WinRAR 里就叫 <b>RAR4</b>；<c>Rar!\x1A\x07\x00</c> 这个签名
/// RAR3 与 RAR4 是**同一个**，靠它根本分不出 3 与 4，所以本文件一律不断言"这一卷是 RAR3"）：
/// 卷号**不在**主头里，而在**卷尾归档结尾块**（type <c>0x7B</c>）的 <c>EARC_VOLNUMBER</c> 字段
/// （flags <c>0x0008</c>）。块里可选字段按 <c>EARC_DATACRC(4)</c> → <c>EARC_VOLNUMBER(2)</c> →
/// 保留区(7) 的**顺序**排，各自看出没出那一位 flag，所以卷号位置 =
/// <c>块起点 + 7 + (有 DATACRC ? 4 : 0)</c>。⚠ 基数（0 起还是 1 起）RAR 的文档没写
/// → 交给"整组必须连成 1..N"自洽判定。</description></item>
/// <item><description><b>跨盘 ZIP</b>：EOCD（<c>PK\x05\x06</c>）偏移 4 = 本盘号、偏移 6 = 中央目录起始盘号；
/// 两个都 0 = 单盘。EOCD 只在**末片**且必须在文件最末（注释长度要自洽）→ 本盘号 + 1 = 总片数。
/// **非末片内容里没有盘号**，只能靠开头是不是 PK 签名认"它是 zip 流"（真 PKZIP 跨盘的第 1 片以
/// 跨盘标记 <c>PK\x07\x08</c> 开头，不是本地文件头 —— 用户 2026-09-29 真样本），
/// 位置则由末片的盘号 + 片数 + 尺寸规律消去法定。</description></item>
/// </list>
///
/// <para><b>⛔ 拿不准一律不认</b>：头截断、CRC 对不上、卷号连不成 1..N、两种基数都成立、
/// 跨盘 zip 片数 ≥ 3（中间几片没有盘号，消去法只能定"恰好剩一个缺口"的那一组，也就是只有 2 片时）
/// —— 全部返回"没认出来"，由调用方原样不动（改错名字比不改更糟）。</para>
///
/// <para><b>职责分工（本类只留公开 API 薄转发，判据都在协作类里，⛔ 不在这里加分支）</b>：</para>
/// <list type="bullet">
/// <item><description>格式探测 / 分派 / 头部字节读取 → <see cref="VolumeContentFormatProbe"/>。</description></item>
/// <item><description>卷号解码：RAR → <see cref="RarVolumeNumberDecoder"/>，ZIP → <see cref="ZipVolumeNumberDecoder"/>；
/// 两族共用的 vint / CRC 原语 → <see cref="VolumeHeaderPrimitives"/>。</description></item>
/// <item><description>首卷判定（头标记 × 内容卷号互证、入口是否第 1 卷）→ <see cref="VolumeFirstVolumeJudge"/>。</description></item>
/// <item><description>整组定序（唯一出口 <c>ResolveGroup</c>）→ <see cref="VolumeNumberGroupResolver"/>。</description></item>
/// <item><description>证据模型（读数工厂 + membership 三档结论）→ <see cref="VolumeContentEvidence"/>。</description></item>
/// <item><description>标准卷名拼法（唯一出口 <c>BuildStandardNames</c>）→ <see cref="VolumeStandardNames"/>。</description></item>
/// </list>
///
/// <para>本类是**纯内容判断**：只读文件头尾若干字节，不建目录、不改名字、不调引擎。</para>
/// </summary>
public static class VolumeNumberFromContent
{
    /// <summary>
    /// **内容证据对"这一份是不是分卷组的一员"的三种回答** —— 用户 2026-10-11 口径：
    /// 「判断一个文件，**绝对不能只看基名和后缀**，绝对还有其他的办法」。
    ///
    /// <para>为什么需要它：按**名字**判"像不像分卷片"有一处结构性缺陷 —— **一个归档本体自己就是它那一族的第 1 卷**
    /// （`LegacyVolumeNames.TryGetIndex("111.rar")` = 1）⇒ 用户那份普普通通的单卷 `111.rar`
    /// 也会被判成"像分卷片"（第 125 轮那个真凶就是这么把它接进别组的）。而**归档自己写的头**能一句话说清：
    /// RAR5 主头的 <c>0x0001 Volume</c> 位、RAR4 的 <c>MHD_FIRSTVOLUME</c>、跨盘 ZIP 的 EOCD 盘号。</para>
    ///
    /// <para><b>真样本实测（`DebugRunner volinfo` 档，只读）</b>：
    /// EEE `111\111.rar`（用户的原包）⇒ <c>NotVolumeMember</c>（它自己写着"不是分卷组的一员"）；
    /// EEE `111(4)\111.z11111111110删除3`（跨盘 ZIP 的中间片，没有 EOCD）⇒ <c>NotAnArchive</c>（**判不出**）；
    /// FFFF `1114__\111_.part4.raAAAAAr`（真 RAR 分卷的一片）⇒ <c>IsVolumeMember = True</c>。
    /// ⇒ 内容证据确实能把"真分卷片"与"普通归档"分开；判不出的那一档照旧按名字（保守，⛔ 不许当"不是"）。</para>
    /// </summary>
    public enum VolumeMembershipVerdict
    {
        /// <summary>内容自述"我是分卷组的一员"（RAR5 的 <c>0x0001 Volume</c> / RAR4 的首卷标记 / 跨盘 ZIP 的盘号）。</summary>
        VolumeMember,

        /// <summary>
        /// **可证不是**分卷组的一员：头读得出来，而且它写着"单卷"
        /// （RAR 的 Volume 位没置 ⇒ <see cref="VolumeNumberFail.NotVolumeMember"/>；
        /// 跨盘 ZIP 的 EOCD 盘号 = 0 ⇒ <see cref="VolumeNumberFail.ZipSingleDisk"/>）。
        /// </summary>
        ProvablyNotVolumeMember,

        /// <summary>
        /// 判不出（魔数认不出 / 头截断 / 头加密 / RAR 老式编号族）—— 这一档**照旧按名字判**，
        /// ⛔ 绝不许当成"不是分卷成员"（跨盘 ZIP 的中间片就在这一档，它照样是分卷的片）。
        /// </summary>
        Unknown
    }

    /// <summary>
    /// **这一份在内容上是不是分卷组的一员**（唯一出口；⛔ 调用方不许自己去看
    /// <see cref="VolumeNumberReading.IsVolumeMember"/> / <see cref="VolumeNumberReading.Fail"/> 再判一次）。
    ///
    /// <para><b>⛔ 消费方只认这三档结论，⛔ 不许按格式分支</b>（用户 2026-10-11 口径：
    /// 「不仅仅是 rar，我们能做的识别文件逻辑要有程序性…换一个 7z 或者用户新接的引擎怎么办」）：
    /// 判据本体按 <see cref="VolumeContentInference.SniffFormat"/> **在一处分派**（<see cref="Read"/> →
    /// <see cref="VolumeContentFormatProbe"/> 再分派到 RAR / ZIP 解码器），**加一个新格式 = 那一处加一条分支**
    /// （必要时加一个 <see cref="VolumeNumberFail"/> 档），所有消费方一个字都不用改。判据实现在
    /// <see cref="VolumeContentEvidence.Classify"/>。</para>
    ///
    /// <para><b>能力矩阵（格式能证明什么，写在唯一出口这里，⛔ 别在各调用点各说一套）</b>：</para>
    /// <list type="bullet">
    /// <item><description><b>RAR（RAR5 / RAR 1.5–4.x）</b>：主头 <c>MHFL_VOLUME 0x0001</c>
    /// （上游 <c>unrar/headers5.hpp</c> 逐字 "Volume."）⇒ 能判"是"；没置位 ⇒ **可证不是**
    /// （<see cref="VolumeNumberFail.NotVolumeMember"/>）。</description></item>
    /// <item><description><b>跨盘 ZIP</b>：EOCD 本盘号 &gt; 0 ⇒ "是"；盘号 = 0 ⇒ **可证不是**
    /// （<see cref="VolumeNumberFail.ZipSingleDisk"/>）。</description></item>
    /// <item><description><b>7z（<c>.7z.001</c> 这一族）</b>：**格式里根本没有卷号字段** —— <c>-v</c> 切出来的
    /// <c>.001</c> 是普通 7z 头、之后是裸字节流 ⇒ 内容头**只能判到"判不出"**（这是格式事实，不是没实现）。
    /// 这一档的判据在别处、且同样是程序性的：认"它是不是第一卷"用
    /// <see cref="VolumeContentInference"/>，成不成立一律由
    /// <see cref="Extraction.VolumeProbeVerifier"/> **用引擎试开**回答（试开通过才算数）。</description></item>
    /// <item><description><b>认不出的字节 / 未知格式</b>：判不出（<see cref="VolumeNumberFail.NotAnArchive"/>）
    /// —— ⛔ 跨盘 ZIP 的中间片也落在这一档（它没有 EOCD），而它**确实是分卷的片**
    /// ⇒ 这一档**绝不许**当成"不是分卷成员"。</description></item>
    /// </list>
    ///
    /// <para>⇒ 与"名字/归属"那两道证据的分工（第 125 轮那次误接就是这么堵住的）：
    /// **① 归属**（`ArchiveTask.IsContinuationTask` / 任务路径）与格式、引擎都无关，任何格式都成立；
    /// **② 内容头**（本方法）对 RAR/ZIP 能给"可证不是"，7z 只能"判不出"；
    /// **③ 名字**（<see cref="VolumeGroupDetector"/>）是兜底 —— 判不出时才轮到它。</para>
    /// </summary>
    public static VolumeMembershipVerdict ClassifyMembership(string? filePath) =>
        ClassifyMembershipFromReading(Read(filePath));

    /// <summary>
    /// 同一份内容读数直接给三档结论：已经 `Read` 过的调用方必须走这里，
    /// ⛔ 不要为了 membership 再读一遍文件。判据实现在 <see cref="VolumeContentEvidence.Classify"/>。
    /// </summary>
    public static VolumeMembershipVerdict ClassifyMembershipFromReading(VolumeNumberReading reading) =>
        VolumeContentEvidence.Classify(reading);

    /// <summary>诊断计数：`Read` 实际进入次数；测试可重置。</summary>
    internal static long ReadCalls => VolumeContentFormatProbe.ReadCalls;

    internal static void ResetReadCallsForTests() => VolumeContentFormatProbe.ResetReadCallsForTests();

    /// <summary>
    /// **内容证据可证"这一份不是分卷组的一员"**（= <see cref="ClassifyMembership"/> 的那一档的便捷出口）。
    ///
    /// <para>⛔ 语义是**单向**的：<c>true</c> = 有正面证据它不是分卷；<c>false</c> = "是分卷"**或**"判不出"
    /// —— 调用方要区分这两者时请用 <see cref="ClassifyMembership"/>（判不出 ⇒ 照旧老口径）。</para>
    /// </summary>
    public static bool IsProvablyNotVolumeMember(string? filePath) =>
        ClassifyMembership(filePath) == VolumeMembershipVerdict.ProvablyNotVolumeMember;

    /// <summary>
    /// 读**一卷**的内容，得出"它说自己是不是分卷组的一员、是第几卷"。
    ///
    /// <para>任何 IO 意外都落成"没认出来 + 哪一档原因"，**绝不抛**。
    /// 实现见 <see cref="VolumeContentFormatProbe.Read"/>。</para>
    /// </summary>
    public static VolumeNumberReading Read(string? filePath) => VolumeContentFormatProbe.Read(filePath);

    /// <summary>
    /// 把"同目录这一批文件"的内容读数**定序成一组**（内容级的唯一出口）。
    ///
    /// <para>只拿与 <paramref name="currentPath"/> **同格式**的读数参与：目录里同时躺着 RAR 与跨盘 zip 时
    /// 不混组（混起来就没有"连成 1..N"可言，那正是最该拒绝的情形）。
    /// 判据实现在 <see cref="VolumeNumberGroupResolver.ResolveGroup"/>。</para>
    /// </summary>
    /// <param name="requireCurrentFirstVolume">
    /// 手上这一卷**必须**是第 1 卷吗？默认 <c>true</c>（老口径，一个字节没放宽）。
    /// <c>false</c> 只给**改名计划**那一处用（方案《分卷族系统化识别》§4 阶段 C）：
    /// RAR <c>partN</c> 族的族标记 <c>partN</c> 本身就是"我是第 1 卷"的同义语，所以
    /// 「谁拿着这一组」这一层判据照旧（<c>OneClickCoordinator</c> 与归组按名字算），
    /// 而"要不要改名"是用户点的那一下 —— 他手上可能是 <c>.part2.rar</c>。
    /// ⛔ 这一档**不是**放宽结论：卷号仍由内容给出、仍要求连成 1..N、改名仍只由
    /// <c>TryApply</c> 全成或全不成地执行（判不出 ⇒ 什么都不做）。</param>
    public static VolumeGroupOrder ResolveGroup(
        string? currentPath,
        IEnumerable<VolumeNumberReading>? readings,
        bool requireCurrentFirstVolume = true) =>
        VolumeNumberGroupResolver.ResolveGroup(currentPath, readings, requireCurrentFirstVolume);

    /// <summary>
    /// **卷名拼法的唯一出口**：由"基名（不含归档后缀）+ 族 + 卷数"拼出整组标准名。
    ///
    /// <para>RAR 族带 <c>.rar</c> 尾巴（<c>part1.rar</c>……）；7z 是数字族（<c>.7z.001</c>……）；
    /// 跨盘 zip 是 <c>.z01</c>……+ 末片 <c>.zip</c>（7-Zip 实测认这一族：改名成它之后
    /// <c>7z l</c> 能报出 <c>Multivolume = +</c> 并把里面的文件列出来）。
    /// 实现见 <see cref="VolumeStandardNames.BuildStandardNames"/>。</para>
    /// </summary>
    public static IReadOnlyList<string> BuildStandardNames(string? stem, VolumeNamingFamily family, int count) =>
        VolumeStandardNames.BuildStandardNames(stem, family, count);

    /// <summary>
    /// 推**名字前缀**（写出来的规范卷名里卷标记之前的那一段）：<c>amb909.7.01</c> → <c>amb909</c>、
    /// <c>amb909.7z.001</c> → <c>amb909.7z</c>。
    ///
    /// <para>⚠ 2026-10-23（用户口径）：这是**名字重建**，不是基名 —— 基名只有两份
    /// （<see cref="Helpers.FileNameHelper.FirstStepBaseName"/> / <see cref="Helpers.FileNameHelper.SecondStepBaseName"/>，
    /// 第一个点之前）。判据整体在 <see cref="Helpers.ExtensionHelper.ResolveNameStem"/>（唯一实现）。
    /// 实现见 <see cref="VolumeStandardNames.TryDeriveStem"/>。</para>
    ///
    /// <para>⛔ 推不出来（名字里认不出或含非法字符）就返回 false，绝不硬剪。</para>
    /// </summary>
    public static bool TryDeriveStem(string? filePath, VolumeContentFormat format, out string stem) =>
        VolumeStandardNames.TryDeriveStem(filePath, format, out stem);

    /// <summary>
    /// **媒体前缀里的 RAR5 主头**（2026-11-11）：只验证“从 <paramref name="fileOffset"/> 起确实是
    /// RAR5 主归档头”，供 <see cref="EmbeddedArchiveLocator"/> 的媒体前缀锚点使用。
    /// ⛔ 不判卷号/不建组；卷号仍走正常 RAR5 那条。实现见 <see cref="RarVolumeNumberDecoder.TryValidateRar5At"/>。
    /// </summary>
    internal static bool TryValidateRar5At(
        string filePath,
        long fileOffset,
        out bool singleComplete) =>
        RarVolumeNumberDecoder.TryValidateRar5At(filePath, fileOffset, out singleComplete);

    /// <summary>
    /// RAR5 风格的 vint：低 7 位是数据、最高位是"还有下一字节"。
    /// 唯一实现见 <see cref="VolumeHeaderPrimitives.TryReadVint"/>（RAR 头解析与 7z 嵌入探测共用一份）。
    /// </summary>
    internal static bool TryReadVint(ReadOnlySpan<byte> data, ref int offset, out ulong value) =>
        VolumeHeaderPrimitives.TryReadVint(data, ref offset, out value);

    /// <summary>
    /// 头 CRC 对得上（取补与不取补两种约定都收，理由见 <see cref="VolumeHeaderPrimitives"/>）。
    ///
    /// <para>⚠ <c>internal</c> 而不是 <c>private</c>：RAR 头的 CRC 与 vint 读法在本仓库里**只留一份**
    /// （<c>RarEncryptionReader</c> 解析 RAR4/RAR5 头时用的是同一份实现）——
    /// 两份 CRC 表/两套 vint 约定必然漂移，而漂移的后果是"同一个头，一处认得出、一处认不出"。
    /// 唯一实现见 <see cref="VolumeHeaderPrimitives.CrcMatches"/>。</para>
    /// </summary>
    internal static bool CrcMatches(uint stored, ReadOnlySpan<byte> data) =>
        VolumeHeaderPrimitives.CrcMatches(stored, data);

    /// <summary>
    /// RAR 1.5–4.x 的 16 位头 CRC（= 同一个 CRC32 的低 16 位；取补与不取补两种约定都收）。
    /// 唯一实现见 <see cref="VolumeHeaderPrimitives.CrcMatches16"/>。
    /// </summary>
    internal static bool CrcMatches16(ushort stored, ReadOnlySpan<byte> data) =>
        VolumeHeaderPrimitives.CrcMatches16(stored, data);
}

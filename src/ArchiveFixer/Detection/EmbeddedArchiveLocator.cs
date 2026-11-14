using System.IO;

namespace ArchiveFixer.Detection;

/// <summary>
/// **单一完整压缩包的专属算法：媒体前缀 + 尾部归档**（2026-10-22，用户口径：
/// 「简单的后缀伪装和媒体前缀，底层能不能靠信息识别」）。
///
/// <para>要回答的问题只有一个：**这份文件里、从某个偏移开始，是不是一份完整的单卷归档**。
/// 三种格式各自有一条**自洽校验**，任一条不过就返回 null（判不出 ⇒ 什么都不做，⛔ 不猜）：</para>
///
/// <list type="bullet">
/// <item><description><b>ZIP</b>：转调既有 <see cref="EmbeddedArchiveDetector"/> ——
/// 它用 EOCD 声明的中央目录偏移与实际位置的差值做自洽校验，并已区分"跨盘末片"。⚠ 这一族**不在这里重写第二份**。</description></item>
/// <item><description><b>7z</b>：搜 <c>37 7A BC AF 27 1C</c> 签名，校验
/// <c>StartHeaderCRC</c>（覆盖起始头 12..32 字节）与 <c>NextHeaderCRC</c>（覆盖下一个头），
/// 再用 <c>32 + NextHeaderOffset + NextHeaderSize</c> 与文件剩余长度对账 ⇒ 自洽。</description></item>
/// <item><description><b>RAR4</b>：搜 <c>52 61 72 21 1A 07 00</c> 签名，读随后的 13 字节主头，
/// 校验 16 位 CRC 且 <c>MHD_VOLUME = 0</c> ⇒ 完整单卷（分卷片一律不认，交第二大步）。</description></item>
/// </list>
///
/// <para>⚠ RAR5 的加密/分卷头用 vint 编码、解析成本高，这一版**不做**（返回 null = 判不出）——
/// 宁可判不出，也不拿"差不多"当结论。</para>
///
/// <para><b>本类只留策略</b>：先问谁、什么条件下退回盲扫、两个公开入口共用一次扫描。
/// 四个内聚块在协作类里 —— 搜签名 = <see cref="SignatureWindowScan"/>；
/// 三种格式的自洽校验 = <see cref="ZipEmbeddedProbe"/> / <see cref="SevenZipEmbeddedProbe"/> /
/// <see cref="Rar4EmbeddedProbe"/>；媒体段长度（偏移解析）= <see cref="MediaContainerExtent"/>；
/// 探测结果与记忆化 = <see cref="EmbeddedArchiveProbe"/> / <see cref="EmbeddedArchiveProbeCache"/>。</para>
/// </summary>
public static class EmbeddedArchiveLocator
{
    /// <summary>
    /// 试着在文件里定位一份**尾部单卷归档**。找到 ⇒ 返回命中（<c>Offset &gt; 0</c>）；
    /// 找不到 / 判不出 / 偏移为 0（那就是普通单卷，由既有那条路回答）⇒ null。
    /// </summary>
    public static EmbeddedArchiveHit? TryLocate(string? path)
    {
        return TryProbe(path, out EmbeddedArchiveProbe probe) ? probe.Hit : null;
    }

    /// <summary>
    /// **媒体结构量到哪为止**（只读事实，不猜）。前缀是媒体、且媒体段正好占满整个文件 ⇒
    /// <paramref name="mediaEnd"/> == <paramref name="fileLength"/>；媒体结构后面还有数据、
    /// 或根本不是媒体容器 ⇒ 调用方按"判不出"处理（⛔ 不把带尾巴的媒体当成普通内容物）。
    /// </summary>
    public static bool TryGetMediaExtent(string? path, out long mediaEnd, out long fileLength)
    {
        mediaEnd = 0;
        fileLength = 0;

        if (!TryProbe(path, out EmbeddedArchiveProbe probe))
        {
            return false;
        }

        mediaEnd = probe.MediaEnd;
        fileLength = probe.FileLength;

        return mediaEnd > 0 && fileLength > 0;
    }

    /// <summary>
    /// 这个名字是不是**媒体容器后缀**（只决定"要不要按媒体结构去量"，⛔ 不是内容结论）。
    /// </summary>
    public static bool IsMediaContainerName(string? path)
        => !string.IsNullOrWhiteSpace(path) && MediaContainerExtent.MatchesFileName(path!);

    /// <summary>
    /// 两个公开入口共用**同一次**扫描结果（键 = 路径 + 字节数 + 修改时间）。
    /// ⛔ 各自再扫一遍会把正常媒体读两次。
    /// </summary>
    private static bool TryProbe(string? path, out EmbeddedArchiveProbe probe)
    {
        return EmbeddedArchiveProbeCache.TryGetOrCompute(path, LocateUncached, out probe);
    }

    /// <summary>
    /// 探测策略本体：**只决定"先问谁、什么时候退回盲扫、命中哪一档就收工"**，判据都在协作类里。
    /// 返回的是"这一次探测的全部事实"，而不是单个命中 —— 媒体长度与命中必须来自同一次扫描。
    /// </summary>
    private static EmbeddedArchiveProbe LocateUncached(string path)
    {
        long fileLength;

        try
        {
            fileLength = new FileInfo(path).Length;
        }
        catch
        {
            return default;
        }

        if (fileLength <= 0)
        {
            return new EmbeddedArchiveProbe(null, 0, fileLength);
        }

        // ① ZIP：既有那份唯一实现（EOCD 偏移自洽），⛔ 不重写第二份。
        if (ZipEmbeddedProbe.TryLocate(path, out EmbeddedArchiveHit zipHit))
        {
            return new EmbeddedArchiveProbe(zipHit, 0, fileLength);
        }

        /*
         * ② **媒体容器锚点**（2026-10-22 用户口径：「可以把加媒体前缀的情况就锁定在 MP4 和 png、jpg 等」）：
         * 先按媒体容器自己的结构把"媒体段"读完，媒体结束处就是归档起点；再在那个偏移上做格式自洽校验。
         * 锚点解析成功且媒体没占满整个文件 ⇒ 归档起点**已知**，⛔ 不再盲扫（省掉对普通大文件的整段扫描）。
         */
        bool mediaName = MediaContainerExtent.MatchesFileName(path);
        long mediaEnd = 0;

        /*
         * 媒体身份**按魔数**，后缀只是兜底：`336.png` 实际是 RIFF/WEBP，
         * 按 `.png` 交给 PNG 解析器会直接失败，整份文件就被当成“认不出”的内容物。
         */
        bool mediaContainer = MediaContainerExtent.TryFindEnd(path, out mediaEnd) && mediaEnd > 0;

        if (mediaContainer)
        {
            /*
             * 媒体段自己就占满整个文件 ⇒ 后面根本没有归档（普通 mp4/jpg/png）⇒ 当场判"没有"，
             * ⛔ 不退回深扫（否则一个正常的 60 MB 视频每次识别都要被整段读一遍）。
             */
            if (mediaEnd >= fileLength)
            {
                return new EmbeddedArchiveProbe(null, mediaEnd, fileLength);
            }

            if (SevenZipEmbeddedProbe.TryValidateAt(
                path, mediaEnd, fileLength, out long anchoredLength, out string anchoredEvidence))
            {
                return new EmbeddedArchiveProbe(
                    new EmbeddedArchiveHit(
                        mediaEnd, anchoredLength, VolumeContentFormat.SevenZip, anchoredEvidence),
                    mediaEnd,
                    fileLength);
            }

            if (Rar4EmbeddedProbe.TryValidateSingleVolume(
                path, mediaEnd, fileLength, out string rarAnchoredEvidence))
            {
                return new EmbeddedArchiveProbe(
                    new EmbeddedArchiveHit(
                        mediaEnd, 0, VolumeContentFormat.Rar, rarAnchoredEvidence),
                    mediaEnd,
                    fileLength);
            }

            /*
             * RAR5 主头的媒体前缀：批样本里的 `.jpg`/`.mp4` 外包装可能就是这种形状。
             * 只验主头 CRC/结构；⛔ 不在这里判卷号，后续仍走 RAR5 正常路。
             */
            if (VolumeNumberFromContent.TryValidateRar5At(path, mediaEnd, out _))
            {
                return new EmbeddedArchiveProbe(
                    new EmbeddedArchiveHit(
                        mediaEnd, 0, VolumeContentFormat.Rar, "RAR5 主头自洽（媒体前缀）"),
                    mediaEnd,
                    fileLength);
            }

            // 前缀确实是媒体，但它后面既不是 7z 单卷也不是 RAR4/RAR5 单卷 ⇒ 判不出（⛔ 不猜、不盲扫）。
            return new EmbeddedArchiveProbe(null, mediaEnd, fileLength);
        }

        // ③ 7z / RAR4：快扫（文件尾 4 MiB，见 SignatureWindowScan.TailWindowBytes）。
        if (SevenZipEmbeddedProbe.TryLocate(path, fileLength, deep: false, out EmbeddedArchiveHit sevenZipHit))
        {
            return new EmbeddedArchiveProbe(sevenZipHit, 0, fileLength);
        }

        if (Rar4EmbeddedProbe.TryLocate(path, fileLength, deep: false, out EmbeddedArchiveHit rarHit))
        {
            return new EmbeddedArchiveProbe(rarHit, 0, fileLength);
        }

        /*
         * ④ 深扫（≤512 MiB，见 SignatureWindowScan.DeepWindowBytes）**只对媒体后缀**：
         * 媒体锚点解析失败（脏媒体 / 截断）时才退回盲扫；
         * 非媒体后缀一律不做深扫 —— 否则内容物里的大文件（60 MB 级）每次识别都要被整段读一遍。
         */
        if (mediaName)
        {
            if (SevenZipEmbeddedProbe.TryLocate(path, fileLength, deep: true, out sevenZipHit))
            {
                return new EmbeddedArchiveProbe(sevenZipHit, 0, fileLength);
            }

            if (Rar4EmbeddedProbe.TryLocate(path, fileLength, deep: true, out rarHit))
            {
                return new EmbeddedArchiveProbe(rarHit, 0, fileLength);
            }
        }

        return new EmbeddedArchiveProbe(null, 0, fileLength);
    }
}

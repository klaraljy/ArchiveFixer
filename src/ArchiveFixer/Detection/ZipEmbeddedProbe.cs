namespace ArchiveFixer.Detection;

/// <summary>
/// **ZIP 候选的判定**：转调既有唯一实现 <see cref="EmbeddedArchiveDetector"/> ——
/// 它用 EOCD 声明的中央目录偏移与实际位置的差值做自洽校验，并已区分"跨盘末片"。
///
/// <para>⚠ 本类**不写第二份 ZIP 判据**，只补本算法需要的两条口径：
/// 偏移 0 = 普通单卷（不归这条路管，由既有那条路回答）、跨盘 / 分卷片 = 交第二大步。</para>
/// </summary>
internal static class ZipEmbeddedProbe
{
    /// <summary>找一份"前面垫了东西"的尾部 ZIP；偏移 0 / 分卷片 / 判不出 ⇒ false。</summary>
    internal static bool TryLocate(string path, out EmbeddedArchiveHit hit)
    {
        hit = default;

        EmbeddedArchiveInfo info;

        try
        {
            info = EmbeddedArchiveDetector.Detect(path);
        }
        catch
        {
            return false;
        }

        if (!info.Found || info.Offset <= 0 || info.IsVolumePart)
        {
            // 偏移 0 = 普通单卷（不归这里管）；跨盘片 = 分卷，交第二大步。
            return false;
        }

        hit = new EmbeddedArchiveHit(
            info.Offset,
            info.ArchiveLength,
            VolumeContentFormat.Zip,
            $"ZIP 尾部自洽：归档起点 {info.Offset}，区间 {info.ArchiveLength} 字节");

        return true;
    }
}

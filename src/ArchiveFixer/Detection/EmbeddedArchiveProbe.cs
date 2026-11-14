namespace ArchiveFixer.Detection;

/// <summary>
/// 一次定位探测的**完整结果**：尾部命中了什么、媒体段量到哪、文件多长。
///
/// <para>为什么这三个量要放一起：定位与媒体长度共用**同一次**扫描
/// （<see cref="EmbeddedArchiveProbeCache"/> 按路径 + 字节数 + 修改时间缓存它），
/// ⛔ 两个公开入口各自再扫一遍会把正常媒体读两次。</para>
/// </summary>
/// <param name="Hit">尾部单卷归档命中（<c>Offset &gt; 0</c>）；null = 没有 / 判不出。</param>
/// <param name="MediaEnd">媒体段结束位置（0 = 不是媒体容器 / 判不出）。</param>
/// <param name="FileLength">探测时的文件长度（0 = 读不到）。</param>
internal readonly record struct EmbeddedArchiveProbe(
    EmbeddedArchiveHit? Hit,
    long MediaEnd,
    long FileLength);

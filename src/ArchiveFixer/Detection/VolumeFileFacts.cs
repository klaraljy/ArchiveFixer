using System.IO;

namespace ArchiveFixer.Detection;

/// <summary>
/// 本模块唯一的"文件事实"读取口：大小与文件名（内容解码、卷号推断、证据建模三层共用）。
///
/// <para><b>为什么单独一层</b>：候选、两张门票、兄弟卷形状读数都要问"这个文件多大 / 叫什么名字"，
/// 而"谁可能是末卷"必须在这三处给出**同一个答案** —— 读数口径一旦各写一份，漂移迟早冒出来。
/// 这一层同时固定失败模型：读不到（不存在 / 被占 / 权限 / 路径非法）⇒ 大小给 <c>0</c>、
/// 名字给空串，**绝不抛**；调用方一律按"判不出 ⇒ 不试开、不动"处理。</para>
///
/// <para>生命周期：纯静态、无状态、不缓存 —— 每次调用现读，保证与磁盘同步；
/// 不持有句柄（<see cref="FileInfo.Length"/> 只在调用瞬间读元数据）。</para>
/// </summary>
internal static class VolumeFileFacts
{
    /// <summary>文件字节数；读不到（含空路径）⇒ <c>0</c>，绝不抛。</summary>
    public static long SizeOf(string? path)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path) ? 0 : new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>文件名（不含目录）；读不到（含空路径）⇒ 空串，绝不抛。</summary>
    public static string SafeFileName(string? path)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFileName(path);
        }
        catch
        {
            return string.Empty;
        }
    }
}

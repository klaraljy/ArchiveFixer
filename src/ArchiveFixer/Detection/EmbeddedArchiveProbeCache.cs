using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;

namespace ArchiveFixer.Detection;

/// <summary>
/// 定位探测的**记忆化**（结果建模的一部分）：键 = 路径 + 字节数 + 修改时间（tick）。
///
/// <para>为什么必须有这一层：定位（尾部归档）与媒体长度共用**同一次**扫描 ——
/// 两个公开入口各自再扫一遍，等于把一份正常媒体读两次；
/// 而这个探测会在识别阶段被每个"认不出格式"的文件问到。</para>
///
/// <para>这是**有界旁路缓存**，不是事实来源：条数超过 <see cref="MaxEntries"/> 就整表清空
/// （⛔ 不做逐条淘汰 —— 探测结果随时可以重算，缓存丢了只是慢一点）；键里带字节数与修改时间，
/// 所以文件被替换后不会命中旧结论。</para>
///
/// <para>路径读不到（不存在 / 元数据取不出）⇒ <c>false</c>：判不出，⛔ 不猜。</para>
/// </summary>
internal static class EmbeddedArchiveProbeCache
{
    /// <summary>缓存条数上限（超过就整表清空）。</summary>
    private const int MaxEntries = 512;

    private static readonly ConcurrentDictionary<string, EmbeddedArchiveProbe> s_cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 命中就直接给结果；没命中就用 <paramref name="compute"/> 算一次再放回去。
    /// </summary>
    internal static bool TryGetOrCompute(
        string? path,
        Func<string, EmbeddedArchiveProbe> compute,
        out EmbeddedArchiveProbe probe)
    {
        probe = default;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        string cacheKey;

        try
        {
            var info = new FileInfo(path);

            cacheKey = path + "|"
                + info.Length.ToString(CultureInfo.InvariantCulture) + "|"
                + info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
        }
        catch
        {
            return false;
        }

        if (s_cache.TryGetValue(cacheKey, out probe))
        {
            return true;
        }

        probe = compute(path);

        if (s_cache.Count > MaxEntries)
        {
            s_cache.Clear();
        }

        s_cache[cacheKey] = probe;

        return true;
    }
}

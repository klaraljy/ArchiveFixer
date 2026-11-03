using ArchiveFixer.Helpers;

namespace ArchiveFixer.Detection
{
    /// <summary>
    /// 卷名解析的唯一对外入口（阶段 7 收口）：
    /// 名字里有没有卷号、第 1 卷叫什么、是不是分卷片、两卷是否同组、规范名怎么取。
    ///
    /// <para>底层仍复用既有事实实现（<see cref="VolumeGroupDetector"/>、<see cref="FileNameHelper"/>、
    /// <see cref="VolumeGroupResolver"/>）；⛔ 新调用点只许走这里，不许再各自挑一个入口。</para>
    /// </summary>
    public static class LegacyVolumeNames
    {
        public static int? TryGetIndex(string? fileName) =>
            string.IsNullOrWhiteSpace(fileName) ? null : ArchiveFixer.Detection.VolumeGroupDetector.TryGetVolumeIndex(fileName!);

        public static string? TryGetFirstVolumeName(string? fileName) =>
            string.IsNullOrWhiteSpace(fileName) ? null : ArchiveFixer.Detection.VolumeGroupDetector.TryGetFirstVolumeName(fileName!);

        public static bool IsVolumePart(string? fileName) =>
            !string.IsNullOrWhiteSpace(fileName) && ArchiveFixer.Helpers.FileNameHelper.IsVolumePartFileName(fileName);

        public static bool BelongsToSameGroup(string? fileNameA, string? fileNameB) =>
            !string.IsNullOrWhiteSpace(fileNameA)
            && !string.IsNullOrWhiteSpace(fileNameB)
            && ArchiveFixer.Detection.VolumeGroupDetector.BelongsToSameGroup(fileNameA!, fileNameB!);

        public static bool TryGetCanonicalName(string? fileName, out string canonical)
        {
            canonical = string.Empty;
            return !string.IsNullOrWhiteSpace(fileName)
                && ArchiveFixer.Detection.VolumeGroupResolver.TryGetCanonicalVolumeName(fileName!, out canonical);
        }
    }
}

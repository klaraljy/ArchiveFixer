using ArchiveFixer.Models;

namespace ArchiveFixer.Security
{
    /// <summary>
    /// 资源预算上限。全部是硬上限，不猜意图（AGENTS.md §2：不做压缩炸弹的"智能判定"）。
    ///
    /// <para>⚠ 2026-09-25 第 36 条改口径：默认值不再是"偏保守"，四条全部**来自用户的设置**
    /// （<see cref="FromSettings"/>，界面在⑥设置 →「安全上限」）。旧默认 4 GiB 单文件 / 20 GiB 总量
    /// 把他一个正常的 12 GiB 分卷包拦在门外，而界面上一个字都没有 —— 他看到的是一句像
    /// "这个包有问题"的判决。<b>上限是必须有的（不变量 8），但用户得看得见、改得动。</b></para>
    /// </summary>
    public sealed class ResourceBudgetOptions
    {
        /// <summary>1 GiB 的字节数（下面四条默认值都用它换算，数字只写一次）。</summary>
        private const long Gib = 1024L * 1024 * 1024;

        /// <summary>解压后总大小上限，默认 512 GiB（用户设置的默认值）。</summary>
        public long MaxTotalSize { get; init; } = AppSettings.DefaultMaxExtractedTotalGiB * Gib;

        /// <summary>单个文件解压后大小上限，默认 64 GiB（用户设置的默认值）。</summary>
        public long MaxSingleFileSize { get; init; } = AppSettings.DefaultMaxSingleExtractedFileGiB * Gib;

        /// <summary>文件数上限，默认 20 万。</summary>
        public int MaxFileCount { get; init; } = AppSettings.DefaultMaxExtractedFileCount;

        /// <summary>展开比上限（解压后 / 压缩包），默认 1000 倍，超过视为压缩炸弹。</summary>
        public double MaxExpansionRatio { get; init; } = AppSettings.DefaultMaxExtractionRatio;

        /// <summary>目标盘需要保留的空闲字节数，默认 512 MiB。</summary>
        public long MinFreeSpaceReserveBytes { get; init; } = 512L * 1024 * 1024;

        /// <summary>默认上限。init-only 属性 + 不可变对象，所以可以安全地在多处共享同一个实例。</summary>
        public static ResourceBudgetOptions Default { get; } = new();

        /// <summary>
        /// 从用户设置算出这一批真正要用的四条上限（**唯一**的换算与钳制入口）。
        ///
        /// <para>为什么钳制放在这里也做一遍：<see cref="AppSettings.Normalize"/> 只在"读盘 / 保存"时跑，
        /// 而测试与内部调用可以拿到一个没归一化过的 <see cref="AppSettings"/>。判包这件事
        /// 宁可多夹一次，也不能让一个 0 或负数把上限变成"什么都不许解"。</para>
        /// </summary>
        public static ResourceBudgetOptions FromSettings(AppSettings? settings)
        {
            if (settings == null)
            {
                return Default;
            }

            int singleGiB = AppSettings.NormalizeSingleFileCapGiB(settings.MaxSingleExtractedFileGiB);
            int totalGiB = AppSettings.NormalizeTotalCapGiB(settings.MaxExtractedTotalGiB, singleGiB);

            return new ResourceBudgetOptions
            {
                MaxSingleFileSize = singleGiB * Gib,
                MaxTotalSize = totalGiB * Gib,
                MaxFileCount = AppSettings.NormalizeExtractedFileCountCap(settings.MaxExtractedFileCount),
                MaxExpansionRatio = AppSettings.NormalizeExtractionRatioCap(settings.MaxExtractionRatio)
            };
        }

        /// <summary>
        /// 一句话说清这次用的四条上限（批首日志与失败文案共用同一份口径）。
        /// ⛔ 不许各处自己拼一串数字：口径分叉过一次（界面说 4 GiB、代码里是 20 GiB）就再也对不上。
        /// </summary>
        public string Describe()
        {
            return $"单文件 {ResourceBudget.FormatSize(MaxSingleFileSize)} / 总大小 {ResourceBudget.FormatSize(MaxTotalSize)}"
                + $" / 文件数 {MaxFileCount} / 展开比 {ResourceBudget.FormatRatio(MaxExpansionRatio)} 倍";
        }
    }
}

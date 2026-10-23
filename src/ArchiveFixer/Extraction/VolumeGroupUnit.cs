using System;
using System.Collections.Generic;
using ArchiveFixer.Detection;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// **一组分卷在程序里的一等事实**（2026-10-18 提升，用户口径：先把「组」变成程序里的一等事实，
    /// 再回到 30 份样本）。
    ///
    /// <para>它只回答组级问题：这一组是谁、源层在哪、入口是哪一份、顺序试开赢的是哪一版、谁负责开这一组。
    /// 片/文件本身的事实仍只来自 <see cref="FileNatureCard"/>；这里 ⛔ 不重新读文件、⛔ 不猜名字。</para>
    ///
    /// <para>生命周期：**组齐那一刻**由调用方创建一次；同组后续任务只查这一份，不再各自从
    /// <c>CurrentPath</c>/名字重算 `entryLayer`、owner、entry。</para>
    /// </summary>
    public sealed class VolumeGroupUnit
    {
        public VolumeGroupUnit(
            string baseName,
            VolumeNamingFamily? family,
            string sourceLayer,
            string entryPath)
        {
            BaseName = baseName ?? string.Empty;
            Family = family;
            SourceLayer = sourceLayer ?? string.Empty;
            EntryPath = entryPath ?? string.Empty;
            CreatedUtc = DateTime.UtcNow;
        }

        /// <summary>组基名（名字只当键，不当判据）。</summary>
        public string BaseName { get; }

        /// <summary>内容判定的命名族；判不出 = null。</summary>
        public VolumeNamingFamily? Family { get; }

        /// <summary>
        /// **源层**：这一组应该在哪里开、产物跟着哪一层走。由组齐那一刻的产出方落点/第 1 卷所在层定下，
        /// 之后搬移/暂存/清理都 ⛔ 不改它（`其余物`/工作区永远不能成为源层）。
        /// </summary>
        public string SourceLayer { get; }

        /// <summary>入口那一份的路径（组齐那一刻的入口；只用于显示/起点，不替代源层）。</summary>
        public string EntryPath { get; }

        private string? _ownerTaskFileName;

        /// <summary>
        /// 唯一 owner 的任务文件名；null = 还没定。**一旦定下，不许再改给另一个任务**
        /// （同一组两个 owner 就是 `4444` 那种“互相说对方解开”的环）。
        /// </summary>
        public string? OwnerTaskFileName
        {
            get => _ownerTaskFileName;
            set
            {
                if (_ownerTaskFileName != null
                    && value != null
                    && !string.Equals(_ownerTaskFileName, value, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"这一组已有 owner「{_ownerTaskFileName}」，不能再交给「{value}」");
                }

                _ownerTaskFileName = value;
            }
        }

        /// <summary>试开确认的顺序（物理路径，末片在最后）；null = 没试开/没确认。</summary>
        public IReadOnlyList<string>? ConfirmedOrder { get; set; }

        /// <summary>这一组每一片来自哪里；建组时写入，收场器只查这份账。</summary>
        public IReadOnlyList<VolumePieceFact> PieceOrigins { get; set; } = Array.Empty<VolumePieceFact>();

        public DateTime CreatedUtc { get; }

        /// <summary>同一基名下两族不许混算 ⇒ 键带族。</summary>
        public string Key => BaseName + "|" + (Family?.ToString() ?? "?");
    }
}

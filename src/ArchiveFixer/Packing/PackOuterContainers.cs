using ArchiveFixer.Models;

namespace ArchiveFixer.Packing
{
    /// <summary>外层容器的文案与派生规则（**唯一来源**；界面、日志、失败原因都引这里）。</summary>
    public static class PackOuterContainers
    {
        /// <summary>会不会产出一个外层容器文件（<see cref="PackOuterContainer.None"/> 不会）。</summary>
        public static bool HasOuterArtifact(this PackOuterContainer container)
        {
            return container != PackOuterContainer.None;
        }

        /// <summary>界面选项上的完整说法（含"要不要额外安装"这句关键信息）。</summary>
        public static string Describe(this PackOuterContainer container)
        {
            return container switch
            {
                PackOuterContainer.SevenZip => StatusText.PackOuterSevenZipText,
                PackOuterContainer.None => StatusText.PackOuterNoneText,
                _ => StatusText.PackOuterRarText
            };
        }

        /// <summary>日志 / 摘要里的短名（<c>rar</c> / <c>7z</c> / <c>不做外层</c>）。</summary>
        public static string ShortName(this PackOuterContainer container)
        {
            return container switch
            {
                PackOuterContainer.SevenZip => StatusText.PackOuterSevenZipShort,
                PackOuterContainer.None => StatusText.PackOuterNoneShort,
                _ => StatusText.PackOuterRarShort
            };
        }

        /// <summary>
        /// 句子里指代外层产物时的名词（"结果 rar 不能放在…" / "外层 7z 不能放在…"）。
        /// 不留空：调用方都是"确实有外层产物"的分支。
        /// </summary>
        public static string Noun(this PackOuterContainer container)
        {
            return container switch
            {
                PackOuterContainer.SevenZip => "外层 7z",
                PackOuterContainer.None => "外层容器",
                _ => "结果 rar"
            };
        }
    }
}

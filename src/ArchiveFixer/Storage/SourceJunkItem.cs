namespace ArchiveFixer.Storage
{
    /// <summary>一个"可能的无用物"（打包者附带的说明 / 网址 / 工具 / 广告之类）。</summary>
    public sealed class SourceJunkItem
    {
        /// <summary>它所在的那个源目录（提示里按目录归组，用户一眼知道去哪儿看）。</summary>
        public string DirectoryPath { get; init; } = string.Empty;

        /// <summary>文件名（只给名字不给完整路径：提示要短，目录那一行已经说明了在哪）。</summary>
        public string FileName { get; init; } = string.Empty;

        /// <summary>完整路径（日志与排障用）。</summary>
        public string FullPath { get; init; } = string.Empty;

        /// <summary>
        /// 它是个**目录**（2026-09-25 第 32 条加）：真实资源包常把密码提示做成一个文件夹
        /// （用户的素材就是 <c>解压密码为：example.com</c>），那种条目要标出来给人看，
        /// 免得提示里"说明.txt"和"某个文件夹"混在一起分不清。
        /// </summary>
        public bool IsDirectory { get; init; }
    }
}

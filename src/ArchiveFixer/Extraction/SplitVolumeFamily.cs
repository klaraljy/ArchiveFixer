namespace ArchiveFixer.Extraction
{
    /// <summary>分卷的"族" —— 只用来回答"容器里那一段和外面那一组是不是同一族"。</summary>
    public enum SplitVolumeFamily
    {
        /// <summary>认不出来（既不是 7z / RAR / ZIP 的头，名字也推不出族）。</summary>
        Unknown,

        /// <summary>7z 分卷（<c>set.7z.001</c> / <c>set.7z.002</c>…）。</summary>
        SevenZip,

        /// <summary>RAR 分卷（新式 <c>set.part1.rar</c>，老式 <c>set.rar</c> + <c>set.r00</c>）。</summary>
        Rar,

        /// <summary>ZIP 分卷（<c>set.zip</c> + <c>set.z01</c>，或 <c>set.zip.001</c> 这一套）。</summary>
        Zip
    }
}


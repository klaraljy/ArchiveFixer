namespace ArchiveFixer.Extraction
{
    public enum SpecificLayerKind
    {
        /// <summary>直接就是内容物（图片/视频/普通文件）。</summary>
        Content,

        /// <summary>根上只有一个完整标准压缩包，继续下一层。</summary>
        SingleArchive,

        /// <summary>根上是同一组标准分卷，接片后继续下一层。</summary>
        StandardVolumeGroup,

        /// <summary>形状超出特定解压支持的三类，停下。</summary>
        Unsupported
    }
}

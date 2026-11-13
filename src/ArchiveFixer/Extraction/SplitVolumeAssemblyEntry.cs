namespace ArchiveFixer.Extraction
{
    /// <summary>拼装计划里的一条：把哪个文件以什么名字接进工作区。</summary>
    public sealed class SplitVolumeAssemblyEntry
    {
        /// <summary>源文件（**只读**：硬链接/复制的来源，绝不改动、绝不删除）。</summary>
        public string SourcePath { get; init; } = string.Empty;

        /// <summary>接进工作区之后用的文件名（后续卷 = 它原来的名字；首卷 = 标准首卷名）。</summary>
        public string TargetName { get; init; } = string.Empty;

        /// <summary>源文件字节数（拼完要逐个核对，见 <see cref="SplitVolumeAssembler.TryAssemble"/>）。</summary>
        public long Length { get; init; }

        /// <summary>这一条是不是"容器里装着的第 1 卷"。</summary>
        public bool IsFirstVolume { get; init; }
    }
}


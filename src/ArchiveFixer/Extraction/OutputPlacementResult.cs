namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 落点解析结果。失败时 <see cref="DestinationDirectory"/> 为空，**绝不静默回退到别的目录**。
    /// </summary>
    public sealed class OutputPlacementResult
    {
        /// <summary>是否解析成功。</summary>
        public bool Success { get; init; }

        /// <summary>失败原因（成功时为 <see cref="OutputPlacementError.None"/>）。</summary>
        public OutputPlacementError Error { get; init; }

        /// <summary>给用户看的一句话（失败原因 / 落点说明）。</summary>
        public string Message { get; init; } = string.Empty;

        /// <summary>目标根：未指定位置那一档是源包所在目录，指定位置那一档是自定义根。</summary>
        public string DestinationRoot { get; init; } = string.Empty;

        /// <summary>最终落点目录（规格 §1.1 的 <c>destDir</c>）。</summary>
        public string DestinationDirectory { get; init; } = string.Empty;

        /// <summary>包基名（已清洗，可直接当文件夹名）。</summary>
        public string ArchiveBaseName { get; init; } = string.Empty;

        /// <summary>最终落点那一层文件夹的名字（就是上面那条规则算出来的"包名"）。</summary>
        public string PackageFolderName { get; init; } = string.Empty;

        /// <summary>
        /// 落点那一层用的是**用户选中的文件夹名**（而不是包基名）—— 四条规则里的"文件夹 + 指定位置"。
        /// </summary>
        public bool UsesSelectedFolderName { get; init; }

        /// <summary>
        /// 从**选中的那个文件夹**到"这个包所在目录"的那一段相对子路径（2026-09-27 用户指示，方案 A）。
        ///
        /// <para>用户原话：<i>"我选中了这个 AAA……里面有五个子文件夹，而且每个都有确切的名字，
        /// 你解压的情况就是将这五个都删掉，而后再将这里面的东西全部拿出来了，这不就导致了文件夹混乱了吗，
        /// 你应该在各自的子文件夹里面操作"</i>。所以这一段**原样保留**：
        /// <c>AAA\新建文件夹_20260916_152637\解压软件\rar-android-722.132.apk</c> 里的包 →
        /// <c>BBB\AAA\新建文件夹_20260916_152637\解压软件\&lt;包基名&gt;\</c>。</para>
        ///
        /// <para>包**直接躺在**选中文件夹里时为空串（那就是老行为：<c>BBB\AAA\&lt;包基名&gt;\</c>）。
        /// 包不在选中文件夹之下（理论上不该发生）时同样为空串 —— 宁可少一层，也不拼出一个跑到别处的路径。</para>
        /// </summary>
        public string RelativeSubPath { get; init; } = string.Empty;

        /// <summary>
        /// 这个落点目录是**同一次导入里的多个包共用**的。
        ///
        /// <para>
        /// ⚠ <b>2026-09-27 起这一档不再出现</b>（用户指示方案 A）："选文件夹 + 指定位置"以前是
        /// 文件夹里每个包都落进同一个 <c>BBB\222\</c>，现在是
        /// <c>BBB\222\&lt;相对子路径&gt;\&lt;包基名&gt;\</c> —— **每包一个目录**。
        /// 保留这个字段是为了让管线里那两处判据（"目录已存在且非空要不要让开"、
        /// "其余物要不要按包名分层"）继续有唯一出口，只是它现在恒为 false。
        /// </para>
        /// </summary>
        public bool SharesDestinationWithOtherPackages { get; init; }

    }
}

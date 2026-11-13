namespace ArchiveFixer.Services
{
    /// <summary>
    /// 删除档位之外的**作用域档位**：只删当前勾选任务那一份，还是删整个共享目录下的全部。
    ///
    /// <para>
    /// 拆成独立枚举而不是往 <see cref="CleanupScope"/> 里再塞一个值：作用域类型（其余物 / 空文件夹）
    /// 与作用域大小（本任务 / 整个目录）是两个正交的问题，混在一起会让枚举变成乘积。
    /// </para>
    /// </summary>
    public enum ArtifactDeleteScope
    {
        /// <summary>只删当前勾选任务自己那一份（<b>默认，也是唯一安全的档</b>）。</summary>
        SelectedTask = 0,

        /// <summary>删掉当前共享目录下的全部其余物（会影响同目录里的其它包，默认不选）。</summary>
        EverythingInDirectory = 1
    }
}

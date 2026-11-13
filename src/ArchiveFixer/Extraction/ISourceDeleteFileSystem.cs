namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 删除动作的唯一出口（用户 2026-09-24 要求：删除裁决要能被测试钉死）。
    ///
    /// <para>
    /// 为什么要留这个接缝：那条红线是"该不删的时候一次都不能调用删除"，
    /// 而"有没有调用"只有把执行体换成记账的假实现才能断言（源码里看 <c>File.Delete</c> 看不出运行时走没走到）。
    /// 与 <c>ISourceMoveFileSystem</c>（源包搬运）同一套做法，产品代码一行都不需要知道它存在。
    /// </para>
    /// </summary>
    public interface ISourceDeleteFileSystem
    {
        bool FileExists(string path);

        /// <summary>删除**一个**文件（不递归、不跟随联接点）。失败时抛异常，由调用方记成"删不掉"。</summary>
        void DeleteFile(string path);
    }
}


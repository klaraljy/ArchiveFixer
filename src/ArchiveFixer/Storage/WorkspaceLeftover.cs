using System;

namespace ArchiveFixer.Storage
{
    /// <summary>工作区里剩下来的一个任务目录（失败 / 取消 / 部分完成留下的）。</summary>
    public sealed class WorkspaceLeftover
    {
        /// <summary>目录全路径。</summary>
        public string DirectoryPath { get; init; } = string.Empty;

        /// <summary>
        /// 这个目录属于**哪个工作区根**（默认跟输出盘之后根会随输出盘变，所以"它在哪"必须逐条记下来）。
        /// 清理时按它对回各自的根做容器内校验（<see cref="WorkspaceCleanupService.Cleanup"/>）。
        /// </summary>
        public string RootDirectory { get; init; } = string.Empty;

        /// <summary>目录名（就是任务工作区的标识，形如 <c>包名-源路径短哈希</c>）。</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>里面的文件数（递归）。</summary>
        public int FileCount { get; init; }

        /// <summary>里面的字节数（递归）。</summary>
        public long TotalBytes { get; init; }

        /// <summary>最后一次写入时间（用来判断"放多久了"）。</summary>
        public DateTime LastWriteTimeUtc { get; init; }

        /// <summary>量不出来（权限 / 路径过长 / 枚举到一半被删）——**不假装它是 0 字节**。</summary>
        public bool MeasurementFailed { get; init; }
    }
}

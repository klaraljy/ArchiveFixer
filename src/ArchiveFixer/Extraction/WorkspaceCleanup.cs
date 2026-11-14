using ArchiveFixer.Storage;

namespace ArchiveFixer.Extraction;

/// <summary>
/// 工作区的**收尾清理**：只删本任务工作区那一棵树，删之前按根目录包含关系再确认一次。
///
/// <para>三道防线（判据逐字沿用原有<see cref="ExtractionWorkspace.Cleanup"/>）：
/// ① 删除前确认任务目录确实位于工作区根之下（相等也不行 —— 那等于把整个根端掉），
/// 不满足就**什么都不删**；② 目录不存在时直接返回，不"顺手"删父目录；
/// ③ 删非空目录用递归删除，删不掉（占用 / 权限）只如实报告，**不抛异常**。</para>
///
/// <para>删除失败时**不清空**层清单：那些层目录还在盘上，清单必须继续如实描述现状。</para>
/// </summary>
internal static class WorkspaceCleanup
{
    /// <summary>
    /// 删除工作区，并如实回答"删了没有、为什么"。
    /// 只在调用方**确认后**调用（成功且产物已发布；失败 / 取消 / 部分完成时不许调）。
    /// </summary>
    /// <param name="layersInvalidated">
    /// true = 工作区确实不在了（删掉或本来就不存在），调用方应当丢掉层清单；
    /// false = 路径越界没动手 / 删除失败，层清单继续有效。
    /// </param>
    internal static WorkspaceCleanupResult Remove(
        string rootDirectory,
        string taskDirectory,
        out bool layersInvalidated)
    {
        layersInvalidated = false;

        if (!WorkspacePathRules.IsInsideRoot(rootDirectory, taskDirectory))
        {
            // 注意：这里**不抛异常**。路径越界意味着工作区的身份本身可疑，
            // 此时最有价值的动作就是"一个字节都不动"，让用户自己去看。
            return new WorkspaceCleanupResult
            {
                TaskDirectory = taskDirectory,
                Cleaned = false,
                Message = $"工作区目录不在工作区根目录之下，未删除任何东西：{taskDirectory}"
            };
        }

        if (!WorkspacePathRules.SafeDirectoryExists(taskDirectory))
        {
            layersInvalidated = true;

            return new WorkspaceCleanupResult
            {
                TaskDirectory = taskDirectory,
                Cleaned = true,
                Message = $"工作区目录已不存在（可能已经被清理过）：{taskDirectory}"
            };
        }

        if (FileOperations.TryDeleteDirectory(taskDirectory, out string deleteError))
        {
            layersInvalidated = true;

            return new WorkspaceCleanupResult
            {
                TaskDirectory = taskDirectory,
                Cleaned = true,
                Message = $"工作区已清理：{taskDirectory}"
            };
        }

        // 目录里有文件被占用、权限不足等等：留着就是了，不影响任务结论。
        return new WorkspaceCleanupResult
        {
            TaskDirectory = taskDirectory,
            Cleaned = false,
            Message = $"清理工作区失败（{deleteError}），目录保留：{taskDirectory}"
        };
    }
}

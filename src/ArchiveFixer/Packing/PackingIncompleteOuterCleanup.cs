using ArchiveFixer.Helpers;
using ArchiveFixer.Storage;
using System;
using System.IO;

namespace ArchiveFixer.Packing;

/// <summary>
/// 打包的**未完成产物清理**（<see cref="PackingService"/> 的协作类，职责：清理）。
///
/// <para>口径只有一条：**删的是本次没做完的那个外层容器**，而且只在"本次真的走到过它"之后调
/// （失败 / 取消两个分支）。⛔ 它绝不碰分卷、不碰源包、不碰落点目录里的别的文件 ——
/// 分卷要留着给用户重试（重试前请他自己删，见 <see cref="PackingProductCheck.DescribePartialVolumes"/>）。</para>
///
/// <para>删不掉也**不改结论**：只把实话写进日志，让用户自己删。</para>
/// </summary>
internal static class PackingIncompleteOuterCleanup
{
    /// <summary>删掉本次没做完的外层容器（**只在本次真的创建过它之后**调）。</summary>
    /// <param name="outerPath">外层容器的全路径。</param>
    /// <param name="outerName">句子里指代它的名字（<c>外层 rar</c> / <c>外层 7z</c>）。</param>
    /// <param name="runLog">日志出口。</param>
    internal static void Delete(string outerPath, string outerName, PackingRunLog runLog)
    {
        try
        {
            if (!File.Exists(outerPath))
            {
                return;
            }

            FileOperations.Delete(outerPath);
            runLog.Write($"已清理没做完的{outerName}：{outerPath}");
        }
        catch (Exception ex)
        {
            runLog.Write($"没做完的{outerName}删不掉（请自己删）：{outerPath} —— {PackPasswordGuard.Sanitize(ex.Message, null)}");
        }
    }
}

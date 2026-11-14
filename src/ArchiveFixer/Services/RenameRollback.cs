using System;
using System.Collections.Generic;
using System.IO;

namespace ArchiveFixer.Services;

/// <summary>
/// 改名两阶段的回滚原语：撤名字 / 腾位 / 落位三步各自的反向移动，以及"删不掉就留着"的兜底。
///
/// <para>从 <see cref="RenameExecution"/> 抽出的协作类（「回滚」）：只做反向 File.Move / File.Delete，
/// ⛔ 不判业务、不写任务状态；回滚本身失败只能到此为止（调用方把失败原因如实报给用户，
/// 数据不会丢 —— 文件就在原目录里，名字里带 .af-vacating / .af-staging）。</para>
/// </summary>
internal static class RenameRollback
{
    /// <summary>回滚"撤名字"：临时名挪回源路径。挪不动时保留临时文件（名字带 .af-staging，数据不丢）。</summary>
    internal static void RollBackStaged(
        List<(string OriginalPath, string TemporaryPath, string FinalPath)> staged)
    {
        for (int i = staged.Count - 1; i >= 0; i--)
        {
            TryMove(staged[i].TemporaryPath, staged[i].OriginalPath);
        }
    }

    /// <summary>回滚"腾位"：临时名挪回它占着的那个名字。</summary>
    internal static void RollBackVacated(
        List<(string OccupiedPath, string TemporaryPath, bool HasIdentity, ulong VolumeSerial, ulong FileIndex)> vacated)
    {
        for (int i = vacated.Count - 1; i >= 0; i--)
        {
            TryMove(vacated[i].TemporaryPath, vacated[i].OccupiedPath);
        }
    }

    /// <summary>回滚"落位"：已经落到最终名的文件挪回临时名（随后由 RollBackStaged 送回源路径）。</summary>
    internal static void RollBackLanded(List<(string TemporaryPath, string FinalPath)> landed)
    {
        for (int i = landed.Count - 1; i >= 0; i--)
        {
            TryMove(landed[i].FinalPath, landed[i].TemporaryPath);
        }
    }

    private static void TryMove(string from, string to)
    {
        try
        {
            if (File.Exists(from) && !File.Exists(to))
            {
                File.Move(from, to);
            }
        }
        catch
        {
            // 回滚本身失败只能到此为止：调用方会把失败原因如实报给用户，
            // 数据不会丢 —— 文件就在原目录里，名字里带 .af-vacating。
        }
    }

    internal static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 删不掉被覆盖的旧文件不影响"改名成功"这个结论，留给用户自己处理。
        }
    }
}

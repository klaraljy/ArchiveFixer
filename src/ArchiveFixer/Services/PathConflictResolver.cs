using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using System;
using System.IO;

namespace ArchiveFixer.Services;

/// <summary>
/// 同名冲突解析器（<see cref="PathService"/> 的协作类）。
///
/// <para>
/// 它只有一件事：给定「落点 + 目标类型（文件/目录）+ 用户档位或用户答案」，
/// 判出这一次撞名该怎么处置，以及「覆盖」那一步的两阶段落位怎么做。
/// </para>
///
/// <para>
/// ⛔ 无状态、不读设置、不读任务、不拿日志服务：档位由调用方从
/// <see cref="AppSettings.ConflictAction"/> 取出来再传进来。这样判定表是纯函数式的，
/// 可以被任何流程复用，也可以脱离整个解压管线单测。
/// </para>
///
/// <para>
/// 失败模型：<b>不抛异常</b>。判不出 ⇒ 退回保守档（<see cref="ConflictResolution"/> 里
/// <see cref="ConflictChoice.Skip"/>、路径原样）；覆盖落位失败 ⇒ 返回 <c>false</c> +
/// 中文 <c>error</c>，并在 <c>error</c> 里讲清「旧数据现在在哪」。因为调用方全在
/// 解压收尾 / 落位这些不可逆路径旁边 —— 让一次撞名抛异常穿出去，等于让一份产物把整批搞崩。
/// </para>
///
/// <para>
/// 为什么单独成类：判定表与两阶段落位是<b>同一件事的两半</b>（判定说「覆盖」，落位就必须
/// 按两阶段走，⛔ 不许先删后移），而 <see cref="PathService"/> 那一层管的是数据根 /
/// 工作区 / 落点这些目录布局 —— 两者生命周期不同：这里全程无状态、可并发调用，
/// 那边有 per-process 的数据根与 per-batch 的工作区根。
/// </para>
/// </summary>
public static class PathConflictResolver
{
    /// <summary>
    /// 处理目录冲突（旧签名，保留给"只想问一句落点在哪"的调用方）。
    ///
    /// ⚠ <c>Ask</c> 档**不再**落进自动重命名分支（这正是本轮修掉的缺陷：界面给了「询问」，
    /// 代码却把它和 <c>AutoRename</c> 并到同一个分支，用户选了询问、程序静默自动改名）。
    /// 没拿到用户答案时这个方法**不替用户决定**：原路径返回，调用方按"跳过"处理；
    /// 要真的询问、要覆盖、要改名，用 <see cref="ResolveConflict"/> 并带上用户的
    /// <see cref="ConflictDecision"/>。
    /// </summary>
    public static string ResolveDirectoryConflict(string targetDirectory, string conflictAction)
    {
        return ResolveConflict(targetDirectory, ConflictTargetKind.Directory, conflictAction).TargetPath;
    }

    /// <summary>
    /// 处理文件路径冲突（旧签名，保留）。语义与
    /// <see cref="ResolveDirectoryConflict"/> 完全一致（<c>Ask</c> 不再静默改名）。
    /// </summary>
    public static string ResolveFileConflict(string targetPath, string conflictAction)
    {
        return ResolveConflict(targetPath, ConflictTargetKind.File, conflictAction).TargetPath;
    }

    /// <summary>
    /// 同名冲突的**唯一解析处**：档位（或用户的答案）+ 目标路径 → 处置方式 + 落点。
    ///
    /// <para>
    /// 判定表（<see cref="ConflictActions"/> 的四档，一行都不许在别处再写一遍）：
    /// </para>
    /// <list type="table">
    /// <item><description><c>Skip</c> → 跳过，路径不变；</description></item>
    /// <item><description><c>Overwrite</c> → 覆盖，路径不变（**落位必须走
    /// <see cref="TryOverwriteLanding"/> 的两阶段**，禁止先删后移）；</description></item>
    /// <item><description><c>AutoRename</c> → <c>名字(1)</c>，绝不覆盖；</description></item>
    /// <item><description><c>Ask</c> + 有 <paramref name="decision"/> → 按用户的选择办；</description></item>
    /// <item><description><c>Ask</c> + 没有答案 → **保守档（跳过）**：绝不静默改名、更不覆盖
    /// （没有人回答过的问题，程序不许替用户拍板）。</description></item>
    /// </list>
    /// </summary>
    /// <param name="targetPath">落点（调用方已经或即将往里写东西的那个路径）。</param>
    /// <param name="kind">目标是文件还是目录（决定自动改名用哪套规则）。</param>
    /// <param name="conflictAction">
    /// 设置项 <see cref="AppSettings.ConflictAction"/> 的值；空 / 非法一律按 <c>AutoRename</c>（不变量 3）。
    /// </param>
    /// <param name="decision">用户对这次冲突的答案（Ask 档才需要；非 Ask 档传 null）。</param>
    public static ConflictResolution ResolveConflict(
        string targetPath,
        ConflictTargetKind kind,
        string conflictAction,
        ConflictDecision? decision = null)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return new ConflictResolution(ConflictChoice.AutoRename, targetPath, false, false);
        }

        bool conflicted = Exists(targetPath);

        if (!conflicted)
        {
            return new ConflictResolution(ConflictChoice.AutoRename, targetPath, false, false);
        }

        string action = ConflictActions.Normalize(conflictAction);

        if (!ConflictActions.IsAsk(action))
        {
            // 非 Ask 档：一个字都不问，直接由档位推出结论（默认 AutoRename 会换成 名字(1)）。
            ConflictChoice choice = ActionChoice(action);

            return new ConflictResolution(choice, ResolveTarget(targetPath, kind, choice), true, false);
        }

        if (decision is { } answered && !answered.IsCancel)
        {
            return new ConflictResolution(answered.Choice, ResolveTarget(targetPath, kind, answered.Choice), true, true);
        }

        /*
         * Ask 档但没人回答：**不替用户决定**。
         *
         * 旧实现把 Ask 和 AutoRename 并到同一个分支（等于"问了也白问"，用户看到的是静默改名）——
         * 那正是"界面说一套、代码做一套"。这里退回保守档：路径原样返回，调用方按"跳过"处理，
         * 产物留在暂存目录里（一个字节都不丢），日志里会写明原因。
         */
        return new ConflictResolution(ConflictChoice.Skip, targetPath, true, false);
    }

    /// <summary>
    /// 两阶段覆盖落位（AGENTS.md §6 第 3 条）：
    /// <c>把占位者挪到临时名 → 新条目落位 → 落位成功后才删掉那个临时名</c>。
    ///
    /// <para>
    /// <b>为什么绝不能"先 File.Delete 再 Move"</b>（基线就是这么写的）：中间任何一步失败
    /// （跨卷、被占用、权限、磁盘满）都会让**被覆盖的那一份永久消失**，而新条目还没到位 ——
    /// 用户丢的正是他本来想保留的那个文件。名称交换 <c>A ↔ B</c> 同理，必须走临时名两阶段。
    /// </para>
    /// <para>
    /// 临时名放在**目标同一个目录**里（<c>名字.af-vacating.ext</c>，与 <c>RenameService</c> 同一套命名）：
    /// 换目录就可能是换卷，跨卷的 <c>Move</c> 不再是原子操作。
    /// 落位失败时把临时名挪回原位 —— 结果只可能是"旧的原样"或"新的就位"，绝不会"两个都没了"。
    /// </para>
    /// </summary>
    /// <param name="sourcePath">新条目（暂存区里那一份）。</param>
    /// <param name="targetPath">落点（此刻确实有同名条目占着）。</param>
    /// <param name="isDirectory">新条目是不是目录。</param>
    /// <param name="error">失败原因（成功时为空）。</param>
    /// <param name="note">成功时的一句话说明（旧条目删掉了 / 删不掉留在哪）—— 日志要能看出覆盖留了什么痕。</param>
    public static bool TryOverwriteLanding(
        string sourcePath,
        string targetPath,
        bool isDirectory,
        out string error,
        out string note)
    {
        error = string.Empty;
        note = string.Empty;

        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(targetPath))
        {
            error = "源路径或目标路径为空";
            return false;
        }

        if (SafePathHelper.PathEquals(sourcePath, targetPath))
        {
            error = "源与目标是同一个路径";
            return false;
        }

        bool targetIsDirectory = Directory.Exists(targetPath) && !File.Exists(targetPath);

        if (targetIsDirectory != isDirectory)
        {
            // 类型对不上（文件撞目录 / 目录撞文件）：不动那棵树，交给调用方按"不覆盖"处理。
            error = $"同名目标类型不同（已存在的是{(targetIsDirectory ? "目录" : "文件")}）";
            return false;
        }

        /*
         * 临时（腾位）路径：与目标同目录，命名沿用 RenameService 的 .af-vacating 惯例
         * （不新造机制，出问题时用户与排障脚本一眼能认出来）。
         */
        string vacatedPath = SafePathHelper.BuildVacatingPath(targetPath, targetIsDirectory);

        // ── 阶段 1：把占位者挪到临时名（此时旧数据仍然完整，只是换了个名字）──
        if (!TryMove(targetPath, vacatedPath, targetIsDirectory, out string vacateError))
        {
            error = $"挪开旧{(targetIsDirectory ? "目录" : "文件")}失败（原样未动）：{vacateError}";
            return false;
        }

        // ── 阶段 2：新条目落位 ──
        if (!TryMove(sourcePath, targetPath, isDirectory, out string landError))
        {
            // 落位失败：把临时名挪回去。挪不回去也必须说清旧数据现在在哪（绝不静默）。
            if (!TryMove(vacatedPath, targetPath, targetIsDirectory, out string backError))
            {
                error = $"落位失败且旧{(targetIsDirectory ? "目录" : "文件")}没能挪回原位" +
                        $"（旧数据在 {vacatedPath}，未丢失）：{landError}；挪回失败：{backError}";
                return false;
            }

            error = $"落位失败（旧{(targetIsDirectory ? "目录" : "文件")}已挪回原位）：{landError}";
            return false;
        }

        // ── 阶段 3：这时才允许删旧的（反过来的顺序就是"先删后移"）──
        bool deleted = TryDelete(vacatedPath, targetIsDirectory);

        note = deleted
            ? $"旧{(targetIsDirectory ? "目录" : "文件")}已删除，覆盖前它叫 {vacatedPath}"
            : $"旧{(targetIsDirectory ? "目录" : "文件")}删不掉，已留在 {vacatedPath}（新条目已就位）";

        return true;
    }

    /// <summary>档位 → 处置方式（只有 AutoRename 会改路径，其余两档路径不变）。</summary>
    private static ConflictChoice ActionChoice(string action) => action switch
    {
        ConflictActions.Skip => ConflictChoice.Skip,
        ConflictActions.Overwrite => ConflictChoice.Overwrite,
        _ => ConflictChoice.AutoRename
    };

    /// <summary>按处置方式算落点：只有自动重命名会换名字。</summary>
    private static string ResolveTarget(string targetPath, ConflictTargetKind kind, ConflictChoice choice)
    {
        if (choice != ConflictChoice.AutoRename)
        {
            return targetPath;
        }

        return kind == ConflictTargetKind.Directory
            ? SafePathHelper.AutoRenameDirectoryPath(targetPath)
            : SafePathHelper.AutoRenameFilePath(targetPath);
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static bool TryMove(string from, string to, bool isDirectory, out string error)
    {
        try
        {
            if (isDirectory)
            {
                Directory.Move(from, to);
            }
            else
            {
                File.Move(from, to);
            }

            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryDelete(string path, bool isDirectory)
    {
        try
        {
            if (isDirectory)
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}

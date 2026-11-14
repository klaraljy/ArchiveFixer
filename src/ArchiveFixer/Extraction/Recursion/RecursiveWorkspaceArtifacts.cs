using ArchiveFixer.Helpers;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.IO;

namespace ArchiveFixer.Extraction;

/// <summary>
/// 递归工作区的两笔过程物账：**未凑齐分卷片的抢救副本**与**跨盘 ZIP 临时链名的清理**。
///
/// <para>账本仍是宿主 <see cref="RecursiveExtractor"/> 的字段（谁建谁负责清），本类只承接
/// "怎么拷、怎么删、哪些不能碰"这三件与盘上事实绑定的动作。⛔ 不新增判据、不扫描目录。</para>
/// </summary>
internal static class RecursiveWorkspaceArtifacts
{
    /// <summary>
    /// 记下"这一片是一条**还没凑齐**的分卷组里的片"（去重；只记路径，什么都不碰）。
    ///
    /// <para>⚠ <b>顺手抢救一份到调用方给的落点</b>（用户 2026-10-06 真机：那一片是 `111.zip`、
    /// 它是下一轮那一组跨盘 ZIP 的入口包）：宿主收尾会把这个工作区清掉，
    /// 而协调器拿到结论时它**已经不在盘上了**（排障日志逐字：`那一片已经不在盘上（…\output\111.zip）`）
    /// ⇒ 协调器那一档永远搬不到东西、"结果校验"因此看到空目录 ⇒ 列表显示「解压失败」+ 源包不处理。
    /// ⇒ **谁建谁负责**：记下它的同时就把它复制一份到调用方给的救援目录
    /// （那个目录是协调器给的、在它自己的暂存目录下，不随本类的工作区一起消失）。
    /// ⛔ 只复制、⛔ 不改名、⛔ 不删原件（本类照旧按原口径处置自己的东西）。</para>
    /// </summary>
    internal static void RememberUnresolvedPiece(
        string? path,
        List<string> unresolvedPieces,
        string? rescueDirectory)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        RescueUnresolvedPiece(path!, rescueDirectory);

        foreach (string existing in unresolvedPieces)
        {
            if (string.Equals(existing, path, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        unresolvedPieces.Add(path!);
    }

    /// <summary>
    /// 把这一片复制一份到调用方给的救援目录（见 <see cref="RememberUnresolvedPiece"/> 的说明）。
    /// 判不出 / 拷不动 ⇒ 什么都不做。
    /// </summary>
    private static void RescueUnresolvedPiece(string piece, string? rescueDirectory)
    {
        if (string.IsNullOrWhiteSpace(rescueDirectory))
        {
            return;
        }

        try
        {
            if (!File.Exists(piece))
            {
                return;
            }

            Directory.CreateDirectory(rescueDirectory);

            string target = Path.Combine(rescueDirectory, Path.GetFileName(piece));

            if (!File.Exists(target))
            {
                /*
                 * ⚠ 2026-10-10：**先试硬链接**（同卷、零字节，而且与原件是**同一份物理文件**）。
                 *
                 * <para>为什么要紧：这一份只是"让协调器在本类的工作区被清掉之后还看得见这一片"
                 * （见上面那段说明），而原件随后会被接片那一档按规范卷名搬进落点层 ——
                 * 两份**同一个 inode** 时，定稿那一站搬这一份就认得出"它已经在落点层里了"
                 * （<see cref="ArchiveFixer.Detection.FileIdentity"/>）⇒ ⛔ 不会再改名落出第二份。
                 * 真机 EEEE 2026-10-10 的 `…\111\111\111(1).zip` 正是"复制出来两份、定稿按同名冲突
                 * 自动改名"落出来的（用户点名的「第二轮续解又解一遍」。</para>
                 *
                 * <para>⛔ 只加一份**新名字**、⛔ 不动原件一个字节；跨卷 / 文件系统不支持硬链接 ⇒
                 * 退回复制，行为与改动前逐字相同。</para>
                 */
                if (!HardLinkHelper.TryCreateHardLink(target, piece))
                {
                    File.Copy(piece, target, overwrite: false);
                }
            }
        }
        catch
        {
            // 拷不动（占用 / 权限 / 空间）⇒ 什么都不做：协调器那一档照旧按"判不出就不做"处置。
        }
    }

    /// <summary>
    /// 把"跨盘 ZIP 末片"那一档建出来的临时链接删掉（**只删我们刚建的那几个名字**；
    /// 硬链接删名字不动数据，用户的源片一个字节不动）。
    ///
    /// <para>⛔ 只在这个工作区里动手：路径必须以 <see cref="ExtractionWorkspace.RootDirectory"/> 开头，
    /// 越界的一律不碰（兜底永远落"什么都不做"）。</para>
    /// </summary>
    internal static void RemoveCrossBoundaryLinks(ExtractionWorkspace? workspace, List<string> links)
    {
        if (links.Count == 0)
        {
            return;
        }

        string root = workspace?.RootDirectory ?? string.Empty;

        foreach (string path in links)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)
                    || root.Length == 0
                    || !SafePathHelper.GetFullPathSafe(path)
                        .StartsWith(
                            SafePathHelper.GetFullPathSafe(root) + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(path))
                {
                    continue;
                }

                FileOperations.Delete(path);
            }
            catch
            {
                // 删不掉就留着 —— 它只是多出来的一个名字；工作区收尾照样整份清掉。
            }
        }

        links.Clear();
    }
}

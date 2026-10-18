using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// **把试开确认过的顺序折成一份内容读取**（用户 2026-10-18 口径：先按高概率顺序试，试开赢的那一版
    /// 落成组入口；⛔ 真实文件不改名、不搬、不覆盖）。
    ///
    /// <para>本类**只折数据、不碰盘**：槽位 = 顺序里的那几片、入口 = 第 1 片。
    /// 真正的"落成规范卷名"仍走既有接片出口（`TryGatherGroupPiecesInto` → `TryAdoptUnresolvedVolumePiece`）——
    /// 那条出口会记"谁吐出的片、谁借的片"，批末收场才能把产出方那一份按档收干净。</para>
    ///
    /// <para>⛔ 自己建链接会绕过那本账 ⇒ 产出方那一份变成残留
    /// （2026-10-18 `19191919` / `6666` 的 c3 现场，直接在源目录建`.zip.001..003` 后实测）。</para>
    /// </summary>
    internal static class VolumeOrderMaterializer
    {
        /// <summary>
        /// **把试开确认的顺序落进一个全新的硬链接暂存目录**（2026-10-18 新批 `24242424` 的收口）。
        ///
        /// <para>为什么不直接改入口层：入口层里可能已经有早先建的 `.z01/.z02` 旧名字，与内容定出的新槽位
        /// 撞名；而那些旧名字同时又是组任务的 `VolumePaths`，被"用户源"红线保护着，原地删不得。
        /// ⇒ 在 `<入口层>\.ArchiveFixer.work\order-&lt;id&gt;\` 里按确认顺序建一套**零字节硬链接**，
        /// 用这套暂存目录当组入口；原入口层一个名字都不动，也不碰任何任务源路径。</para>
        ///
        /// <para>⛔ 只建链接、不复制；建不出来 / 源片不在 ⇒ 删掉半成品暂存目录并返回 null（调用方回老路）。</para>
        /// </summary>
        internal static CardVolumeGroupReading? TryStageConfirmedOrder(
            string? entryLayer,
            IReadOnlyList<string>? orderedPieces,
            string? stem,
            VolumeNamingFamily family,
            out string stagingDirectory,
            out string failure)
        {
            stagingDirectory = string.Empty;
            failure = string.Empty;

            List<string> pieces = (orderedPieces ?? Array.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToList();

            if (pieces.Count < 2 || string.IsNullOrWhiteSpace(entryLayer) || string.IsNullOrWhiteSpace(stem))
            {
                failure = "顺序/入口层/基名不全，暂存不了";
                return null;
            }

            IReadOnlyList<string> names = VolumeNumberFromContent.BuildStandardNames(stem, family, pieces.Count);

            if (names.Count != pieces.Count)
            {
                failure = $"按族拼不出 {pieces.Count} 个规范卷名";
                return null;
            }

            string workRoot = Path.Combine(entryLayer, ".ArchiveFixer.work");
            string staging = Path.Combine(workRoot, "order-" + Guid.NewGuid().ToString("N"));
            var created = new List<string>();

            try
            {
                Directory.CreateDirectory(staging);

                for (int index = 0; index < pieces.Count; index++)
                {
                    if (!File.Exists(pieces[index]))
                    {
                        failure = $"第 {index + 1} 片不在了（{pieces[index]}）";
                        return RollbackStaging(staging, failure);
                    }

                    string target = Path.Combine(staging, names[index]);

                    if (!HardLinkHelper.CanHardLink(pieces[index], staging)
                        || !HardLinkHelper.TryCreateHardLink(target, pieces[index]))
                    {
                        failure = $"第 {index + 1} 片的暂存链接建不出来（跨卷或文件系统不支持硬链接）";
                        return RollbackStaging(staging, failure);
                    }

                    created.Add(target);
                }

                var slots = new List<CardVolumeSlot>(pieces.Count);
                long sum = 0;

                for (int index = 0; index < names.Count; index++)
                {
                    string path = Path.Combine(staging, names[index]);
                    long size = 0;

                    try
                    {
                        size = new FileInfo(path).Length;
                    }
                    catch
                    {
                        // 量不出大小不影响归组。
                    }

                    sum += size;
                    slots.Add(new CardVolumeSlot(index + 1, path, size, index == names.Count - 1));
                }

                stagingDirectory = staging;

                return new CardVolumeGroupReading
                {
                    Complete = true,
                    HasFirstPiece = true,
                    PieceCount = slots.Count,
                    DeclaredTotalBytes = sum,
                    SumBytes = sum,
                    Slots = slots,
                    EntryPath = family == VolumeNamingFamily.ZipSpanned
                        ? slots[slots.Count - 1].Path
                        : slots[0].Path,
                    NamingFamily = family,
                    Reason = $"试开确认的顺序已落进硬链接暂存目录（{slots.Count} 片，合计 {sum} 字节）"
                };
            }
            catch (Exception ex)
            {
                failure = $"暂存链接时出错：{ex.Message}";
                return RollbackStaging(staging, failure);
            }
        }

        private static CardVolumeGroupReading? RollbackStaging(string staging, string failure)
        {
            try
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, recursive: true);
                }
            }
            catch
            {
                // 清不掉只是多一个暂存目录；绝不因为清理失败把结论吞掉。
            }

            return null;
        }

        /// <summary>
        /// 把**试开确认过的顺序**折成一份"可证完整"的组读取（⛔ 不碰盘）：
        /// 槽位 = 顺序里的那几片、入口 = 第 1 片。调用方随后走既有的接片出口把它们按规范卷名接进入口层。
        /// 判不出（少于 2 片 / 基名为空）⇒ null。
        /// </summary>
        internal static CardVolumeGroupReading? BuildConfirmedReading(
            IReadOnlyList<string>? orderedPieces,
            string? stem,
            VolumeNamingFamily family)
        {
            List<string> pieces = (orderedPieces ?? Array.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToList();

            if (pieces.Count < 2 || string.IsNullOrWhiteSpace(stem))
            {
                return null;
            }

            var slots = new List<CardVolumeSlot>(pieces.Count);
            long sum = 0;

            for (int index = 0; index < pieces.Count; index++)
            {
                long size = 0;

                try
                {
                    if (File.Exists(pieces[index]))
                    {
                        size = new FileInfo(pieces[index]).Length;
                    }
                }
                catch
                {
                    // 量不出大小不影响归组。
                }

                sum += size;
                slots.Add(new CardVolumeSlot(
                    Index: index + 1,
                    Path: pieces[index],
                    Size: size,
                    IsLast: index == pieces.Count - 1));
            }

            return new CardVolumeGroupReading
            {
                Complete = true,
                HasFirstPiece = true,
                PieceCount = slots.Count,
                DeclaredTotalBytes = sum,
                SumBytes = sum,
                Slots = slots,
                /*
                 * 入口按族定：通用分片 = 第 1 片（槽位 1）；跨盘 ZIP = **末片**（`.zip`，名字上它反而是入口）
                 * —— `CardVolumeGroupReading.EntryPath` 的契约与 `TryGatherGroupPiecesInto` 都读这一格。
                 */
                EntryPath = family == VolumeNamingFamily.ZipSpanned
                    ? slots[slots.Count - 1].Path
                    : slots[0].Path,
                NamingFamily = family,
                Reason = $"内容试开定序成立（{slots.Count} 片，合计 {sum} 字节）"
            };
        }
    }
}

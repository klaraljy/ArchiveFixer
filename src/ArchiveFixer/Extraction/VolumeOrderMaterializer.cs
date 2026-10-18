using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Detection;

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
                EntryPath = slots[0].Path,
                NamingFamily = family,
                Reason = $"内容试开定序成立（{slots.Count} 片，合计 {sum} 字节）"
            };
        }
    }
}

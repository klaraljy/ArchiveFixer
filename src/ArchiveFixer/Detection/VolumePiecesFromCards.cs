using System;
using System.Collections.Generic;
using System.Linq;

namespace ArchiveFixer.Detection
{
    /// <summary>
    /// **一层里"哪几份是同一组的分卷片"—— 只按底层识别卡回答**（用户 2026-10-17 口径：
    /// 「分卷的底层识别算法还要加强」＋「7z 的中间卷一开始识别不到…这个时候利用现有的知识该怎么判断」）。
    ///
    /// <para><b>为什么需要它</b>：7z 格式里**没有卷号字段**，所以中间卷从内容上永远判不出"我是第几卷"；
    /// 只有首卷的起始头自述两件事 ——「整包应当有多少字节」与「按它算出来共几片」。而真机上常见的情形
    /// 恰恰是首卷**没被识别出来**（名字被改成 `xxx.7z.txt` 之类），那时中间卷与"普通内容物"在内容上
    /// 没有区别。</para>
    ///
    /// <para><b>判据顺序（三条证据，串成一个出口）</b>：</para>
    /// <list type="number">
    /// <item><description><b>首卷的自述</b>（最硬的证据）：卡上 `BodyKind = VolumeFirstPiece`，且
    /// `DeclaredTotalBytes &gt; 0`、`DeclaredVolumeCount &gt; 0` ⇒ 这一组"应当有几片、共多少字节"是白拿的。</description></item>
    /// <item><description><b>体积规律</b>（用户口径「除末卷外等大」）：一组里最多只有一片比其余的**小**
    /// —— 那一片就是末卷；其余必须**逐字节等大**。两片都比其余更小 / 一片大一片小 ⇒ 判不出。</description></item>
    /// <item><description><b>合计对上自述</b>：这几片的字节数之和 **==** 首卷自述的整包字节数 ⇒ 整组可证完整。</description></item>
    /// </list>
    ///
    /// <para>⛔ **三条里任何一条不成立都不猜**：没有首卷自述 ⇒ 不给槽位（`HasFirstPiece = false`，
    /// 只如实说"判不出哪一份是首卷"）；体积规律不成立 ⇒ 不下"完整"的结论。调用方拿到
    /// `Complete = false` 时该做的仍然是"什么都不做"（与 `VolumeGatherPlanner` 的兜底同一条）。</para>
    ///
    /// <para>⛔ 纯函数：只读**传进来的卡**，不读盘、不碰任何文件（卡本身由调用方用
    /// <see cref="FileNature.Inspect"/> 拍好）。</para>
    /// </summary>
    public static class VolumePiecesFromCards
    {
        /// <summary>
        /// 这一层里的**同组候选**（调用方只传"同一层、名字上像一组"的那几份，或者干脆传整层里
        /// 内容自证是分卷片的那些）⇒ 给出这一组的结论。
        /// </summary>
        public static CardVolumeGroupReading Read(IReadOnlyList<FileNatureCard>? cards)
        {
            var pieces = (cards ?? Array.Empty<FileNatureCard>())
                .Where(card => card != null && !string.IsNullOrWhiteSpace(card.Path))
                .ToList();

            if (pieces.Count == 0)
            {
                return CardVolumeGroupReading.None("这一层里一份分卷片都没有");
            }

            /*
             * ① 首卷的自述（7z 独有）：片数与整包字节数都在卡上。
             * ⛔ 只有 `VolumeFirstPiece` 那一档有；`VolumePiece`（后续片）按定义没有。
             */
            FileNatureCard? first = pieces.FirstOrDefault(card =>
                card.BodyKind == ArchiveBodyKind.VolumeFirstPiece
                && card.DeclaredVolumeCount > 0
                && card.DeclaredTotalBytes > 0);

            var sizes = pieces.Select(card => card.Size).ToList();
            long sum = sizes.Sum();

            /*
             * ② 体积规律：除末卷外每一片都必须与"满片尺寸"逐字节相等，而且"比满片小的"最多一片。
             * 判据只读盘上的字节数（卡的 `Size`），⛔ 不看任何名字。
             */
            long fullSize = sizes.Max();
            int smallerCount = sizes.Count(size => size < fullSize);

            if (fullSize <= 0 || smallerCount > 1)
            {
                return CardVolumeGroupReading.Incomplete(
                    $"体积规律不成立：{sizes.Count} 片里有 {smallerCount} 片比满片（{fullSize} 字节）小",
                    hasFirstPiece: first != null,
                    pieceCount: pieces.Count,
                    declaredTotalBytes: first?.DeclaredTotalBytes ?? 0,
                    sumBytes: sum,
                    entryPath: first?.Path ?? string.Empty);
            }

            if (first == null)
            {
                /*
                 * 没有首卷自述 ⇒ 槽位与"整组齐不齐"都判不出（7z 中间卷的内容里没有这两件事）。
                 * 如实说：这一层里确实有 N 份分卷片、体积规律成立，但**哪一份是首卷、还缺不缺**——
                 * 判不出（⛔ 不猜，也不拿"名字像是 .001"去顶替）。
                 */
                return CardVolumeGroupReading.Incomplete(
                    $"体积规律成立（{pieces.Count} 片，满片 {fullSize} 字节），但没有首卷的自述 ⇒ "
                    + "判不出哪一份是首卷、也判不出整组齐不齐",
                    hasFirstPiece: false,
                    pieceCount: pieces.Count,
                    declaredTotalBytes: 0,
                    sumBytes: sum,
                    entryPath: string.Empty);
            }

            /*
             * ③ 合计 == 首卷自述的整包字节数 ⇒ 可证完整。
             * ⚠ 片数也要对上（自述的片数把首卷算在里面）。
             */
            if (pieces.Count != first.DeclaredVolumeCount || sum != first.DeclaredTotalBytes)
            {
                long missing = first.DeclaredTotalBytes - sum;

                return CardVolumeGroupReading.Incomplete(
                    $"对不上首卷的自述：自述 {first.DeclaredVolumeCount} 片 / {first.DeclaredTotalBytes} 字节，"
                    + $"手上 {pieces.Count} 片 / {sum} 字节"
                    + (missing > 0 ? $"（还差 {missing} 字节）" : "（多出来了）"),
                    hasFirstPiece: true,
                    pieceCount: pieces.Count,
                    declaredTotalBytes: first.DeclaredTotalBytes,
                    sumBytes: sum,
                    entryPath: first.Path);
            }

            var slots = pieces
                .Select((card, index) => new CardVolumeSlot(
                    Index: index + 1,
                    Path: card.Path,
                    Size: card.Size,
                    IsLast: card.Size < fullSize || (pieces.Count == 1 && card.Size == fullSize)))
                .ToList();

            return new CardVolumeGroupReading
            {
                Complete = true,
                HasFirstPiece = true,
                PieceCount = pieces.Count,
                DeclaredTotalBytes = first.DeclaredTotalBytes,
                SumBytes = sum,
                Slots = slots,
                EntryPath = first.Path,
                Reason = $"可证完整：{pieces.Count} 片合计 {sum} 字节 == 首卷自述的整包字节数，满片 {fullSize} 字节"
            };
        }
    }

    /// <summary>一组里的一片（槽位只按"能确定的那几件事"编号，⛔ 不是卷号）。</summary>
    public readonly record struct CardVolumeSlot(int Index, string Path, long Size, bool IsLast);

    /// <summary>「这一层里的分卷片归成一组」的结论。</summary>
    public sealed class CardVolumeGroupReading
    {
        /// <summary>可证完整（片数 + 字节数都对得上首卷自述，且体积规律成立）。</summary>
        public bool Complete { get; init; }

        /// <summary>这一层里有没有**首卷**（有它才有"整包应当多少字节"这条自述）。</summary>
        public bool HasFirstPiece { get; init; }

        /// <summary>参与这一组的片数。</summary>
        public int PieceCount { get; init; }

        /// <summary>首卷自述的整包字节数（没有首卷时为 0）。</summary>
        public long DeclaredTotalBytes { get; init; }

        /// <summary>这几片实际的字节数之和。</summary>
        public long SumBytes { get; init; }

        /// <summary>槽位（只在可证完整时有值）。</summary>
        public IReadOnlyList<CardVolumeSlot> Slots { get; init; } = Array.Empty<CardVolumeSlot>();

        /// <summary>
        /// 这一组的**入口那一份**（引擎这一趟要打开的文件）。判据只有一条：卡上自证
        /// `BodyKind = VolumeFirstPiece` 的那一份 —— 它自己就是"从它开始解"的那一片
        /// （7z / RAR 老式 / ZIP 通用分片这一族；跨盘 ZIP 的入口是末片 `X.zip`，由族规则另行回答）。
        /// 空 = 这一层里没有首卷 ⇒ **判不出**（⛔ 不拿名字顶替）。
        /// </summary>
        public string EntryPath { get; init; } = string.Empty;

        /// <summary>判据输入（一句人话：为什么是这个结论）。</summary>
        public string Reason { get; init; } = string.Empty;

        internal static CardVolumeGroupReading None(string reason) => new()
        {
            Reason = reason
        };

        internal static CardVolumeGroupReading Incomplete(
            string reason,
            bool hasFirstPiece,
            int pieceCount,
            long declaredTotalBytes,
            long sumBytes,
            string entryPath) => new()
            {
                Complete = false,
                HasFirstPiece = hasFirstPiece,
                PieceCount = pieceCount,
                DeclaredTotalBytes = declaredTotalBytes,
                SumBytes = sumBytes,
                EntryPath = entryPath,
                Reason = reason
            };
    }
}

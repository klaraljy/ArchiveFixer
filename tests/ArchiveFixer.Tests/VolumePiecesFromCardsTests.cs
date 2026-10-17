using System.Collections.Generic;
using ArchiveFixer.Detection;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **分卷底层识别加强**（用户 2026-10-17 口径）：7z 中间卷在内容里没有卷号，只有首卷的起始头
    /// 自述"整包多少字节 / 共几片" ⇒ 判据顺序 = 首卷自述 → 体积规律（除末卷外等大）→ 合计对得上。
    ///
    /// <para>⛔ 三条里任何一条不成立都**不许猜**：没有首卷自述时只如实说"判不出哪一份是首卷"。</para>
    /// </summary>
    public class VolumePiecesFromCardsTests
    {
        private static FileNatureCard Piece(string path, long size, ArchiveBodyKind kind,
            long declaredTotal = 0, int declaredCount = 0, int? volumeNumber = null) => new()
            {
                Path = path,
                Size = size,
                BodyKind = kind,
                DeclaredTotalBytes = declaredTotal,
                DeclaredVolumeCount = declaredCount,
                VolumeNumber = volumeNumber
            };

        [Fact]
        public void 卡上有卷号_槽位按卷号排_不看传入顺序()
        {
            // 传入顺序被打乱：.003、.001、.002 —— 卡上有卷号就按卷号排（底层识别优先）。
            var cards = new List<FileNatureCard>
            {
                Piece(@"C:\x\a.7z.003", 10490213, ArchiveBodyKind.VolumePiece, volumeNumber: 3),
                Piece(@"C:\x\a.7z.001", 26214400, ArchiveBodyKind.VolumeFirstPiece, 62919013, 3, volumeNumber: 1),
                Piece(@"C:\x\a.7z.002", 26214400, ArchiveBodyKind.VolumePiece, volumeNumber: 2)
            };

            CardVolumeGroupReading reading = VolumePiecesFromCards.Read(cards);

            Assert.True(reading.Complete, reading.Reason);
            Assert.Equal(new[] { 1, 2, 3 }, reading.Slots.Select(slot => slot.Index).ToArray());
            Assert.Equal(@"C:\x\a.7z.001", reading.Slots[0].Path);
            Assert.Equal(@"C:\x\a.7z.001", reading.EntryPath);
        }

        [Fact]
        public void 卡上没有卷号_槽位按传入顺序_并如实标末卷()
        {
            var cards = new List<FileNatureCard>
            {
                Piece(@"C:\x\b.7z.001", 26214400, ArchiveBodyKind.VolumeFirstPiece, 36665000, 2),
                Piece(@"C:\x\b.7z.002", 10450600, ArchiveBodyKind.VolumePiece)
            };

            CardVolumeGroupReading reading = VolumePiecesFromCards.Read(cards);

            Assert.True(reading.Complete, reading.Reason);
            Assert.Equal(new[] { 1, 2 }, reading.Slots.Select(slot => slot.Index).ToArray());
            Assert.True(reading.Slots[1].IsLast);
            Assert.False(reading.Slots[0].IsLast);
        }

        [Fact]
        public void 首卷自述加上体积规律与合计_可证完整()
        {
            // 真机 10101010 的形状：两个满片 26214400 + 一个 10490213 的末片，合计 = 首卷自述。
            var cards = new List<FileNatureCard>
            {
                Piece(@"C:\x\pack111-L2.7z.001", 26214400, ArchiveBodyKind.VolumeFirstPiece, 62919013, 3),
                Piece(@"C:\x\pack111-L2.7z.002", 26214400, ArchiveBodyKind.VolumePiece),
                Piece(@"C:\x\pack111-L2.7z.003", 10490213, ArchiveBodyKind.VolumePiece)
            };

            CardVolumeGroupReading reading = VolumePiecesFromCards.Read(cards);

            Assert.True(reading.Complete, reading.Reason);
            Assert.True(reading.HasFirstPiece);
            Assert.Equal(3, reading.PieceCount);
            Assert.Equal(62919013, reading.SumBytes);
            Assert.Equal(3, reading.Slots.Count);
            Assert.Single(reading.Slots, slot => slot.IsLast);

            // 入口那一份 = 自证首卷的那一份（名字认不出族时也有据可依）。
            Assert.Equal(@"C:\x\pack111-L2.7z.001", reading.EntryPath);
        }

        [Fact]
        public void 少一片_合计对不上_不许当完整()
        {
            var cards = new List<FileNatureCard>
            {
                Piece(@"C:\x\pack111-L2.7z.001", 26214400, ArchiveBodyKind.VolumeFirstPiece, 62919013, 3),
                Piece(@"C:\x\pack111-L2.7z.002", 26214400, ArchiveBodyKind.VolumePiece)
            };

            CardVolumeGroupReading reading = VolumePiecesFromCards.Read(cards);

            Assert.False(reading.Complete);
            Assert.Contains("还差", reading.Reason);
            Assert.Empty(reading.Slots);

            // 缺口的唯一口径（日志与闸门都从这里读，别各自再算）。
            Assert.Equal(62919013 - 52428800, reading.MissingBytes);

            // 不完整也能点名"入口那一份是谁"（它自己就是首卷）。
            Assert.Equal(@"C:\x\pack111-L2.7z.001", reading.EntryPath);
        }

        [Fact]
        public void 只有中间卷_体积规律成立也必须说判不出首卷()
        {
            // 用户点名的那一档：首卷没被识别出来 ⇒ 中间卷在内容上与内容物没区别。
            var cards = new List<FileNatureCard>
            {
                Piece(@"C:\x\pack111-L2.7z.002", 26214400, ArchiveBodyKind.VolumePiece),
                Piece(@"C:\x\pack111-L2.7z.003", 10490213, ArchiveBodyKind.VolumePiece)
            };

            CardVolumeGroupReading reading = VolumePiecesFromCards.Read(cards);

            Assert.False(reading.Complete);
            Assert.False(reading.HasFirstPiece);
            Assert.Equal(2, reading.PieceCount);
            Assert.Contains("判不出哪一份是首卷", reading.Reason);
            Assert.Empty(reading.Slots);
        }

        [Fact]
        public void 体积规律不成立_两片都比满片小_判不出()
        {
            var cards = new List<FileNatureCard>
            {
                Piece(@"C:\x\a.7z.001", 26214400, ArchiveBodyKind.VolumeFirstPiece, 62919013, 3),
                Piece(@"C:\x\a.7z.002", 10490213, ArchiveBodyKind.VolumePiece),
                Piece(@"C:\x\a.7z.003", 10490964, ArchiveBodyKind.VolumePiece)
            };

            CardVolumeGroupReading reading = VolumePiecesFromCards.Read(cards);

            Assert.False(reading.Complete);
            Assert.Contains("体积规律不成立", reading.Reason);
        }

        [Fact]
        public void 一份都没有_如实说这一层没有分卷片()
        {
            CardVolumeGroupReading reading = VolumePiecesFromCards.Read(new List<FileNatureCard>());

            Assert.False(reading.Complete);
            Assert.Contains("一份分卷片都没有", reading.Reason);
        }
    }
}

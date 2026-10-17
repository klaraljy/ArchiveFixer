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
            long declaredTotal = 0, int declaredCount = 0, int? volumeNumber = null,
            ArchiveFixer.Detection.VolumeContentFormat format = ArchiveFixer.Detection.VolumeContentFormat.Unknown,
            int? rawVolumeField = null, bool firstVolumeFlag = false) => new()
            {
                Path = path,
                Size = size,
                BodyKind = kind,
                DeclaredTotalBytes = declaredTotal,
                DeclaredVolumeCount = declaredCount,
                VolumeNumber = volumeNumber,
                Format = format,
                RawVolumeField = rawVolumeField,
                FirstVolumeFlag = firstVolumeFlag
            };

        /// <summary>
        /// 设计文档 §6.3 第 2 步 / §9 工单 #6：**RAR4 用卡上 `RawVolumeField` 锚定基数换算绝对卷号**
        /// （卡上 `VolumeNumber` 为空，只有 raw + 首卷标志）。
        /// </summary>
        [Fact]
        public void RAR4_按raw锚定基数_换算出绝对卷号()
        {
            var cards = new List<FileNatureCard>
            {
                Piece(@"C:\x\bbb-L2.txt", 26214400, ArchiveBodyKind.VolumePiece,
                    rawVolumeField: 100, firstVolumeFlag: true),
                Piece(@"C:\x\bbb-L2.jpg", 26214400, ArchiveBodyKind.VolumePiece, rawVolumeField: 101),
                Piece(@"C:\x\bbb-L2.part1.rar", 10490500, ArchiveBodyKind.VolumePiece, rawVolumeField: 102)
            };

            IReadOnlyDictionary<string, int> slots = VolumePiecesFromCards.ResolveRar4Slots(cards);

            Assert.Equal(3, slots.Count);
            Assert.Equal(1, slots[@"C:\x\bbb-L2.txt"]);
            Assert.Equal(2, slots[@"C:\x\bbb-L2.jpg"]);
            Assert.Equal(3, slots[@"C:\x\bbb-L2.part1.rar"]);
        }

        [Fact]
        public void RAR4_缺一片可以判_缺两片以上判不出()
        {
            var oneHole = new List<FileNatureCard>
            {
                Piece(@"C:\x\a.txt", 1, ArchiveBodyKind.VolumePiece, rawVolumeField: 7, firstVolumeFlag: true),
                Piece(@"C:\x\b.txt", 1, ArchiveBodyKind.VolumePiece, rawVolumeField: 9)
            };

            Assert.Equal(2, VolumePiecesFromCards.ResolveRar4Slots(oneHole).Count);

            var twoHoles = new List<FileNatureCard>
            {
                Piece(@"C:\x\a.txt", 1, ArchiveBodyKind.VolumePiece, rawVolumeField: 7, firstVolumeFlag: true),
                Piece(@"C:\x\b.txt", 1, ArchiveBodyKind.VolumePiece, rawVolumeField: 10)
            };

            Assert.Empty(VolumePiecesFromCards.ResolveRar4Slots(twoHoles));
        }

        [Fact]
        public void RAR4_闸门_没首卷标志或raw重复或读不出_一律判不出()
        {
            var noFirstFlag = new List<FileNatureCard>
            {
                Piece(@"C:\x\a.txt", 1, ArchiveBodyKind.VolumePiece, rawVolumeField: 5),
                Piece(@"C:\x\b.txt", 1, ArchiveBodyKind.VolumePiece, rawVolumeField: 6)
            };

            Assert.Empty(VolumePiecesFromCards.ResolveRar4Slots(noFirstFlag));

            var duplicated = new List<FileNatureCard>
            {
                Piece(@"C:\x\a.txt", 1, ArchiveBodyKind.VolumePiece, rawVolumeField: 5, firstVolumeFlag: true),
                Piece(@"C:\x\b.txt", 1, ArchiveBodyKind.VolumePiece, rawVolumeField: 5)
            };

            Assert.Empty(VolumePiecesFromCards.ResolveRar4Slots(duplicated));

            var missingRaw = new List<FileNatureCard>
            {
                Piece(@"C:\x\a.txt", 1, ArchiveBodyKind.VolumePiece, rawVolumeField: 5, firstVolumeFlag: true),
                Piece(@"C:\x\b.txt", 1, ArchiveBodyKind.VolumePiece)
            };

            Assert.Empty(VolumePiecesFromCards.ResolveRar4Slots(missingRaw));
        }

        [Fact]
        public void 按卡的格式定族_七z与rar与zip各给各的名字()
        {
            var seven = Piece(@"C:\x\pack111-L2.7z.txt", 1, ArchiveBodyKind.VolumeFirstPiece,
                format: ArchiveFixer.Detection.VolumeContentFormat.SevenZip);
            var rar = Piece(@"C:\x\aaa-L2.part0631中文3.rar", 1, ArchiveBodyKind.VolumePiece,
                format: ArchiveFixer.Detection.VolumeContentFormat.Rar);
            var zip = Piece(@"C:\x\bbb-L2.zi删除p", 1, ArchiveBodyKind.VolumePiece,
                format: ArchiveFixer.Detection.VolumeContentFormat.Zip);

            string sevenName = VolumePiecesFromCards.CanonicalNameForSlot(seven, 2);
            string rarName = VolumePiecesFromCards.CanonicalNameForSlot(rar, 2);
            string zipName = VolumePiecesFromCards.CanonicalNameForSlot(zip, 2);

            Assert.True(sevenName.Length > 0, "7z 族给不出名字（实际「" + sevenName + "」）");
            Assert.True(rarName.Length > 0, "RAR 族给不出名字（实际「" + rarName + "」）");
            Assert.True(zipName.Length > 0, "ZIP 族给不出名字（实际「" + zipName + "」）");
            Assert.Contains("pack111-L2", sevenName);
            Assert.Contains("aaa-L2", rarName);

            // 卡给不出格式 ⇒ 空串（⛔ 不猜）。
            var unknown = Piece(@"C:\x\ccc.txt", 1, ArchiveBodyKind.VolumePiece);
            Assert.Equal(string.Empty, VolumePiecesFromCards.CanonicalNameForSlot(unknown, 2));
        }

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

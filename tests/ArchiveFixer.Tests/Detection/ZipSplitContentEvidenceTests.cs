using System.Collections.Generic;
using ArchiveFixer.Detection;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 设计文档 `docs/分卷专属算法-待校验.md` §6.6 A / §9 工单 #10：**通用分片族 `X.zip.001..00N`**。
    ///
    /// <para>这一族的中间片是**裸切块**（头不是 PK 签名 ⇒ 内容格式判不出），只有末片的尾部有 EOCD；
    /// 7-Zip `-tzip -v` 切出来的末片 EOCD 写的是"盘号 0" ⇒ 它算不上跨盘成员，但
    /// **"尾部有 EOCD"这条内容事实**就是"哪一片是末片"的证据。定序 = 序号（名字）+ 拼接合法性（内容）。</para>
    /// </summary>
    public class ZipSplitContentEvidenceTests
    {
        private static VolumeNumberReading Piece(
            string path, long size, bool member, bool endRecord = false) => new()
            {
                Path = path,
                Format = endRecord || member ? VolumeContentFormat.Zip : VolumeContentFormat.Unknown,
                Size = size,
                IsVolumeMember = member,
                HasZipEndRecord = endRecord,
                Fail = endRecord ? VolumeNumberFail.ZipSingleDisk : VolumeNumberFail.None
            };

        [Fact]
        public void 通用分片_末片由内容EOCD认出_其余按序号定序()
        {
            var readings = new List<VolumeNumberReading>
            {
                Piece(@"C:\x\X.zip.001", 1000, member: true),
                Piece(@"C:\x\X.zip.002", 1000, member: false),
                Piece(@"C:\x\X.zip.003", 1000, member: false),
                Piece(@"C:\x\X.zip.004", 100, member: false, endRecord: true)
            };

            VolumeGroupOrder order = VolumeNumberFromContent.ResolveGroup(
                @"C:\x\X.zip.001",
                readings,
                requireCurrentFirstVolume: false);

            Assert.True(order.Confirmed, $"应当由内容确认这一组，实际 Fail={order.Fail}");
            Assert.Equal(4, order.Slots.Count);
            Assert.Equal(4, order.Slots[3].Number);
            Assert.Equal(@"C:\x\X.zip.004", order.Slots[3].Path);
        }

        [Fact]
        public void 通用分片_序号不连续或末片号对不上_判不出()
        {
            // 有 001、003、004（缺 002）⇒ 序号不连续 ⇒ 判不出（⛔ 不猜）。
            var gap = new List<VolumeNumberReading>
            {
                Piece(@"C:\x\X.zip.001", 1000, member: true),
                Piece(@"C:\x\X.zip.003", 1000, member: false),
                Piece(@"C:\x\X.zip.004", 100, member: false, endRecord: true)
            };

            VolumeGroupOrder order = VolumeNumberFromContent.ResolveGroup(
                @"C:\x\X.zip.001",
                gap,
                requireCurrentFirstVolume: false);

            Assert.False(order.Confirmed);
            Assert.Equal(VolumeNumberFail.ZipPartsMissing, order.Fail);
        }

        [Fact]
        public void 通用分片_非末片比末片还小_判不出()
        {
            var badSizes = new List<VolumeNumberReading>
            {
                Piece(@"C:\x\X.zip.001", 10, member: true),
                Piece(@"C:\x\X.zip.002", 100, member: false, endRecord: true)
            };

            VolumeGroupOrder order = VolumeNumberFromContent.ResolveGroup(
                @"C:\x\X.zip.001",
                badSizes,
                requireCurrentFirstVolume: false);

            Assert.False(order.Confirmed);
        }
    }
}

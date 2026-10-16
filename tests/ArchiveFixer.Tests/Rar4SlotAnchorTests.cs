using ArchiveFixer.Detection;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **RAR 1.5–4.x（RAR4）的绝对卷号**（用户 2026-10-16 拍板，逐字「同意」）：
    /// 主头 `MHD_FIRSTVOLUME` 认出第 1 卷 ⇒ 它那一份的**原始字段就是基数**
    /// ⇒ `绝对卷号 = 这一片的原始字段 − 基数 + 1`。
    ///
    /// <para>为什么单独立一条：RAR4 的**头里没有绝对卷号**（只有卷尾归档结尾块的原始字段，
    /// 基数 0 起还是 1 起在 RAR 文档里没写）—— 真机 `4444` 三片走本出口读卡逐字：
    /// 第 1 卷 `首卷标志=True 原始卷号字段=0`、另两片 `=1 / =2`。</para>
    ///
    /// <para>⛔ 判不出（入口不是第 1 卷 / 任一侧读不出原始字段 / 算出来小于 1 / 不是 RAR 族）⇒
    /// 一律返回 null —— 调用方因此什么都不做，⛔ 不猜号。</para>
    /// </summary>
    public class Rar4SlotAnchorTests
    {
        private static FileNatureCard Card(
            VolumeContentFormat format,
            bool first,
            int? raw,
            ArchiveBodyKind bodyKind = ArchiveBodyKind.VolumePiece) => new()
        {
            Format = format,
            FirstVolumeFlag = first,
            RawVolumeField = raw,
            BodyKind = bodyKind
        };

        /// <summary>这一组第 1 卷的卡（权威 = `BodyKind`；⚠ 真机上它常常 `FirstVolumeFlag=False`）。</summary>
        private static FileNatureCard FirstVolume(int? raw, bool firstVolumeFlag = false) =>
            Card(VolumeContentFormat.Rar, firstVolumeFlag, raw, ArchiveBodyKind.VolumeFirstPiece);

        [Fact]
        public void 入口自报第一卷_原始字段就是基数_其余片按它换算()
        {
            // 真机 4444 的形状：第 1 卷 raw=0 ⇒ 基数 0 ⇒ raw 1/2 分别落在第 2/3 片。
            FileNatureCard entry = FirstVolume(0);

            Assert.Equal(2, VolumePieceFacts.ResolveRar4SlotIndex(Card(VolumeContentFormat.Rar, false, 1), entry));
            Assert.Equal(3, VolumePieceFacts.ResolveRar4SlotIndex(Card(VolumeContentFormat.Rar, false, 2), entry));
        }

        [Fact]
        public void 基数不是0也照样换算_不许写死加一()
        {
            // 同一族里基数是"第 1 卷自己那个原始字段"，⛔ 不是写死的 0 —— 这一条专门钉住这点。
            FileNatureCard entry = FirstVolume(5);

            Assert.Equal(1, VolumePieceFacts.ResolveRar4SlotIndex(Card(VolumeContentFormat.Rar, false, 5), entry));
            Assert.Equal(4, VolumePieceFacts.ResolveRar4SlotIndex(Card(VolumeContentFormat.Rar, false, 8), entry));
        }

        [Fact]
        public void 入口不是第一卷_判不出()
        {
            // 锚必须是**第 1 卷**：手上这一份自己就是后续片时没有基数可锚 ⇒ 判不出。
            Assert.Null(VolumePieceFacts.ResolveRar4SlotIndex(
                Card(VolumeContentFormat.Rar, false, 2),
                Card(VolumeContentFormat.Rar, first: false, raw: 1)));
        }

        [Fact]
        public void 任一侧读不出原始字段_判不出()
        {
            FileNatureCard entry = FirstVolume(0);

            Assert.Null(VolumePieceFacts.ResolveRar4SlotIndex(Card(VolumeContentFormat.Rar, false, null), entry));
            Assert.Null(VolumePieceFacts.ResolveRar4SlotIndex(
                Card(VolumeContentFormat.Rar, false, 2),
                FirstVolume(null)));
        }

        [Fact]
        public void 换算结果小于一_判不出()
        {
            // 原始字段比基数还小 ⇒ 换算出来是 0 / 负数 ⇒ 不猜（宁可什么都不做）。
            FileNatureCard entry = FirstVolume(3);

            Assert.Null(VolumePieceFacts.ResolveRar4SlotIndex(Card(VolumeContentFormat.Rar, false, 2), entry));
        }

        [Fact]
        public void 不是RAR族_判不出()
        {
            // 7z / 跨盘 ZIP 各有各的卷号来源，⛔ 不许拿 RAR4 这条公式去套它们。
            FileNatureCard entry = FirstVolume(0);

            Assert.Null(VolumePieceFacts.ResolveRar4SlotIndex(Card(VolumeContentFormat.SevenZip, false, 2), entry));
            Assert.Null(VolumePieceFacts.ResolveRar4SlotIndex(Card(VolumeContentFormat.Zip, false, 2), entry));
        }
    }
}

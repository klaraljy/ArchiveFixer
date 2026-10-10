using System;
using System.IO;
using ArchiveFixer.Extraction;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 链尾「其余物」整份处理前的第七道门槛（<see cref="RestVolumeCompletenessGate"/>）。
    ///
    /// <para><b>为什么必须有这一条</b>：用户 2026-09-30 真机 `一只顶美合集` 那一单，
    /// 内层包是一组 6 片跨盘 ZIP —— 末片当时没被认出来（ZIP64 收尾那两条闸门）⇒
    /// 末片被当成**内容物**留在成品目录，同组 5 卷被当成**过程物**收进其余物，
    /// 链尾几道门槛全都过得去 ⇒ 其余物**整份彻底删除**，一组包被拆开、25 GB 永久消失。</para>
    ///
    /// <para><b>红检</b>：把 `ExtractionCoordinator` 里那次调用撤掉 ⇒ 这些用例仍然绿（它们是纯函数用例），
    /// 所以真正的红在"接线"那一侧 —— 本文件同时钉住"半套必须拦下"与"整组/普通内容不许误拦"两类。</para>
    /// </summary>
    public sealed class RestVolumeCompletenessGateTests : IDisposable
    {
        private readonly string _root;

        public RestVolumeCompletenessGateTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerRestVolume", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch
            {
                // 临时目录清不掉不影响结论。
            }
        }

        [Fact]
        public void 其余物里是半套分卷而末片留在成品目录_必须拦下并点名两边()
        {
            string output = Path.Combine(_root, "111");
            string rest = Path.Combine(output, "其余物");
            Directory.CreateDirectory(rest);

            // 真机那一单的形状：其余物里 5 卷 + 成品目录里那个名字被改坏的末片。
            File.WriteAllBytes(Path.Combine(rest, "一只顶美.z01"), new byte[64]);
            File.WriteAllBytes(Path.Combine(rest, "一只顶美.z05"), new byte[64]);
            File.WriteAllBytes(Path.Combine(output, "一只顶美.z删除ip"), new byte[64]);

            string? blocker = RestVolumeCompletenessGate.DescribeBlocker(rest);

            Assert.NotNull(blocker);
            Assert.Contains("一只顶美.z01", blocker, StringComparison.Ordinal);
            Assert.Contains("一只顶美.z删除ip", blocker, StringComparison.Ordinal);
            Assert.Contains("一只顶美", blocker, StringComparison.Ordinal);
        }

        [Fact]
        public void 整组都在其余物里_放行()
        {
            string output = Path.Combine(_root, "whole");
            string rest = Path.Combine(output, "其余物");
            Directory.CreateDirectory(rest);

            File.WriteAllBytes(Path.Combine(rest, "222.z01"), new byte[64]);
            File.WriteAllBytes(Path.Combine(rest, "222.z02"), new byte[64]);
            File.WriteAllBytes(Path.Combine(rest, "222.zip"), new byte[64]);

            Assert.Null(RestVolumeCompletenessGate.DescribeBlocker(rest));
        }

        [Fact]
        public void 外面只有同基名的普通内容文件_不许误拦()
        {
            string output = Path.Combine(_root, "content");
            string rest = Path.Combine(output, "其余物");
            Directory.CreateDirectory(rest);
            Directory.CreateDirectory(Path.Combine(output, "一只顶美"));

            // 内容物与包基名撞车是常态（`111\111\内容物`）：普通视频/图片**不是**同组的伙伴。
            File.WriteAllBytes(Path.Combine(rest, "一只顶美.z01"), new byte[64]);
            File.WriteAllBytes(Path.Combine(output, "一只顶美", "一只顶美.mp4"), new byte[64]);
            File.WriteAllBytes(Path.Combine(output, "一只顶美.jpg"), new byte[64]);

            Assert.Null(RestVolumeCompletenessGate.DescribeBlocker(rest));
        }

        [Fact]
        public void 外面只有同基名的目录_不许误拦()
        {
            string output = Path.Combine(_root, "folder");
            string rest = Path.Combine(output, "其余物");
            Directory.CreateDirectory(rest);
            Directory.CreateDirectory(Path.Combine(output, "333"));

            File.WriteAllBytes(Path.Combine(rest, "333.z01"), new byte[64]);

            Assert.Null(RestVolumeCompletenessGate.DescribeBlocker(rest));
        }

        [Fact]
        public void 外面的同基名是另一个归档本体_也要拦下()
        {
            string output = Path.Combine(_root, "body");
            string rest = Path.Combine(output, "其余物");
            Directory.CreateDirectory(rest);

            File.WriteAllBytes(Path.Combine(rest, "444.z01"), new byte[64]);
            File.WriteAllBytes(Path.Combine(output, "444.zip"), new byte[64]);

            string? blocker = RestVolumeCompletenessGate.DescribeBlocker(rest);

            Assert.NotNull(blocker);
            Assert.Contains("444.zip", blocker, StringComparison.Ordinal);
        }

        [Fact]
        public void 其余物不存在或里面没有归档件_放行()
        {
            string output = Path.Combine(_root, "empty");
            Directory.CreateDirectory(output);

            Assert.Null(RestVolumeCompletenessGate.DescribeBlocker(Path.Combine(output, "其余物")));

            string rest = Path.Combine(output, "其余物");
            Directory.CreateDirectory(rest);
            File.WriteAllText(Path.Combine(rest, "说明.txt"), "不是归档件");

            Assert.Null(RestVolumeCompletenessGate.DescribeBlocker(rest));
        }

        /// <summary>
        /// **跨族同基名不是"同组的另一片"**（真机 EEEE 2026-10-10 实测 + 用户当场拍板换判据）。
        ///
        /// <para><b>现场</b>：空间不足模式下 `111.rar` 的源包**没被当场回收** —— 被这道闸门拦下，
        /// 日志逐字：「逐层回收准备删的「111.rar」是一组分卷的一片，而同组的另一片「111.zip」
        /// 还在成品目录里（两边基名都是「111」）」。可 `111.rar` 是 **RarOld 族**（归档本体）、
        /// `111.zip` 是 **ZipSpanned 族**（跨盘 ZIP 末片）—— 按项目**唯一那把"同一组 = 族 + 基名"的尺子**
        /// （<see cref="ArchiveFixer.Detection.VolumeGroupDetector.BelongsToSameGroup"/>，
        /// 守门 <c>StalledGroupRegistrationTests</c> 已逐字钉住这对名字不同族）它们**不是一组**。
        /// 老判据只比基名 ⇒ 误拦 ⇒ 那 48 MB 没能在定稿那一刻还给用户（盘真紧时就是成败之差）。</para>
        ///
        /// <para><b>红检</b>：把判据退回"只比基名" ⇒ 本条当场红（返回了 blocker）。</para>
        /// </summary>
        [Fact]
        public void 跨族同基名_不是同组的另一片_不许拦()
        {
            string output = Path.Combine(_root, "crossfamily");
            string rest = Path.Combine(output, "其余物");
            Directory.CreateDirectory(rest);

            // 其余物里是 RAR 那一族的归档本体；成品目录里是 ZIP 那一族的末片 —— 基名都是 111，但族不同。
            File.WriteAllBytes(Path.Combine(rest, "111.rar"), new byte[64]);
            File.WriteAllBytes(Path.Combine(output, "111.zip"), new byte[64]);

            Assert.Null(RestVolumeCompletenessGate.DescribeBlocker(rest));

            /*
             * 真机挡住的那一次走的是**逐层回收**那一档（候选 = 准备删的那一份文件，
             * 不是整个其余物目录）⇒ 同一份判据的另一个入口也钉一遍。
             */
            string candidate = Path.Combine(rest, "111.rar");

            Assert.Null(RestVolumeCompletenessGate.DescribeLayerReclaimBlocker(new[] { candidate }, output));
        }

        /// <summary>
        /// **哨兵（保守方向一个字不松）**：另一片的名字**认不出族**时 ⇒ 判不出 ⇒ **照旧拦**。
        ///
        /// <para>删东西的闸门兜底永远落在"什么都不做"：只有"两边都解析得出来、而且族确实不同"才放行
        /// （唯一出口 <c>VolumeGroupDetector.AreProvablyDifferentFamilies</c>）。</para>
        /// </summary>
        [Fact]
        public void 另一片的名字认不出族_判不出_照旧拦下()
        {
            string output = Path.Combine(_root, "unparsable");
            string rest = Path.Combine(output, "其余物");
            Directory.CreateDirectory(rest);

            // 其余物里是正常的一片；成品目录里那一份名字被改坏（去杂质才认得出是归档件）。
            File.WriteAllBytes(Path.Combine(rest, "111.z01"), new byte[64]);
            File.WriteAllBytes(Path.Combine(output, "111.zi删除p"), new byte[64]);

            Assert.NotNull(RestVolumeCompletenessGate.DescribeBlocker(rest));
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **内容级卷号识别**（用户 2026-09-29 任务）：RAR5 / RAR 1.5–4.x（WinRAR 里叫 RAR4）的卷号、
    /// 跨盘 ZIP 的盘号 —— 全部用**表驱动 + 自己构造字节**钉住，不靠真样本。
    /// （真样本另有一组用例：<c>RealVolumeSampleTests</c>，样本不在时自动跳过。）
    ///
    /// <para>这一组钉的是三件事：① 字段读得对（偏移 / vint / 卷尾块 / EOCD 注释自洽）；
    /// ② 拿不准时**不认**（截断、CRC 对不上、加密头、卷号连不成 1..N、两种基数都成立、片数 ≥ 3、
    /// 结尾块布局与官方说明不符）；③ 认出来之后拼出来的名字**只有一套拼法**（RAR 族带 <c>.rar</c> 尾巴）。</para>
    /// </summary>
    public class VolumeNumberContentTests : IDisposable
    {
        private readonly string _root;

        public VolumeNumberContentTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerVolumeNumber", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch
            {
                // 清不掉只是脏一点。
            }
        }

        // ── RAR5：卷号在主归档头里 ──

        [Theory]
        // archiveFlags 0x0001 = 分卷组的一员；0x0002 = 后面有卷号字段（第 1 卷没有）
        [InlineData(0x0000u, null, false, 0)] // 单卷 rar：不是分卷组
        [InlineData(0x0001u, null, true, 1)]  // 第 1 卷：没有卷号字段
        [InlineData(0x0003u, 1u, true, 2)]    // 第 2 卷：字段值 1
        [InlineData(0x0003u, 2u, true, 3)]    // 第 3 卷：字段值 2
        [InlineData(0x0003u, 9u, true, 10)]
        public void RAR5_卷号在主归档头里_字段值加一(
            uint archiveFlags,
            uint? volumeField,
            bool expectedMember,
            int expectedNumber)
        {
            string path = Write("x.bin", Rar5MainHeader(archiveFlags, volumeField));

            VolumeNumberReading reading = VolumeNumberFromContent.Read(path);

            Assert.Equal(VolumeContentFormat.Rar, reading.Format);
            Assert.Equal(expectedMember, reading.IsVolumeMember);

            if (expectedMember)
            {
                Assert.Equal(VolumeNumberFail.None, reading.Fail);
                Assert.Equal(expectedNumber, reading.Number);
            }
            else
            {
                Assert.Equal(VolumeNumberFail.NotVolumeMember, reading.Fail);
            }
        }

        [Fact]
        public void RAR5_主头带扩展区与数据区_字段位置照样读得对()
        {
            /*
             * 通用块头里"扩展区大小 / 数据区大小"是**可选字段**（headerFlags 的 0x0001 / 0x0002），
             * 少读或多读一个就会把 archiveFlags 读错 → 卷号全错。这一条把两种都打开，钉住字段顺序。
             */
            string path = Write("x.bin", Rar5MainHeader(0x0003u, 4u, headerFlags: 0x0003u, extraAreaSize: 6, dataAreaSize: 3));

            VolumeNumberReading reading = VolumeNumberFromContent.Read(path);

            Assert.True(reading.IsVolumeMember);
            Assert.Equal(5, reading.Number);
        }

        [Theory]
        // 签名在、但主头被截断：头还没读完 / 头大小说到了文件外面
        [InlineData(1)]
        [InlineData(4)]
        [InlineData(10)]
        public void RAR5_头被截断_一律不认(int keepBytes)
        {
            byte[] full = Rar5MainHeader(0x0003u, 1u);
            string path = Write("x.bin", full.Take(full.Length - keepBytes).ToArray());

            VolumeNumberReading reading = VolumeNumberFromContent.Read(path);

            Assert.False(reading.IsVolumeMember);
            Assert.Equal(VolumeNumberFail.HeadersUnreadable, reading.Fail);
        }

        [Fact]
        public void RAR5_头部CRC对不上_不认()
        {
            byte[] bytes = Rar5MainHeader(0x0003u, 1u);

            // 把卷号字段那一字节改掉，CRC 就不再成立（内容被改动过的东西一律不认）
            bytes[^1] ^= 0xFF;

            string path = Write("x.bin", bytes);

            VolumeNumberReading reading = VolumeNumberFromContent.Read(path);

            Assert.False(reading.IsVolumeMember);
            Assert.Equal(VolumeNumberFail.HeadersUnreadable, reading.Fail);
        }

        [Fact]
        public void RAR5_归档头被加密_读不出卷号_不认()
        {
            // 头一份块是"归档加密头"（type 4）：之后所有头都是密文，卷号读不出来。
            string path = Write("x.bin", Rar5MainHeader(0x0003u, 1u, headerType: 4));

            VolumeNumberReading reading = VolumeNumberFromContent.Read(path);

            Assert.False(reading.IsVolumeMember);
            Assert.Equal(VolumeNumberFail.HeadersUnreadable, reading.Fail);
        }

        // ── RAR 1.5–4.x（RAR4）：卷号在卷尾归档结尾块里 ──

        [Theory]
        [InlineData(0, true)]  // 第 1 卷：卷尾字段值 0（基数待定）、主头带"第 1 卷"标记
        [InlineData(1, false)] // 第 2 卷
        [InlineData(4, false)]
        public void RAR4_卷号在卷尾归档结尾块里_原值保留(
            ushort volumeField,
            bool firstVolume)
        {
            string path = Write("x.bin", Rar4Archive(0x0001, volumeField, firstVolume));

            VolumeNumberReading reading = VolumeNumberFromContent.Read(path);

            Assert.True(reading.IsVolumeMember);
            Assert.Equal(RarNumberingFamily.New, reading.RarNumbering);
            Assert.Equal(firstVolume, reading.FirstVolumeFlag);

            // ⛔ 单卷上不做加减：基数是整组的事（见下面的定序用例）
            Assert.Equal((int)volumeField, reading.RawField);
            Assert.Null(reading.Number);
        }

        [Fact]
        public void RAR4_卷尾块写的是老口径的四字节卷号_布局对不上_不认()
        {
            /*
             * 这一条钉的是 2026-09-29 真样本逮到的那个缺陷：老代码把卷号当成**四字节**、
             * 按"块起点 + HEAD_SIZE − 11"读，于是把 EARC_DATACRC 的尾巴当成了卷号
             * （真样本读出 0xCF7C / 0x01EF02 → 整组连不成 1..N → RAR 分卷一律不认）。
             *
             * 真样本的字节是硬证据：HEAD_SIZE = 20 = 7 + 4（DATACRC）+ 2（VOLNUMBER）+ 7（保留区）。
             * 所以"四字节卷号 + 7 字节保留区"这种块（HEAD_SIZE 22）必须**读不出来** ——
             * ⛔ 不许为了兼容一个错的偏移去猜：字段总长与 HEAD_SIZE 不符就是不认。
             */
            byte[] full = Rar4Archive(0x0001, 1, firstVolume: false, legacyFourByteVolumeNumber: true);
            string path = Write("x.bin", full);

            VolumeNumberReading reading = VolumeNumberFromContent.Read(path);

            Assert.False(reading.IsVolumeMember);
            Assert.Equal(VolumeNumberFail.HeadersUnreadable, reading.Fail);
        }

        [Fact]
        public void RAR4_老式编号族_一个字节都不动()
        {
            // MHD_NEWNUMBERING 不设 = 老式 .rar/.r00 族：本程序只认 partN.rar，不猜。
            string path = Write("x.bin", Rar4Archive(0x0001, 1, firstVolume: true, newNumbering: false));

            VolumeNumberReading reading = VolumeNumberFromContent.Read(path);

            Assert.True(reading.IsVolumeMember);
            Assert.Equal(VolumeNumberFail.RarOldNumbering, reading.Fail);
        }

        [Fact]
        public void RAR4_卷尾结尾块被截断_不认()
        {
            byte[] full = Rar4Archive(0x0001, 1, firstVolume: true);
            string path = Write("x.bin", full.Take(full.Length - 3).ToArray());

            VolumeNumberReading reading = VolumeNumberFromContent.Read(path);

            Assert.False(reading.IsVolumeMember);
            Assert.Equal(VolumeNumberFail.HeadersUnreadable, reading.Fail);
        }

        [Fact]
        public void RAR4_主头CRC对不上_不认()
        {
            byte[] bytes = Rar4Archive(0x0001, 1, firstVolume: true);

            bytes[10] ^= 0x5A; // 主头保留区（CRC 覆盖范围内）

            string path = Write("x.bin", bytes);

            VolumeNumberReading reading = VolumeNumberFromContent.Read(path);

            Assert.False(reading.IsVolumeMember);
            Assert.Equal(VolumeNumberFail.HeadersUnreadable, reading.Fail);
        }

        // ── 跨盘 ZIP ──

        [Theory]
        // 本盘号（0 起） → 卷号 = 盘号 + 1、总片数 = 盘号 + 1
        [InlineData(0, 0, false, 0)] // 都 0 = 单盘 zip
        [InlineData(1, 0, true, 2)]  // 2 片的末片
        [InlineData(2, 0, true, 3)]  // 3 片的末片
        public void ZIP_末片的EOCD里有盘号(int diskNumber, int cdStartDisk, bool expectedMember, int expectedNumber)
        {
            string path = Write("x.bin", ZipTail(diskNumber, cdStartDisk, Array.Empty<byte>()));

            VolumeNumberReading reading = VolumeNumberFromContent.Read(path);

            Assert.Equal(VolumeContentFormat.Zip, reading.Format);
            Assert.Equal(expectedMember, reading.IsVolumeMember);

            if (expectedMember)
            {
                Assert.Equal(expectedNumber, reading.Number);
                Assert.Equal(expectedNumber, reading.Total);
            }
            else
            {
                Assert.Equal(VolumeNumberFail.ZipSingleDisk, reading.Fail);
            }
        }

        [Fact]
        public void ZIP_末片带注释_注释长度自洽时照样认()
        {
            byte[] comment = { 0x41, 0x42, 0x43, 0x44 };
            string path = Write("x.bin", ZipTail(1, 0, comment));

            VolumeNumberReading reading = VolumeNumberFromContent.Read(path);

            Assert.True(reading.IsVolumeMember);
            Assert.Equal(2, reading.Number);
        }

        [Fact]
        public void ZIP_注释长度写错_当不成末片_整组也不认()
        {
            /*
             * EOCD 必须**正好**收在文件末尾：注释长度说 4 字节、文件里却只有 2 字节 ——
             * 这种"看着像 EOCD"的东西一律不能当末片（它是判"这一片是不是末片"的唯一依据）。
             *
             * ⚠ 它开头是本地文件头，所以按新判据它仍然是"像 zip 流的一片"（成员、但没有盘号）；
             * 关键是**它当不了末片**，于是整组凑不齐 → 一个字节都不动。
             */
            byte[] bytes = ZipTail(1, 0, new byte[] { 0x41, 0x42 });
            bytes[^3] = 0x04; // 注释长度改成 4

            string path = Write("x.bin", bytes);

            VolumeNumberReading reading = VolumeNumberFromContent.Read(path);

            Assert.Null(reading.Number);

            VolumeGroupOrder order = VolumeNumberFromContent.ResolveGroup(path, new[] { reading });

            Assert.False(order.Confirmed);
        }

        [Fact]
        public void ZIP_非末片_开头是任意PK签名就算这一组的成员()
        {
            /*
             * 判据（用户 2026-09-29 真样本定的）：非末片**不看结尾**，只看开头是不是 zip 流的签名。
             * 真 PKZIP / 网盘那种跨盘 zip 的第 1 片**以跨盘标记 PK\x07\x08 开头**（7-Zip 叫它
             * Embedded Stub），而且切点落在数据中间，结尾根本不是标记 ——
             * 老判据"本地文件头开头 + 标记结尾"两条都要求，于是真样本的第一片永远认不出来。
             */
            string localHeader = Write("a.bin", ZipSegment(4096, ZipSegmentHead.LocalFileHeader));
            string spanningMarker = Write("b.bin", ZipSegment(4096, ZipSegmentHead.SpanningMarker));
            string centralDirectory = Write("c.bin", ZipSegment(4096, ZipSegmentHead.CentralDirectory));
            string notZip = Write("d.bin", ZipSegment(4096, ZipSegmentHead.NotZip));

            foreach (string path in new[] { localHeader, spanningMarker, centralDirectory })
            {
                VolumeNumberReading reading = VolumeNumberFromContent.Read(path);

                Assert.True(reading.IsVolumeMember, $"{Path.GetFileName(path)} 没被认成跨盘组的成员");
                Assert.Null(reading.Number); // 内容里没有盘号：位置只能靠消去法
                Assert.Equal(VolumeNumberFail.None, reading.Fail);
            }

            Assert.False(VolumeNumberFromContent.Read(notZip).IsVolumeMember);
        }

        [Fact]
        public void ZIP_非末片比末片还小_不是一组_不认()
        {
            /*
             * 尺寸规律：跨盘 zip 的非末片都是切分上限那么大的满片，末片是余量 ——
             * 所以"该接在末片前面"的那一片必定不小于末片。反过来说明它们不是一组，
             * 认了就等于把一个不相干的文件改成 `.z01`。
             */
            string big = Write("big.bin", ZipSegment(8192, ZipSegmentHead.LocalFileHeader));
            string small = Write("small.bin", ZipSegment(16, ZipSegmentHead.LocalFileHeader));
            string tail = Write("tail.bin", ZipTail(1, 0, Array.Empty<byte>()));

            VolumeGroupOrder ok = VolumeNumberFromContent.ResolveGroup(
                big,
                new[] { VolumeNumberFromContent.Read(big), VolumeNumberFromContent.Read(tail) });

            Assert.True(ok.Confirmed);

            VolumeGroupOrder refused = VolumeNumberFromContent.ResolveGroup(
                small,
                new[] { VolumeNumberFromContent.Read(small), VolumeNumberFromContent.Read(tail) });

            Assert.False(refused.Confirmed);
        }

        [Fact]
        public void ZIP_七z切出来的裸分片末片_盘号是0_不许当跨盘组()
        {
            /*
             * 反例：`7z a -tzip -v1m` 的末片结尾也有一个**合法** EOCD，但它说的是"单盘"（盘号 0）——
             * 7z 那种切法是裸字节流，不是 PKZIP 跨盘。认成跨盘就会把一组名字改坏。
             */
            string path = Write("x.zip.003", ZipTail(0, 0, Array.Empty<byte>()));

            VolumeGroupOrder order = VolumeNumberFromContent.ResolveGroup(path, new[] { VolumeNumberFromContent.Read(path) });

            Assert.False(order.Confirmed);
            Assert.Equal(VolumeNumberFail.ZipSingleDisk, order.Fail);
        }

        // ── 定序：整组卷号必须连成 1..N ──

        [Fact]
        public void RAR5_整组卷号连成1到N_才认_名字随便叫()
        {
            string[] paths =
            {
                Write("aaa.dat", Rar5MainHeader(0x0001u, null)),  // 第 1 卷
                Write("bbb.dat", Rar5MainHeader(0x0003u, 1u)),    // 第 2 卷
                Write("ccc.dat", Rar5MainHeader(0x0003u, 2u))     // 第 3 卷
            };

            VolumeGroupOrder order = VolumeNumberFromContent.ResolveGroup(paths[0], paths.Select(VolumeNumberFromContent.Read));

            Assert.True(order.Confirmed);
            Assert.Equal(3, order.Count);
            Assert.Equal(paths[0], order.Slots[0].Path);
            Assert.Equal(paths[1], order.Slots[1].Path);
            Assert.Equal(paths[2], order.Slots[2].Path);
        }

        [Fact]
        public void RAR5_缺一卷_卷号连不成1到N_整组不动()
        {
            string[] paths =
            {
                Write("aaa.dat", Rar5MainHeader(0x0001u, null)),
                Write("ccc.dat", Rar5MainHeader(0x0003u, 2u)) // 直接是第 3 卷：第 2 卷不在
            };

            VolumeGroupOrder order = VolumeNumberFromContent.ResolveGroup(paths[0], paths.Select(VolumeNumberFromContent.Read));

            Assert.False(order.Confirmed);
            Assert.Equal(VolumeNumberFail.GroupNotContiguous, order.Fail);
        }

        [Fact]
        public void RAR5_手上的不是第一巻_不认()
        {
            string[] paths =
            {
                Write("aaa.dat", Rar5MainHeader(0x0001u, null)),
                Write("bbb.dat", Rar5MainHeader(0x0003u, 1u))
            };

            VolumeGroupOrder order = VolumeNumberFromContent.ResolveGroup(paths[1], paths.Select(VolumeNumberFromContent.Read));

            Assert.False(order.Confirmed);
            Assert.Equal(VolumeNumberFail.CurrentNotFirstVolume, order.Fail);
        }

        [Theory]
        // 卷尾字段值 a/b/c + "第 1 卷"标记长在第几个文件上 → （认不认、不认是哪一档）
        [InlineData(0, 1, 2, 0, true, VolumeNumberFail.None)]                   // 0 起的一整组：{0,1,2} → 卷号 1,2,3
        [InlineData(1, 2, 3, 0, true, VolumeNumberFail.None)]                   // 1 起的一整组：{1,2,3} → 卷号 1,2,3
        [InlineData(0, 2, 3, 0, false, VolumeNumberFail.GroupBaseAmbiguous)]    // {0,2,3} 两种基数都连不成 1..N → 不认
        [InlineData(0, 1, 2, 2, false, VolumeNumberFail.RarFirstVolumeMismatch)] // "第 1 卷"标记长在第 3 个文件上 → 自相矛盾
        public void RAR4_基数靠整组自洽判定_第1卷标记要一致(
            ushort a,
            ushort b,
            ushort c,
            int firstFlagIndex,
            bool expectedConfirmed,
            VolumeNumberFail expectedFail)
        {
            var paths = new string[3];

            for (int index = 0; index < 3; index++)
            {
                ushort field = index switch { 0 => a, 1 => b, _ => c };
                paths[index] = Write($"v{index}.dat", Rar4Archive(0x0001, field, firstVolume: index == firstFlagIndex));
            }

            VolumeGroupOrder order = VolumeNumberFromContent.ResolveGroup(paths[0], paths.Select(VolumeNumberFromContent.Read));

            Assert.Equal(expectedConfirmed, order.Confirmed);
            Assert.Equal(expectedFail, order.Fail);

            if (expectedConfirmed)
            {
                Assert.Equal(paths[0], order.Slots[0].Path);
                Assert.Equal(paths[2], order.Slots[2].Path);
            }
        }

        [Fact]
        public void RAR4_只有一卷时没有整组上下文_两种基数都说得通_不许认()
        {
            /*
             * 这一条正是用户说的"两种基数解释都成立时不许认"：
             * 单看一卷（字段值 0），"0 起 → 它是第 1 卷"与"1 起 → 它是第 0 卷（无效）"都说得通，
             * 没有整组就定不了基数。单卷读取因此只给原始值、不给卷号，定序也必须有 ≥ 2 卷。
             */
            string path = Write("solo.dat", Rar4Archive(0x0001, 0, firstVolume: true));

            VolumeNumberReading reading = VolumeNumberFromContent.Read(path);

            Assert.Null(reading.Number); // 单卷上没有结论
            Assert.Equal(0, reading.RawField);

            VolumeGroupOrder order = VolumeNumberFromContent.ResolveGroup(path, new[] { reading });

            Assert.False(order.Confirmed);
            Assert.Equal(VolumeNumberFail.GroupIncomplete, order.Fail);
        }

        [Fact]
        public void ZIP_两片时用消去法定序_三片以上不认()
        {
            string segment = Write("seg.bin", ZipSegment(8192, ZipSegmentHead.LocalFileHeader));
            string tailTwo = Write("tail2.bin", ZipTail(1, 0, Array.Empty<byte>()));
            string tailThree = Write("tail3.bin", ZipTail(2, 0, Array.Empty<byte>()));

            VolumeGroupOrder two = VolumeNumberFromContent.ResolveGroup(
                tailTwo,
                new[] { VolumeNumberFromContent.Read(segment), VolumeNumberFromContent.Read(tailTwo) });

            Assert.True(two.Confirmed);
            Assert.Equal(2, two.Count);
            Assert.Equal(segment, two.Slots[0].Path);
            Assert.Equal(tailTwo, two.Slots[1].Path);

            /*
             * ⚠ **口径变更（2026-10-07，用户拍板"按①做"）**：老口径是"三片以上不认"
             * （ZipTooManyDisks）。新口径 = 片数由**末片自述的 n** 决定：
             * 本用例这一档是"末片说 3 片、只拿到 1 片非末片" ⇒ 正确结论是**缺片**（ZipPartsMissing），
             * ⛔ 不是"片数太多"。改动的来由见 docs/需求变更.md 与提交说明。
             */
            VolumeGroupOrder three = VolumeNumberFromContent.ResolveGroup(
                tailThree,
                new[] { VolumeNumberFromContent.Read(segment), VolumeNumberFromContent.Read(tailThree) });

            Assert.False(three.Confirmed);
            Assert.Equal(VolumeNumberFail.ZipPartsMissing, three.Fail);
            Assert.Equal(1, three.Detail);

            // ── n ≥ 3：受约束唯一指派（名字里的 zNN 决定盘号；超范围/撞号才拒） ──

            string z01 = Write("g.z01", ZipSegment(8192, ZipSegmentHead.LocalFileHeader));
            string z02 = Write("g.z02", ZipSegment(8192, ZipSegmentHead.LocalFileHeader));
            string tail3 = Write("g.zip", ZipTail(2, 0, Array.Empty<byte>()));

            VolumeGroupOrder full = VolumeNumberFromContent.ResolveGroup(
                tail3,
                new[]
                {
                    VolumeNumberFromContent.Read(z01),
                    VolumeNumberFromContent.Read(z02),
                    VolumeNumberFromContent.Read(tail3)
                });

            Assert.True(full.Confirmed);
            Assert.Equal(3, full.Count);
            Assert.Equal(z01, full.Slots[0].Path);
            Assert.Equal(z02, full.Slots[1].Path);
            Assert.Equal(tail3, full.Slots[2].Path);

            // 超范围（z11 ≥ n）⇒ 这一片不属于这一组 ⇒ 判不出
            string z11 = Write("g.z11", ZipSegment(8192, ZipSegmentHead.LocalFileHeader));
            VolumeGroupOrder outOfRange = VolumeNumberFromContent.ResolveGroup(
                tail3,
                new[]
                {
                    VolumeNumberFromContent.Read(z01),
                    VolumeNumberFromContent.Read(z11),
                    VolumeNumberFromContent.Read(tail3)
                });

            Assert.False(outOfRange.Confirmed);

            // 撞号（两片都自称 z01）⇒ 判不出，⛔ 不许任取一份去凑
            string z01b = Write("g.z01.dup", ZipSegment(8192, ZipSegmentHead.LocalFileHeader));
            VolumeGroupOrder collision = VolumeNumberFromContent.ResolveGroup(
                tail3,
                new[]
                {
                    VolumeNumberFromContent.Read(z01),
                    VolumeNumberFromContent.Read(z01b),
                    VolumeNumberFromContent.Read(tail3)
                });

            Assert.False(collision.Confirmed);
        }

        [Fact]
        public void ZIP_末片说两片_但只找到末片_少片不认()
        {
            string tail = Write("tail.bin", ZipTail(1, 0, Array.Empty<byte>()));

            VolumeGroupOrder order = VolumeNumberFromContent.ResolveGroup(tail, new[] { VolumeNumberFromContent.Read(tail) });

            Assert.False(order.Confirmed);
            Assert.Equal(VolumeNumberFail.ZipPartsMissing, order.Fail);
        }

        // ── 卷名拼法：只此一个出口 ──

        [Fact]
        public void 卷名拼法_三族各一套_都在唯一出口里()
        {
            Assert.Equal(
                new[] { "amb909.7z.001", "amb909.7z.002" },
                VolumeNumberFromContent.BuildStandardNames("amb909", VolumeNamingFamily.SevenZipNumbered, 2));

            // RAR 族带 .rar 尾巴；卷数超过 9 才补零（与 WinRAR 自己的口径一致）
            Assert.Equal(
                new[] { "amb909.part1.rar", "amb909.part2.rar", "amb909.part3.rar" },
                VolumeNumberFromContent.BuildStandardNames("amb909", VolumeNamingFamily.RarPart, 3));

            Assert.Equal(
                new[] { "amb909.part01.rar", "amb909.part02.rar" },
                VolumeNumberFromContent.BuildStandardNames("amb909", VolumeNamingFamily.RarPart, 12).Take(2));

            // 跨盘 zip：末片叫 .zip，之前的片叫 .z01/.z02……
            Assert.Equal(
                new[] { "sp.z01", "sp.z02", "sp.zip" },
                VolumeNumberFromContent.BuildStandardNames("sp", VolumeNamingFamily.ZipSpanned, 3));

            // 只有 1 片时不存在"跨盘组"
            Assert.Empty(VolumeNumberFromContent.BuildStandardNames("sp", VolumeNamingFamily.ZipSpanned, 1));
        }

        [Theory]
        [InlineData("amb909.part1.rar", VolumeContentFormat.Rar, "amb909")]
        [InlineData("amb909.rar", VolumeContentFormat.Rar, "amb909")]
        [InlineData("sp.z01", VolumeContentFormat.Zip, "sp")]
        [InlineData("sp.zip", VolumeContentFormat.Zip, "sp")]
        [InlineData("movie.7z.002", VolumeContentFormat.SevenZip, "movie")]
        public void 基名_剥掉卷标记段与后缀段(string fileName, VolumeContentFormat format, string expected)
        {
            Assert.True(VolumeNumberFromContent.TryDeriveStem(
                Path.Combine(_root, fileName), format, out string stem));

            Assert.Equal(expected, stem);
        }

        // ── 接进 VolumeNameRepair 那条内容分支（计划 + 只改名字） ──

        [Fact]
        public async Task RAR5_整组名字被改烂_计划按内容卷号给标准名_改完内容一字节没动()
        {
            string directory = Path.Combine(_root, "rar5group");
            Directory.CreateDirectory(directory);

            string first = Write(Path.Combine(directory, "amb909.7.01"), Rar5MainHeader(0x0001u, null));
            string second = Write(Path.Combine(directory, "amb909.z.2"), Rar5MainHeader(0x0003u, 1u));
            string third = Write(Path.Combine(directory, "amb909..3"), Rar5MainHeader(0x0003u, 2u));

            var sizes = new Dictionary<string, long>
            {
                [first] = new FileInfo(first).Length,
                [second] = new FileInfo(second).Length,
                [third] = new FileInfo(third).Length
            };

            VolumeNameRepairPlan plan = await VolumeNameRepair.PlanByContentAsync(
                first,
                VolumeNameRepair.EnumerateVolumeCandidatesInDirectory(first),
                engine: new Engines.SevenZip.SevenZipEngine(),
                workRootDirectory: WorkRootFor(first));

            Assert.True(plan.CanRepair, plan.Reason);
            Assert.Equal(3, plan.Items.Count);
            Assert.Equal(new[] { "amb909.part1.rar", "amb909.part2.rar", "amb909.part3.rar" },
                plan.Items.Select(i => i.SuggestedFileName).ToArray());

            VolumeNameRepairResult result = VolumeNameRepair.TryApply(plan);

            Assert.True(result.Success, result.Message);

            foreach ((string oldPath, long size) in sizes)
            {
                Assert.False(File.Exists(oldPath), $"旧名字还在：{oldPath}");
                _ = size;
            }

            Assert.True(File.Exists(Path.Combine(directory, "amb909.part1.rar")));
            Assert.Equal(
                sizes[first],
                new FileInfo(Path.Combine(directory, "amb909.part1.rar")).Length);
        }

        [Fact]
        public async Task RAR5_目标名已被占用_整组不改()
        {
            string directory = Path.Combine(_root, "rar5taken");
            Directory.CreateDirectory(directory);

            string first = Write(Path.Combine(directory, "amb909.7.01"), Rar5MainHeader(0x0001u, null));
            string second = Write(Path.Combine(directory, "amb909.z.2"), Rar5MainHeader(0x0003u, 1u));

            // 目标名先被别人占了：⛔ 绝不覆盖 —— 整组都不改
            Write(Path.Combine(directory, "amb909.part2.rar"), new byte[] { 1, 2, 3 });

            VolumeNameRepairPlan plan = await VolumeNameRepair.PlanByContentAsync(
                first,
                VolumeNameRepair.EnumerateVolumeCandidatesInDirectory(first),
                engine: new Engines.SevenZip.SevenZipEngine(),
                workRootDirectory: WorkRootFor(first));

            Assert.False(plan.CanRepair);
            Assert.Contains("amb909.part2.rar", plan.Reason, StringComparison.Ordinal);
            Assert.True(File.Exists(first), "源文件被动了");
            Assert.True(File.Exists(second), "源文件被动了");
        }

        [Fact]
        public async Task RAR5_单卷包被改了名字_不是分卷_不许动它()
        {
            string directory = Path.Combine(_root, "rarsolo");
            Directory.CreateDirectory(directory);

            string solo = Write(Path.Combine(directory, "solo.7.01"), Rar5MainHeader(0x0000u, null));

            VolumeNameRepairPlan plan = await VolumeNameRepair.PlanByContentAsync(
                solo,
                VolumeNameRepair.EnumerateVolumeCandidatesInDirectory(solo),
                engine: new Engines.SevenZip.SevenZipEngine(),
                workRootDirectory: WorkRootFor(solo));

            Assert.False(plan.CanRepair);
            Assert.True(File.Exists(solo), "单卷包被改掉了名字");
        }

        [Fact]
        public async Task ZIP_两片跨盘_末片名字本来就对_计划只改那一片被改烂的()
        {
            string directory = Path.Combine(_root, "zipspan");
            Directory.CreateDirectory(directory);

            /*
             * 真机里最常见的样子：末片还叫 sp2.zip，中间那一片被网盘/自己改烂了（sp2.disk1）。
             * 末片的内容说"本盘号 1"（= 第 2 片、总共 2 片），首片内容是 zip 流（真样本那种以
             * 跨盘标记开场的样子）→ 消去法把它定成第 1 片，于是它该叫 sp2.z01。
             */
            string tail = Write(Path.Combine(directory, "sp2.zip"), ZipTail(1, 0, Array.Empty<byte>()));
            string first = Write(Path.Combine(directory, "sp2.disk1"), ZipSegment(4096, ZipSegmentHead.SpanningMarker));

            VolumeNameRepairPlan plan = await VolumeNameRepair.PlanByContentAsync(
                tail,
                VolumeNameRepair.EnumerateVolumeCandidatesInDirectory(tail),
                engine: new Engines.SevenZip.SevenZipEngine(),
                workRootDirectory: WorkRootFor(tail));

            Assert.True(plan.CanRepair, plan.Reason);
            Assert.Equal(2, plan.Items.Count);
            Assert.Equal("sp2.z01", plan.Items[0].SuggestedFileName);
            Assert.Equal("sp2.zip", plan.Items[1].SuggestedFileName);

            // 末片的名字本来就是对的 → 计划要改的是那一片被改烂的（TryApply 拿它当入口）
            Assert.Equal(first, plan.CurrentPath);

            VolumeNameRepairResult result = VolumeNameRepair.TryApply(plan);

            Assert.True(result.Success, result.Message);
            Assert.True(File.Exists(Path.Combine(directory, "sp2.z01")));
            Assert.True(File.Exists(Path.Combine(directory, "sp2.zip")));
            Assert.False(File.Exists(first), "旧名字还在");
        }

        [Fact]
        public async Task ZIP_单盘包_内容说盘号0_不是跨盘_不许动它()
        {
            string directory = Path.Combine(_root, "zipsolo");
            Directory.CreateDirectory(directory);

            string solo = Write(Path.Combine(directory, "solo.7.01"), ZipTail(0, 0, Array.Empty<byte>()));

            VolumeNameRepairPlan plan = await VolumeNameRepair.PlanByContentAsync(
                solo,
                VolumeNameRepair.EnumerateVolumeCandidatesInDirectory(solo),
                engine: new Engines.SevenZip.SevenZipEngine(),
                workRootDirectory: WorkRootFor(solo));

            Assert.False(plan.CanRepair);
            Assert.True(File.Exists(solo), "单盘包被改掉了名字");
        }

        // ── 构造字节（测试自己算 CRC：⛔ 不调产品里那个 CRC 实现，免得"自己验自己"） ──

        /// <summary>
        /// 这一单的"目标工作区根"（同卷）：<c>&lt;样本目录&gt;\.ArchiveFixer.work</c>。
        /// 真实调用方传的是 <c>&lt;目标目录&gt;\.ArchiveFixer.work</c>（不变量 12：需要临时物的地方由调用方传进来）；
        /// 用例里样本就在临时目录里，用**同一个常量**拼，⛔ 不写死名字。
        /// </summary>
        private static string WorkRootFor(string samplePath) =>
            Path.Combine(
                Path.GetDirectoryName(samplePath)!,
                Detection.VolumeContentInference.WorkDirectoryName);

        /// <summary>RAR5 主归档头：签名 + CRC32 + HeaderSize(vint) + Type + Flags + [扩展区大小] + [数据区大小] + ArchiveFlags + [卷号]。</summary>
        private static byte[] Rar5MainHeader(
            uint archiveFlags,
            uint? volumeField,
            uint headerFlags = 0,
            ulong extraAreaSize = 0,
            ulong dataAreaSize = 0,
            ulong headerType = 1)
        {
            var body = new List<byte>();

            body.AddRange(Vint(headerType));
            body.AddRange(Vint(headerFlags));

            if ((headerFlags & 0x0001) != 0)
            {
                body.AddRange(Vint(extraAreaSize));
            }

            if ((headerFlags & 0x0002) != 0)
            {
                body.AddRange(Vint(dataAreaSize));
            }

            body.AddRange(Vint(archiveFlags));

            if (volumeField.HasValue)
            {
                body.AddRange(Vint(volumeField.Value));
            }

            // 扩展区本体（可选的 6 字节）：CRC 覆盖范围包括它，所以必须一起写出来
            for (ulong i = 0; i < extraAreaSize; i++)
            {
                body.Add(0x00);
            }

            byte[] sizeField = Vint((ulong)body.Count).ToArray();

            var header = new List<byte> { (byte)'R', (byte)'a', (byte)'r', (byte)'!', 0x1A, 0x07, 0x01, 0x00 };

            // CRC32 覆盖 [Header size 字段, 头末尾]
            var covered = new List<byte>();
            covered.AddRange(sizeField);
            covered.AddRange(body);

            header.AddRange(BitConverter.GetBytes(TestCrc32(covered)));
            header.AddRange(sizeField);
            header.AddRange(body);

            return header.ToArray();
        }

        /// <summary>
        /// RAR 1.5–4.x（WinRAR 里叫 RAR4）：签名 + 主头（13 字节）+ 归档结尾块（卷号在里面）。
        ///
        /// <para>结尾块的布局照**真样本**写（2026-09-29：两套真包都是
        /// <c>HEAD_SIZE = 20 = 7 + 4(EARC_DATACRC) + 2(EARC_VOLNUMBER) + 7(EARC_REVSPACE)</c>，
        /// 卷号是 2 字节；只有第 1 卷带 <c>EARC_NEXT_VOLUME</c>）。
        /// <paramref name="legacyFourByteVolumeNumber"/> = 老代码以为的那种"四字节卷号"布局，
        /// 专门用来钉"布局不符就不认"。</para>
        /// </summary>
        private static byte[] Rar4Archive(
            ushort mainFlags,
            ushort volumeField,
            bool firstVolume,
            bool newNumbering = true,
            bool legacyFourByteVolumeNumber = false,
            bool nextVolume = true)
        {
            ushort flags = mainFlags;

            if (newNumbering)
            {
                flags |= 0x0010;
            }

            if (firstVolume)
            {
                flags |= 0x0100;
            }

            var bytes = new List<byte> { (byte)'R', (byte)'a', (byte)'r', (byte)'!', 0x1A, 0x07, 0x00 };

            // 主头：CRC16(2) TYPE(1)=0x73 FLAGS(2) SIZE(2)=13 RESERVED1(2) RESERVED2(4)
            var main = new List<byte>();
            main.AddRange(new byte[] { 0x73, 0x00, 0x00, 0x0D, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });
            main[0] = 0x73;
            main[1] = (byte)(flags & 0xFF);
            main[2] = (byte)(flags >> 8);

            bytes.AddRange(BitConverter.GetBytes(TestCrc16(main.ToArray())));
            bytes.AddRange(main);

            /*
             * 归档结尾块：TYPE(1) FLAGS(2) SIZE(2) [DATACRC(4)] VOLNUMBER(2 或老的 4) [REVSPACE(7)]。
             * 块总长（含前面的 CRC 2 字节）= HEAD_SIZE —— 真样本就是 7 + 4 + 2 + 7 = 20。
             */
            int volumeFieldSize = legacyFourByteVolumeNumber ? 4 : 2;

            ushort endFlags = 0x0002 | 0x0004 | 0x0008;

            if (nextVolume)
            {
                endFlags |= 0x0001; // EARC_NEXT_VOLUME：后面还有一卷
            }

            ushort endSize = (ushort)(7 + 4 + volumeFieldSize + 7);

            var end = new List<byte> { 0x7B, 0x00, 0x00 };
            end[1] = (byte)(endFlags & 0xFF);
            end[2] = (byte)(endFlags >> 8);
            end.Add((byte)(endSize & 0xFF));
            end.Add((byte)(endSize >> 8));
            end.AddRange(new byte[] { 0x11, 0x22, 0x33, 0x44 });              // EARC_DATACRC
            end.AddRange(BitConverter.GetBytes(volumeField));                 // EARC_VOLNUMBER（后面补 0 就当 4 字节）
            end.AddRange(new byte[volumeFieldSize - 2]);
            end.AddRange(new byte[7]);                                        // EARC_REVSPACE

            bytes.AddRange(BitConverter.GetBytes(TestCrc16(end.ToArray())));
            bytes.AddRange(end);

            return bytes.ToArray();
        }

        /// <summary>跨盘 zip 的**末片**：随便一段数据 + EOCD（盘号在 EOCD 里）。</summary>
        private static byte[] ZipTail(int diskNumber, int centralDirectoryDisk, byte[] comment)
        {
            var bytes = new List<byte> { 0x50, 0x4B, 0x03, 0x04 };
            bytes.AddRange(new byte[26]);
            bytes.AddRange(new byte[] { 0xAA, 0xBB, 0xCC });

            bytes.AddRange(new byte[] { 0x50, 0x4B, 0x05, 0x06 });
            bytes.AddRange(BitConverter.GetBytes((ushort)diskNumber));
            bytes.AddRange(BitConverter.GetBytes((ushort)centralDirectoryDisk));
            bytes.AddRange(BitConverter.GetBytes((ushort)1));
            bytes.AddRange(BitConverter.GetBytes((ushort)1));
            bytes.AddRange(BitConverter.GetBytes(0u));
            bytes.AddRange(BitConverter.GetBytes(0u));
            bytes.AddRange(BitConverter.GetBytes((ushort)comment.Length));
            bytes.AddRange(comment);

            return bytes.ToArray();
        }

        /// <summary>跨盘 zip 的**非末片**能以哪个签名开头（真样本的第 1 片就是"跨盘标记"那一档）。</summary>
        private enum ZipSegmentHead
        {
            /// <summary>本地文件头 <c>PK\x03\x04</c>（老判据只认这一种）。</summary>
            LocalFileHeader,

            /// <summary>跨盘标记 <c>PK\x07\x08</c>（真 PKZIP 跨盘第 1 片的开头，7-Zip 叫 Embedded Stub）。</summary>
            SpanningMarker,

            /// <summary>中央目录 <c>PK\x01\x02</c>。</summary>
            CentralDirectory,

            /// <summary>根本不是 PK。</summary>
            NotZip
        }

        /// <summary>
        /// 跨盘 zip 的**非末片**：开头是给定签名，后面接一段填充。
        /// ⚠ 真样本的切点落在数据中间，结尾**没有**任何标记 —— 所以这里也不再往结尾贴标记。
        /// </summary>
        private static byte[] ZipSegment(int size, ZipSegmentHead head)
        {
            var bytes = new List<byte>();

            switch (head)
            {
                case ZipSegmentHead.LocalFileHeader:
                    bytes.AddRange(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x00, 0x00, 0x00, 0x00 });
                    break;

                case ZipSegmentHead.SpanningMarker:
                    bytes.AddRange(new byte[] { 0x50, 0x4B, 0x07, 0x08 });
                    break;

                case ZipSegmentHead.CentralDirectory:
                    bytes.AddRange(new byte[] { 0x50, 0x4B, 0x01, 0x02 });
                    break;

                default:
                    bytes.AddRange(new byte[] { 0x2C, 0xE5, 0xA7, 0xA0 });
                    break;
            }

            while (bytes.Count < size)
            {
                bytes.Add(0x5A);
            }

            return bytes.ToArray();
        }

        private static IEnumerable<byte> Vint(ulong value)
        {
            do
            {
                byte current = (byte)(value & 0x7F);
                value >>= 7;

                if (value != 0)
                {
                    current |= 0x80;
                }

                yield return current;
            }
            while (value != 0);
        }

        private static readonly uint[] TestCrcTable = BuildTestCrcTable();

        private static uint[] BuildTestCrcTable()
        {
            var table = new uint[256];

            for (uint index = 0; index < 256; index++)
            {
                uint value = index;

                for (int bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) != 0 ? 0xEDB88320 ^ (value >> 1) : value >> 1;
                }

                table[index] = value;
            }

            return table;
        }

        /// <summary>标准 CRC-32（zlib 口径：初值取反、末尾取反）。</summary>
        private static uint TestCrc32(IReadOnlyList<byte> data)
        {
            uint crc = 0xFFFFFFFF;

            foreach (byte current in data)
            {
                crc = (crc >> 8) ^ TestCrcTable[(crc ^ current) & 0xFF];
            }

            return crc ^ 0xFFFFFFFF;
        }

        private static ushort TestCrc16(IReadOnlyList<byte> data) => (ushort)(TestCrc32(data) & 0xFFFF);

        private string Write(string path, byte[] bytes)
        {
            File.WriteAllBytes(path, bytes);

            return path;
        }
    }
}

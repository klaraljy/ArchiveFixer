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

        /// <summary>
        /// **停因那一档：名字被伪装过头的那一片，照样按「缺卷」处理**（用户 2026-10-13 口径
        /// 「没有完整的**不是缺卷**……你就**只能显示跳过**」）。
        ///
        /// <para>现场（困难批 `run-4444`，夹具 `dbg-7c44505e` 逐字）：第 2 层开 `bbb-L2.jpg`
        /// （后缀整个被换成 `.jpg`、内容其实是 RAR 分卷的一片）—— 引擎在真密码那一趟**真解出了
        /// 2 个文件 / 10489352 字节**、同时报 `Data Error in encrypted file`，于是老判据落
        /// 「密码对了但数据坏了 ⇒ 压缩包损坏」⇒ 整单「解压失败」+ **已经解出来的那一片被扔掉**
        /// ⇒ 那一组永远缺片。事实是**这一组不全**（手上只有这一卷）。</para>
        ///
        /// <para>三条对照，全部只读事实：
        /// ① 内容自证是分卷组成员 + 同组入口不在旁边 ⇒ **缺卷**；
        /// ② 同组入口就在旁边 ⇒ 不判缺卷（走引擎给的那一档）；
        /// ③ 内容**判不出**（一坨零字节）⇒ 照旧老口径 —— 判据**没有放宽到"判不出"**。</para>
        ///
        /// <para>⚠ 对照②用的是**同族同基名、但不是后续卷**的那一份（`bbb-L2.part1.jpg`）——
        /// 这一格如实记下一个**已知限制**：`IsGroupEntryPresentBeside` 仍然按名字判（它要"同族同基名"，
        /// 而后缀被换成 `.jpg` 的那一片**按名字推不出族**）⇒ 一个叫 `bbb-L2.rar` 的入口摆在同一层里
        /// 它也看不见。⛔ 这不是本轮引入的：那一档一直是名字尺子；本轮的改动只是把**"这一份是不是这一组的片"**
        /// 那一半换成内容证据。它不会让结论变坏（最坏情况 = 该"缺卷"却按缺卷收场 ⇒ 片保住、批末再凑，
        /// 而老行为是整单失败 + 把那一片扔掉）。</para>
        /// </summary>
        [Fact]
        public void 停因_名字被伪装成jpg的那一片_照样按缺卷处理()
        {
            string disguisedDir = Path.Combine(_root, "disguised");
            Directory.CreateDirectory(disguisedDir);

            // ① 名字伪装成 .jpg、内容自证是分卷组的一员（RAR5 的 Volume 位开着）
            string piece = Path.Combine(disguisedDir, "bbb-L2.jpg");
            File.WriteAllBytes(piece, Rar5MainHeader(0x0001u, null));

            Assert.True(
                RecursiveExtractor.IsPieceOfIncompleteGroupForTests(piece),
                "内容自证是分卷组成员、同组入口又不在旁边 ⇒ 必须是「缺卷」");

            // ② 同层的"同族同基名"那份也认不出入口（**已知限制**，如实钉住）：它按名字判，
            //    而后缀被换成 .jpg 的那一片按名字推不出族 ⇒ 那个闸门在伪装名上等于瞎的。
            File.WriteAllBytes(Path.Combine(disguisedDir, "bbb-L2.part1.jpg"), Rar5MainHeader(0x0001u, null));
            File.WriteAllBytes(Path.Combine(disguisedDir, "bbb-L2.rar"), Rar5MainHeader(0x0001u, null));

            Assert.False(
                RecursiveExtractor.IsGroupEntryPresentBesideForTests(piece),
                "已知限制：名字被伪装成非归档后缀时，「同组入口在不在旁边」这把**名字**尺子看不见入口");

            /*
             * ②b ⛔ **这一份自己不算"同组的入口"**（2026-10-13 困难批 `run-23232323` 定案）：
             * 夹具 `dbg-c6092d64` 里 `out\bbb-W0-0\bbb-L2.zip` 只是**它自己**一个文件，
             * 老写法却判成"入口在"（它当然同族同基名、又不在"后续卷"那一档）⇒ 补充闸门等于瞎的。
             */
            string aloneDir = Path.Combine(_root, "alone");
            Directory.CreateDirectory(aloneDir);

            string alone = Path.Combine(aloneDir, "ddd-L2.zip");
            File.WriteAllBytes(alone, new byte[4096]);

            Assert.False(
                RecursiveExtractor.IsGroupEntryPresentBesideForTests(alone),
                "目录里只有它自己 ⇒ 不许把这一份自己当成「同组的入口」");

            /*
             * ②c 内容**自己写着"我不是第 1 片"**（RAR5 卷号 2）⇒ 那一档不必再问"入口在不在旁边"
             * ——入口不可能就是这一份自己。同层摆一个会被当成"入口"的同族同基名文件也照样算缺卷。
             */
            string secondDir = Path.Combine(_root, "second");
            Directory.CreateDirectory(secondDir);

            string second = Path.Combine(secondDir, "eee-L2.rar");
            File.WriteAllBytes(second, Rar5MainHeader(0x0003u, 1u));
            File.WriteAllBytes(Path.Combine(secondDir, "eee-L2.part1.rar"), Rar5MainHeader(0x0001u, null));

            Assert.True(
                RecursiveExtractor.IsPieceOfIncompleteGroupForTests(second),
                "内容自证卷号 2 ⇒ 它不是第一片 ⇒ 入口必在别处 ⇒ 必须是「缺卷」");

            // ③ 内容**判不出**（一坨零字节）⇒ 照旧老口径：判据不许放宽到"判不出"
            string unknownDir = Path.Combine(_root, "unknown");
            Directory.CreateDirectory(unknownDir);

            string unknown = Path.Combine(unknownDir, "ccc-L2.jpg");
            File.WriteAllBytes(unknown, new byte[4096]);

            Assert.False(
                RecursiveExtractor.IsPieceOfIncompleteGroupForTests(unknown),
                "内容判不出 ⇒ 判据不许放宽（照旧走引擎给的那一档）");

            // ④ 内容**可证不是**分卷成员（单卷 RAR：Volume 位没置）⇒ 同样不许走这一档
            string singleDir = Path.Combine(_root, "single");
            Directory.CreateDirectory(singleDir);

            string single = Path.Combine(singleDir, "ddd-L2.jpg");
            File.WriteAllBytes(single, Rar5MainHeader(0x0000u, null));

            Assert.False(
                RecursiveExtractor.IsPieceOfIncompleteGroupForTests(single),
                "内容可证不是分卷成员 ⇒ 更不许判成「缺卷」");

            // ⑤ 老那一半（名字像后续卷）照旧有效：名字读得出卷号 ⇒ 不看内容也算这一档
            string namedDir = Path.Combine(_root, "named");
            Directory.CreateDirectory(namedDir);

            string named = Path.Combine(namedDir, "eee-L2.part3.rar");
            File.WriteAllBytes(named, new byte[4096]);

            Assert.True(
                RecursiveExtractor.IsPieceOfIncompleteGroupForTests(named),
                "名字就是「这一组的后续卷」⇒ 老口径照旧（内容判不出也认）");
        }

        /// <summary>
        /// **内容可证"不是分卷的一员" ⇒ 名字里的卷标记一律作废**（2026-10-18，困难批 `24242424` 现场）。
        ///
        /// <para>现场逐字（`walk` 探针）：`222-W1-1.932` 的真实内容是**单盘 ZIP**，卡上
        /// `Membership=ProvablyNotVolumeMember / BodyKind=SingleComplete`；而名字尺子把末段
        /// `mp4-932` 的容差档读成"卷号 932"（`names` 探针逐字 `Tolerant(末段「mp4-932」) = True（→932，垃圾「mp4-」）`）。
        /// 老写法里有三处**名字比内容先说话**，于是：
        /// ① `ResolveSlotIndex` 回来 932；
        /// ② `IsPieceOfIncompleteGroup` 回来 true（递归路把它当"这一组的后续卷、入口不在旁边"）；
        /// ③ `VolumeNameRepair.Plan` 把它还原成 `222-W1-1.932`（凭空造出卷号）。
        /// 后果 = 递归路的密码候选**只试了 1/9**（`7z t -p<密码本第 7 条>` 实测 `Everything is Ok`）
        /// ⇒ 那一片永远出不来 ⇒ 那一组只剩槽位 1、3，缺 2。</para>
        ///
        /// <para>本用例钉住的三条判据都在**同一个出口**：<c>VolumePieceFacts.ContentProvesNotAVolumePiece</c>。</para>
        /// </summary>
        [Fact]
        public void 内容可证是单盘ZIP_名字里那个三位数字不算卷号()
        {
            string dir = Path.Combine(_root, "named-single-zip");
            Directory.CreateDirectory(dir);

            // 名字故意做成"名字尺子读得出卷号 932"的形状（`222-W1-1.mp4-932` 的真机名字）。
            string path = Path.Combine(dir, "bbb-L2.mp4-932");

            using (FileStream stream = File.Create(path))
            using (var zip = new System.IO.Compression.ZipArchive(
                       stream,
                       System.IO.Compression.ZipArchiveMode.Create))
            {
                System.IO.Compression.ZipArchiveEntry entry = zip.CreateEntry("payload.txt");
                using StreamWriter writer = new(entry.Open());
                writer.Write("hello");
            }

            FileNatureCard card = FileNature.Inspect(path);

            Assert.Equal(VolumeNumberFromContent.VolumeMembershipVerdict.ProvablyNotVolumeMember, card.Membership);
            Assert.Equal(ArchiveBodyKind.SingleComplete, card.BodyKind);
            Assert.Equal(932, card.VolumeIndexByName);   // 名字确实读成 932 —— 不是"没读出来"，是"读出来但不作数"

            // ① 槽位：内容否决 ⇒ 判不出（⛔ 不许拿名字的 932 顶替）
            Assert.Null(VolumePieceFacts.ResolveSlotIndex(path, card));

            // ② 递归路那一档：内容否决 ⇒ 不按「缺卷」收场（否则密码候选只试 1 个）
            Assert.False(
                RecursiveExtractor.IsPieceOfIncompleteGroupForTests(path),
                "内容可证是单卷 ZIP ⇒ 名字里的 932 不算卷号，不许按「缺卷」收场");

            // ③ 卷名修复：内容否决 ⇒ 一个字节都不改（⛔ 不许凭空造出 `bbb-L2.932`）
            VolumeNameRepairPlan plan = VolumeNameRepair.Plan(path, new[] { Path.GetFileName(path) });

            Assert.False(plan.CanRepair);
        }

        /// <summary>
        /// **卡上的专属算法（用户 2026-10-13 口径：「名字被伪装到读不出卷号 ⇒ 组凑不来」是底层识别的事）**：
        /// RAR 那一族**按内容自证"我是这一组的第一片"**，而且把"内容里那个卷号字段的原始值"留在卡上
        /// （第 1 片当锚点、其余几片靠差值定序 —— RAR4 老编号族的基数格式没写死，只有相对大小可靠）。
        ///
        /// <para>真字节形状全部来自本类既有的两个构造器（RAR5 = 主头里 <c>MHFL_VOLUME</c> 开位 + 第 1 卷不带卷号字段；
        /// RAR4 = 主头 <c>MHD_FIRSTVOLUME 0x0100</c> + 归档结尾块里的 <c>EARC_VOLNUMBER</c>）。
        /// ⛔ 判据一个字不看名字：这两份文件名都叫 <c>x.bin</c>。</para>
        /// </summary>
        [Fact]
        public void 卡上的专属算法_RAR按内容自证第一片_并留下卷号原始值()
        {
            // RAR5：第 1 卷（没有卷号字段 ⇒ 内容自证的是"1"）与第 2 卷（字段值 1 ⇒ 卷号 2）
            string rar5First = Write("rar5-first.bin", Rar5MainHeader(0x0001u, null));
            string rar5Second = Write("rar5-second.bin", Rar5MainHeader(0x0003u, 1u));

            // RAR4：主头的"第 1 卷"标记 + 结尾块的卷号字段（真样本：第 1 卷 0、第 2 卷 1）
            string rar4First = Write("rar4-first.bin", Rar4Archive(0x0001, 0, firstVolume: true));
            string rar4Second = Write("rar4-second.bin", Rar4Archive(0x0001, 1, firstVolume: false));

            FileNatureCard card5First = FileNature.Inspect(rar5First);
            FileNatureCard card5Second = FileNature.Inspect(rar5Second);
            FileNatureCard card4First = FileNature.Inspect(rar4First);
            FileNatureCard card4Second = FileNature.Inspect(rar4Second);

            // ① 第一片：内容自证（RAR5 靠"卷号 = 1"、RAR4 靠主头那个"第 1 卷"标记）
            Assert.Equal(ArchiveBodyKind.VolumeFirstPiece, card5First.BodyKind);
            Assert.Equal(ArchiveBodyKind.VolumeFirstPiece, card4First.BodyKind);

            // ② 不是第一片的那两份：RAR5 读得出卷号 2 ⇒ 可证"是某组的一片、不是第一片"；
            //    RAR4 读不出卷号（基数待定）⇒ 照旧"判不出"，⛔ 但不许当成"不是分卷"（参与者 = true）。
            Assert.Equal(ArchiveBodyKind.VolumePiece, card5Second.BodyKind);
            // 2026-10-22：RAR4 也读得出卷号了（结尾块字段 1 → 第 2 卷）⇒ 与 RAR5 同一档
            Assert.Equal(ArchiveBodyKind.VolumePiece, card4Second.BodyKind);
            Assert.Equal(1, card4First.VolumeNumber);
            Assert.Equal(2, card4Second.VolumeNumber);
            Assert.True(card4Second.ParticipatesInVolumeHandling);

            // ③ 卷号原始值留在卡上（RAR5 字段值 1、RAR4 结尾块 0/1），供"以第一片为锚点定序"用
            Assert.Equal(1, card5Second.RawVolumeField);
            Assert.Equal(0, card4First.RawVolumeField);
            Assert.Equal(1, card4Second.RawVolumeField);
        }

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

            // 2026-10-22 新口径（用户点名必须改的那一行）：RAR4 卷号 = 结尾块字段值 + 1（与 RAR5 同一算法）
            Assert.Equal((int)volumeField, reading.RawField);
            Assert.Equal((int)volumeField + 1, reading.Number);
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

        /// <summary>
        /// **真混族：RAR5 卷与 legacy 卷混在同一组候选里 ⇒ 整组拒**（2026-10-07 清单第 (8) 条待补守门⑤）。
        ///
        /// <para>两族各说各的卷号体系（RAR5 = 主归档头字段、legacy = 卷尾归档结尾块），
        /// 混在一起时谁也证明不了"我是第几片" ⇒ 判不出 ⇒ 一个名字都不许改。</para>
        ///
        /// <para>⚠ 记账（读代码后的更正）：`ReadRar4Legacy` 的**老式编号族从不设 `Number`**
        /// （`VolumeNumberFromContent.cs:604-619` 直接 `Refuse(RarOldNumbering)`），所以真混族要用
        /// "新式编号 + 卷尾卷号"的 legacy 卷来造；`ResolveGroup` 里那句"混族"判定
        /// （`:863-870`）实际就是"有 legacy 卷 vs 有 RAR5 卷"，与这里造的样本一致。</para>
        ///
        /// <para><b>红检（实做，如实记结果）</b>：把 `:866` 的 `if (hasRar4Legacy && hasRar5)` 改成
        /// `if (false &amp;&amp; …)` ⇒ 本条**仍然是绿的** —— 因为混族落到 legacy 分支后，
        /// 基数校验（`:886-892`）同样拒（RAR5 第 1 卷的 `RawField` 恒为 0，与 legacy 的 0 撞在一起
        /// ⇒ 0 起/1 起都不连号）。⇒ 本条钉的是**对外契约**（"混族不许认成一组"），
        /// ⛔ 不是 `:866-870` 那一段的专属守门；"混族被拒"这件事有**两条独立判据**兜底。</para>
        /// </summary>
        [Fact]
        public void 真混族_RAR5卷与legacy卷混在一起_整组拒()
        {
            string[] paths =
            {
                Write("a.dat", Rar5MainHeader(0x0001u, null)),             // RAR5 第 1 卷
                Write("b.dat", Rar5MainHeader(0x0003u, 1u)),               // RAR5 第 2 卷
                Write("c.dat", Rar4Archive(0x0001, 1, firstVolume: false)) // legacy（新式编号）卷尾卷号 = 1
            };

            VolumeGroupOrder order = VolumeNumberFromContent.ResolveGroup(
                paths[0],
                paths.Select(VolumeNumberFromContent.Read));

            Assert.False(order.Confirmed, $"混族不许认成一组（Fail={order.Fail}）");
        }

        /// <summary>
        /// **撞号：两卷自称同一个卷号 ⇒ 整组拒**（清单第 (8) 条待补守门② 的后一半；
        /// 前半"两种基数都说不通"由 <c>RAR4_基数靠整组自洽判定_第1卷标记要一致</c> 钉住）。
        /// ⛔ 不许任取一份去凑 1..N。
        /// </summary>
        [Fact]
        public void RAR5_两卷撞同一个卷号_整组拒()
        {
            string[] paths =
            {
                Write("a.dat", Rar5MainHeader(0x0001u, null)),
                Write("b.dat", Rar5MainHeader(0x0003u, 1u)),  // 第 2 卷
                Write("c.dat", Rar5MainHeader(0x0003u, 1u))   // 也自称第 2 卷（撞号）
            };

            VolumeGroupOrder order = VolumeNumberFromContent.ResolveGroup(
                paths[0],
                paths.Select(VolumeNumberFromContent.Read));

            Assert.False(order.Confirmed, $"撞号不许认成一组（Fail={order.Fail}）");
        }

        [Theory]
        // 卷尾字段值 a/b/c + "第 1 卷"标记长在第几个文件上 → （认不认、不认是哪一档）
        [InlineData(0, 1, 2, 0, true, VolumeNumberFail.None)]                   // 0 起的一整组：{0,1,2} → 卷号 1,2,3
        [InlineData(1, 2, 3, 0, false, VolumeNumberFail.GroupNotContiguous)]   // 2026-10-22 新口径：卷号=原值+1 ⇒ {2,3,4} 连不成 1..N
        [InlineData(0, 2, 3, 0, false, VolumeNumberFail.GroupNotContiguous)]    // 新口径：卷号 {1,3,4} 连不成 1..N → 不认
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

            // 2026-10-22：单卷也读得出卷号了（字段 0 → 第 1 卷）；"整组齐没齐"仍由定序那一档回答
            Assert.Equal(1, reading.Number);
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

        /// <summary>
        /// 与 <see cref="ZipTail"/> 同形，但**中央目录偏移由调用方给**（`-v` 切出来的末片把它写成整包偏移，
        /// 于是它 ≥ 本文件长度 —— 这就是"我是最后一片"那条内容证据）。
        /// </summary>
        private static byte[] ZipTailWithCdOffset(uint centralDirectoryOffset)
        {
            var bytes = new List<byte> { 0x50, 0x4B, 0x03, 0x04 };
            bytes.AddRange(new byte[26]);
            bytes.AddRange(new byte[] { 0xAA, 0xBB, 0xCC });

            bytes.AddRange(new byte[] { 0x50, 0x4B, 0x05, 0x06 });
            bytes.AddRange(BitConverter.GetBytes((ushort)0));
            bytes.AddRange(BitConverter.GetBytes((ushort)0));
            bytes.AddRange(BitConverter.GetBytes((ushort)1));
            bytes.AddRange(BitConverter.GetBytes((ushort)1));
            bytes.AddRange(BitConverter.GetBytes(119u));
            bytes.AddRange(BitConverter.GetBytes(centralDirectoryOffset));
            bytes.AddRange(BitConverter.GetBytes((ushort)0));

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

        /// <summary>
        /// **盘号 0/0 的 EOCD 也可能是"分片的最后一片"** —— 判据的另一半是**中央目录偏移 ≥ 本文件长度**。
        ///
        /// <para>真机 2026-10-18 运行期取证（样本 6666）：`打包111-L2.zip.000003`（10 490 952 字节）的 EOCD 逐字
        /// `diskNumber=0 / centralDirectoryDisk=0 / cdOffset=62 919 611`（62 919 611 ≥ 10 490 952）。
        /// 老代码在 `FromEndOfCentralDirectory` 里一见两个盘号都是 0 就返回 `ZipSingleDisk`、
        /// `ZipEndRecordMeansSplit=false` ⇒ 卡上判成 `单卷/分卷=SingleComplete`、`参与分卷那一套=False`
        /// ⇒ 末片被排除在收拢之外，整组永远拼不起来（① ② 双红）。</para>
        ///
        /// <para>⚠ 对照那一格钉的是**不许放宽**：普通的单卷 zip（中央目录就在自己身上）照旧"单卷"。
        /// 这正是这条判据当初要治的毛病（把一份普通单卷 zip 当成某分卷组的一片）。</para>
        /// </summary>
        [Fact]
        public void 盘号都是0的EOCD_中央目录偏移超过文件长度_算分片末片()
        {
            string splitPath = Write(Path.Combine(_root, "split-part.zip.003"), ZipTailWithCdOffset(999_999));

            VolumeNumberReading split = VolumeNumberFromContent.Read(splitPath);

            Assert.True(split.HasZipEndRecord);
            Assert.True(split.ZipEndRecordMeansSplit, "中央目录偏移 ≥ 本文件长度 ⇒ 这就是分片的最后一片");
            Assert.NotEqual(
                VolumeNumberFromContent.VolumeMembershipVerdict.ProvablyNotVolumeMember,
                VolumeNumberFromContent.ClassifyMembership(splitPath));
            Assert.NotEqual(ArchiveBodyKind.SingleComplete, FileNature.Inspect(splitPath).BodyKind);

            string singlePath = Write(Path.Combine(_root, "plain-single.zip"), ZipTailWithCdOffset(0));

            VolumeNumberReading single = VolumeNumberFromContent.Read(singlePath);

            Assert.True(single.HasZipEndRecord);
            Assert.False(single.ZipEndRecordMeansSplit, "中央目录就在自己身上 ⇒ ⛔ 不许当成末片");
            Assert.Equal(
                VolumeNumberFromContent.VolumeMembershipVerdict.ProvablyNotVolumeMember,
                VolumeNumberFromContent.ClassifyMembership(singlePath));
        }


        private static ushort TestCrc16(IReadOnlyList<byte> data) => (ushort)(TestCrc32(data) & 0xFFFF);

        private string Write(string path, byte[] bytes)
        {
            File.WriteAllBytes(path, bytes);

            return path;
        }
    }
}

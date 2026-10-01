using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ArchiveFixer.Detection;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **跨盘 ZIP 最后一片的识别 + 内层那一组的整组改名**（用户 2026-10-01 真机：
    /// `222.zscip.zip` 解出来的两个条目 `222.zi删除p` + `222.z0sc1` 本身是一份完整的两卷 split zip）。
    ///
    /// <para><b>两处病</b>：</para>
    /// <list type="number">
    /// <item><description><b>识别</b>：末片有中央目录 + EOCD、**没有本地文件头**，走到"内嵌归档"那条路上
    /// 被两道判据（`delta &gt; 0`、"起点必须是本地文件头"）判死 ⇒ 结论 `Unknown`
    /// ⇒ 续解扫描落「按内容认不出是归档」⇒ 这一组永远进不了任务表；</description></item>
    /// <item><description><b>命名</b>：整组改名的两个调用点都在"任务表"那一侧，递归产物从没进过任务表
    /// ⇒ `222.zi删除p` / `222.z0sc1` 原样落地 ⇒ 7-Zip / WinRAR / UnRAR 按标准卷名都找不到兄弟（实测同一句"打不开"）。</description></item>
    /// </list>
    ///
    /// <para>本文件钉四件事：① 真字节形状的末片必须认成 `ZIP_SPANNED`；② 普通单体 zip 与"不是 zip 的垃圾"
    /// 一律**不许**被误报；③ 定稿前那一组必须被改成 `X.zip` + `X.z01`；④ 判不出 / 目标名被占 ⇒ 一个字节都不改。</para>
    /// </summary>
    public sealed class SplitZipTailTests : IDisposable
    {
        private readonly string _root;

        public SplitZipTailTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerSplitTail-" + Guid.NewGuid().ToString("N"));
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

        // ================================================================ ① 末片识别

        /// <summary>
        /// **真字节形状**：`222.zi删除p` 的 EOCD 是
        /// <c>50 4B 05 06 | 01 00 01 00 | 06 00 06 00 | 5E 02 00 00 | 03 15 6C 0F | 00 00</c>
        /// —— 盘号字段说自己是分卷（本盘 1 / 中央目录起始盘 0 / 总盘 2）。
        /// 这种文件必须认成 `ZIP_SPANNED`，⛔ 不许落 `Unknown`（落 Unknown ⇒ 被扫描跳过 ⇒ 整组进不了任务表）。
        /// </summary>
        [Fact]
        public async Task 末片_盘号说自己是分卷_必须认成ZIP_SPANNED()
        {
            string path = WriteSplitZipTail(_root, "222.zi删除p", centralDirectoryBytes: 350, entries: 6);

            DetectResult result = await new ArchiveDetectService().DetectAsync(path);

            Assert.True(
                result.IsArchive,
                $"末片必须被认成归档。文件前 8 字节={string.Join(' ', File.ReadAllBytes(path).Take(8).Select(b => b.ToString("X2")))}"
                + $" 长度={new FileInfo(path).Length} 实际结论={result.Format}/{result.Message}"
                + $" 字节@0={string.Join(' ', File.ReadAllBytes(path).Skip(0).Take(4).Select(b => b.ToString("X2")))}"
                + $" 字节@4={string.Join(' ', File.ReadAllBytes(path).Skip(4).Take(8).Select(b => b.ToString("X2")))}");
            Assert.True(result.IsKnownFormat);
            Assert.Equal("ZIP_SPANNED", result.Format);

            // ⛔ 不是内嵌归档：偏移必须是 0（设了它下游会去抠一个不存在的区间）。
            Assert.Equal(0, result.EmbeddedArchiveOffset);
            Assert.False(result.EmbeddedDirectReadSupported);
        }

        /// <summary>
        /// **对照组（⛔ 不许误报）**：普通单体 zip（`盘 0 / 共 1 盘`）照旧走文件头那条路，
        /// 落 `ZIP` 而不是 `ZIP_SPANNED`；而"只有 EOCD、盘号不是分卷"的垃圾文件照旧 `Unknown`。
        /// </summary>
        [Fact]
        public async Task 对照组_普通zip与假EOCD垃圾_都不许被误报成分卷()
        {
            var service = new ArchiveDetectService();

            // 真普通 zip：文件头就是 PK\x03\x04（交给文件头那条路）。
            string plain = Path.Combine(_root, "plain.zip");
            File.WriteAllBytes(plain, BuildPlainZip());

            DetectResult plainResult = await service.DetectAsync(plain);

            Assert.True(plainResult.IsArchive);
            Assert.NotEqual("ZIP_SPANNED", plainResult.Format);

            // 假 EOCD：盘号字段说自己**不是**分卷（全 0）⇒ 这一档照旧判不出。
            string fake = WriteSplitZipTail(_root, "fake.bin", centralDirectoryBytes: 32, entries: 2, forceSingleDisk: true);

            DetectResult fakeResult = await service.DetectAsync(fake);

            Assert.False(
                fakeResult.IsArchive && fakeResult.Format == "ZIP_SPANNED",
                "盘号说自己是单盘的文件⛔ 不许被认成跨盘分片");
        }

        // ================================================================ ② 内层那一组整组改名

        /// <summary>
        /// **真机形状**：暂存目录里躺着 `222.zi删除p`（末片，名字被塞了中文）+ `222.z0sc1`（第 1 片）。
        /// 定稿规划必须把它们改成 **`222.zip` + `222.z01`**（只改名字、内容一个字节不动），
        /// 之后 7-Zip 才按标准卷名找得到兄弟。
        /// </summary>
        [Fact]
        public void 定稿前_内层那一组必须改成标准卷名()
        {
            string stage = NewDirectory("stage-split");
            string tail = WriteSplitZipTail(stage, "222.zi删除p", centralDirectoryBytes: 350, entries: 6);
            string first = WriteSplitZipFirstPart(stage, "222.z0sc1", 4096);

            byte[] before = File.ReadAllBytes(tail);

            ExtractionCoordinator.FinalLayoutPlan plan = ExtractionCoordinator.PlanFinalLayout(
                stage,
                Path.Combine(_root, "out-split"),
                sharedOutputRoot: false,
                "222.zscip");

            Assert.False(plan.Failed, plan.FailureReason);

            Assert.True(File.Exists(Path.Combine(stage, "222.zip")), "末片必须被改成 222.zip");
            Assert.True(File.Exists(Path.Combine(stage, "222.z01")), "第 1 片必须被改成 222.z01");
            Assert.False(File.Exists(tail), "旧名不该还在");
            Assert.False(File.Exists(first), "旧名不该还在");

            // ⛔ 只改名字：内容逐字节不变。
            Assert.Equal(before, File.ReadAllBytes(Path.Combine(stage, "222.zip")));
        }

        /// <summary>
        /// **对照组（⛔ 一个字节都不许改）**：目标名 `222.zip` 已经被**另一个文件**占着
        /// —— 那一组保持原样（改了就覆盖用户的数据）。
        /// </summary>
        [Fact]
        public void 对照组_目标名被占_这一组保持原样()
        {
            string stage = NewDirectory("stage-occupied");
            string tail = WriteSplitZipTail(stage, "222.zi删除p", centralDirectoryBytes: 350, entries: 6);
            WriteSplitZipFirstPart(stage, "222.z0sc1", 4096);

            // 占位者：另一个叫 `222.zip` 的文件（真机里它确实存在）。
            string occupant = Path.Combine(stage, "222.zip");
            File.WriteAllBytes(occupant, new byte[64]);

            ExtractionCoordinator.FinalLayoutPlan plan = ExtractionCoordinator.PlanFinalLayout(
                stage,
                Path.Combine(_root, "out-occupied"),
                sharedOutputRoot: false,
                "222.zscip");

            Assert.False(plan.Failed, plan.FailureReason);

            // 占位者一个字节都没动；原名那两份也照旧在（这一组没有被"改一半"）。
            Assert.Equal(64, new FileInfo(occupant).Length);
            Assert.True(File.Exists(tail), "目标名被占时⛔ 不许动原来的名字");
            Assert.False(File.Exists(Path.Combine(stage, "222.z01")), "这一组要么全改、要么全不改");
        }

        /// <summary>
        /// **对照组（判不出 ⇒ 不动）**：只有一片、没有任何兄弟 ⇒ 判定器给"判不出"，
        /// 名字一个字都不许改（兜底落在"什么都不做"那一档）。
        /// </summary>
        [Fact]
        public void 对照组_只有一片_判不出时一个字都不改()
        {
            string stage = NewDirectory("stage-alone");
            string tail = WriteSplitZipTail(stage, "222.zi删除p", centralDirectoryBytes: 350, entries: 6);

            ExtractionCoordinator.FinalLayoutPlan plan = ExtractionCoordinator.PlanFinalLayout(
                stage,
                Path.Combine(_root, "out-alone"),
                sharedOutputRoot: false,
                "222.zscip");

            Assert.True(File.Exists(tail), "判不出时⛔ 连名字都不许动");
            Assert.False(File.Exists(Path.Combine(stage, "222.zip")));
            Assert.NotNull(plan);
        }

        // ================================================================ 样本构造（按真机字节形状）

        /// <summary>
        /// 造一片"跨盘 zip 的末片"：PK 开头（不是本地头）+ 末尾写真的 EOCD。
        /// EOCD 的盘号字段按真机字节写：本盘 1 / 中央目录起始盘 0 / 总盘 2。
        /// </summary>
        private static string WriteSplitZipTail(
            string directory,
            string fileName,
            int centralDirectoryBytes,
            int entries,
            bool forceSingleDisk = false)
        {
            string path = Path.Combine(directory, fileName);

            var bytes = new List<byte>();

            // 中央目录起点：写真的中央目录签名（判定器要验它）。
            bytes.AddRange(new byte[] { 0x50, 0x4B, 0x01, 0x02 });
            bytes.AddRange(new byte[Math.Max(0, centralDirectoryBytes - 4)]);

            long cdSize = centralDirectoryBytes;
            long cdOffset = 0; // 签名就在文件开头（真机里是中央目录自己的相对偏移，判定器按这个位置验签名）

            var eocd = new List<byte> { 0x50, 0x4B, 0x05, 0x06 };

            void U16(int value) => eocd.AddRange(new[] { (byte)(value & 0xFF), (byte)((value >> 8) & 0xFF) });
            void U32(long value) => eocd.AddRange(new[]
            {
                (byte)(value & 0xFF),
                (byte)((value >> 8) & 0xFF),
                (byte)((value >> 16) & 0xFF),
                (byte)((value >> 24) & 0xFF)
            });

            U16(forceSingleDisk ? 0 : 1);  // 本盘号
            U16(forceSingleDisk ? 0 : 1);  // 中央目录起始盘号（真机字节：这两个字段都是 01 00）
            U16(entries);                  // 本盘条目数
            U16(entries);                  // 条目总数
            U32(cdSize);                   // 中央目录大小
            U32(cdOffset);                 // 中央目录偏移
            U16(0);                        // 注释长度

            bytes.AddRange(eocd);
            File.WriteAllBytes(path, bytes.ToArray());

            return path;
        }

        /// <summary>造一片"跨盘 zip 的第 1 片"：跨盘开头 <c>PK\x07\x08</c> + 本地文件头。</summary>
        private static string WriteSplitZipFirstPart(string directory, string fileName, int size)
        {
            string path = Path.Combine(directory, fileName);

            var bytes = new byte[size];
            bytes[0] = 0x50;
            bytes[1] = 0x4B;
            bytes[2] = 0x07;
            bytes[3] = 0x08;
            bytes[4] = 0x50;
            bytes[5] = 0x4B;
            bytes[6] = 0x03;
            bytes[7] = 0x04;

            File.WriteAllBytes(path, bytes);

            return path;
        }

        /// <summary>一个最小的真普通 zip（本地头 + 空中央目录 + EOCD，盘号全是 0）。</summary>
        private static byte[] BuildPlainZip()
        {
            var bytes = new List<byte> { 0x50, 0x4B, 0x03, 0x04 };
            bytes.AddRange(new byte[26]);
            bytes.AddRange(new byte[] { 0x50, 0x4B, 0x01, 0x02 });
            bytes.AddRange(new byte[42]);
            bytes.AddRange(new byte[] { 0x50, 0x4B, 0x05, 0x06 });
            bytes.AddRange(new byte[18]);

            return bytes.ToArray();
        }

        private string NewDirectory(string name)
        {
            string path = Path.Combine(_root, name);
            Directory.CreateDirectory(path);

            return path;
        }
    }
}

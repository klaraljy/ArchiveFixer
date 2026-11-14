using System.Collections.Generic;
using System.Linq;

using ArchiveFixer.Detection;
using ArchiveFixer.ViewModels;

using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **"缺什么补什么"的打分与分配**（用户 2026-10-14 凌晨口径 ①③）。
    ///
    /// <para>用户原话（逐字）：「根据**剩下的缺什么，就补什么**去设计尝试一下……`.7z.005729572` 这种，
    /// 就要分析同伴缺什么，缺 `003` 那就很大概率是 `003`（只是可能不是一定）；两个都看不懂就看**大小**、
    /// 看**后缀有没有相似的地方**」＋「如果只剩下 `003` 和 `006` 没有找出来，就直接试**概率修改**：
    /// 第一次前面那个当 `003`、后面那个当 `006`，不成功就**换一边**」。</para>
    ///
    /// <para>这一档是**纯算**（⛔ 不碰盘），所以用表驱动钉住三件事：① 分数最高那一版把
    /// "后缀更像某个槽位"的候选放进那个槽位；② 排在第二的是它的**换边**（供引擎试开失败后用）；
    /// ③ **数目不等的直接返回空**（判不出 ⇒ 什么都不做）。</para>
    /// </summary>
    public class VolumeSlotScoringTests
    {
        private static VolumeSlotScoring.Candidate Make(string name, long size, int? number = null)
            => new VolumeSlotScoring.Candidate
            {
                Path = "/tmp/" + name,
                FileName = name,
                Size = size,
                ContentNumber = number
            };

        [Fact]
        public void 缺003与006_两个看不懂的候选_先按顺序失败再换边()
        {
            // 用户举的那个例子：同伴缺 003、006；手上两份名字都被打乱成看不懂的样子
            var missing = new List<int> { 3, 6 };
            var candidates = new List<VolumeSlotScoring.Candidate>
            {
                Make("pack111.7z.005729572", size: 25 * 1024 * 1024),
                Make("pack111.7z.007549875", size: 10 * 1024 * 1024)
            };

            IReadOnlyList<VolumeSlotScoring.Assignment> ranked = VolumeSlotScoring.Rank(
                missing,
                candidates,
                groupBaseName: "pack111",
                fullVolumeSize: 25 * 1024 * 1024,
                lastSlot: 6,
                canonicalNameForSlot: slot => $"pack111.7z.00{slot}");

            Assert.Equal(2, ranked.Count);

            /*
             * ⚠ 用户口径（逐字）：「这个两个完全看不懂看大小、看后缀有没有什么相似的地方，
             * 如果只剩下 003 和 006 没有找出来……就直接试**概率修改**：第一次前面的为 003 后面 006，
             * 不成功就**换一边**」—— 也就是说：**证据拉不开差距时，规律就是"先按顺序试、失败换边"**。
             *
             * 实测（本类的 `相似度` 那一条钉住的事实）：`.005729572` 对 `.003` 与对 `.006` 的相似度
             * **并列**（LCS 都是 `pack111.7z.00`）⇒ 这一档本来就分不出来，⛔ 不许假装分得出来。
             * 真正把它分开的是**体积那一票**：25 MiB 只落在"非末卷槽位 3"（末槽位 6 允许小于满体积）。
             */
            (VolumeSlotScoring.Candidate first, int firstSlot) = ranked[0].Pairs[0];
            (VolumeSlotScoring.Candidate second, int secondSlot) = ranked[0].Pairs[1];

            Assert.Equal("pack111.7z.005729572", first.FileName);
            Assert.Equal(3, firstSlot);
            Assert.Equal("pack111.7z.007549875", second.FileName);
            Assert.Equal(6, secondSlot);

            // 第二名必须是它的换边（引擎试开失败后照着换）：
            // 同一个候选、换到另一个槽位（`.005729572` 去 6、`.007549875` 去 3）
            Assert.Equal("pack111.7z.005729572", ranked[1].Pairs[0].Candidate.FileName);
            Assert.Equal(6, ranked[1].Pairs[0].Slot);
            Assert.Equal("pack111.7z.007549875", ranked[1].Pairs[1].Candidate.FileName);
            Assert.Equal(3, ranked[1].Pairs[1].Slot);

            // 判据输入要能写进日志（用户口径②：顺序与次数可调，就得看得见为什么）
            Assert.Equal(2, ranked[0].Evidence.Count);
            Assert.Contains("后缀相似", ranked[0].Evidence[0]);
            Assert.Contains("体积", ranked[0].Evidence[0]);
        }

        [Fact]
        public void 相似度_骨架相同但夹了垃圾_两份候选并列_所以只能靠顺序与体积()
        {
            /*
             * 这一条钉的是**限制**（如实记）：`.005729572` 对 `.003` 与对 `.006` 的相似度**并列**
             * —— 因为公共子序列都只到 `pack111.7z.00`。这正是用户口径里"两个完全看不懂"那一档，
             * 处置就是"先按顺序试、失败换边"，⛔ 不是假装后缀能把它分开。
             */
            double toThree = VolumeSlotScoring.Similarity("pack111.7z.005729572", "pack111.7z.003");
            double toSix = VolumeSlotScoring.Similarity("pack111.7z.005729572", "pack111.7z.006");

            Assert.Equal(toThree, toSix, 3);

            // 而**真的**相似（卷标记骨架逐字相同、只是尾巴多了一截）必须被认出来
            double exact = VolumeSlotScoring.Similarity("pack111.7z.003垃圾", "pack111.7z.003");

            Assert.True(exact > toThree, $"骨架逐字相同的那一份要更像：{exact:0.000} 应大于 {toThree:0.000}");
        }

        [Theory]
        [InlineData(1, 0)]  // 缺 1 个槽位、给 0 个候选 ⇒ 判不出
        [InlineData(0, 1)]  // 不缺槽位、给 1 个候选
        [InlineData(2, 1)]  // 缺 2 个、只给 1 个
        [InlineData(1, 2)]  // 缺 1 个、给 2 个
        public void 数目不等的直接返回空_判不出就什么都不做(int missingCount, int candidateCount)
        {
            var missing = Enumerable.Range(2, missingCount).ToList();
            var candidates = Enumerable.Range(0, candidateCount)
                .Select(index => Make($"x.7z.00{index + 9}", 1024))
                .ToList();

            IReadOnlyList<VolumeSlotScoring.Assignment> ranked = VolumeSlotScoring.Rank(
                missing,
                candidates,
                groupBaseName: "x",
                fullVolumeSize: 1024,
                lastSlot: 3,
                canonicalNameForSlot: slot => $"x.7z.00{slot}");

            Assert.Empty(ranked);
        }

        [Fact]
        public void 满体积那一票_把等大的候选优先给非末卷槽位()
        {
            // 两个候选名字都读不出卷号、名字相似度也拉不开差距（同前缀同长度）⇒ 只能靠体积那一票
            var missing = new List<int> { 2, 3 };
            var candidates = new List<VolumeSlotScoring.Candidate>
            {
                Make("y.7z.aaaaaaa", size: 4096),
                Make("y.7z.bbbbbbb", size: 1024)
            };

            IReadOnlyList<VolumeSlotScoring.Assignment> ranked = VolumeSlotScoring.Rank(
                missing,
                candidates,
                groupBaseName: "y",
                fullVolumeSize: 4096,
                lastSlot: 3,
                canonicalNameForSlot: slot => $"y.7z.0000{slot}");

            // 末槽位 3 允许"小于满体积"（除末卷外等大那条规律），非末槽位 2 要求正好等于满体积
            // ⇒ 4096 那一份必须落到槽位 2；反之若它落到 3，就该判 0 分。
            (VolumeSlotScoring.Candidate candidate, int slot) = ranked[0].Pairs
                .Single(pair => pair.Candidate.Size == 4096);

            Assert.Equal(2, slot);
        }

        [Theory]
        // ⚠ 2026-10-14（实测 `3333` 组合 1 定案）：缺片的槽位名**照"已经在层里的那几片"的写法**，
        // 而不是走那一族没有的通用 default（原来出来 `111-L2.003`，可层里叫 `111-L2.zip.001/.002`
        // ⇒ 引擎按名字找不到 `.zip.003` ⇒ 整组凑不齐、①红）。
        [InlineData("111-L2.zip.002", 3, "111-L2.zip.003")] // ZIP 通用分片：`.zip` 是基名的一部分
        [InlineData("111-L2.7z.001", 2, "111-L2.7z.002")]   // 7z 分卷
        [InlineData("111-L2.001", 2, "111-L2.002")]         // 已经是纯数字分片
        [InlineData("x.part2.rar", 3, "x.part3.rar")]       // RAR 新式
        [InlineData("x.z01", 3, "x.z03")]                   // 跨盘 ZIP 族（编号在尾巴上，宽度照旧）
        public void 槽位名_照既有片的写法换卷号(string existingName, int slot, string expected)
        {
            var path = "/tmp/" + existingName;

            Assert.True(
                ExtractionCoordinator.TryNameSlotLikeExistingPiece(path, slot, out string actual),
                $"`{existingName}` 应当拼得出名字");

            Assert.Equal(expected, actual);
        }
    }
}

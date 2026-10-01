using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **跨盘 zip 的"按归档自己的索引定盘"**（用户 2026-10-01 的专属算法；真机样本 <c>FFF\111</c>）。
    ///
    /// <para>要证的三件事：</para>
    /// <list type="number">
    /// <item><description><b>能定</b>：末片的中央目录写着"每个文件的本地头在第几盘、离那一盘开头多少字节" ⇒
    /// 名字里一个卷号都没有也能把每一片认出来（<see cref="SpannedZipIndex"/>）。</description></item>
    /// <item><description><b>定不了就说定不了</b>：某一片里"一个文件都没开始"时，它和另一片同样空白的片
    /// 在字节上完全对称 ⇒ 如实报 <see cref="SpannedZipPin.UndecidedDisks"/>，⛔ 不靠名字/时间猜。</description></item>
    /// <item><description><b>顺序要引擎说了算</b>：剩下那几片的排列只有"按这个顺序真解一遍"才算数
    /// （跨盘 zip 的中央目录在末片里、不看中间片 ⇒ ⛔ 列目录验不出顺序）；加密的包还得有对的密码。</description></item>
    /// </list>
    ///
    /// <para>合成样本一律用**本机 WinRAR**（<c>-afzip -v</c>）现造真跨盘 zip —— 顺序的真值来自 WinRAR 自己写下的
    /// 盘号，⛔ 不来自我们的读数（否则就是拿结论当判据）。本机没装 WinRAR 的用例自己跳过、⛔ 不伪装成验过。</para>
    /// </summary>
    public class SpannedZipIndexTests : IDisposable
    {
        private readonly string _root;
        private readonly string? _winRar;

        public SpannedZipIndexTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerSpannedZipIndex", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _winRar = LocateWinRar();
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
                // 临时目录清不掉不影响结论。
            }
        }

        /// <summary>
        /// **末片不在了**（用户 2026-10-01 要的那句诊断）：手上的几片彼此等大、其中一片开头就是跨盘标记
        /// ⇒ 结论只能是"这是一组跨盘 zip，缺的是**末片**"，而不是含糊的「分卷缺失」。
        /// ⛔ 一个字节都不动。
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task 末片不在了_要判出缺的是末片_而不是含糊的缺卷()
        {
            if (_winRar == null)
            {
                return;
            }

            string directory = Path.Combine(_root, "tail-missing");
            List<string> parts = MakeSpannedZip(directory, "set.zip", entryCount: 20, entryBytes: 20 * 1024, volume: "64k");

            Assert.True(parts.Count >= 4, "至少要 4 片");

            List<string> disguised = DisguiseNames(directory, parts, "set");
            string tail = FindSpannedTail(disguised);

            // 把末片挪走（模拟"末片丢了"）。
            string movedAway = tail + ".moved";
            File.Move(tail, movedAway);

            var remaining = CandidatesIn(directory)
                .Where(c => !string.Equals(c.Path, movedAway, StringComparison.OrdinalIgnoreCase))
                .ToList();

            VolumeNameRepairPlan plan = await VolumeNameRepair.PlanByContentAsync(
                remaining[0].Path,
                remaining,
                new Engines.SevenZip.SevenZipEngine(),
                workRootDirectory: WorkRootFor(remaining[0].Path));

            Assert.False(plan.CanRepair, "末片不在还敢改名？");
            Assert.True(plan.SpannedTailMissing, "必须判出'缺的是末片'这一档");
            Assert.Contains("末片", plan.Reason);
            Assert.Empty(plan.Items);

            // 一个字节都没动：盘上还是原来那几份 + 挪走的那一份。
            Assert.Equal(parts.Count, Directory.GetFiles(directory, "set.*").Length);
        }

        /// <summary>
        /// **入口这一道门**（⛔ 少了它，上面那套再准也轮不到跑）：真机 `FFF\111` 那一组，
        /// 扫描期按名字归组得到 **0 组**（实测），7 份文件里 6 份的名字连"卷标记"都算不上
        /// ⇒ 老判据（名字带卷标记 / 账上归过组）两条都不成立 ⇒ 内容路压根不会被问到。
        /// 现在多一条与名字无关的事实：**目录里真有一片自述是跨盘 zip 末片**。
        /// </summary>
        [Fact]
        public void 真机FFF_扫描期归不出组_入口必须靠目录里有末片这条事实放行()
        {
            const string directory = @"H:\BaiduNetdiskDownload\测试\FFF\111";

            if (!Directory.Exists(directory))
            {
                return;
            }

            List<VolumeCandidate> candidates = CandidatesIn(directory);

            // 事实一：扫描期按名字归组**一组都归不出来**（这就是老口径下内容路跑不到的原因）。
            Assert.Empty(VolumeGroupDetector.Group(candidates));

            // 事实二：名字里带卷标记的只有末片那一个，其余六份老判据一律为 false。
            string[] currentNames = { "222", "222.删除C", "222.z0删除A", "222.z删除B", "222.z删除E", "222.zDDD" };

            foreach (string name in currentNames)
            {
                Assert.False(
                    FileNameHelper.IsVolumePartFileName(name),
                    $"「{name}」不该被当成标准卷名（这正是老入口失效的原因）");
            }

            // 事实三：新的入口判据为**真**，而且对组里任何一份都成立（谁先开工都能问到内容路）。
            foreach (string file in Directory.GetFiles(directory))
            {
                Assert.True(
                    VolumeNameRepair.HasSpannedZipTailNearby(file),
                    $"「{Path.GetFileName(file)}」这一份旁边躺着跨盘 zip 末片，入口必须放行");
            }
        }

        /// <summary>
        /// 真机样本（**只读**）：`H:\…\测试\FFF\111` 那一组 7 片，名字里一个卷号都没有。
        /// 索引能钉住 5 片（第 1 / 3 / 5 / 6 / 7 片），剩下第 2、第 4 片内容里一个文件都没开始 ⇒ 如实报定不下来。
        /// </summary>
        [Fact]
        public void 真机FFF一组七片_索引钉住五片_另两片如实报定不下来()
        {
            const string directory = @"H:\BaiduNetdiskDownload\测试\FFF\111";

            if (!Directory.Exists(directory))
            {
                return;   // 真样本不在就跳过（⛔ 不伪装成验过）
            }

            List<VolumeCandidate> candidates = CandidatesIn(directory);
            string tail = Path.Combine(directory, "222.zi删除p");

            SpannedZipIndex? index = SpannedZipIndex.TryRead(tail);

            Assert.NotNull(index);
            Assert.Equal(7, index!.DiskCount);

            SpannedZipPin pin = index.Pin(candidates);

            Assert.True(pin.Consistent, "候选池里剩下的份数必须恰好等于定不下来的盘数");
            Assert.False(pin.AllPinned, "这一组有两片内容里一个文件都没开始 —— ⛔ 不许假装全钉住了");

            Assert.Equal(Path.Combine(directory, "222.z0删除A"), pin.Slots[0]);
            Assert.Equal(Path.Combine(directory, "222.删除C"), pin.Slots[2]);
            Assert.Equal(Path.Combine(directory, "222.z删除E"), pin.Slots[4]);
            Assert.Equal(Path.Combine(directory, "222"), pin.Slots[5]);
            Assert.Equal(tail, pin.Slots[6]);

            Assert.Equal(new[] { 1, 3 }, pin.UndecidedDisks);
            Assert.Empty(pin.MissingDisks);
            Assert.Equal(2, pin.UndecidedCandidates.Count);
        }

        /// <summary>
        /// 真机样本（**只读**）：没有工作区根 ⇒ 一次都不许试拼（工作区只准设在解压的地方），
        /// 如实报"无法确认"、一个字节都不动。⛔ 不许把"没试"写成"顺序不对"。
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task 真机FFF_没有工作区根时_不许试拼_一个字节都不动()
        {
            const string directory = @"H:\BaiduNetdiskDownload\测试\FFF\111";

            if (!Directory.Exists(directory))
            {
                return;
            }

            Dictionary<string, DateTime> before = Directory
                .GetFiles(directory)
                .ToDictionary(p => p, File.GetLastWriteTimeUtc, StringComparer.OrdinalIgnoreCase);

            VolumeNameRepairPlan plan = await VolumeNameRepair
                .PlanByContentAsync(
                    Path.Combine(directory, "222.z删除B"),
                    CandidatesIn(directory),
                    new Engines.SevenZip.SevenZipEngine(),
                    workRootDirectory: null);

            Assert.False(plan.CanRepair, "证不出顺序就不许改名");
            Assert.False(plan.TrialAttempted, "没有工作区根 ⇒ 一次都不许试拼");
            Assert.Empty(plan.Items);

            foreach (KeyValuePair<string, DateTime> entry in before)
            {
                Assert.True(File.Exists(entry.Key), $"源文件被动过了：{Path.GetFileName(entry.Key)}");
                Assert.Equal(entry.Value, File.GetLastWriteTimeUtc(entry.Key));
            }
        }

        /// <summary>
        /// 合成真跨盘 zip（WinRAR 现造，名字标准）：把每一片都改成"一个卷号都没有"的名字并打乱之后，
        /// 索引仍然能把它们**逐片**排成真值顺序（真值 = WinRAR 自己的 <c>.z01…/.zip</c>）。
        /// </summary>
        [Fact]
        public void 合成跨盘zip_名字全改成没有卷号_索引照样逐片排出正确顺序()
        {
            if (_winRar == null)
            {
                return;
            }

            string directory = Path.Combine(_root, "many-entries");
            List<string> parts = MakeSpannedZip(directory, "set.zip", entryCount: 20, entryBytes: 20 * 1024, volume: "64k");

            Assert.True(parts.Count >= 4, $"这一组至少要 4 片才说明得了问题，实际 {parts.Count} 片");

            List<string> disguised = DisguiseNames(directory, parts, "set");

            var candidates = disguised
                .Select(p => new VolumeCandidate { Path = p, Size = new FileInfo(p).Length })
                .ToList();

            SpannedZipIndex? index = SpannedZipIndex.TryRead(FindSpannedTail(disguised));

            Assert.NotNull(index);

            SpannedZipPin pin = index!.Pin(candidates);

            Assert.True(pin.AllPinned, "每一片里都有条目起点 ⇒ 应当全部钉住（不是'定不下来'）");
            Assert.Empty(pin.MissingDisks);

            // 身份 = 这一片在 WinRAR 真值顺序里的位次；排出来的必须正好是 0、1、2……
            var identity = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < parts.Count; i++)
            {
                identity[disguised[i]] = i;
            }

            for (int disk = 0; disk < pin.Slots.Count; disk++)
            {
                Assert.Equal(disk, identity[pin.Slots[disk]!]);
            }
        }

        /// <summary>
        /// 少了一片（内容里没有条目起点的那种）：⛔ 不许硬凑 —— 索引路如实让位（<c>null</c> 计划），
        /// 由统一算法去说"分不清 / 缺卷"。
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task 合成跨盘zip_少一片_索引路如实让位不硬凑()
        {
            if (_winRar == null)
            {
                return;
            }

            string directory = Path.Combine(_root, "missing-part");
            List<string> disguised = MakeTwoUndecidedSpannedZip(directory, out _);

            // 删掉一片"内容里没有条目起点"的（按索引算出来的那些），模拟真机上少了一片。
            string tail = FindSpannedTail(disguised);
            SpannedZipPin before = SpannedZipIndex.TryRead(tail)!.Pin(CandidatesIn(directory));

            Assert.True(
                before.Consistent && before.UndecidedDisks.Count > 0,
                "这一组本来就该有'定不下来'的片，样本形状不对");

            string removed = before.UndecidedCandidates[0];
            File.Delete(removed);

            var remaining = Directory
                .GetFiles(directory, "set.*")
                .Select(p => new VolumeCandidate { Path = p, Size = new FileInfo(p).Length })
                .ToList();

            SpannedZipPin after = SpannedZipIndex.TryRead(tail)!.Pin(remaining);

            Assert.False(after.AllPinned, "少了一片却判成'全钉住了' —— 那就是拿用户的包去赌");
            Assert.False(after.Consistent);

            // 产品这一次调用必须**什么都不改**（索引路让位、统一算法也认不出来）。
            VolumeNameRepairPlan plan = await VolumeNameRepair
                .PlanByContentAsync(
                    tail,
                    remaining,
                    new Engines.SevenZip.SevenZipEngine(),
                    workRootDirectory: WorkRootFor(tail));

            Assert.False(plan.CanRepair, "少了一片还敢改名？");
            Assert.Empty(plan.Items);
        }

        /// <summary>
        /// **专属算法的收尾**：索引钉住大部分片、只剩两片定不下来 ⇒ 引擎**试拼**按正确的顺序通过；
        /// 而且最终改出来的名字就是标准跨盘卷名（第 1 片 <c>.z01</c> …… 末片 <c>.zip</c>）。
        /// </summary>
        [SevenZipFact]
        public async System.Threading.Tasks.Task 剩下两片定不下来时_引擎试拼能把顺序定下来并改成标准名()
        {
            if (_winRar == null)
            {
                return;
            }

            string directory = Path.Combine(_root, "two-undecided");
            List<string> disguised = MakeTwoUndecidedSpannedZip(directory, out _);
            string tail = FindSpannedTail(disguised);

            SpannedZipIndex index = SpannedZipIndex.TryRead(tail)!;
            SpannedZipPin pin = index.Pin(CandidatesIn(directory));

            Assert.False(pin.AllPinned, "这一组必须**恰好**留下定不下来的片，否则这条用例证明不了试拼");
            Assert.True(pin.Consistent, "定不下来的片数必须与剩下的候选数相等");
            Assert.True(pin.UndecidedDisks.Count <= 3, $"定不下来的片太多了（{pin.UndecidedDisks.Count} 片），试拼会变成暴力搜索");

            VolumeNameRepairPlan plan = await VolumeNameRepair.PlanByContentAsync(
                disguised.First(p => !string.Equals(p, tail, StringComparison.OrdinalIgnoreCase)),
                CandidatesIn(directory),
                new Engines.SevenZip.SevenZipEngine(),
                workRootDirectory: WorkRootFor(tail));

            Assert.True(plan.CanRepair, plan.Reason);
            Assert.True(plan.TrialAttempted, "这一档必须真的试拼过（不是靠猜）");

            // 改出来的名字必须是标准跨盘卷名，而且**顺序与真值一致**。
            Assert.Equal(disguised.Count, plan.Items.Count);

            var identity = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < disguised.Count; i++)
            {
                identity[disguised[i]] = i;
            }

            for (int i = 0; i < plan.Items.Count; i++)
            {
                Assert.Equal(identity[plan.Items[i].CurrentPath], i);
                Assert.Equal(
                    i == plan.Items.Count - 1 ? "set.zip" : "set.z" + (i + 1).ToString("D2"),
                    plan.Items[i].SuggestedFileName);
            }

            // 真的改名：改完引擎能打开这一组（列得出里面的条目）。
            VolumeNameRepairResult applied = VolumeNameRepair.TryApply(plan);
            Assert.True(applied.Success, applied.Message);
            Assert.True(CanList(directory, Path.Combine(directory, "set.zip")), "改完名字之后引擎还是打不开这一组");
        }

        /// <summary>
        /// **加密的跨盘 zip**（AES）：密码不对 ⇒ 如实说"顺序没法验证"（⛔ 不许说成"顺序不对"）；
        /// 密码给对了 ⇒ 试拼就能把顺序定下来。密码只活在内存里，⛔ 不进结论、不进日志。
        /// </summary>
        [SevenZipFact]
        public async System.Threading.Tasks.Task 加密的跨盘zip_密码不对不许说成顺序不对_密码对了才定得下来()
        {
            if (_winRar == null)
            {
                return;
            }

            string directory = Path.Combine(_root, "encrypted");
            List<string> disguised = MakeTwoUndecidedSpannedZip(directory, out _, password: "TestPass123");

            string tail = FindSpannedTail(disguised);
            string driver = disguised.First(p => !string.Equals(p, tail, StringComparison.OrdinalIgnoreCase));

            string workRoot = WorkRootFor(tail);

            VolumeNameRepairPlan withoutPassword = await VolumeNameRepair.PlanByContentAsync(
                driver,
                CandidatesIn(directory),
                new Engines.SevenZip.SevenZipEngine(),
                workRoot,
                new[] { "definitely-not-the-password" });

            Assert.False(withoutPassword.CanRepair);
            Assert.True(withoutPassword.TrialAttempted, "密码不对也要如实说'试过了'");
            Assert.Contains("密码", withoutPassword.Reason);

            VolumeNameRepairPlan withPassword = await VolumeNameRepair.PlanByContentAsync(
                driver,
                CandidatesIn(directory),
                new Engines.SevenZip.SevenZipEngine(),
                workRoot,
                new[] { "definitely-not-the-password", "TestPass123" });

            Assert.True(withPassword.CanRepair, withPassword.Reason);
            Assert.True(withPassword.TrialAttempted);
        }

        /// <summary>
        /// ⛔ 名字改得再像也不算数：把**两片交换**（顺序真错了）时，索引排出来的顺序必须仍然由锚点说话 ——
        /// 也就是说"名字乱序"对结论没有任何影响（正面用例已经钉了顺序，这条钉的是"不看名字"）。
        /// </summary>
        [Fact]
        public void 名字里塞了像卷号的东西_也不许改变结论()
        {
            if (_winRar == null)
            {
                return;
            }

            string directory = Path.Combine(_root, "fake-numbers");
            List<string> parts = MakeSpannedZip(directory, "set.zip", entryCount: 20, entryBytes: 20 * 1024, volume: "64k");

            // 故意把"像卷号"的名字反着给：第 1 片叫 set.z99、第 2 片叫 set.z98……
            var renamed = new List<string>();

            for (int i = 0; i < parts.Count; i++)
            {
                string target = Path.Combine(directory, "set.z" + (99 - i).ToString("D2"));
                File.Move(parts[i], target);
                renamed.Add(target);
            }

            SpannedZipPin pin = SpannedZipIndex
                .TryRead(FindSpannedTail(renamed))!
                .Pin(CandidatesIn(directory));

            Assert.True(pin.AllPinned, "锚点齐的时候必须全部钉住");

            var identity = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < renamed.Count; i++)
            {
                identity[renamed[i]] = i;
            }

            for (int disk = 0; disk < pin.Slots.Count; disk++)
            {
                Assert.Equal(disk, identity[pin.Slots[disk]!]);
            }
        }

        /// <summary>
        /// **跨目录找同组的卷**（用户 2026-10-01 点名两次，并明确"绝大多数只会在一个父文件夹和父文件夹的
        /// 同级子文件夹当中"）：一组卷被拆到"父文件夹的两层里"时，候选池要能看到，而且**收进入口那一层**
        /// —— 引擎找兄弟卷只看入口文件旁边那一层，散着放即使名字都对也解不开（改完必须真能打开）。
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task 一组卷被拆到父目录和兄弟目录里_要收进入口那一层_而且改完真能打开()
        {
            if (_winRar == null)
            {
                return;
            }

            // 形状：`包\A`（归档在自己这一层）+ `包\B`（兄弟目录，另一半在这儿）。
            string package = Path.Combine(_root, "cross-dir", "包");
            string own = Path.Combine(package, "A");
            string sibling = Path.Combine(package, "B");

            Directory.CreateDirectory(own);
            Directory.CreateDirectory(sibling);

            List<string> parts = MakeSpannedZip(own, "set.zip", entryCount: 20, entryBytes: 20 * 1024, volume: "64k");

            Assert.True(parts.Count >= 4, "至少要 4 片");

            List<string> disguised = DisguiseNames(own, parts, "set");
            string tailBefore = FindSpannedTail(disguised);
            string entryDirectory = Path.GetDirectoryName(tailBefore)!;

            // 把一半挪到兄弟目录（末片不动，所以入口那一层就是 `A`）—— 名字已经改好（一个卷号都没有）。
            var movedOut = new List<string>();

            for (int i = 0; i < disguised.Count; i++)
            {
                if (i % 2 == 0 && !string.Equals(disguised[i], tailBefore, StringComparison.OrdinalIgnoreCase))
                {
                    string target = Path.Combine(sibling, Path.GetFileName(disguised[i]));
                    File.Move(disguised[i], target);
                    disguised[i] = target;
                    movedOut.Add(target);
                }
            }

            Assert.NotEmpty(movedOut);

            string driver = disguised.First(p => !string.Equals(p, tailBefore, StringComparison.OrdinalIgnoreCase));

            VolumeNameRepairPlan plan = await VolumeNameRepair.PlanByContentAsync(
                driver,
                VolumeNameRepair.EnumerateVolumeCandidatesNearby(driver),
                new Engines.SevenZip.SevenZipEngine(),
                workRootDirectory: WorkRootFor(driver));

            Assert.True(plan.CanRepair, plan.Reason);
            Assert.Equal(disguised.Count, plan.Items.Count);
            Assert.True(plan.GatheredVolumes > 0, "散在兄弟目录里的卷必须被收过来（否则改名也解不开）");

            foreach (VolumeRepairItem item in plan.Items)
            {
                Assert.True(
                    string.Equals(Path.GetDirectoryName(item.TargetPath), entryDirectory, StringComparison.OrdinalIgnoreCase),
                    $"「{item.CurrentFileName}」必须落到入口那一层（{entryDirectory}）");
            }

            VolumeNameRepairResult applied = VolumeNameRepair.TryApply(plan);
            Assert.True(applied.Success, applied.Message);

            // 兄弟目录里那几卷应当已经**不在原地**了（整组收进入口那一层）。
            foreach (string moved in movedOut)
            {
                Assert.False(File.Exists(moved), $"「{Path.GetFileName(moved)}」应当已经收进入口那一层");
            }

            string newTail = Path.Combine(entryDirectory, "set.zip");

            Assert.True(File.Exists(newTail));
            Assert.True(CanList(entryDirectory, newTail), "收齐 + 改名之后引擎必须能列出这一组");
        }

        /// <summary>
        /// 候选池的**边界**（用户 2026-10-01 定的）：看**自己这一层 + 自己的直接子目录 + 父目录这一家**
        /// （父目录 + 父目录的各子目录）；⛔ 祖父及以上不看、⛔ 孙目录（自己子目录的子目录）不看、
        /// ⛔ 与这一家无关的目录不看。
        /// </summary>
        [Fact]
        public void 候选池_看自己这一层和自己的子目录和父目录这一家_但不看祖父和孙目录()
        {
            string grandParent = Path.Combine(_root, "nearby-scope");
            string parent = Path.Combine(grandParent, "父目录");
            string own = Path.Combine(parent, "自己这一层");
            string sibling = Path.Combine(parent, "兄弟目录");
            string ownChild = Path.Combine(own, "自己的子目录");
            string grandChild = Path.Combine(ownChild, "孙目录");
            string unrelated = Path.Combine(grandParent, "无关目录");

            foreach (string directory in new[] { parent, own, sibling, ownChild, grandChild, unrelated })
            {
                Directory.CreateDirectory(directory);
            }

            // ⚠ 父目录这一家有 16 KiB 粗筛：样本都造得比它大，免得"太小被筛掉"与"范围不对"混在一起。
            var payload = new byte[32 * 1024];

            File.WriteAllBytes(Path.Combine(own, "own.bin"), payload);                  // 自己这一层 ✓
            File.WriteAllBytes(Path.Combine(ownChild, "ownchild.bin"), payload);        // 自己的直接子目录 ✓
            File.WriteAllBytes(Path.Combine(parent, "parent.bin"), payload);            // 父目录 ✓
            File.WriteAllBytes(Path.Combine(sibling, "sibling.bin"), payload);          // 父目录的子目录（兄弟）✓
            File.WriteAllBytes(Path.Combine(grandChild, "grandchild.bin"), payload);    // 孙目录 ✗
            File.WriteAllBytes(Path.Combine(grandParent, "grandparent.bin"), payload);  // 祖父 ✗
            File.WriteAllBytes(Path.Combine(unrelated, "unrelated.bin"), payload);      // 与这一家无关 ✗

            List<string> names = VolumeNameRepair
                .EnumerateVolumeCandidatesNearby(Path.Combine(own, "own.bin"))
                .Select(c => Path.GetFileName(c.Path))
                .ToList();

            Assert.Contains("own.bin", names);
            Assert.Contains("ownchild.bin", names);
            Assert.Contains("parent.bin", names);
            Assert.Contains("sibling.bin", names);
            Assert.DoesNotContain("grandchild.bin", names);
            Assert.DoesNotContain("grandparent.bin", names);
            Assert.DoesNotContain("unrelated.bin", names);
        }

        // ── 造样本 ──

        /// <summary>
        /// 造一个"**只剩少数几片定不下来**"的真跨盘 zip（试拼那一档要用的形状）。
        ///
        /// <para>形状怎么来的（实测本机 WinRAR 的切法）：片大小 1 MiB、每个条目 1.5 MiB
        /// ⇒ 条目起点大约每 1.4~1.5 片落一个 ⇒ 中间必然出现"整段夹在数据里、一个条目起点都没有"的片
        /// （实测 5 个 1.5 MiB 的条目 = 8 片，其中第 3、第 6 片没有条目起点，与真机 <c>FFF</c> 那一组的形状同类）。
        /// ⚠ 片大小取 64 KB 时 WinRAR 的切法完全不同（只切一片就把剩下的全塞进末片），所以这里用 1 MiB。</para>
        ///
        /// <para>**具体几片不定死**：逐个尺寸试造、用 <see cref="SpannedZipIndex"/> 自己判形状，
        /// 命中"定不下来 1~3 片"就用它（⛔ 不写死"第几片是第几片"，也不为了让用例好看去改判据）。</para>
        /// </summary>
        private List<string> MakeTwoUndecidedSpannedZip(string directory, out int totalParts, string? password = null)
        {
            (int Count, int Bytes)[] shapes =
            {
                (5, 1572864),   // 5 × 1.5 MiB，片 1 MiB —— 实测留下 2 片没有条目起点
                (6, 1572864),
                (5, 1468006),   // 1.4 MiB
                (5, 1677721),   // 1.6 MiB
                (7, 1310720),   // 1.25 MiB
                (6, 1835008)    // 1.75 MiB
            };

            foreach ((int count, int bytes) in shapes)
            {
                string candidateDirectory = Path.Combine(directory, "try-" + count + "x" + bytes);

                Directory.CreateDirectory(candidateDirectory);

                string source = Path.Combine(candidateDirectory, "src");
                Directory.CreateDirectory(source);

                var random = new Random(20261001);

                for (int i = 0; i < count; i++)
                {
                    var payload = new byte[bytes];
                    random.NextBytes(payload);
                    File.WriteAllBytes(Path.Combine(source, "big" + i + ".bin"), payload);
                }

                List<string> parts = RunWinRarSpannedZip(candidateDirectory, "set.zip", source, "1m", password);

                Directory.Delete(source, recursive: true);

                if (parts.Count < 4)
                {
                    continue;
                }

                List<string> disguised = DisguiseNames(candidateDirectory, parts, "set");
                SpannedZipPin pin = SpannedZipIndex
                    .TryRead(FindSpannedTail(disguised))!
                    .Pin(CandidatesIn(candidateDirectory));

                if (pin.Consistent && pin.UndecidedDisks.Count is >= 1 and <= 2)
                {
                    // 命中：把这一份搬到用例要的目录里（名字已经改好、顺序仍然是真值顺序）。
                    var moved = new List<string>();

                    foreach (string file in disguised)
                    {
                        string destination = Path.Combine(directory, Path.GetFileName(file));
                        File.Move(file, destination);
                        moved.Add(destination);
                    }

                    totalParts = moved.Count;

                    return moved;
                }
            }

            throw new InvalidOperationException("造不出'只剩一两片定不下来'的跨盘 zip 样本（本机 WinRAR 的切法变了？）");
        }

        private List<string> MakeSpannedZip(
            string directory,
            string archiveName,
            int entryCount,
            int entryBytes,
            string volume)
        {
            Directory.CreateDirectory(directory);

            string source = Path.Combine(directory, "src");
            Directory.CreateDirectory(source);

            var random = new Random(20261001);

            for (int i = 0; i < entryCount; i++)
            {
                var bytes = new byte[entryBytes];
                random.NextBytes(bytes);
                File.WriteAllBytes(Path.Combine(source, "f" + i.ToString("D2") + ".bin"), bytes);
            }

            return RunWinRarSpannedZip(directory, archiveName, source, volume, password: null);
        }

        /// <summary>用本机 WinRAR 现造真跨盘 zip，返回**按真值顺序**排好的各片（最后一个是末片）。</summary>
        private List<string> RunWinRarSpannedZip(
            string directory,
            string archiveName,
            string source,
            string volume,
            string? password)
        {
            var arguments = new List<string> { "a", "-afzip", "-m0", "-v" + volume, "-ep1", "-ibck" };

            if (!string.IsNullOrEmpty(password))
            {
                arguments.Add("-p" + password);
            }

            arguments.Add(Path.Combine(directory, archiveName));
            arguments.Add(Path.Combine(source, "*"));

            var startInfo = new ProcessStartInfo(_winRar!)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using Process process = Process.Start(startInfo)!;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.True(process.ExitCode == 0, $"WinRAR 造样本失败（退出码 {process.ExitCode}）");

            /*
             * ⚠ `-ibck`（后台跑，⛔ 不弹窗打扰用户）会让 WinRAR **立刻返回**，真正的打包还在后台做 ——
             * 所以这里必须等它落定（末片出现 + 各片大小连续两次读都一样），否则读到的是半成品
             * （第一版就是这么错的：盘数、片数看着都对，内容却还没写完）。
             */
            WaitForArchiveSettled(directory, archiveName);

            var volumes = Directory
                .GetFiles(directory, Path.GetFileNameWithoutExtension(archiveName) + ".z*")
                .Where(p => !string.Equals(Path.GetFileName(p), archiveName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            string tail = Path.Combine(directory, archiveName);
            Assert.True(File.Exists(tail), "跨盘 zip 的末片应当叫 <基名>.zip");

            volumes.Add(tail);

            return volumes;
        }

        /// <summary>等后台的 WinRAR 落定：末片出现，而且各片的大小连续两次读完全一样。</summary>
        private static void WaitForArchiveSettled(string directory, string archiveName)
        {
            var deadline = DateTime.UtcNow.AddSeconds(60);
            string? previous = null;

            while (DateTime.UtcNow < deadline)
            {
                System.Threading.Thread.Sleep(200);

                string snapshot = string.Join(
                    ";",
                    Directory
                        .GetFiles(directory)
                        .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                        .Select(p => Path.GetFileName(p) + "=" + new FileInfo(p).Length));

                if (!File.Exists(Path.Combine(directory, archiveName)))
                {
                    previous = null;
                    continue;
                }

                if (previous != null && string.Equals(previous, snapshot, StringComparison.Ordinal))
                {
                    return;
                }

                previous = snapshot;
            }

            throw new InvalidOperationException("等 WinRAR 打包落定超时（60 秒）");
        }

        /// <summary>
        /// 按真机那种改法把每一片的名字里的卷号**全删掉**：中间片改成 <c>set.z甲</c> 这种（一个数字都没有），
        /// 末片改成 <c>set.zi删除p</c>（真机 <c>222.zi删除p</c> 那一形状：网盘把「删除」塞进了后缀里）。
        ///
        /// <para>返回与 <paramref name="parts"/> **同序**的名字表（下标 = 真值位次）。</para>
        /// </summary>
        private static List<string> DisguiseNames(string directory, List<string> parts, string stem)
        {
            string[] words = { "甲", "乙", "丙", "丁", "戊", "己", "庚", "辛", "壬", "癸", "子", "丑", "寅", "卯", "辰", "巳", "午", "未", "申", "酉" };
            var disguised = new List<string>();

            for (int i = 0; i < parts.Count; i++)
            {
                bool isTail = i == parts.Count - 1;
                string name = isTail
                    ? stem + ".zi删除p"
                    : stem + ".z" + words[i % words.Length] + (i >= words.Length ? "x" + i : string.Empty);

                string target = Path.Combine(directory, name);
                File.Move(parts[i], target);
                disguised.Add(target);
            }

            return disguised;
        }

        /// <summary>哪一份自述是"跨盘 zip 的末片"（判据只有 VolumeNumberFromContent 一处）。</summary>
        private static string FindSpannedTail(IEnumerable<string> paths) => paths
            .Single(p =>
            {
                VolumeNumberReading reading = VolumeNumberFromContent.Read(p);
                return reading.Format == VolumeContentFormat.Zip && reading.IsVolumeMember && reading.Number != null;
            });

        private static List<VolumeCandidate> CandidatesIn(string directory)
        {
            var candidates = new List<VolumeCandidate>();

            foreach (string file in Directory.GetFiles(directory))
            {
                candidates.Add(new VolumeCandidate { Path = file, Size = new FileInfo(file).Length });
            }

            return candidates;
        }

        /// <summary>试拼用的工作区根：与样本**同卷**（硬链接不能跨卷），用完自己删。</summary>
        private static string WorkRootFor(string samplePath)
        {
            string root = Path.Combine(Path.GetDirectoryName(samplePath)!, ".test-work");

            Directory.CreateDirectory(root);

            return root;
        }

        private static bool CanList(string directory, string tailPath)
        {
            var engine = new Engines.SevenZip.SevenZipEngine();

            ArchiveListResult list = engine
                .ListAsync(ArchiveRequest.For(tailPath))
                .GetAwaiter()
                .GetResult();

            return list.Success && !list.IsRawSplitStream && list.Entries.Count > 0;
        }

        private static string? LocateWinRar()
        {
            string[] candidates =
            {
                @"C:\Program Files\WinRAR\WinRAR.exe",
                @"C:\Program Files (x86)\WinRAR\WinRAR.exe"
            };

            return candidates.FirstOrDefault(File.Exists);
        }
    }

    /// <summary>小工具：把一个表达式直接投进 lambda（只为把上面那行写得短一点）。</summary>
    internal static class SpannedZipTestExtensions
    {
        public static TResult Let<T, TResult>(this T value, Func<T, TResult> map) => map(value);
    }
}

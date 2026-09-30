using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Detection;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **分卷组装判定器的守门用例**（用户 2026-09-30：把分卷组装算法真正做出来）。
    ///
    /// <para>唯一出口 = <see cref="VolumeGroupResolver"/>；对外只有一个结论
    /// （<see cref="VolumeGroupVerdict"/> 四档）。本文件钉三件事：</para>
    /// <list type="number">
    /// <item><description>用户交代的**三个真案**各有断言：
    /// ① <c>111.7z.001</c> + 无后缀的 <c>111</c> + <c>111.7z.003</c> ⇒ 缺第 2 卷（有证据）；
    /// ② <c>一只顶美.z01…z05</c> + 被改名的 <c>一只顶美.z删除ip</c> ⇒ 试开成立才算齐，且永远不许进可删残留；
    /// ③ 续卷伪装成 <c>.mp4</c> / <c>.jpg</c> ⇒ 按体积 + 基名段认出来。</description></item>
    /// <item><description>**判不出 ⇒ 一律不删源、不移动源**：定稿计划整份作废（<c>Failed</c>），
    /// <c>ProcessArtifactSources</c> 为空。</description></item>
    /// <item><description>导入期的"疑似无用物"提醒里，判定器认下来的组成员一个都不许出现
    /// （25 GB 那次事故就是被这个提醒框送进删除的）。</description></item>
    /// </list>
    /// </summary>
    public sealed class VolumeGroupResolverTests : IDisposable
    {
        private readonly string _root;

        public VolumeGroupResolverTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixer-volume-resolver-" + Guid.NewGuid().ToString("N"));
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

        // ================================================================ 真案 ①
        // 111.7z.001 + 111（无后缀）+ 111.7z.003

        /// <summary>
        /// **真案 ①**（用户原话：中间那卷被改名 / 丢了后缀，只能靠体积 + 位置认）。
        ///
        /// <para>必须判 <see cref="VolumeGroupVerdict.IncompleteMissingVolume"/> 而不是"完整"：
        /// 卷号 1..3 中间有个洞，而洞那一槽的规范名**带卷号**（<c>111.7z.002</c>）——
        /// 一个名字里没有任何卷号的文件**不可能**被断言成"就是那一卷"（断言了就得改用户的名字，
        /// 而程序永远不改用户的文件名）。同目录那个 <c>111</c> 按体积 + 位置**推定**到了第 2 卷，
        /// 但只当推定，写进结论的依据里。</para>
        /// </summary>
        [Fact]
        public void 真案一_中间那卷丢了后缀_判缺第2卷且说明依据()
        {
            string directory = NewDirectory("case1");
            const long full = 4096;

            string first = WriteFile(directory, "111.7z.001", full);
            string nameless = WriteFile(directory, "111", full);
            string third = WriteFile(directory, "111.7z.003", 512);

            VolumeGroupResolution resolution = Resolve(first);

            Assert.Equal(VolumeGroupVerdict.IncompleteMissingVolume, resolution.Verdict);
            Assert.Contains("111.7z.002", resolution.MissingVolumeNames);
            Assert.Contains("缺第 2 卷", resolution.Reason, StringComparison.Ordinal);

            // 依据要写出来 —— 用户要看得到"为什么这样判"。
            Assert.Contains(
                resolution.Evidence,
                row => row.Kind == VolumeEvidenceKind.Sequence && row.Outcome == VolumeEvidenceOutcome.NotSatisfied);
            Assert.Contains(
                resolution.Evidence,
                row => row.Kind == VolumeEvidenceKind.PositionInference &&
                       row.Detail.Contains("111", StringComparison.Ordinal));

            // 那个无后缀的 111 属于这一组 ⇒ 永远不许进可删残留名单。
            Assert.True(resolution.IsGroupMember(nameless), "无后缀的那一卷必须被认成同一组");
            Assert.False(resolution.CanEnterDeletableRestItems);
            Assert.Contains(third, resolution.GroupFilePaths);
        }

        /// <summary>反面：把 <c>111</c> 拿掉（真缺一卷）⇒ 仍然是"缺第 2 卷"，只是没有推定候选。</summary>
        [Fact]
        public void 真案一_同目录没有那个无后缀文件_照样判缺第2卷()
        {
            string directory = NewDirectory("case1b");

            string first = WriteFile(directory, "111.7z.001", 4096);
            WriteFile(directory, "111.7z.003", 512);

            VolumeGroupResolution resolution = Resolve(first);

            Assert.Equal(VolumeGroupVerdict.IncompleteMissingVolume, resolution.Verdict);
            Assert.Contains("111.7z.002", resolution.MissingVolumeNames);

            // 判不出"它是谁"就不许把它说成"可能是谁" —— 没有候选就别编。
            Assert.Empty(resolution.PositionInferredNotes);
        }

        // ================================================================ 真案 ②
        // 一只顶美.z01…z05 + 一只顶美.z删除ip（末卷被改名）

        /// <summary>
        /// **真案 ②**（用户机器上 25 GB 那次）：跨盘的末卷被重命名成 <c>一只顶美.z删除ip</c>。
        ///
        /// <para>没有试开时只能是"疑缺卷（弱证据）"：名字上看缺 <c>一只顶美.zip</c>，
        /// 同目录那个文件基名段对得上、体积也对得上，但名字里没有卷号 —— 弱证据**只报不删**。
        /// 判 <c>完整</c> 的资格只由试开给。</para>
        /// </summary>
        [Fact]
        public void 真案二_末卷被改名_没试开时只报疑缺卷_不许说完整()
        {
            string directory = NewDirectory("case2-weak");
            string renamed = BuildRenamedTailGroup(directory);

            VolumeGroupResolution resolution = Resolve(Path.Combine(directory, "一只顶美.z01"));

            Assert.Equal(VolumeGroupVerdict.IncompleteSuspected, resolution.Verdict);
            Assert.Contains("疑缺卷", resolution.Reason, StringComparison.Ordinal);
            Assert.Empty(resolution.MissingVolumeNames);

            // ⛔ 弱证据不许进可删的其余物（25 GB 那次就是在这里丢的）。
            Assert.False(resolution.CanEnterDeletableRestItems);
            Assert.True(resolution.IsGroupMember(renamed), "被改名的那一卷必须被认成同一组");
            Assert.True(resolution.HasRenamedVolume);
        }

        /// <summary>
        /// **真案 ② 的决定性一档**：试开成立 ⇒ 判"完整"，被改名的那一卷**在第 1 卷位子上**
        /// （<c>x.zip</c> 是 PKZIP 跨盘的第 1 段，这是 <see cref="VolumeGroupDetector"/> 的族规则）。
        ///
        /// <para>但**仍然不许进可删的其余物** —— 它的名字不是标准卷名，7-Zip 按原名根本打不开，
        /// 删了就是不可逆的数据丢失。</para>
        /// </summary>
        [Fact]
        public async Task 真案二_试开成立判完整_但那卷永远不许进可删残留()
        {
            string directory = NewDirectory("case2-strong");
            string renamed = BuildRenamedTailGroup(directory);

            var opener = new RecordingTrialOpener { Confirm = true };

            var resolver = new VolumeGroupResolver(opener.OpenAsync);

            VolumeGroupResolution resolution = await resolver.ResolveAsync(new VolumeGroupQuery
            {
                AnchorPath = Path.Combine(directory, "一只顶美.z01")
            });

            Assert.Equal(VolumeGroupVerdict.Complete, resolution.Verdict);
            Assert.Contains("试开", resolution.Reason, StringComparison.Ordinal);

            // 试开请求必须把"被改名的那一卷"当第 1 卷，后面按 .z01…z05 的卷序排。
            Assert.NotNull(opener.LastRequest);
            Assert.Equal(renamed, opener.LastRequest!.FirstVolumePath);

            List<string> ordering = opener.LastRequest.Orderings[0].Select(v => Path.GetFileName(v.Path)).ToList();

            Assert.Equal(
                new[] { "一只顶美.z01", "一只顶美.z02", "一只顶美.z03", "一只顶美.z04", "一只顶美.z05" },
                ordering);

            Assert.Equal(6, resolution.Volumes.Count);
            Assert.Equal(renamed, resolution.Volumes[0].Path);
            Assert.True(resolution.IsGroupMember(renamed));
            Assert.True(resolution.HasRenamedVolume);

            // ⛔ 这条断言就是"25 GB 那次事故"的守门：组齐了也不许删。
            Assert.False(resolution.CanEnterDeletableRestItems);
        }

        /// <summary>对照组：末卷名字**标准**（<c>一只顶美.zip</c> 在位）⇒ 完整且允许进其余物（老行为不许被误伤）。</summary>
        [Fact]
        public void 真案二_末卷名字标准_判完整且允许进其余物()
        {
            string directory = NewDirectory("case2-ok");

            WriteFile(directory, "一只顶美.z01", 4096);
            WriteFile(directory, "一只顶美.z02", 4096);
            WriteFile(directory, "一只顶美.z03", 4096);
            WriteFile(directory, "一只顶美.zip", 4096);

            VolumeGroupResolution resolution = Resolve(Path.Combine(directory, "一只顶美.z01"));

            Assert.Equal(VolumeGroupVerdict.Complete, resolution.Verdict);
            Assert.False(resolution.HasRenamedVolume);
            Assert.True(resolution.CanEnterDeletableRestItems);
        }

        // ================================================================ 真案 ③
        // 续卷伪装成别的类型（.mp4 / .jpg）

        /// <summary>
        /// **真案 ③**：续卷伪装成 <c>.mp4</c>。名字上完全看不出来（<c>.mp4</c> 不是卷标记），
        /// 只能靠**基名段相同 + 体积与满卷相等**认出来。
        ///
        /// <para>没试开时判"疑缺卷"（弱证据）：名字序号看不出后面还有卷，但同目录有等大的候选
        /// —— 它很可能就是续卷，也可能不是。</para>
        /// </summary>
        [Fact]
        public void 真案三_续卷伪装成mp4_按体积与基名段认出来()
        {
            string directory = NewDirectory("case3");
            const long full = 8192;

            string first = WriteFile(directory, "电影.7z.001", full);
            string disguised = WriteFile(directory, "电影.mp4", full);

            VolumeGroupResolution resolution = Resolve(first);

            Assert.Equal(VolumeGroupVerdict.IncompleteSuspected, resolution.Verdict);
            Assert.True(resolution.IsGroupMember(disguised), "伪装成 .mp4 的续卷必须被认成同一组");
            Assert.False(resolution.CanEnterDeletableRestItems);
            Assert.Contains(
                resolution.PositionInferredNotes,
                note => note.Contains("电影.mp4", StringComparison.Ordinal));
        }

        /// <summary>伪装成 <c>.mp4</c> 的续卷 + 试开成立 ⇒ 完整；它仍然是"名字不标准"的那一卷（不许删）。</summary>
        [Fact]
        public async Task 真案三_伪装续卷_试开成立判完整()
        {
            string directory = NewDirectory("case3b");
            const long full = 8192;

            string first = WriteFile(directory, "电影.7z.001", full);
            string disguised = WriteFile(directory, "电影.mp4", full);

            var opener = new RecordingTrialOpener { Confirm = true };
            var resolver = new VolumeGroupResolver(opener.OpenAsync);

            VolumeGroupResolution resolution = await resolver.ResolveAsync(new VolumeGroupQuery
            {
                AnchorPath = first
            });

            Assert.Equal(VolumeGroupVerdict.Complete, resolution.Verdict);
            Assert.Equal(2, resolution.Volumes.Count);
            Assert.Equal(disguised, resolution.Volumes[1].Path);
            Assert.True(resolution.IsGroupMember(disguised));
            Assert.False(resolution.CanEnterDeletableRestItems);
        }

        /// <summary>更短的末卷被改名成别的后缀（<c>.apk</c>）⇒ "候选比名字序号还多"，弱证据档。</summary>
        [Fact]
        public void 真案三_更短的末卷被改名成apk_也认得出()
        {
            string directory = NewDirectory("case3c");

            string first = WriteFile(directory, "安装包.7z.001", 8192);
            WriteFile(directory, "安装包.7z.002", 8192);
            string renamedTail = WriteFile(directory, "安装包.apk", 300);

            VolumeGroupResolution resolution = Resolve(first);

            Assert.Equal(VolumeGroupVerdict.IncompleteSuspected, resolution.Verdict);
            Assert.True(resolution.IsGroupMember(renamedTail));
            Assert.False(resolution.CanEnterDeletableRestItems);
        }

        // ================================================================ 判不出 / 边界

        /// <summary>名字里没有卷标记、同目录也没有同基名的组 ⇒ **判不出**（⛔ 不猜、不删、不移动）。</summary>
        [Fact]
        public void 判不出_名字里没有卷标记也没有同基名的组()
        {
            string directory = NewDirectory("undetermined");

            string lonely = WriteFile(directory, "随手一个.mp4", 1024);
            WriteFile(directory, "别的.7z.001", 1024);
            WriteFile(directory, "别的.7z.002", 1024);

            VolumeGroupResolution resolution = Resolve(lonely);

            Assert.Equal(VolumeGroupVerdict.Undetermined, resolution.Verdict);
            Assert.Contains("判不出", resolution.Reason, StringComparison.Ordinal);
            Assert.Empty(resolution.GroupFilePaths);
            Assert.False(resolution.CanEnterDeletableRestItems);
        }

        /// <summary>
        /// **同一份被数成两卷**必须判不出来：<c>111.7z.002</c> 是 <c>111.7z.001</c> 的硬链接
        /// （同一个 FileId）—— 物理上只有一份数据，它的卷号是"假的"，不许据此说"完整"。
        /// </summary>
        [Fact]
        public void 同一份硬链接成两卷_判不出_不许当完整()
        {
            string directory = NewDirectory("hardlink");

            string first = WriteFile(directory, "x.7z.001", 4096);
            string second = Path.Combine(directory, "x.7z.002");

            Assert.True(TryCreateHardLink(second, first), "本机文件系统不支持硬链接，这条用例失去意义");

            VolumeGroupResolution resolution = Resolve(first);

            Assert.Equal(VolumeGroupVerdict.Undetermined, resolution.Verdict);
            Assert.Contains("同一个文件", resolution.Reason, StringComparison.Ordinal);
            Assert.False(resolution.CanEnterDeletableRestItems);
        }

        /// <summary>证据表：标准完整的数字分卷，六条证据一条都不能缺（"依据在哪"必须看得见）。</summary>
        [Fact]
        public void 证据表_六条证据一条不缺()
        {
            string directory = NewDirectory("evidence");

            string first = WriteFile(directory, "x.7z.001", 4096);
            WriteFile(directory, "x.7z.002", 4096);
            WriteFile(directory, "x.7z.003", 1024);

            VolumeGroupResolution resolution = Resolve(first);

            Assert.Equal(VolumeGroupVerdict.Complete, resolution.Verdict);
            Assert.True(resolution.CanEnterDeletableRestItems);

            foreach (VolumeEvidenceKind kind in Enum.GetValues<VolumeEvidenceKind>())
            {
                Assert.Contains(resolution.Evidence, row => row.Kind == kind);
            }

            Assert.Contains(
                resolution.Evidence,
                row => row.Kind == VolumeEvidenceKind.SizePattern && row.Outcome == VolumeEvidenceOutcome.Satisfied);
            Assert.Contains(
                resolution.Evidence,
                row => row.Kind == VolumeEvidenceKind.PhysicalIdentity && row.Outcome == VolumeEvidenceOutcome.Satisfied);

            // 定稿闸门与导入期都不试开 ⇒ 这一条如实记"没试"（⛔ 不许记成"不成立"）。
            Assert.Contains(
                resolution.Evidence,
                row => row.Kind == VolumeEvidenceKind.TrialOpen && row.Outcome == VolumeEvidenceOutcome.Unknown);
        }

        /// <summary>试开临时物落点与工作区根必须是同一个名字（⛔ 一处改名、另一处忘改 = 脏目录留在别人盘上）。</summary>
        [Fact]
        public void 试开临时目录名_与工作区根同名()
        {
            Assert.Equal(
                WorkspaceRootResolver.DefaultWorkspaceDirectoryName,
                VolumeContentInference.WorkDirectoryName);
        }

        // ================================================================ 删除闸门（唯一出口的下游）
        // ⛔ 判不出 / 弱证据 ⇒ 整份定稿计划作废：不搬、不删（含源包与其余物）

        /// <summary>真案 ② 的暂存目录 ⇒ 整份计划作废，一卷都不许当成可删的其余物。</summary>
        [Fact]
        public void 真案二_暂存目录里是改名末卷_整份计划作废()
        {
            string stage = NewDirectory("stage-case2");

            WriteFile(stage, "一只顶美.z01", 4096);
            WriteFile(stage, "一只顶美.z02", 4096);
            WriteFile(stage, "一只顶美.z03", 4096);
            string renamed = WriteFile(stage, "一只顶美.z删除ip", 1024);

            ExtractionCoordinator.FinalLayoutPlan plan = ExtractionCoordinator.PlanFinalLayout(
                stage,
                Path.Combine(_root, "out-case2"),
                sharedOutputRoot: false,
                "一只顶美");

            Assert.True(plan.Failed, "被改名的末卷那一组必须让整份计划作废");
            Assert.Contains("不完整", plan.FailureReason, StringComparison.Ordinal);
            Assert.Empty(plan.ProcessArtifactSources);
            Assert.Empty(plan.Moves);

            // ⛔ 连"内容物"都不许搬：那一卷不是内容物，它是这一组的一卷。
            Assert.DoesNotContain(renamed, plan.ProcessArtifactSources);
        }

        /// <summary>真案 ① 的暂存目录（无后缀那一卷）⇒ 同样整份作废。</summary>
        [Fact]
        public void 真案一_暂存目录里有无后缀那一卷_整份计划作废()
        {
            string stage = NewDirectory("stage-case1");

            WriteFile(stage, "111.7z.001", 4096);
            string nameless = WriteFile(stage, "111", 4096);
            WriteFile(stage, "111.7z.003", 512);

            ExtractionCoordinator.FinalLayoutPlan plan = ExtractionCoordinator.PlanFinalLayout(
                stage,
                Path.Combine(_root, "out-case1"),
                sharedOutputRoot: false,
                "111");

            Assert.True(plan.Failed);
            Assert.Empty(plan.ProcessArtifactSources);
            Assert.Empty(plan.Moves);
            Assert.DoesNotContain(nameless, plan.ProcessArtifactSources);
        }

        /// <summary>真案 ③ 的暂存目录（伪装成 .mp4 的续卷）⇒ 同样整份作废。</summary>
        [Fact]
        public void 真案三_暂存目录里有伪装续卷_整份计划作废()
        {
            string stage = NewDirectory("stage-case3");

            WriteFile(stage, "电影.7z.001", 8192);
            string disguised = WriteFile(stage, "电影.mp4", 8192);

            ExtractionCoordinator.FinalLayoutPlan plan = ExtractionCoordinator.PlanFinalLayout(
                stage,
                Path.Combine(_root, "out-case3"),
                sharedOutputRoot: false,
                "电影");

            Assert.True(plan.Failed);
            Assert.Empty(plan.ProcessArtifactSources);
            Assert.Empty(plan.Moves);
            Assert.DoesNotContain(disguised, plan.ProcessArtifactSources);
        }

        // ================================================================ 删除闸门本身（commit 9a54d99 那道）

        /// <summary>
        /// **删除闸门**（`ExtractionCoordinator.TryConfirmVolumeGroupComplete`）现在由判定器回答：
        /// 被改名的末卷那一组**确认不了完整** ⇒ 一卷都不许放行（老判据在这里也是 false，
        /// 但它给的原因是"缺末卷"，而现在给的是"疑缺卷 + 那个文件体积位置都对得上"）。
        /// </summary>
        [Fact]
        public void 删除闸门_改名末卷那一组_确认不了完整()
        {
            string directory = NewDirectory("gate-renamed");
            BuildRenamedTailGroup(directory);

            bool complete = ExtractionCoordinator.TryConfirmVolumeGroupComplete(
                Path.Combine(directory, "一只顶美.z01"),
                out string note);

            Assert.False(complete);
            Assert.Contains("一只顶美", note, StringComparison.Ordinal);
        }

        /// <summary>反面：名字标准的完整组照常放行（老行为不许被误伤）。</summary>
        [Fact]
        public void 删除闸门_标准完整组_照常放行()
        {
            string directory = NewDirectory("gate-ok");

            WriteFile(directory, "x.7z.001", 4096);
            WriteFile(directory, "x.7z.002", 4096);
            WriteFile(directory, "x.7z.003", 512);

            Assert.True(ExtractionCoordinator.TryConfirmVolumeGroupComplete(
                Path.Combine(directory, "x.7z.001"),
                out string note));
            Assert.Equal(string.Empty, note);
        }

        /// <summary>⛔ **判不出 ⇒ 一律按"确认不了"处理**（兜底落在"什么都不做"那一档）。</summary>
        [Fact]
        public void 删除闸门_判不出时一律不放行()
        {
            string directory = NewDirectory("gate-undetermined");
            string lonely = WriteFile(directory, "随手.mp4", 512);

            Assert.False(ExtractionCoordinator.TryConfirmVolumeGroupComplete(lonely, out string note));
            Assert.Contains("判不出", note, StringComparison.Ordinal);
        }

        // ================================================================ 导入期的"疑似无用物"名单
        // ⛔ 判定器认下来的组成员一个都不许出现在里面（25 GB 就是被这个提醒框送进删除的）

        /// <summary>
        /// 续卷伪装成 <c>.jpg</c>（<c>.jpg</c> 在"疑似无用物"后缀表里，而且**裸切续卷没有魔数**，
        /// 所以只看名字 + 只看魔数都保护不到它）。
        ///
        /// <para>先钉"老判据确实会把它当候选"（否则这个用例什么都没证明），再钉判定器把它保护住。</para>
        /// </summary>
        [Fact]
        public async Task 伪装成jpg的续卷_导入提醒里不许列成无用物()
        {
            string directory = NewDirectory("import");

            string first = WriteFile(directory, "影片.7z.001", 8192);
            string disguised = WriteFile(directory, "影片.jpg", 8192);
            string realJunk = WriteFile(directory, "说明.txt", 64);

            // ① 名字判据本身确实会把 .jpg 当候选（这是这个用例的意义所在）。
            Assert.True(SourceJunkScanner.LooksLikeJunkCandidate("影片.jpg"));

            // ② 它没有归档魔数 —— 裸切的续卷本来就没有任何格式标记。
            var prober = new MagicArchiveProber();
            Assert.False(await prober.IsArchiveAsync(disguised), "裸切续卷不该被魔数认成归档");

            // ③ 判定器认得出它属于这一组。
            Assert.True(Resolve(first).IsGroupMember(disguised));

            var task = new ArchiveTask(first, 1)
            {
                IsArchive = true,
                DetectedFormat = "7Z"
            };

            SourceJunkScanResult scanned = await SourceJunkScanner.ScanAsync(
                new[] { task },
                prober,
                CancellationToken.None);

            Assert.DoesNotContain(
                scanned.Items,
                item => string.Equals(item.FileName, "影片.jpg", StringComparison.OrdinalIgnoreCase));

            // 真的无用物照旧要报出来（不许因为保护而整体失效）。
            Assert.Contains(
                scanned.Items,
                item => string.Equals(item.FileName, "说明.txt", StringComparison.OrdinalIgnoreCase));

            _ = realJunk;
        }

        // ================================================================ 真 7z 试开（端到端那一档）

        /// <summary>
        /// **真 7z 试开**：现造一组真的 7z 分卷，把**末卷**改名成别的后缀，
        /// 再让判定器按推定顺序做硬链接 + 交给内置 7z 试开 —— 引擎列得出清单才算"齐"。
        ///
        /// <para>这条用例证明"试开确认"这一档真的能跑（不是只有替身会说话），
        /// 而且它**只读**：源文件一个字节都不动（硬链接 + 收工删试开目录）。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 真七z_末卷被改名成别的后缀_试开能认出整组是齐的()
        {
            string directory = NewDirectory("real7z");
            string payload = Path.Combine(directory, "data.bin");

            File.WriteAllBytes(payload, MakeBytes(2 * 1024 * 1024 + 12345));

            Run7z(directory, "a", "-t7z", "-mx0", "-v1m", "包.7z", "data.bin");

            Assert.True(File.Exists(Path.Combine(directory, "包.7z.003")), "样本不是三卷");

            // 真机那种改法：末卷的名字整个换掉（后缀也不对）。
            string renamedTail = Path.Combine(directory, "包.7z删掉");
            File.Move(Path.Combine(directory, "包.7z.003"), renamedTail);

            // ⛔ 源文件一个字节都不许动：先记下签名与长度。
            string first = Path.Combine(directory, "包.7z.001");
            byte[] before = File.ReadAllBytes(first);
            long tailLengthBefore = new FileInfo(renamedTail).Length;

            var engine = new ArchiveFixer.Engines.SevenZip.SevenZipEngine();
            var verifier = new ArchiveFixer.Extraction.VolumeProbeVerifier(engine);
            var resolver = new VolumeGroupResolver(verifier.TryOpenAsync);

            VolumeGroupResolution resolution = await resolver.ResolveAsync(new VolumeGroupQuery
            {
                AnchorPath = first,
                WorkRootDirectory = Path.Combine(directory, VolumeContentInference.WorkDirectoryName)
            });

            Assert.Equal(VolumeGroupVerdict.Complete, resolution.Verdict);
            Assert.Equal(3, resolution.Volumes.Count);
            Assert.True(resolution.IsGroupMember(renamedTail), "被改名的末卷必须被认成同一组");

            // 组齐 ≠ 可以删：它的名字不是标准卷名，7-Zip 按原名打不开。
            Assert.False(resolution.CanEnterDeletableRestItems);

            // 只读：源文件与那一卷都还在、内容没变。
            Assert.Equal(before, File.ReadAllBytes(first));
            Assert.Equal(tailLengthBefore, new FileInfo(renamedTail).Length);
            Assert.True(File.Exists(renamedTail));
        }

        // ================================================================ 辅助

        private static VolumeGroupResolution Resolve(string anchor) =>
            new VolumeGroupResolver().Resolve(new VolumeGroupQuery
            {
                AnchorPath = anchor,
                AllowTrialOpen = false
            });

        /// <summary>造一组"末卷被改名成 一只顶美.z删除ip"的跨盘样本（合成、内容无所谓）。</summary>
        private static string BuildRenamedTailGroup(string directory)
        {
            for (int i = 1; i <= 5; i++)
            {
                WriteFile(directory, $"一只顶美.z{i:D2}", 4096);
            }

            return WriteFile(directory, "一只顶美.z删除ip", 1024);
        }

        private string NewDirectory(string name)
        {
            string path = Path.Combine(_root, name);
            Directory.CreateDirectory(path);

            return path;
        }

        private static string WriteFile(string directory, string name, long size)
        {
            string path = Path.Combine(directory, name);

            // 内容无所谓（判定器只看名字 / 体积 / 位置 / FileId）；但要真的落盘。
            File.WriteAllBytes(path, MakeBytes((int)Math.Min(size, 64 * 1024)));

            if (size > 64 * 1024)
            {
                using FileStream stream = File.Open(path, FileMode.Open, FileAccess.Write);
                stream.SetLength(size);
            }

            return path;
        }

        private static byte[] MakeBytes(int count)
        {
            var bytes = new byte[count];

            for (int i = 0; i < count; i++)
            {
                bytes[i] = (byte)(i % 251);
            }

            return bytes;
        }

        private static bool TryCreateHardLink(string linkPath, string existingPath)
        {
            try
            {
                return NativeHardLink.CreateHardLink(linkPath, existingPath);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>试开的替身：记下请求，按需要回答"成立 / 不成立"（真的引擎那档由真 7z 用例覆盖）。</summary>
        private sealed class RecordingTrialOpener
        {
            public bool Confirm { get; init; }

            public VolumeTrialRequest? LastRequest { get; private set; }

            public Task<VolumeTrialOutcome> OpenAsync(VolumeTrialRequest request, CancellationToken cancellationToken)
            {
                LastRequest = request;

                if (!Confirm)
                {
                    return Task.FromResult(new VolumeTrialOutcome
                    {
                        Attempted = true,
                        Confirmed = false,
                        Attempts = 1,
                        Reason = "替身：不成立"
                    });
                }

                var ordered = new List<string> { request.FirstVolumePath };

                foreach (VolumeCandidate volume in request.Orderings[0])
                {
                    ordered.Add(volume.Path);
                }

                return Task.FromResult(new VolumeTrialOutcome
                {
                    Attempted = true,
                    Confirmed = true,
                    Attempts = 1,
                    OrderedVolumePaths = ordered,
                    Reason = "替身：成立"
                });
            }
        }

        private static void Run7z(string workingDirectory, params string[] args)
        {
            string? sevenZip = SevenZipFactAttribute.LocateSevenZipPath();

            Assert.False(string.IsNullOrEmpty(sevenZip), "找不到内置 7z.exe");

            var psi = new ProcessStartInfo(sevenZip!)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDirectory
            };

            foreach (string arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using Process process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 7z.exe");

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();

            Assert.True(process.WaitForExit(120_000), "7z.exe 超时");

            Assert.True(
                process.ExitCode == 0,
                $"7z.exe 失败（退出码 {process.ExitCode}）：{stdout}{stderr}");
        }

        /// <summary>硬链接（用例自己造的，用来钉"同一 FileId ⇒ 同一份"）。</summary>
        private static class NativeHardLink
        {
            [System.Runtime.InteropServices.DllImport(
                "kernel32.dll",
                CharSet = System.Runtime.InteropServices.CharSet.Unicode,
                SetLastError = true)]
            [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
            private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

            public static bool CreateHardLink(string linkPath, string existingPath) =>
                CreateHardLinkW(linkPath, existingPath, IntPtr.Zero);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 需要真 <c>Rar.exe</c>（造真 RAR 分卷用）+ 真 <c>7z.exe</c>（造外层包用）的用例标记：
    /// 任一个不在就**在发现阶段跳过并说明**，与 <see cref="SevenZipFactAttribute"/> 同一套写法
    /// （⛔ 不用"运行时异常"假装跳过）。
    ///
    /// <para>为什么需要它：RAR 的 <c>partN.rar</c> 分卷只能由 RARLAB 自己的 <c>Rar.exe</c> 造出来，
    /// 而 AGENTS.md §3 明令 <c>Rar.exe</c> / <c>WinRAR.exe</c> **绝不打包、绝不复制** ——
    /// 所以它只能"机器上装了就用、没装就跳过"，绝不进仓库。</para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class RarVolumeFactAttribute : FactAttribute
    {
        private static readonly Lazy<string> LocatedRar = new(LocateRarPath);

        public RarVolumeFactAttribute()
        {
            if (string.IsNullOrEmpty(SevenZipFactAttribute.LocateSevenZipPath()))
            {
                Skip = "测试机上没有可用的 7z.exe，跳过需要真实引擎的用例。";
                return;
            }

            if (string.IsNullOrEmpty(LocatedRar.Value))
            {
                Skip = "测试机上没有 Rar.exe（WinRAR），跳过需要真 RAR 分卷的用例（样本造不出来）。";
            }
        }

        internal static string LocateRarPath()
        {
            foreach (string candidate in new[]
            {
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "WinRAR",
                    "Rar.exe"),
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    "WinRAR",
                    "Rar.exe")
            })
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return string.Empty;
        }
    }

    /// <summary>
    /// **「还原」工序的递归层挂点**（方案《分卷族系统化识别》§2.1 挂点②，用户 2026-10-03）。
    ///
    /// <para>用户口径：「这个就相当于伪装了一层，这个读取底层的情况，将伪装去掉就行了，不是说你要直接用这个
    /// 伪装文件去匹配，我们**首先第一步就是识别底层文件找出伪装文件，然后还原，再接着匹配**」。
    /// 顺序不可颠倒：**① 按魔数认出底层 → ② 还原名字 → ③ 才进族骨架匹配（组卷 / 定序）**。</para>
    ///
    /// <para>现场（AGENTS.md §11.4 §51）：包**里面**解出来的那一层过去没有任何一步先擦伪装尾巴
    /// （<c>RecursiveExtractor</c> 对 <c>VolumeNameRepair</c> 零引用）⇒ 一组
    /// <c>风景01.part1.rar删除</c> / <c>.part2.rar删除</c> 解出来之后名字还是脏的，
    /// 引擎按标准名找不到兄弟卷、只报「分卷缺失」，里面的内容永远出不来。</para>
    ///
    /// <para><b>红检</b>：把 <c>RecursiveExtractor.ProbeInnerArchivesAsync</c> 里那次
    /// <c>VolumeNameRepair.RestoreDisguisedInnerPackageNames(...)</c> 调用撤掉
    /// ⇒ 用例 <see cref="递归层内_一组伪装的7z分卷_先还原再组卷能解开到内容"/> 变红
    /// （内层整组解不开、报分卷缺失）。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public sealed class InnerLayerDisguiseRestoreTests : IDisposable
    {
        /// <summary>伪装尾巴：网盘给每一卷缀的那种（与真机现场同一个形状）。</summary>
        private const string DisguiseTail = "删除";

        /// <summary>只有第 1 层才有的内容标记（用来证明"真的解到了内容"）。</summary>
        private const string PayloadMarker = "伪装尾巴被还原之后才解得出来的内容";

        private readonly string _root;
        private readonly string _sevenZip;
        private readonly string _rar;

        public InnerLayerDisguiseRestoreTests()
        {
            _sevenZip = SevenZipFactAttribute.LocateSevenZipPath();
            _rar = RarVolumeFactAttribute.LocateRarPath();
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerInnerRestore", Guid.NewGuid().ToString("N"));
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
                // 临时目录清不掉不影响结论。
            }
        }

        // ───────────────────────── ① 递归层内：先还原、再组卷、能解开 ─────────────────────────

        [SevenZipFact]
        public async Task 递归层内_一组伪装的7z分卷_先还原再组卷能解开到内容()
        {
            RequireSevenZip();

            // 造一组**真 7z 分卷**（每卷 64 KiB ⇒ 至少 3 卷），再把每一卷的名字缀上「删除」。
            string userDirectory = Path.Combine(_root, "user");
            Directory.CreateDirectory(userDirectory);

            List<string> disguised = BuildDisguisedSevenZipVolumes(userDirectory, "风景01");
            Assert.True(disguised.Count >= 2, $"至少要造出两卷，实际 {disguised.Count} 卷");

            string originalPayload = PayloadPathOfStage(baseName: "风景01", "7z");

            // 用户手上的外层包：里面躺着的就是那几卷伪装名。
            string outer = Path.Combine(userDirectory, "outer.zip");
            Run7z("a", "-tzip", outer, disguised.ToArray());

            // 用户源目录的"开工前账"（用例④的对照断言）。
            Dictionary<string, string> sourceBefore = SnapshotDirectory(userDirectory);

            string output = Path.Combine(_root, "out");
            var logs = new List<string>();

            RecursionResult result = await ExtractAsync(outer, output, logs);

            // ①-1 真的解出了内容（不是"报了个成功但什么都没有"，也不是只解出第一卷那 64 KiB）。
            FindPayload(output, originalPayload);

            // ①-2 日志里有"还原"那一行，并且逐卷点名（旧名 → 新名）。
            string restoreLine = Assert.Single(logs, line => line.Contains("还原伪装后缀", StringComparison.Ordinal));
            Assert.Contains("风景01.7z.001" + DisguiseTail + " → 风景01.7z.001", restoreLine, StringComparison.Ordinal);
            Assert.Contains("风景01.7z.002" + DisguiseTail + " → 风景01.7z.002", restoreLine, StringComparison.Ordinal);

            // ①-3 这一层产物里**不该再留下伪装名**（名字各归位）。
            Assert.DoesNotContain(
                Directory.GetFiles(output, "*", SearchOption.AllDirectories),
                path => Path.GetFileName(path).Contains(DisguiseTail, StringComparison.Ordinal));

            Assert.Equal(RecursionStopReason.Completed, result.StopReason);

            // ④ 用户源目录一个字节不动（名字、大小、内容哈希三项都不许变）。
            Assert.Equal(sourceBefore, SnapshotDirectory(userDirectory));
        }

        /// <summary>
        /// **「名字像分卷的一片、但认不出魔数」也要在第一大步里改回标准名**（用户 2026-10-08 口径）。
        ///
        /// <para>真机现场：`111(3)` 解出来的 `111.z0删除2` —— 7z 的续卷是**裸切字节流**、跨盘 ZIP 的中间片
        /// 也没有文件头，探测器一律认不出；老写法只把 `IsArchiveAsync` 为真的收进候选
        /// ⇒ 它永远进不了「还原」名单、名字一直脏着（真机日志 190 行：它被当成"分卷的后续卷"跳过）。</para>
        ///
        /// <para>用户口径：「简单的改名操作可以弄，就是删除中文字符的操作」；缺卷结论与组卷归位留到批末。</para>
        ///
        /// <para>⛔ 本条同时钉住"**只改名、不进续解名单**"：这个文件的内容不是任何归档，
        /// 递归必须**正常收尾**（拿它去试引擎就会多出一次没意义的调用）。</para>
        ///
        /// <para><b>红检</b>：把 <c>RecursiveExtractor.ProbeInnerArchivesAsync</c> 里
        /// <c>renameOnlyPieces.Add(entry)</c> 那一档撤掉 ⇒ 本条变红（盘上仍叫 `111.z0删除2`）。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 递归层内_只凭名字认得的脏卷片_第一大步就改回标准名()
        {
            RequireSevenZip();

            string userDirectory = Path.Combine(_root, "user-piece");
            Directory.CreateDirectory(userDirectory);

            // 脏名的分卷片，内容**不是**任何归档（模拟 7z 续卷：裸切字节流、没有魔数）。
            string piece = Path.Combine(userDirectory, "111.z0删除2");

            File.WriteAllBytes(piece, new byte[4096]);

            string outer = Path.Combine(userDirectory, "outer-piece.zip");

            Run7z("a", "-tzip", outer, piece);

            string output = Path.Combine(_root, "out-piece");
            var logs = new List<string>();

            RecursionResult result = await ExtractAsync(outer, output, logs);

            // ① 改名发生了（日志点名"旧名 → 新名"）。
            string restoreLine = Assert.Single(logs, line => line.Contains("还原伪装后缀", StringComparison.Ordinal));

            Assert.Contains("111.z0删除2 → 111.z02", restoreLine, StringComparison.Ordinal);

            // ② 盘上名字真的换了，这一层不留脏名。
            Assert.True(
                Directory.GetFiles(output, "111.z02", SearchOption.AllDirectories).Length > 0,
                "产物里没有 111.z02：" + string.Join(" | ", Directory.GetFiles(output, "*", SearchOption.AllDirectories)));

            Assert.DoesNotContain(
                Directory.GetFiles(output, "*", SearchOption.AllDirectories),
                path => Path.GetFileName(path).Contains("删除", StringComparison.Ordinal));

            // ③ ⛔ 只改名、不进续解名单：内容不是归档 ⇒ 这一趟正常收尾（不是"分卷缺失"）。
            Assert.Equal(RecursionStopReason.Completed, result.StopReason);

            // ④ 用户源目录一个字节不动。
            Assert.True(File.Exists(piece));
            Assert.Equal(4096, new FileInfo(piece).Length);
        }

        [RarVolumeFact]
        public async Task 递归层内_一组伪装的RAR分卷_先还原再组卷能解开到内容()
        {
            RequireSevenZip();
            RequireRar();

            /*
             * 用户点名的那个形状：`风景01.partN.rar删除`。
             * ⚠ RAR 分卷只能由 Rar.exe 造（AGENTS.md §3：Rar.exe 绝不打包），所以这条用例
             * 在没装 WinRAR 的机器上**跳过并说明**，⛔ 不伪装成验过。
             */
            string userDirectory = Path.Combine(_root, "user-rar");
            Directory.CreateDirectory(userDirectory);

            List<string> disguised = BuildDisguisedRarVolumes(userDirectory, "风景01");
            Assert.True(disguised.Count >= 2, $"至少要造出两卷，实际 {disguised.Count} 卷");

            string originalPayload = PayloadPathOfStage(baseName: "风景01", "rar");

            string outer = Path.Combine(userDirectory, "outer-rar.zip");
            Run7z("a", "-tzip", outer, disguised.ToArray());

            Dictionary<string, string> sourceBefore = SnapshotDirectory(userDirectory);

            string output = Path.Combine(_root, "out-rar");
            var logs = new List<string>();

            await ExtractAsync(outer, output, logs);

            FindPayload(output, originalPayload);

            string restoreLine = Assert.Single(logs, line => line.Contains("还原伪装后缀", StringComparison.Ordinal));
            Assert.Contains("风景01.part1.rar" + DisguiseTail + " → 风景01.part1.rar", restoreLine, StringComparison.Ordinal);

            Assert.Equal(sourceBefore, SnapshotDirectory(userDirectory));
        }

        // ───────────────────────── ② 目标名被占 ⇒ 整组一个名字都不改 ─────────────────────────

        [Fact]
        public void 还原目标名已被占_整组一个名字都不改_并且只写一行WARN()
        {
            string directory = Path.Combine(_root, "taken");
            Directory.CreateDirectory(directory);

            string first = WriteFakeArchive(Path.Combine(directory, "风景01.7z.001" + DisguiseTail), SevenZipMagic);
            string second = WriteFakeArchive(Path.Combine(directory, "风景01.7z.002" + DisguiseTail), null);

            // 目标名已经被一份标准名的文件占着（⛔ 绝不覆盖）。
            WriteFakeArchive(Path.Combine(directory, "风景01.7z.001"), null);

            List<string> logs = RunRestore(new[] { first, second });

            // 名字一个都没变。
            Assert.True(File.Exists(first));
            Assert.True(File.Exists(second));
            Assert.False(File.Exists(Path.Combine(directory, "风景01.7z.002")));

            // 一行 WARN，点名"目标名被占"，⛔ 不写"还原成功"。
            string warn = Assert.Single(logs);
            Assert.StartsWith("WARN|", warn, StringComparison.Ordinal);
            Assert.Contains("还原伪装后缀没做", warn, StringComparison.Ordinal);
            Assert.Contains("风景01.7z.001", warn, StringComparison.Ordinal);
        }

        // ───────── ③ 魔数认不出：㈠档（卷标记段被伪装）照旧归一，㈡档（本体后缀被伪装）什么都不做 ─────────

        [Fact]
        public void 魔数认不出_卷标记段被伪装_按规范标记归一()
        {
            /*
             * ⚠ **口径变更（2026-10-08，用户拍板"选项 1"，EEEE 真机 `111(3)\111(3)\111.z0删除2`）**：
             * 旧口径 = "魔数认不出 ⇒ 一个字节都不动"；新口径 = **格式未知时仍允许㈠档**
             * （卷标记段被伪装 —— 规范标记 `zNN`/`partN`/`NNN` 自己就说得清族，压根不需要魔数）。
             *
             * <para>冲突在哪 / 怎么判的：本条原名 `魔数认不出_一个字节都不动_也不写日志`，是旧口径的守门；
             * 用户 2026-10-08 明确选"放宽"，按项目规矩「新指令永远覆盖旧指令」改写为钉新口径。</para>
             *
             * <para>为什么不冲突：它与**源层**那条路本来就是同一口径（
             * <c>VolumeNameRepairTests.孤立一片的脏卷名_骨架算得出就要能归一</c> 早就钉住"孤立一片、无魔数
             * 也要能出改名计划"）；本条只是把递归层对齐过去，⛔ 不是新开一门判据。</para>
             *
             * <para>来由与四件事汇报见 <c>docs/需求变更.md</c>「分族判据整改：剩余清单复核」。</para>
             */
            string directory = Path.Combine(_root, "unknown-mark");
            Directory.CreateDirectory(directory);

            // 名字是"伪装的分卷名"，内容认不出底层 —— 但规范标记 `001` 自己就定了族（7z 数字族）。
            string mystery = Path.Combine(directory, "风景01.7z.001" + DisguiseTail);
            File.WriteAllBytes(mystery, Encoding.UTF8.GetBytes("这不是任何已知归档的头"));

            List<string> logs = RunRestore(new[] { mystery });

            Assert.True(File.Exists(Path.Combine(directory, "风景01.7z.001")));
            Assert.False(File.Exists(mystery));

            string info = Assert.Single(logs);
            Assert.StartsWith("INFO|", info, StringComparison.Ordinal);
        }

        [Fact]
        public void 魔数认不出_本体后缀被伪装_一个字节都不动_也不写日志()
        {
            /*
             * ㈡档（`风景01.zscip` ⇒ `风景01.zip`）"该扣哪个后缀"**完全依赖"认出来是什么格式"** ⇒
             * ⛔ 照旧必须有魔数：格式未知还去猜后缀，就是 2026-09-29 真机把整组改成解不开的那条死路。
             * 这也是本次放宽的**边界**（用户 2026-10-08 的"选项 1"只放宽㈠档）。
             */
            string directory = Path.Combine(_root, "unknown-body");
            Directory.CreateDirectory(directory);

            string mystery = Path.Combine(directory, "风景01.zscip");
            File.WriteAllBytes(mystery, Encoding.UTF8.GetBytes("这不是任何已知归档的头"));

            List<string> logs = RunRestore(new[] { mystery });

            Assert.True(File.Exists(mystery));
            Assert.False(File.Exists(Path.Combine(directory, "风景01.zip")));
            Assert.Empty(logs);
        }

        [Fact]
        public void 名字本来就标准_一个都不改也不写日志()
        {
            string directory = Path.Combine(_root, "standard");
            Directory.CreateDirectory(directory);

            string first = WriteFakeArchive(Path.Combine(directory, "风景01.7z.001"), SevenZipMagic);
            string second = WriteFakeArchive(Path.Combine(directory, "风景01.7z.002"), null);

            List<string> logs = RunRestore(new[] { first, second });

            Assert.True(File.Exists(first));
            Assert.True(File.Exists(second));
            Assert.Empty(logs);
        }

        [Fact]
        public void 续卷没有魔数_靠同组第1卷的魔数认出底层_整组一起还原()
        {
            /*
             * 7z / 跨盘 zip 只有第 1 卷带魔数（中间片是裸切字节流）——
             * "认出底层"这一步必须按组做，否则续卷永远还原不了，整组照样打不开。
             */
            string directory = Path.Combine(_root, "continuation");
            Directory.CreateDirectory(directory);

            string first = WriteFakeArchive(Path.Combine(directory, "风景01.7z.001" + DisguiseTail), SevenZipMagic);
            string second = WriteFakeArchive(Path.Combine(directory, "风景01.7z.002" + DisguiseTail), null);
            string third = WriteFakeArchive(Path.Combine(directory, "风景01.7z.003" + DisguiseTail), null);

            List<string> logs = RunRestore(new[] { first });

            Assert.True(File.Exists(Path.Combine(directory, "风景01.7z.001")));
            Assert.True(File.Exists(Path.Combine(directory, "风景01.7z.002")));
            Assert.True(File.Exists(Path.Combine(directory, "风景01.7z.003")));
            Assert.False(File.Exists(second));
            Assert.False(File.Exists(third));

            string info = Assert.Single(logs);
            Assert.StartsWith("INFO|", info, StringComparison.Ordinal);
        }

        [Fact]
        public void 还原之后的名字_与认出来的底层不同族_不许改()
        {
            /*
             * 名字看着像 RAR 的 `partN.rar`，内容却是 7z —— 扣上 `.part1.rar` 只会更糟
             * （改名不可逆）。族对不上 ⇒ 判"不还原"。
             */
            string directory = Path.Combine(_root, "family-mismatch");
            Directory.CreateDirectory(directory);

            string odd = WriteFakeArchive(Path.Combine(directory, "风景01.part1.rar" + DisguiseTail), SevenZipMagic);

            List<string> logs = RunRestore(new[] { odd });

            Assert.True(File.Exists(odd));
            Assert.False(File.Exists(Path.Combine(directory, "风景01.part1.rar")));
            Assert.Empty(logs);
        }

        // ───────────────────────── 基础设施 ─────────────────────────

        private static readonly byte[] SevenZipMagic = { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C };

        /// <summary>RAR 1.5–4.x 的签名（<c>Rar!\x1A\x07\x00</c>）—— 还原只读魔数，不读整个包。</summary>
        private static readonly byte[] RarMagic = { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 };

        /// <summary>
        /// **还原工序也过同一道「整组自洽」**（用户 2026-10-04）：「末尾段本身脏」的 `444.pa8rt1.rar`
        /// 孤零零一个（同目录里配不出自洽的一组）⇒ **一个名字都不改**（只写一行 WARN）；
        /// 旁边真有 `444.pa8rt2.rar` 配成一组（基名逐字相同 + 卷标记连续 + 除末片外等大）⇒ 两卷都还原。
        ///
        /// <para>⛔ 判据只有一处（`VolumeNameRepair` 里那个 <c>TryConfirmSelfConsistentVolumeGroup</c>），
        /// 批首/手动那条改名链与这里走的是**同一份**。</para>
        /// </summary>
        [Fact]
        public void 还原_末尾段本身脏时要过整组自洽()
        {
            string loneDirectory = Path.Combine(_root, "restore-skeleton-lone");
            Directory.CreateDirectory(loneDirectory);
            string lone = WriteFakeArchive(Path.Combine(loneDirectory, "444.pa8rt1.rar"), RarMagic);

            List<string> loneLogs = RunRestore(new[] { lone });

            Assert.True(File.Exists(lone), "配不出自洽的一组 ⇒ 原样不动");
            Assert.False(File.Exists(Path.Combine(loneDirectory, "444.part1.rar")));
            Assert.Contains(
                loneLogs,
                line => line.StartsWith("WARN|", StringComparison.Ordinal) &&
                        line.Contains(StatusText.VolumeRepairNoSiblings, StringComparison.Ordinal));

            // 对照组：真配得出一组 ⇒ 两卷都还原成标准名。
            string pairDirectory = Path.Combine(_root, "restore-skeleton-pair");
            Directory.CreateDirectory(pairDirectory);
            string p1 = WriteFakeArchive(Path.Combine(pairDirectory, "444.pa8rt1.rar"), RarMagic);
            string p2 = WriteFakeArchive(Path.Combine(pairDirectory, "444.pa8rt2.rar"), RarMagic);

            List<string> pairLogs = RunRestore(new[] { p1 });

            Assert.True(File.Exists(Path.Combine(pairDirectory, "444.part1.rar")), "整组自洽成立 ⇒ 该还原");
            Assert.True(File.Exists(Path.Combine(pairDirectory, "444.part2.rar")));
            Assert.False(File.Exists(p1));
            Assert.False(File.Exists(p2));
            Assert.Contains(
                pairLogs,
                line => line.Contains("444.pa8rt1.rar → 444.part1.rar", StringComparison.Ordinal));
        }

        /// <summary>调一次「还原」（递归层挂点用的那条公开入口），把日志收成 `级别|文案`。</summary>
        private static List<string> RunRestore(IReadOnlyList<string> candidates)
        {
            var logs = new List<string>();

            VolumeNameRepair.RestoreDisguisedInnerPackageNames(
                candidates,
                (level, message) => logs.Add(level + "|" + message));

            return logs;
        }

        /// <summary>写一份"头部是（可空的）归档魔数"的假文件 —— 还原只读魔数，不读整个包。</summary>
        private static string WriteFakeArchive(string path, byte[]? magic)
        {
            using var stream = File.Create(path);

            if (magic != null)
            {
                stream.Write(magic);
            }

            stream.Write(Encoding.UTF8.GetBytes(new string('x', 200)));
            return path;
        }

        private async Task<RecursionResult> ExtractAsync(string archivePath, string outputDirectory, List<string> logs)
        {
            var task = new ArchiveTask(archivePath);
            var extractor = new RecursiveExtractor(
                new SevenZipEngine(),
                new MagicArchiveProber(),
                _ => new[] { string.Empty },
                null,
                (level, message) => logs.Add(level + "|" + message));

            return await extractor.ExtractAsync(
                task,
                outputDirectory,
                RecursionMode.SingleChain,
                null,
                CancellationToken.None);
        }

        /// <summary>造一组真 7z 分卷（64 KiB 一卷），再把每一卷改名成 <c>&lt;基名&gt;.7z.NNN删除</c>。</summary>
        private List<string> BuildDisguisedSevenZipVolumes(string directory, string baseName)
        {
            string stage = Path.Combine(_root, "stage-" + baseName + "-7z");
            Directory.CreateDirectory(stage);

            string payload = WritePayload(stage);
            string volumeBase = Path.Combine(stage, baseName + ".7z");

            Run7z("a", "-t7z", "-v64k", volumeBase, payload);

            return RenameVolumesInto(directory, stage, baseName + ".7z.*");
        }

        /// <summary>造一组真 RAR 分卷（<c>Rar.exe</c>，RAR5 ⇒ <c>partN.rar</c>），再缀上伪装尾巴。</summary>
        private List<string> BuildDisguisedRarVolumes(string directory, string baseName)
        {
            string stage = Path.Combine(_root, "stage-" + baseName + "-rar");
            Directory.CreateDirectory(stage);

            string payload = WritePayload(stage);
            string volumeBase = Path.Combine(stage, baseName + ".rar");

            RunProcess(_rar, new[] { "a", "-ma5", "-v64k", "-idq", volumeBase, payload }, stage);

            return RenameVolumesInto(directory, stage, baseName + ".part*.rar");
        }

        /// <summary>
        /// 造"只有第 1 层才有"的内容：**开头一个可读标记 + 200 KiB 随机字节**。
        ///
        /// <para>⚠ 随机字节是刻意的：分卷内容必须**压不动**，否则 200 KiB 的重复文本会被压成
        /// 一卷（64 KiB 都填不满），那就造不出"真分卷组"、这条用例也就验不到组卷。</para>
        /// </summary>
        private string WritePayload(string directory)
        {
            string payload = Path.Combine(directory, "payload.txt");

            var bytes = new List<byte>();
            bytes.AddRange(Encoding.UTF8.GetBytes(PayloadMarker + "\n"));

            var random = new byte[200 * 1024];
            Random.Shared.NextBytes(random);
            bytes.AddRange(random);

            File.WriteAllBytes(payload, bytes.ToArray());
            return payload;
        }

        /// <summary>把造好的分卷按 <paramref name="pattern"/> 搬到目标目录并缀上伪装尾巴，返回（新路径）清单。</summary>
        private static List<string> RenameVolumesInto(string directory, string stage, string pattern)
        {
            var renamed = new List<string>();

            foreach (string volume in Directory.GetFiles(stage, pattern)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                string target = Path.Combine(directory, Path.GetFileName(volume) + DisguiseTail);
                File.Move(volume, target);
                renamed.Add(target);
            }

            return renamed;
        }

        /// <summary>造样本时留在 stage 目录里的原始 payload（用来逐字节比对解出来的那一份）。</summary>
        private string PayloadPathOfStage(string baseName, string family) =>
            Path.Combine(_root, "stage-" + baseName + "-" + family, "payload.txt");

        private static string FindPayload(string outputRoot, string originalPayload)
        {
            string[] payloads = Directory.GetFiles(outputRoot, "payload.txt", SearchOption.AllDirectories);

            Assert.True(payloads.Length == 1, $"应该恰好解出一份 payload.txt，实际 {payloads.Length} 份（目录：{outputRoot}）");

            byte[] bytes = File.ReadAllBytes(payloads[0]);
            byte[] marker = Encoding.UTF8.GetBytes(PayloadMarker);

            Assert.True(
                bytes.AsSpan().IndexOf(marker) >= 0,
                "解出来的 payload.txt 里找不到那一层才有的内容标记（说明解出来的不是我们要的那一份）。");

            /*
             * ⚠ 必须**逐字节相同**，不能只看"标记在不在"：标记在文件开头，只解出第一卷（64 KiB）
             * 时它也照样在 —— 那种"半份"正是"整组没拼起来"的现场。
             */
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(originalPayload))),
                Convert.ToHexString(SHA256.HashData(bytes)));

            return payloads[0];
        }

        /// <summary>目录快照：文件名 → 大小+SHA256（用例④"用户源目录一个字节不动"的判据）。</summary>
        private static Dictionary<string, string> SnapshotDirectory(string directory)
        {
            var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (string path in Directory.GetFiles(directory))
            {
                using FileStream stream = File.OpenRead(path);
                snapshot[Path.GetFileName(path)] = new FileInfo(path).Length + ":" + Convert.ToHexString(SHA256.HashData(stream));
            }

            return snapshot;
        }

        private void RequireSevenZip()
        {
            if (string.IsNullOrEmpty(_sevenZip))
            {
                throw new InvalidOperationException("找不到内置 7z.exe，且 [SevenZipFact] 没有把它跳过：环境与特性探测结果不一致。");
            }
        }

        private void RequireRar()
        {
            if (string.IsNullOrEmpty(_rar))
            {
                throw new InvalidOperationException("找不到 Rar.exe，且 [RarFact] 没有把它跳过：环境与特性探测结果不一致。");
            }
        }

        private void Run7z(params object[] args)
        {
            var expanded = new List<string>();

            foreach (object arg in args)
            {
                if (arg is IEnumerable<string> many)
                {
                    expanded.AddRange(many);
                }
                else
                {
                    expanded.Add(arg.ToString() ?? string.Empty);
                }
            }

            RunProcess(_sevenZip, expanded, _root);
        }

        private void RunProcess(string executable, IReadOnlyList<string> args, string workingDirectory)
        {
            var psi = new ProcessStartInfo(executable)
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

            using Process process = Process.Start(psi)
                ?? throw new InvalidOperationException($"无法启动 {Path.GetFileName(executable)}");

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(120_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // 已经退了就无所谓。
                }

                throw new InvalidOperationException($"造样本超时：{Path.GetFileName(executable)} {string.Join(' ', psi.ArgumentList)}");
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"造样本失败（exit {process.ExitCode}）：{Path.GetFileName(executable)} {string.Join(' ', psi.ArgumentList)}\n{stdout}\n{stderr}");
            }
        }
    }
}

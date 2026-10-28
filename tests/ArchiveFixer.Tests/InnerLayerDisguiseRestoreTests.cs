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




        // ───────────────────────── ② 目标名被占 ⇒ 整组一个名字都不改 ─────────────────────────


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

        /// <summary>
        /// **卷标记自己那一截被撑坏**（`part删删删1`：数字已经挨不着 `part`、边界拆不出来）时，
        /// 「纯文本清洗」那一档照样要把名字还原成 `part1.rar`。
        ///
        /// <para>用户口径（2026-10-11）：「简单的改后缀，像删除中文字符这么简单的为什么做不了」。</para>
        ///
        /// <para><b>现场</b>（2026-10-13 困难批 `11111111` 真机日志逐字）：同一批的
        /// `222-L2.part2.中文rar → 222-L2.part2.rar` 改得好好的，而 `222-L2.part删删删1.rar`
        /// **一次改名行都没有**；探针实测差别在第一步 —— `TryResolveVolumeBaseName(…, DisguisedVolume)`
        /// 对前者 True、对后者 **False** ⇒ 那一档整档不做 ⇒ 那一组的入口包在盘上永远不叫
        /// `222-L2.part1.rar` ⇒ 判据输入「产出它的那一单=判不出」⇒「推不出这一组该拼到哪一层」⇒
        /// 接片一个字节都不动 ⇒ 整组永远凑不齐 ⇒ ①「内容物解出来」红。</para>
        ///
        /// <para><b>红检</b>：把「纯 ASCII 基名逐点试拆」那一档撤掉 ⇒ 本条变红（盘上还是脏名）。</para>
        /// </summary>
        [Fact]
        public void 还原_卷标记自己那一段被撑坏_纯文本清洗照样还原()
        {
            string directory = Path.Combine(_root, "restore-marker-smashed");
            Directory.CreateDirectory(directory);
            string smashed = WriteFakeArchive(Path.Combine(directory, "222-L2.part删删删1.rar"), RarMagic);
            string tailOnly = WriteFakeArchive(Path.Combine(directory, "333-L2.part2.中文rar"), RarMagic);

            List<string> logs = RunRestore(new[] { smashed, tailOnly });

            Assert.False(File.Exists(smashed), "卷标记被撑坏的那一份也该还原成 part1.rar");
            Assert.True(File.Exists(Path.Combine(directory, "222-L2.part1.rar")));
            Assert.Contains(logs, line => line.Contains("222-L2.part删删删1.rar → 222-L2.part1.rar", StringComparison.Ordinal));

            // 老口径照旧（对照组）：脏字符只在归档后缀那一段里 ⇒ 一样还原。
            Assert.True(File.Exists(Path.Combine(directory, "333-L2.part2.rar")));

            /*
             * ⛔ 新那一档是**按点枚举**的，必须自己把住"不许拼出空点段"：
             * `444-L2.中.rar` 若被去成 `.rar`，拼起来就是 `444-L2..rar`（谁都不是那个形态）。
             */
            string hollow = WriteFakeArchive(Path.Combine(directory, "444-L2.中.rar"), RarMagic);

            RunRestore(new[] { hollow });

            Assert.DoesNotContain(
                Directory.GetFiles(directory),
                path => Path.GetFileName(path).Contains("..", StringComparison.Ordinal));
        }

        /// <summary>
        /// **纯字母数字的"垃圾"挤在卷号前面**时也要还原（2026-10-13 下午，困难批 `1111` 取证）：
        /// `111-L2.7z.yvA001` ⇒ `111-L2.7z.001`。
        ///
        /// <para>现场逐字（`run-1111`）：造包细则第 ② 法的字符池里**有字母**，于是
        /// `111-L2.7z.001` 被写成 `111-L2.7z.yvA001`；链里试开它逐字
        /// `第 1 层：111-L2.7z.yvA001：解压失败：7-Zip 无法识别或不支持该格式`
        /// ⇒ 那一组的入口包永远不叫规范名 ⇒ 整组凑不齐 ⇒ ①红。</para>
        ///
        /// <para><b>红检</b>：把「纯字母数字垃圾 + 上下文」那一档撤掉 ⇒ 本条变红（盘上还是脏名）。</para>
        /// </summary>
        [Fact]
        public void 还原_纯字母数字垃圾挤在卷号前面_也要还原()
        {
            string directory = Path.Combine(_root, "restore-ascii-junk-marker");
            Directory.CreateDirectory(directory);
            string smuggled = WriteFakeArchive(Path.Combine(directory, "111-L2.7z.yvA001"), SevenZipMagic);

            RunRestore(new[] { smuggled });

            Assert.False(File.Exists(smuggled), "纯字母数字垃圾挤在卷号前的那一份也该还原成 .001");
            Assert.True(File.Exists(Path.Combine(directory, "111-L2.7z.001")));

            /*
             * ⛔ 反向：真名字（`backup-002` 这种）不许被吃 —— 名字里没有"<后缀>.<垃圾><卷号>"那个上下文
             * ⇒ 判不出 ⇒ 一个字节都不动。
             */
            string realName = WriteFakeArchive(Path.Combine(directory, "backup-002"), SevenZipMagic);

            RunRestore(new[] { realName });

            Assert.True(File.Exists(realName), "`backup-002` 是正常名字，不许动");
        }

        /// <summary>
        /// **底层识别的专属算法：这一份到底是"完整的单卷归档"还是"某分卷组的第一片"**
        /// （用户 2026-10-13 口径：「『名字被伪装到读不出卷号 ⇒ 组凑不齐』这个是**底层识别**的问题，
        /// 赶快升级修改专属算法（单一和分卷）」）。
        ///
        /// <para>7z 那一族格式里没有卷号 ⇒ 只能由**起始头明文自述的整包字节数**回答：
        /// 自述 **≤** 自己 ⇒ 完整单卷；自述 **&gt;** 自己 ⇒ 这一组的第一片，而且片数也算得出来
        /// （整包 ÷ 这一片，向上取整）。用**真 7z** 造两份对照夹具（同一份内容：一份切成 64 KiB 分卷、
        /// 一份不切）实测。</para>
        ///
        /// <para><b>红检</b>：把 `FileNature.Inspect` 里那一段专属算法撤掉 ⇒ 本条变红（两档都成 Unknown）。</para>
        /// </summary>
        [Fact]
        public void 底层识别_单一与分卷的专属算法_按内容自证()
        {
            string stage = Path.Combine(_root, "single-vs-volume");
            Directory.CreateDirectory(stage);

            string payload = WritePayload(stage);
            string single = Path.Combine(stage, "pack111.7z");
            string volumeBase = Path.Combine(stage, "vol111.7z");

            Run7z("a", "-t7z", single, payload);
            Run7z("a", "-t7z", "-v64k", volumeBase, payload);

            FileNatureCard singleCard = FileNature.Inspect(single);
            Assert.Equal(ArchiveBodyKind.SingleComplete, singleCard.BodyKind);
            Assert.True(
                singleCard.DeclaredTotalBytes > 0 && singleCard.DeclaredTotalBytes <= singleCard.Size,
                "完整单卷：起始头自述的整包字节数应当 ≤ 自己");

            string firstVolume = volumeBase + ".001";
            Assert.True(File.Exists(firstVolume), "7z -v 应当切出 .001");

            FileNatureCard firstCard = FileNature.Inspect(firstVolume);
            Assert.Equal(ArchiveBodyKind.VolumeFirstPiece, firstCard.BodyKind);
            Assert.True(firstCard.DeclaredTotalBytes > firstCard.Size, "第一片：自述整包字节数应当大于自己");
            Assert.Equal(0, firstCard.DeclaredVolumeCount); // 7z 没有片数字段，非均匀时不能按等大反推

            // ⛔ 中间片与"判不出"那一档：内容里没有身份信息 ⇒ 只能 Unknown（不许猜成"不是分卷"）。
            FileNatureCard middleCard = FileNature.Inspect(volumeBase + ".002");
            Assert.Equal(ArchiveBodyKind.Unknown, middleCard.BodyKind);
            Assert.True(middleCard.ParticipatesInVolumeHandling, "判不出照旧参与分卷那一套");
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

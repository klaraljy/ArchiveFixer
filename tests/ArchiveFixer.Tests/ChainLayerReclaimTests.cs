using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using ArchiveFixer.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **普通档「逐层回收」**（用户 2026-10-03 拍板的大改）在真链路端到端的行为。
    ///
    /// <para>规格书：`_tmp\方案-普通档逐层回收其余物.md`（§1 目标 / §3 七条红线 / §5 用例 A~D /
    /// §7 B 档位分叉 + §7.1 用例 E~F）。这里钉四件事：</para>
    /// <list type="number">
    /// <item><description><b>用例 A</b>：普通档 + 「彻底删除」⇒ **每层当场回收**这一层的内层包（走"删这一层的源"
    /// 那个执行体），最外层源包留到链尾才被处理；峰值 = 源包 + 当前层 + 下一层（≤ 3）。</description></item>
    /// <item><description><b>用例 B</b>：中间某一层失败 ⇒ **那一层的过程物一个字节都不动**、源包原地不动、
    /// 其余物不生成；已成功回收的上层不回滚（用户明确接受）。</description></item>
    /// <item><description><b>用例 C</b>：「空间不足」模式**一个字不改** —— 仍每层各删各的（含最外层源包），
    /// 峰值 ≤ 2 倍源包（用户给的新判据）。</description></item>
    /// <item><description><b>用例 E（B 档位分叉）**：⛔ **2026-10-15 已删** —— 它钉的是③页「移入回收站」
    /// 那一档，而那一档整条下线（用户口径：已经不存在放入回收站了）。</description></item>
    /// </list>
    ///
    /// <para><b>为什么必须用真 7z</b>（AGENTS.md §11 验收规则）：合成假引擎证明得了"我按自己以为的形状处理得对"，
    /// 证明不了"真的 7z 一层层套出来的东西我处理得对"。所以整条链都当真：真 7z.exe 造包、
    /// 真 <see cref="SevenZipEngine"/>、真 <see cref="MainViewModel"/> 与真的解压管线。</para>
    ///
    /// <para><b>⛔ 两条自我约束</b>：① 绝不往**用户的系统回收站**里塞东西 —— 回收站那一档全部走注入的假执行器
    /// （<c>ExtractionCoordinator.RestDeleteExecutor</c> 这个测试缝）；② 只在临时目录里动真实文件系统，
    /// <see cref="Dispose"/> 里清干净。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class ChainLayerReclaimTests : IDisposable
    {
        /// <summary>拿它给某一层加密码、密码本里没有 ⇒ 那一层必然失败（用例 B）。</summary>
        private const string UnknownLayerPassword = "测试用未知密码";

        /// <summary>③页「删除操作 = 彻底删除」那一档（唯一决定"逐层回收开不开"的事实位就是它）。</summary>
        private const string PermanentDeleteRestMode = RestHandlingModes.Delete;

        private readonly string _root;
        private readonly string _sevenZip;
        private readonly ITestOutputHelper _output;

        public ChainLayerReclaimTests(ITestOutputHelper output)
        {
            _output = output;
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerLayerReclaim", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _sevenZip = SevenZipFactAttribute.LocateSevenZipPath();
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
                // 临时目录清不掉不影响测试结论。
            }
        }

        // ================================================================ 用例 A（逐层回收）


        // ================================================================ 用例 A2（新口径：无条件）


        // ================================================================ 用例 A3（「最后一层过程物」按档留/删）



        // ================================================================ 用例 B（失败那一层不动）


        /*
         * ⛔ 2026-10-15：原来这里有两条用例（用例 B2 / 用例 E），钉的是③页「移入回收站」那一档
         * 在逐层回收上的行为（B2 = 那一档不逐层、链尾整份一次进回收站；E = 2026-10-12 改口径后
         * 那一档**也逐层**当场进回收站）。回收站那一档本身**整条下线**（用户口径逐字：
         * 「我们这里现在只有留下和彻底删除操作，已经不存在放入回收站了」，见 `RestHandlingModes`）
         * ⇒ 这两条钉的功能已经不存在，按新现实整条撤掉，⛔ 不是把判据放宽。
         * 现在钉"逐层回收"的是：用例 A（彻底删除档逐层永久删）、用例 C（空间不足模式）、
         * 用例 R1/R2/R4（递归路那几档）。
         */

        // ================================================================ 用例 C（空间不足模式不变）

        // ================================================================ 用例 E（已删，见上面那段说明）

        // ================================================================ 用例 G（半套分卷 ⇒ 一个字节都不删）


        // ================================================================ 用例 H / I（第二轮复核逮出的两条放行路径）




        // ================================================================ 用例 J（定稿搬运失败：原因必须让用户看得见）



        /// <summary>
        /// 用例 J 的夹具：`outer.7z → level2.7z → c01..c06.txt`，续解层那 6 个落点被独占句柄占住
        /// ⇒ 定稿搬运 6 条全失败。返回（夹具、失败的那一单、最外层源包路径）。
        /// </summary>
        private async Task<(Harness Harness, ArchiveTask Failed, string Outer)> RunFinalizeMoveFailureAsync()
        {
            Harness harness = CreateHarness(
                RestHandlingModes.Delete,
                SourceHandlingMode.KeepInPlace,
                settings => settings.ConflictAction = ConflictActions.Overwrite);

            string outer = BuildFinalizeFailureChain();

            string layerDirectory = Path.Combine(_root, "out", "outer", "level2");
            Directory.CreateDirectory(layerDirectory);

            var holds = new List<FileStream>();

            try
            {
                for (int i = 1; i <= 6; i++)
                {
                    string placeholder = Path.Combine(layerDirectory, $"c{i:00}.txt");
                    File.WriteAllText(placeholder, "占位（不是解压产物）\n", new UTF8Encoding(false));
                    holds.Add(new FileStream(placeholder, FileMode.Open, FileAccess.Read, FileShare.None));
                }

                await harness.AddPathsAsync(outer);

                await harness.RunOneClickAsync().WaitAsync(TimeSpan.FromSeconds(180));
            }
            finally
            {
                foreach (FileStream hold in holds)
                {
                    hold.Dispose();
                }
            }

            ArchiveTask failed = Assert.Single(harness.Vm.Tasks, task => task.IsContinuationTask);

            return (harness, failed, outer);
        }

        /// <summary>
        /// 夹具：<c>outer.7z → level2.7z → c01..c06.txt</c>，而且**最外层自己也有内容物**
        /// （`layer1.txt`）⇒ 续解那一层按既有判据**不另建层**（父层已产出内容物），
        /// 它那 6 个文件的落点 = 父层内容目录下面那一层（`out\outer\level2\`）。
        /// </summary>
        private string BuildFinalizeFailureChain()
        {
            string build = Path.Combine(_root, "ff-build");
            Directory.CreateDirectory(build);

            for (int i = 1; i <= 6; i++)
            {
                File.WriteAllText(
                    Path.Combine(build, $"c{i:00}.txt"),
                    $"第 2 层的内容 {i}\n",
                    new UTF8Encoding(false));
            }

            Run7z(build, "a", "-t7z", "level2.7z", "c01.txt", "c02.txt", "c03.txt", "c04.txt", "c05.txt", "c06.txt");

            File.WriteAllText(Path.Combine(build, "layer1.txt"), "第 1 层自己的内容\n", new UTF8Encoding(false));

            string sourceDirectory = Path.Combine(_root, "src");
            Directory.CreateDirectory(sourceDirectory);

            string outer = Path.Combine(sourceDirectory, "outer.7z");
            Run7z(build, "a", "-t7z", outer, "level2.7z", "layer1.txt");

            Directory.Delete(build, recursive: true);

            return outer;
        }

        // ================================================================ 用例 K（措辞与事实对齐：用户 2026-10-04 真机）




        /// <summary>
        /// 夹具（用例 K1）：<c>outer.7z → level2.7z + movie.mp4</c> —— 这一层里**只有 1 个**内层归档，
        /// 旁边有个**非说明类**文件（`.mp4`）。真机那批（`第6集.7z`）就是这个形状：
        /// ⚠ 2026-10-04 起它**不再**是停下来的理由（判据只看内层归档的数量，1 个一律继续解）。
        /// </summary>
        private string BuildSingleInnerWithSiblingChain()
        {
            string build = Path.Combine(_root, "k1-build");
            Directory.CreateDirectory(build);

            File.WriteAllText(Path.Combine(build, "payload.txt"), "内层的内容\n", new UTF8Encoding(false));
            Run7z(build, "a", "-t7z", "level2.7z", "payload.txt");
            File.Delete(Path.Combine(build, "payload.txt"));

            File.WriteAllBytes(Path.Combine(build, "movie.mp4"), new byte[4096]);

            string sourceDirectory = Path.Combine(_root, "src");
            Directory.CreateDirectory(sourceDirectory);

            string outer = Path.Combine(sourceDirectory, "outer.7z");
            Run7z(build, "a", "-t7z", outer, "level2.7z", "movie.mp4");

            Directory.Delete(build, recursive: true);

            return outer;
        }

        /// <summary>
        /// 夹具（用例 K2）：<c>outer.7z → level2.7z →（level3.7z + level4.7z + movie.mp4）</c> ——
        /// 多分支出现在**第 1 层**（更深的层）⇒ 一键档不弹框、停因是 <c>BranchNotExpanded</c>。
        ///
        /// <para>⚠ 2026-10-04：原来那一层的"多分支"是 **1 个内层包 + 一个 `.mp4`**（旧判据）；
        /// 用户当场推翻之后判据只看**内层归档的数量**（1 个一律继续解）⇒ 那个夹具不再会停，
        /// 本用例也就测不到"停半路要给两条出路"。现在这一层放**两个**内层包，才真的构成多分支。</para>
        /// </summary>
        private string BuildDeepBranchChain()
        {
            string build = Path.Combine(_root, "k2-build");
            Directory.CreateDirectory(build);

            File.WriteAllText(Path.Combine(build, "leaf.txt"), "最深一层\n", new UTF8Encoding(false));
            Run7z(build, "a", "-t7z", "level3.7z", "leaf.txt");
            File.Delete(Path.Combine(build, "leaf.txt"));

            File.WriteAllText(Path.Combine(build, "leaf2.txt"), "另一个分支\n", new UTF8Encoding(false));
            Run7z(build, "a", "-t7z", "level4.7z", "leaf2.txt");
            File.Delete(Path.Combine(build, "leaf2.txt"));

            File.WriteAllBytes(Path.Combine(build, "movie.mp4"), new byte[4096]);

            Run7z(build, "a", "-t7z", "level2.7z", "level3.7z", "level4.7z", "movie.mp4");
            File.Delete(Path.Combine(build, "level3.7z"));
            File.Delete(Path.Combine(build, "level4.7z"));
            File.Delete(Path.Combine(build, "movie.mp4"));

            string sourceDirectory = Path.Combine(_root, "src");
            Directory.CreateDirectory(sourceDirectory);

            string outer = Path.Combine(sourceDirectory, "outer.7z");
            Run7z(build, "a", "-t7z", outer, "level2.7z");

            Directory.Delete(build, recursive: true);

            return outer;
        }

        // ================================================================ 用例 D（三处消费点同一数字）


        // ================================================================ 用例 F（留下档：残渣没有、源包还在、过程物没了）


        // ================================================================ 只读实测（② 的判据来源）


        // ================================================================ 造样本 / 断言辅助

        /// <summary>
        /// 只读：这一单的**精估放行需求**（真清单）—— 用来算「真实峰值 / 放行需求」这个比值
        /// （用户 2026-10-03 要求先报事实：需求是按逐层回收的净增量算的，而有的档不逐层回收）。
        /// </summary>
        private async Task<long> MeasureDemandAsync(string outer)
        {
            var task = new ArchiveTask(outer, 1) { FileName = "outer.7z" };

            TaskSpaceEstimate cheap = SpaceEstimator.FromSourceFiles(task);

            ArchiveListResult list = await new SevenZipEngine().ListAsync(
                ArchiveRequest.For(outer),
                CancellationToken.None);

            Assert.True(list.Success, list.Message);

            return SpaceEstimator.RefineWithListing(cheap, list).FreeSpaceDemandBytes;
        }

        /// <summary>
        /// **用例 G / H / H2 共用的夹具**（复刻 §44.2）：`outer.7z` 里装着
        /// 「一组真 7z 分卷 `mid.7z.001/.002/...`」＋「同组一片、名字被改坏的 `mid.7z.0删除02`」＋`layer1.txt`。
        ///
        /// <para>那一组里是第 2 层的内容（`final.txt` + `layer2.txt` + 5 KiB 不可压数据，保证真的切成多片）；
        /// 名字被改坏的那一片取"**卷标记里夹垃圾**"那种坏法（用户 2026-09-28 真机 `amb909.7sz.00c1` 那一类）
        /// —— 它与 `mid.7z.001` **同族同基名**，闸门认得出它跟分卷是一组
        /// （⚠ 2026-10-10 起判据是"基名 + 族"：原来那个 `mid.z删除ip` 是 zip 族、与 7z 分卷**可证跨族**，
        /// 用它会把这套夹具的前提写坏 ⇒ 三条用例全红，见下面写入点那段注释）。</para>
        /// </summary>
        private string BuildSplitVolumeChain(int nestedDepth = 1)
        {
            string build = Path.Combine(_root, "chain-build");
            Directory.CreateDirectory(build);

            File.WriteAllText(Path.Combine(build, "final.txt"), "最深一层的内容\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(build, "layer2.txt"), "第 2 层自己的内容\n", new UTF8Encoding(false));

            // 5 KiB 不可压数据：`-v2k` 下必然切成多片（不然只会出一片，就不是"一组分卷"了）。
            byte[] blob = new byte[5 * 1024];
            new Random(20261003).NextBytes(blob);
            File.WriteAllBytes(Path.Combine(build, "blob.bin"), blob);

            Run7z(build, "a", "-t7z", "-mx0", "-v2k", "mid.7z", "final.txt", "layer2.txt", "blob.bin");

            /*
             * 三档形状（**只看"候选离那两条分支的共同祖先有多远"**）：
             *
             * · `nestedDepth = 1`（用例 G）：分卷组与另一片各在一层里（`pieces\` vs `stray\`）。
             * · `nestedDepth = 2`（用例 H）：组在 `AAA\BBB\`、另一片在**同级另一条分支** `CCC\` ——
             *   第一版套了 `pieces\AAA\BBB\`（候选比共同祖先深三层）时，祖先链上溯三层刚好差一层、
             *   扫不到 `CCC\` ⇒ 用例红（实测踩过这一脚），所以这一档刻意只套两层。
             * · `nestedDepth = 3`（用例 H2，2026-10-03 第三轮）：组在 `X\AAA\BBB\`、另一片在
             *   **另一条分支** `Y\CCC\` —— 候选离共同祖先（那个同时含 `X` 与 `Y` 的目录）**四层**，
             *   3 层上限必然漏扫（这正是用户批准「取消 3 层上限」要关掉的那个口子）。
             */
            string piecesDirectory = nestedDepth switch
            {
                <= 1 => Path.Combine(build, "pieces"),
                2 => Path.Combine(build, "AAA", "BBB"),
                _ => Path.Combine(build, "X", "AAA", "BBB")
            };

            string strayDirectory = nestedDepth switch
            {
                <= 1 => Path.Combine(build, "stray"),
                2 => Path.Combine(build, "CCC"),
                _ => Path.Combine(build, "Y", "CCC")
            };

            Directory.CreateDirectory(piecesDirectory);
            Directory.CreateDirectory(strayDirectory);

            var pieces = new List<string>();

            foreach (string piece in Directory.GetFiles(build, "mid.7z.*"))
            {
                string target = Path.Combine(piecesDirectory, Path.GetFileName(piece));
                File.Move(piece, target);
                pieces.Add(target);
            }

            Assert.True(pieces.Count >= 2, "`-v2k` 下应当切出至少两片，实际 " + pieces.Count + " 片");

            /*
             * 名字被改坏的同组一片（§44.2：`一只顶美.z删除ip` 那种）—— **必须与分卷同一族**：
             * 判据 2026-10-10 起是"基名 + 族"（用户拍板，跨族同基名不再算同组），
             * 而这里的组是 **7z 数字分卷族**（`mid.7z.001`）⇒ 这一片只能用**卷标记里夹垃圾**那种坏法
             * （`mid.7z.0删除02`）。
             * ⚠ 原来写的是 `mid.z删除ip`（zip 族）—— 探针实测 `VolumeGroupDetector` 对它与 `mid.7z.001`
             * 判「可证跨族=True」，也就是**它压根不在这一组里**，夹具的前提本身就不成立（于是新判据一落地
             * 这三条用例就红）。⛔ 别改回 zip 族那个名字。
             */
            File.WriteAllText(
                Path.Combine(strayDirectory, "mid.7z.0删除02"),
                "名字被改坏的那一片（占位，不是真归档）\n",
                new UTF8Encoding(false));

            File.WriteAllText(Path.Combine(build, "layer1.txt"), "第 1 层自己的内容\n", new UTF8Encoding(false));

            string sourceDirectory = Path.Combine(_root, "src");
            Directory.CreateDirectory(sourceDirectory);

            string outer = Path.Combine(sourceDirectory, "outer.7z");

            var outerArgs = new List<object> { "a", "-t7z", outer };

            foreach (string piece in pieces)
            {
                outerArgs.Add(Path.GetRelativePath(build, piece));
            }

            outerArgs.Add(Path.GetRelativePath(build, Path.Combine(strayDirectory, "mid.7z.0删除02")));
            outerArgs.Add("layer1.txt");

            Run7z(build, outerArgs.ToArray());

            Directory.Delete(build, recursive: true);

            return outer;
        }

        /// <summary>
        /// 造一条嵌套链：<c>outer.7z → level2.7z → … → levelN.7z → final.txt</c>，
        /// **每一层都另外带一个自己的内容文件**（`layerN.txt`）—— 真实机器上的包大多如此，
        /// 而且这样最外层那一单第 1 轮就有内容物可定稿（源包搬运因此走"本轮直接搬"）。
        ///
        /// <para><paramref name="passwordLevel"/> 指定的那一层用密码包（<c>-mhe=on</c>）造，
        /// 密码刻意不进密码本 ⇒ 轮到那一层必然失败（用例 B 用）。
        /// <paramref name="passwordOuter"/> = true ⇒ **最外层源包自己**用密码包造
        /// ⇒ 根源包那一层就失败（用例 K3 用：只有这一档才会触发"其余物没处理"那句点名）。</para>
        /// </summary>
        /// <summary>
        /// 造一条嵌套链（旧签名，等价于"在当前临时根里造"）：<c>outer.7z → level2.7z → … → levelN.7z → final.txt</c>，
        /// **每一层都另外带一个自己的内容文件**（`layerN.txt`）—— 真实机器上的包大多如此，
        /// 而且这样最外层那一单第 1 轮就有内容物可定稿（源包搬运因此走"本轮直接搬"）。
        ///
        /// <para><paramref name="passwordLevel"/> 指定的那一层用密码包（<c>-mhe=on</c>）造，
        /// 密码刻意不进密码本 ⇒ 轮到那一层必然失败（用例 B 用）。
        /// <paramref name="passwordOuter"/> = true ⇒ **最外层源包自己**用密码包造
        /// ⇒ 根源包那一层就失败（用例 K3 用：只有这一档才会触发"其余物没处理"那句点名）。</para>
        /// </summary>
        private string BuildChain(int levelCount, int? passwordLevel = null, bool passwordOuter = false) =>
            BuildChainInto(_root, levelCount, passwordLevel, passwordOuter);

        /// <summary>
        /// 「造一条嵌套链」的**唯一实现**（<paramref name="rootDirectory"/> 决定样本落在哪个临时根里）。
        ///
        /// <para>为什么要带根：递归路那一组用例要**跑两遍对照**（逐层回收开 / 关），
        /// 两遍必须各有各的临时根 —— 共用一份会让第二遍看到第一遍留下的其余物与工作区，
        /// 而"两遍产物逐字节相同"那条判据正是靠"两遍互不干扰"才成立的。</para>
        /// </summary>
        private string BuildChainInto(
            string rootDirectory,
            int levelCount,
            int? passwordLevel = null,
            bool passwordOuter = false)
        {
            Assert.True(levelCount >= 2, "至少要有 outer + 一层内层包");

            string build = Path.Combine(rootDirectory, "chain-build");
            Directory.CreateDirectory(build);

            File.WriteAllText(Path.Combine(build, "final.txt"), "最深一层的内容\n", new UTF8Encoding(false));

            for (int level = levelCount; level >= 2; level--)
            {
                File.WriteAllText(
                    Path.Combine(build, $"layer{level}.txt"),
                    $"第 {level} 层自己的内容\n",
                    new UTF8Encoding(false));

                string inner = level == levelCount ? "final.txt" : $"level{level + 1}.7z";

                if (passwordLevel == level)
                {
                    Run7z(build, "a", "-t7z", $"level{level}.7z", "-p" + UnknownLayerPassword, "-mhe=on", inner, $"layer{level}.txt");
                }
                else
                {
                    Run7z(build, "a", "-t7z", $"level{level}.7z", inner, $"layer{level}.txt");
                }
            }

            File.WriteAllText(Path.Combine(build, "layer1.txt"), "第 1 层自己的内容\n", new UTF8Encoding(false));

            string sourceDirectory = Path.Combine(rootDirectory, "src");
            Directory.CreateDirectory(sourceDirectory);

            string outer = Path.Combine(sourceDirectory, "outer.7z");

            if (passwordOuter)
            {
                Run7z(build, "a", "-t7z", outer, "-p" + UnknownLayerPassword, "-mhe=on", "level2.7z", "layer1.txt");
            }
            else
            {
                Run7z(build, "a", "-t7z", outer, "level2.7z", "layer1.txt");
            }

            Directory.Delete(build, recursive: true);

            return outer;
        }

        private void Run7z(string workingDirectory, params object[] args)
        {
            var psi = new ProcessStartInfo(_sevenZip)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDirectory
            };

            foreach (object a in args)
            {
                psi.ArgumentList.Add(a.ToString() ?? string.Empty);
            }

            using Process p = Process.Start(psi)!;
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(120_000);

            if (p.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"7z 失败（exit {p.ExitCode}）：{string.Join(' ', psi.ArgumentList)}\n{stdout}\n{stderr}");
            }
        }

        private static bool IsNamed(string path, string fileName) =>
            string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase);

        private static string[] EnumerateAll(string root) =>
            Directory.Exists(root)
                ? Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories)
                : Array.Empty<string>();

        /// <summary>在本次测试的临时根下面按文件名找文件（包会被搬到别的目录，所以按名字找）。</summary>
        private string[] FindFiles(string pattern) =>
            Directory.Exists(_root)
                ? Directory.GetFiles(_root, pattern, SearchOption.AllDirectories)
                : Array.Empty<string>();

        private string[] FindDirectories(string name) =>
            Directory.Exists(_root)
                ? Directory.GetDirectories(_root, name, SearchOption.AllDirectories)
                : Array.Empty<string>();

        private Harness CreateHarness(
            string restMode,
            SourceHandlingMode sourceMode,
            Action<AppSettings>? tweak = null,
            IArchiveEngine? engineOverride = null)
        {
            string dataRoot = Path.Combine(_root, "data");
            string outputRoot = Path.Combine(_root, "out");

            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(outputRoot);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var settingsService = new SettingsService(pathService);

            AppSettings settings = AppSettings.CreateDefault();
            settings.CustomOutputDirectory = outputRoot;
            settings.ExtractToOriginalDirectory = false;
            settings.KeepArchiveNameFolder = true;
            settings.AutoScanAfterDrop = false;
            settings.TryEmptyPasswordFirst = false;
            settings.SourceHandling = sourceMode.ToString();
            settings.RestHandlingAfterVerify = restMode;
            settings.CustomSevenZipExePath = string.Empty;

            tweak?.Invoke(settings);

            settingsService.Save(settings);

            var engine = engineOverride ?? new SevenZipEngine();
            var passwordService = new PasswordService();
            var logService = new LogService(pathService);

            string? previousWorkspaceRoot = RecursiveExtractor.ConfiguredWorkspaceRoot;
            string previousSevenZipPath = ToolLocator.Default.CustomSevenZipExePath;

            var vm = new MainViewModel(
                new FileScanService(),
                new ArchiveDetectService(),
                new RenameService(),
                engine,
                passwordService,
                logService,
                settingsService,
                pathService,
                new TaskSummaryService(),
                new ClipboardService(),
                new DialogService());

            RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;
            ToolLocator.Default.CustomSevenZipExePath = previousSevenZipPath;

            var scan = new ScanCoordinator(vm, new FileScanService(), new ArchiveDetectService(), new DialogService());
            var rename = new RenameCoordinator(vm, scan, new RenameService(), new DialogService());
            var extraction = new ExtractionCoordinator(vm, engine, passwordService, pathService, new DialogService());

            var sourceDeletes = new RecordingDeleteFileSystem(_root);
            extraction.SourcePackageDeleteFileSystem = sourceDeletes;

            // 默认档下成功的任务在日志里只留一行，而本类要断言的"每层都说过它回收了"正是那些中间行。
            extraction.KeepTaskDetailInLog = true;

            var oneClick = new OneClickCoordinator(vm, scan, rename, extraction, new DialogService());

            return new Harness(vm, oneClick, extraction, logService, sourceDeletes);
        }

        private sealed class Harness
        {
            private readonly OneClickCoordinator _oneClick;

            public Harness(
                MainViewModel vm,
                OneClickCoordinator oneClick,
                ExtractionCoordinator coordinator,
                LogService log,
                RecordingDeleteFileSystem sourceDeletes)
            {
                Vm = vm;
                _oneClick = oneClick;
                Coordinator = coordinator;
                Log = log;
                SourceDeletes = sourceDeletes;
            }

            public MainViewModel Vm { get; }

            public ExtractionCoordinator Coordinator { get; }

            public LogService Log { get; }

            public RecordingDeleteFileSystem SourceDeletes { get; }

            public Task AddPathsAsync(params string[] paths) => Vm.AddPathsAsync(paths);

            public Task<OneClickOutcome> RunOneClickAsync() => _oneClick.RunPipelineAsync(Vm.Tasks.ToList());
        }

        /// <summary>
        /// 记账式的"删源包"执行体（产品代码默认走真实文件系统，这里只替换执行体、判据一个字不改）：
        /// 逐层回收走的正是它（<c>SourcePackageDeleteFileSystem</c>）。
        /// </summary>
        private sealed class RecordingDeleteFileSystem : ISourceDeleteFileSystem
        {
            private readonly string _root;

            public RecordingDeleteFileSystem(string root) => _root = root;

            public List<string> DeletedPaths { get; } = new();

            public bool FileExists(string path) => File.Exists(path);

            public void DeleteFile(string path)
            {
                DeletedPaths.Add(path);
                File.Delete(path);
            }
        }

        /// <summary>
        /// 假的其余物删除执行器（**绝不碰用户的系统回收站**）：记调用、按档真删临时目录。
        /// </summary>
        private sealed class FakeDeleteExecutor : IDeleteExecutor
        {
            public string RecycleMessage { get; set; } = "已移入回收站（假执行器）";

            public List<string> RecycleCalls { get; } = new();

            public List<string> PermanentCalls { get; } = new();

            public RecycleAttemptResult TryMoveToRecycleBin(string path, bool isDirectory, out string message)
            {
                RecycleCalls.Add(path);
                message = RecycleMessage;

                Delete(path, isDirectory);

                return RecycleAttemptResult.Recycled;
            }

            public void DeletePermanently(string path, bool isDirectory)
            {
                PermanentCalls.Add(path);
                Delete(path, isDirectory);
            }

            private static void Delete(string path, bool isDirectory)
            {
                if (isDirectory && Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        /// <summary>
        /// 每 10 毫秒采一帧：盘上同时有几个 <c>*.7z</c>、它们一共多少字节、以及本次临时根下面
        /// 出现过的**所有**路径（用来回答"工作区里还剩什么"）。
        ///
        /// <para>采样只会**少看**、绝不会多看，所以"最多看到 N 个"这类上界断言是安全的
        /// （与 <c>ChainSpaceReclaimTests.PackagePeakSampler</c> 同一套做法）。</para>
        /// </summary>
        private sealed class DiskSampler : IDisposable
        {
            private readonly string _root;
            private readonly List<string> _peakSnapshot = new();
            private readonly HashSet<string> _allPathsSeen = new(StringComparer.OrdinalIgnoreCase);
            private volatile bool _running;
            private Task? _loop;

            public DiskSampler(string root) => _root = root;

            public int MaxPackageCount { get; private set; }

            public long MaxPackageBytes { get; private set; }

            public IReadOnlyList<string> PeakSnapshot => _peakSnapshot;

            public IReadOnlyCollection<string> AllPathsSeen => _allPathsSeen;

            public void Start()
            {
                _running = true;

                _loop = Task.Run(
                    async () =>
                    {
                        while (_running)
                        {
                            Observe();
                            await Task.Delay(10).ConfigureAwait(false);
                        }
                    });
            }

            public void Stop()
            {
                _running = false;

                try
                {
                    _loop?.Wait(TimeSpan.FromSeconds(5));
                }
                catch
                {
                    // 采样线程收不干净不影响结论。
                }
            }

            public void Dispose() => Stop();

            private void Observe()
            {
                try
                {
                    if (!Directory.Exists(_root))
                    {
                        return;
                    }

                    string[] packages = Directory.GetFiles(_root, "*.7z", SearchOption.AllDirectories);
                    long bytes = packages.Sum(p => new FileInfo(p).Length);

                    if (packages.Length > MaxPackageCount)
                    {
                        MaxPackageCount = packages.Length;

                        _peakSnapshot.Clear();
                        _peakSnapshot.AddRange(packages.Select(Path.GetFileName).OfType<string>());
                    }

                    MaxPackageBytes = Math.Max(MaxPackageBytes, bytes);

                    foreach (string path in Directory.GetFileSystemEntries(_root, "*", SearchOption.AllDirectories))
                    {
                        _allPathsSeen.Add(path);
                    }
                }
                catch
                {
                    // 采样是观测行为：目录正在被搬动时读不到就当这一帧没看见。
                }
            }
        }

        // ==================================================================================
        // 递归路（一条链是**一个任务**、层是工作区目录）—— 用户 2026-10-05：「两条路要同步」
        //
        // 上面那一整组钉的是**续解链**（层 = 任务）。而「展开所有分支」那条 4 层链是**一个任务**内
        // 由 `RecursiveExtractor` 展开的（层 = 工作区目录），过去**没有**逐层回收 —— 过程物一直攒到
        // 定稿那一刻才释放（真机第六批：空间曲线 13:39:32 最低 62.48 GiB → 13:40:57 回到 82.63 GiB，
        // 峰值 26.94 GiB 而结果只有 7.28 GB）。
        //
        // ⛔ 这一组**不是新功能**：AGENTS.md §11.3 早就写定「续解链每一层『定稿 + 输出校验通过 +
        // 未取消 + 可证完整』之后当场按『删除操作』处理这一层的过程物」，只是**只在一条路上落了地**。
        // ==================================================================================



        /*
         * ⛔ 2026-10-15：原来这里有「用例 R3」（递归路上③页「移入回收站」档也逐层当场进回收站）——
         * 与用例 B2/E 同一条理由：**回收站那一档整条下线**，这条钉的功能已经不存在，
         * 按新现实整条撤掉（⛔ 不是放宽判据）。递归路的逐层回收由用例 R1/R2/R4 钉着（那些走彻底删除档）。
         */


        /// <summary>
        /// 造一条"第一层的内层包是**一组三片分卷**"的链：
        /// <c>outer.7z → level2.7z.001/.002/.003 → mid.7z → final.txt</c>（外加各层自己的 txt）。
        ///
        /// <para><c>-v16k</c> 切出来的三片在同一层里，入口是 <c>.001</c>（自报第 1 卷）——
        /// 与真机那条链（3 片 7z 分卷）同形。⚠ 分卷本体只有第 1 片带魔数，
        /// 所以"这一组算一个内层归档"靠的是既有折叠尺（同目录 + 同基名 + 是后续卷）。</para>
        /// </summary>
        private string BuildVolumeGroupChain()
        {
            string build = Path.Combine(_root, "vol-build");
            Directory.CreateDirectory(build);

            File.WriteAllText(Path.Combine(build, "final.txt"), "最深一层的内容\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(build, "layer2.txt"), "第 2 层自己的内容\n", new UTF8Encoding(false));

            // 32 KiB 不可压数据：`-v16k` 下必然切出 ≥3 片（不然就不是"一组分卷"了）。
            byte[] blob = new byte[32 * 1024];
            new Random(20261005).NextBytes(blob);
            File.WriteAllBytes(Path.Combine(build, "blob.bin"), blob);

            // 第 2 层（最内层）自己的包：装 final.txt + layer2.txt + blob（分卷那一组要装它）。
            Run7z(build, "a", "-t7z", "-mx0", "mid.7z", "final.txt", "layer2.txt", "blob.bin");

            File.WriteAllText(Path.Combine(build, "layer1.txt"), "第 1 层自己的内容\n", new UTF8Encoding(false));

            // 第 1 层的内层包 = 一组三片分卷（装 mid.7z + layer1.txt…… 用 layer2.txt 做陪衬）。
            Run7z(build, "a", "-t7z", "-mx0", "-v16k", "level2.7z", "mid.7z", "layer2.txt");

            string[] parts = Directory.GetFiles(build, "level2.7z.*");

            Assert.True(
                parts.Length >= 3,
                "`-v16k` 下应当切出至少三片，实际 " + parts.Length + " 片：" + string.Join("、", parts.Select(Path.GetFileName)));

            string sourceDirectory = Path.Combine(_root, "src");
            Directory.CreateDirectory(sourceDirectory);

            string outer = Path.Combine(sourceDirectory, "outer.7z");

            var outerArgs = new List<object> { "a", "-t7z", outer };

            foreach (string part in parts.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                outerArgs.Add(Path.GetRelativePath(build, part));
            }

            outerArgs.Add("layer1.txt");

            Run7z(build, outerArgs.ToArray());

            Directory.Delete(build, recursive: true);

            return outer;
        }

        /// <summary>
        /// 跑一遍"递归展开一条四层链"（②页「展开所有分支」），把判据需要的事实全部带回来：
        /// 那一次跑的临时根、产物文件清单、逐字节指纹、假引擎拍到的每一帧、以及记账式删除执行体。
        ///
        /// <para>⚠ 每一次都在**自己那一个临时根**里跑：两遍对照必须互不干扰（源包要留在原处）。</para>
        /// </summary>
        private async Task<RecursionReclaimRun> RunRecursionChainAsync(string restMode)
        {
            string runRoot = Path.Combine(_root, "run-" + Guid.NewGuid().ToString("N")[..8]);

            Directory.CreateDirectory(runRoot);

            var run = new RecursionReclaimRun(runRoot);

            string buildSource = Path.Combine(runRoot, "src");

            Directory.CreateDirectory(buildSource);

            (Harness harness, ExtractionProbeEngine probe, string outer) = CreateProbeHarness(runRoot, restMode);

            run.Harness = harness;
            run.Probe = probe;
            run.SourcePackage = outer;

            await harness.AddPathsAsync(outer);

            await harness.RunOneClickAsync().WaitAsync(TimeSpan.FromSeconds(240));

            run.ProducedFiles = Directory.Exists(runRoot)
                ? Directory.GetFiles(runRoot, "*", SearchOption.AllDirectories)
                    .Where(file => !IsNamed(file, "outer.7z"))

                    /*
                     * 只比**内容物**（`out\` 那棵树）：另两个是每次跑都必然不同的运行时文件 ——
                     * `data\logs\...<时间戳>.log`（日志文件名带跑的时间）与
                     * `data\appsettings.json` / `data\password-list.dat`（运行期写盘的状态）。
                     * ⛔ 把它们算进"逐字节不变"只会让判据变脆，与"过程物早删晚删不影响产物"这件事无关。
                     */
                    .Where(file => !file.Contains(
                        Path.DirectorySeparatorChar + "data" + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                    .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : new List<string>();

            foreach (string file in run.ProducedFiles)
            {
                run.Digest[Path.GetRelativePath(runRoot, file)] = Sha256(file);
            }

            return run;
        }

        /// <summary>
        /// 在一个**给定的临时根**里造一条四层真链 + 一套带假引擎的测试宿主
        /// （样本来源目录、数据目录、输出目录全在那一个根下面 —— 两遍对照因此互不干扰）。
        /// </summary>
        private (Harness Harness, ExtractionProbeEngine Probe, string Outer) CreateProbeHarness(
            string runRoot,
            string restMode)
        {
            string dataRoot = Path.Combine(runRoot, "data");
            string outputRoot = Path.Combine(runRoot, "out");

            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(outputRoot);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var settingsService = new SettingsService(pathService);

            AppSettings settings = AppSettings.CreateDefault();
            settings.CustomOutputDirectory = outputRoot;
            settings.ExtractToOriginalDirectory = false;
            settings.KeepArchiveNameFolder = true;

            // ★ 出厂默认档：展开所有分支 ⇒ 整条链由 **RecursiveExtractor** 在一个任务里展开。

            settings.AutoScanAfterDrop = false;
            settings.TryEmptyPasswordFirst = false;
            settings.SourceHandling = SourceHandlingMode.KeepInPlace.ToString();
            settings.RestHandlingAfterVerify = restMode;
            settings.CustomSevenZipExePath = string.Empty;

            settingsService.Save(settings);

            var probe = new ExtractionProbeEngine(new SevenZipEngine(), runRoot);
            var logService = new LogService(pathService);

            var vm = new MainViewModel(
                new FileScanService(),
                new ArchiveDetectService(),
                new RenameService(),
                probe,
                new PasswordService(),
                logService,
                settingsService,
                pathService,
                new TaskSummaryService(),
                new ClipboardService(),
                new DialogService());

            var scan = new ScanCoordinator(vm, new FileScanService(), new ArchiveDetectService(), new DialogService());
            var rename = new RenameCoordinator(vm, scan, new RenameService(), new DialogService());
            var extraction = new ExtractionCoordinator(vm, probe, new PasswordService(), pathService, new DialogService());

            var sourceDeletes = new RecordingDeleteFileSystem(runRoot);

            extraction.SourcePackageDeleteFileSystem = sourceDeletes;
            extraction.KeepTaskDetailInLog = true;

            var oneClick = new OneClickCoordinator(vm, scan, rename, extraction, new DialogService());

            string outer = BuildChainInto(runRoot, 4);

            return (new Harness(vm, oneClick, extraction, logService, sourceDeletes), probe, outer);
        }

        /// <summary>一次"递归跑一条四层链"的全部事实（见 <see cref="RunRecursionChainAsync"/>）。</summary>
        private sealed class RecursionReclaimRun
        {
            public RecursionReclaimRun(string root) => Root = root;

            public string Root { get; }

            public Harness Harness { get; set; } = null!;

            public ExtractionProbeEngine Probe { get; set; } = null!;

            public string SourcePackage { get; set; } = string.Empty;

            public IReadOnlyList<string> ProducedFiles { get; set; } = Array.Empty<string>();

            public Dictionary<string, string> Digest { get; } = new(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 假的引擎包装（**只观测、不改变任何结论**）：每一次开始解压之前，把"这一次跑的临时根下面
        /// 还剩哪几个 <c>level*.7z</c>"记一帧 —— 递归路逐层回收的判据就是这一帧。
        /// ⛔ 它原样转发给真 7-Zip，不拼参数、不改结论（AGENTS.md §3 四条禁止项）。
        /// </summary>
        private sealed class ExtractionProbeEngine : IArchiveEngine
        {
            private readonly IArchiveEngine _inner;
            private readonly string _root;

            public ExtractionProbeEngine(IArchiveEngine inner, string root)
            {
                _inner = inner;
                _root = root;
            }

            /// <summary>每一次开始解压：要解的归档 + 那一刻盘上还剩哪几个 <c>level*.7z</c>（按名字）。</summary>
            public List<(string Archive, IReadOnlyList<string> Snapshot)> ExtractStarts { get; } = new();

            public string Name => _inner.Id;

            public string Id => _inner.Id;

            public string DisplayName => _inner.DisplayName;

            public string Version => _inner.Version;

            public bool IsAvailable => _inner.IsAvailable;

            public EngineCapabilities Capabilities => _inner.Capabilities;

            public Task<ArchiveProbeResult> ProbeAsync(ArchiveRequest request, CancellationToken cancellationToken = default) =>
                _inner.ProbeAsync(request, cancellationToken);

            public Task<ArchiveListResult> ListAsync(ArchiveRequest request, CancellationToken cancellationToken = default) =>
                _inner.ListAsync(request, cancellationToken);

            public Task<ArchiveOperationResult> TestAsync(ArchiveRequest request, CancellationToken cancellationToken = default) =>
                _inner.TestAsync(request, cancellationToken);

            public Task<ArchiveOperationResult> ExtractAsync(
                ArchiveRequest request,
                ExtractOptions options,
                CancellationToken cancellationToken = default)
            {
                ExtractStarts.Add((request.ArchivePath, SnapshotLevelPackages()));

                return _inner.ExtractAsync(request, options, cancellationToken);
            }

            private IReadOnlyList<string> SnapshotLevelPackages()
            {
                try
                {
                    return Directory.Exists(_root)
                        ? Directory.GetFiles(_root, "level*.7z", SearchOption.AllDirectories)
                            .Select(path => Path.GetFileName(path) ?? string.Empty)
                            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                            .ToList()
                        : new List<string>();
                }
                catch
                {
                    // 观测失败就当这一帧什么都没看见（判据会因此变红时宁可红，也不许猜）。
                    return new List<string>();
                }
            }
        }

        private static string Sha256(string path)
        {
            using var stream = File.OpenRead(path);
            using var sha = System.Security.Cryptography.SHA256.Create();

            return Convert.ToHexString(sha.ComputeHash(stream));
        }
    }
}

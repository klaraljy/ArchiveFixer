using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **内容级识别 + 试开验证的端到端**（用户 2026-09-28 真机：7z 三卷的名字被改成
    /// <c>amb909.7.01</c> / <c>amb909.z.2</c> / <c>amb909..3</c> —— 后缀与卷号都被改烂）。
    ///
    /// <para>这一组钉的是**结果**，不是"报了个好听的结论"：跑完既有管线之后，
    /// 必须真的在输出目录里找到解出来的文件、内容逐字节相同。</para>
    ///
    /// <para>真 7z 造样本：<c>-v1m</c> 切 2.5 MiB → 三卷（两个满卷 + 一个不满的尾卷），
    /// 与真机那组的形状一致。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class DisguisedVolumeContentProbeTests : IDisposable
    {
        private const int PayloadBytes = 2621440; // 2.5 MiB

        /// <summary>「文件名也加密」那一档用的占位密码（⛔ 真密码绝不进仓库，§8）。</summary>
        private const string ProbePassword = "VolumeProbe-Right-2026";

        private readonly string _root;
        private readonly string? _sevenZip;
        private readonly Xunit.Abstractions.ITestOutputHelper _output;

        public DisguisedVolumeContentProbeTests(Xunit.Abstractions.ITestOutputHelper output)
        {
            _output = output;
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerVolumeProbe", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _sevenZip = LocateSevenZip();
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

            try
            {
                // 试开目录由产品代码自己删；万一没删干净，这里补一刀（⛔ 只删我们自己造的这一个名字）。
                string work = Path.Combine(Path.GetTempPath(), VolumeContentInference.WorkDirectoryName);

                if (Directory.Exists(work) && !Directory.EnumerateFileSystemEntries(work).Any())
                {
                    Directory.Delete(work);
                }
            }
            catch
            {
                // 同上。
            }
        }

        [SevenZipFact]
        public async Task 真七z_三卷名字被改烂_既有管线能真的解出内容()
        {
            string source = NewDirectory("amb909");
            byte[] payload = MakePayload(source);

            Run7z(source, "a", "-t7z", "-mx0", "-v1m", "amb909.7z", "data.bin");

            Assert.True(File.Exists(Path.Combine(source, "amb909.7z.003")), "样本不是三卷");

            // 真机那种改法：卷号被改烂、后缀也不对
            File.Move(Path.Combine(source, "amb909.7z.001"), Path.Combine(source, "amb909.7.01"));
            File.Move(Path.Combine(source, "amb909.7z.002"), Path.Combine(source, "amb909.z.2"));
            File.Move(Path.Combine(source, "amb909.7z.003"), Path.Combine(source, "amb909..3"));

            string first = Path.Combine(source, "amb909.7.01");
            Harness harness = CreateHarness();
            ArchiveTask task = await AddTaskAsync(harness, first);

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);

            // 先钉"真的解出内容了"（这条红检时最有价值：它同时证明"报成功"与"内容对"是两件事）。
            AssertPayloadExtracted(harness, payload);

            Assert.Equal(Path.Combine(source, "amb909.7z.001"), task.CurrentPath);
            Assert.True(File.Exists(Path.Combine(source, "amb909.7z.001")), "整组没改回标准名");
            Assert.False(File.Exists(first), "旧名字还在");
        }

        [SevenZipFact]
        public async Task 真七z_卷号后面粘着删除两个字_既有管线能真的解出内容()
        {
            string source = NewDirectory("giu910");
            byte[] payload = MakePayload(source);

            Run7z(source, "a", "-t7z", "-mx0", "-v1m", "giu910.7z", "data.bin");

            Assert.True(File.Exists(Path.Combine(source, "giu910.7z.003")), "样本不是三卷");

            for (int index = 1; index <= 3; index++)
            {
                File.Move(
                    Path.Combine(source, $"giu910.7z.{index:D3}"),
                    Path.Combine(source, $"giu910.7z.{index:D3}删除"));
            }

            Harness harness = CreateHarness();
            ArchiveTask task = await AddTaskAsync(harness, Path.Combine(source, "giu910.7z.001删除"));

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            AssertPayloadExtracted(harness, payload);
        }

        [SevenZipFact]
        public async Task 真七z_两卷_第一卷满片第二卷是余量_名字只剩卷号_既有管线能真的解出内容()
        {
            /*
             * 用户 2026-09-29 的真实现场（`<测试目录>` 里那两个文件）：
             *   `amb909.7.01` = 2 GiB（**正好**是切分上限，开头是 7z 魔数）
             *   `amb909.z.2`  = 1.89 GB（7-Zip 认不出格式 —— 它是一段**裸的续卷**）
             * 老行为：第一卷报「分卷缺失 —— 同目录里也没有找到像后续卷的文件」，第二卷被当"不是压缩包"跳过，
             * 于是这一组**能救的包救不回来**。
             *
             * 真因是"尺寸规律"要的是"有与第一卷等长的文件"，而两卷时第二卷就是余量、必然更短 ——
             * 那条规律永远给不出证据。现在由"两卷形状"这张门票放过闸门，成不成立仍旧由硬链接试开回答。
             *
             * ⚠ 这里用 `-v1m` 的小样本代替 2 GiB 那个形状（形状一样：第一卷正好等于切分上限、第二卷是余量），
             * 名字照真机那样只留"1 和 2"两个号。
             */
            string source = NewDirectory("amb909-two");
            byte[] payload = MakePayload(source, 1572864); // 1.5 MiB → 1 MiB + 0.5 MiB

            Run7z(source, "a", "-t7z", "-mx0", "-v1m", "amb909.7z", "data.bin");

            Assert.True(File.Exists(Path.Combine(source, "amb909.7z.002")), "样本不是两卷");
            Assert.False(File.Exists(Path.Combine(source, "amb909.7z.003")), "样本该正好两卷（用来钉「没有等长兄弟」这个形状）");

            long firstLength = new FileInfo(Path.Combine(source, "amb909.7z.001")).Length;
            long secondLength = new FileInfo(Path.Combine(source, "amb909.7z.002")).Length;

            Assert.Equal(1024 * 1024, firstLength);              // 第一卷 = 切分上限（满片）
            Assert.True(secondLength < firstLength, "第二卷应当是余量（比第一卷短）");

            // 真机那种改法：卷号被改烂、后缀也不对
            File.Move(Path.Combine(source, "amb909.7z.001"), Path.Combine(source, "amb909.7.01"));
            File.Move(Path.Combine(source, "amb909.7z.002"), Path.Combine(source, "amb909.z.2"));

            string first = Path.Combine(source, "amb909.7.01");
            Harness harness = CreateHarness();
            ArchiveTask task = await AddTaskAsync(harness, first);

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);

            // **真的解出内容了**（逐字节一致）—— 这条比状态断言更有价值。
            AssertPayloadExtracted(harness, payload);

            // 整组改回标准名，旧名字一个不剩。
            Assert.True(File.Exists(Path.Combine(source, "amb909.7z.001")), "整组没改回标准名：缺 amb909.7z.001");
            Assert.True(File.Exists(Path.Combine(source, "amb909.7z.002")), "整组没改回标准名：缺 amb909.7z.002");
            Assert.False(File.Exists(first), "旧名字还在：amb909.7.01");
            Assert.False(File.Exists(Path.Combine(source, "amb909.z.2")), "旧名字还在：amb909.z.2");

            Assert.Equal(Path.Combine(source, "amb909.7z.001"), task.CurrentPath);
            Assert.True(task.VolumeNameAutoRenamed, "不是管线按内容改名的那一条路");
        }

        [SevenZipFact]
        public async Task 真七z_两卷_名字里没有数字尾巴_靠体积规律也认得出这一组()
        {
            /*
             * 第二张门票的**第二条**（用户原话："对不上再用体积规律（7z -v 的除末卷外都是满片：
             * 正好等于切分上限，例如 2 GiB）"）：名字里连数字都没有时，只要第一卷正好是整数 MiB
             * （`-v` 的切分上限永远是整数 MiB），"它是满片"就在体积上说得通 —— 放行去试开。
             *
             * ⛔ 放行的只是"敢试一次"：这一组能不能成立仍旧只由试开回答（下面那条反例钉着）。
             */
            string source = NewDirectory("amb909-notail");
            byte[] payload = MakePayload(source, 1572864);

            Run7z(source, "a", "-t7z", "-mx0", "-v1m", "amb909.7z", "data.bin");

            Assert.True(File.Exists(Path.Combine(source, "amb909.7z.002")), "样本不是两卷");

            File.Move(Path.Combine(source, "amb909.7z.001"), Path.Combine(source, "amb909.aa"));
            File.Move(Path.Combine(source, "amb909.7z.002"), Path.Combine(source, "amb909.bb"));

            Harness harness = CreateHarness();
            ArchiveTask task = await AddTaskAsync(harness, Path.Combine(source, "amb909.aa"));

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            AssertPayloadExtracted(harness, payload);

            // 基名推不出归档后缀（`aa` 不是 `7z` 的"差一个字符"），所以基名原样留着 —— 名字只影响好不好看。
            Assert.True(File.Exists(Path.Combine(source, "amb909.aa.7z.001")), "整组没改回标准名：缺 amb909.aa.7z.001");
            Assert.True(File.Exists(Path.Combine(source, "amb909.aa.7z.002")), "整组没改回标准名：缺 amb909.aa.7z.002");
            Assert.False(File.Exists(Path.Combine(source, "amb909.bb")), "旧名字还在：amb909.bb");
        }

        [SevenZipFact]
        public async Task 真七z_两卷_只剩第一卷加一个无关小文件_试开不成立就什么都不做()
        {
            /*
             * 反向守门（放开的闸门必须由试开兜住）：后续卷真的不在时，目录里那个**更短**的无关文件
             * 会被"两卷形状"放进来试开一次 —— 引擎读不出来，于是**一个字节都不许动**：
             * 名字不改、不报成功、源包原地不动。
             */
            string source = NewDirectory("amb909-missing-second");
            MakePayload(source, 1572864);

            Run7z(source, "a", "-t7z", "-mx0", "-v1m", "amb909.7z", "data.bin");

            string first = Path.Combine(source, "amb909.7.01");
            File.Move(Path.Combine(source, "amb909.7z.001"), first);

            // 第二卷拿掉，换成一个无关的小文件（它比第一卷短，会被当候选）。
            File.Delete(Path.Combine(source, "amb909.7z.002"));
            File.WriteAllText(Path.Combine(source, "readme.txt"), "说明文件，与这一组分卷无关");

            byte[] before = File.ReadAllBytes(first);

            Harness harness = CreateHarness();
            ArchiveTask task = await AddTaskAsync(harness, first);

            await harness.Coordinator.StartExtractAsync();

            Assert.NotEqual(StatusText.ExtractSuccess, task.Status);
            Assert.NotEqual(TaskOutcome.Succeeded, task.Outcome);

            // 一个字节都不许动：源包还在、名字没变、没有凭空造出一个标准名。
            Assert.True(File.Exists(first), "源包被改名或搬走了");
            Assert.Equal(before, File.ReadAllBytes(first));
            Assert.False(File.Exists(Path.Combine(source, "amb909.7z.001")), "试开没成立却把名字改了");
            Assert.True(File.Exists(Path.Combine(source, "readme.txt")), "无关文件被动过");
            Assert.Empty(Directory.GetFiles(harness.OutputRoot, "*", SearchOption.AllDirectories));
        }

        [SevenZipFact]
        public async Task 真七z_两卷_两个文件都在任务列表里_整组照样救得回来()
        {
            /*
             * 真机的批次是**两个文件都被导入**（第一卷 + 那个认不出格式的续卷）—— 这一条照那个形状走一遍，
             * 钉住"整组救回来"这件事不受影响；第二个任务自己落到什么状态**不在本用例的断言范围**里
             * （它在管线里排在后面，而那时文件已经被整组改名了）。
             */
            string source = NewDirectory("amb909-two-tasks");
            byte[] payload = MakePayload(source, 1572864);

            Run7z(source, "a", "-t7z", "-mx0", "-v1m", "amb909.7z", "data.bin");

            File.Move(Path.Combine(source, "amb909.7z.001"), Path.Combine(source, "amb909.7.01"));
            File.Move(Path.Combine(source, "amb909.7z.002"), Path.Combine(source, "amb909.z.2"));

            Harness harness = CreateHarness();

            ArchiveTask first = await AddTaskAsync(harness, Path.Combine(source, "amb909.7.01"));

            ArchiveTask second = await AddTaskAsync(harness, Path.Combine(source, "amb909.z.2"));

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractSuccess, first.Status);
            AssertPayloadExtracted(harness, payload);

            Assert.True(File.Exists(Path.Combine(source, "amb909.7z.001")), "整组没改回标准名：缺 amb909.7z.001");
            Assert.True(File.Exists(Path.Combine(source, "amb909.7z.002")), "整组没改回标准名：缺 amb909.7z.002");

            /*
             * 第二个任务不许报成功：它是那一组的**续卷**，整组已经由第一个任务解出来了，
             * 它自己不产出任何东西 —— 报成功就是"成功"用错了（不变量 6）。
             */
            Assert.NotEqual(StatusText.ExtractSuccess, second.Status);

            _output.WriteLine(
                $"第二个任务（认不出格式的那一片）：状态=[{second.Status}] 终态=[{second.Outcome}] 原因=[{second.ErrorMessage}]");
        }

        [SevenZipFact]
        public async Task 真七z_完整包只改了后缀_不许被当成第一卷改名()
        {
            /*
             * 反例守门：一个**完整**的 7z 被改成 `solo.7.01` 时，它自己就能打开 ——
             * 那就绝不能"按内容推顺序"去把它改名（改完反而打不开了）。
             * 这条钉的正是"试开必须先确认第一卷自己打不开"这道闸门。
             */
            string source = NewDirectory("solo");
            byte[] payload = MakePayload(source);

            Run7z(source, "a", "-t7z", "-mx0", "solo.7z", "data.bin");

            File.Move(Path.Combine(source, "solo.7z"), Path.Combine(source, "solo.7.01"));

            Harness harness = CreateHarness();
            ArchiveTask task = await AddTaskAsync(harness, Path.Combine(source, "solo.7.01"));

            await harness.Coordinator.StartExtractAsync();

            Assert.True(File.Exists(Path.Combine(source, "solo.7.01")), "完整包被改掉了名字");
            Assert.False(File.Exists(Path.Combine(source, "solo.7.7z.001")), "不该按内容推的名字改名");
            Assert.Equal(StatusText.ExtractSuccess, task.Status);
        }

        // ================= 文件名也加密（7z -mhe）的那一档：用户 2026-09-29 真机的**真形状** =================

        /// <summary>
        /// **真机副本上实测出来的断点**（用户 2026-09-29 第二次真机报，20:05 那条日志）：
        /// 现场那两个文件本来就是一组**完整**的两卷包，只是当年造包时开了 <c>-mhe</c>（**文件名也加密**）——
        /// 于是硬链接试开拿不到密码，`7z l` 报的是
        /// <c>Cannot open encrypted archive. Wrong password?</c>（引擎结构化结论 = <c>EncryptedHeaders</c>），
        /// 老写法只认"列得出清单"→ 试开永远不成立 → 一个字节都不动 → 管线随后报「分卷缺失」
        /// （**诊断说错了**：卷齐得很，缺的是密码）。
        ///
        /// <para>这一条钉的就是"文件名也加密的两卷、名字是 <c>.7.01</c>/<c>.z.2</c>"这个真形状：
        /// 整组改回标准名 + **真的解出内容、逐字节一致**（密码本来就在「密码」页的密码本里）。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 真七z_两卷_文件名也加密_既有管线照样改回标准名并解出内容()
        {
            string source = NewDirectory("amb909-mhe");
            byte[] payload = MakePayload(source, 1572864); // 1.5 MiB → 1 MiB + 0.5 MiB（第一卷满片、第二卷是余量）

            Run7z(source, "a", "-t7z", "-mx0", "-mhe=on", "-p" + ProbePassword, "-v1m", "amb909.7z", "data.bin");

            Assert.True(File.Exists(Path.Combine(source, "amb909.7z.002")), "样本不是两卷");
            Assert.False(File.Exists(Path.Combine(source, "amb909.7z.003")), "样本该正好两卷");

            File.Move(Path.Combine(source, "amb909.7z.001"), Path.Combine(source, "amb909.7.01"));
            File.Move(Path.Combine(source, "amb909.7z.002"), Path.Combine(source, "amb909.z.2"));

            string first = Path.Combine(source, "amb909.7.01");
            Harness harness = CreateHarness(new[] { ProbePassword });
            ArchiveTask task = await AddTaskAsync(harness, first);

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            AssertPayloadExtracted(harness, payload);

            Assert.True(File.Exists(Path.Combine(source, "amb909.7z.001")), "整组没改回标准名：缺 amb909.7z.001");
            Assert.True(File.Exists(Path.Combine(source, "amb909.7z.002")), "整组没改回标准名：缺 amb909.7z.002");
            Assert.False(File.Exists(first), "旧名字还在：amb909.7.01");
            Assert.False(File.Exists(Path.Combine(source, "amb909.z.2")), "旧名字还在：amb909.z.2");
            Assert.True(task.VolumeNameAutoRenamed, "不是管线按内容改名的那一条路");
        }

        /// <summary>
        /// 反向守门（放开的判据必须由事实兜住）：**文件名也加密**的那一组，第二卷真的不在时，
        /// 目录里那个无关小文件会被"两卷形状"放进去试开一次 —— 引擎报的是"打不开 / 数据不全"
        /// （**不是**"这是加密归档"），于是**一个字节都不许动**。
        /// </summary>
        [SevenZipFact]
        public async Task 真七z_两卷_文件名也加密_只剩第一卷加无关小文件_试开不成立就什么都不做()
        {
            string source = NewDirectory("amb909-mhe-missing-second");
            MakePayload(source, 1572864);

            Run7z(source, "a", "-t7z", "-mx0", "-mhe=on", "-p" + ProbePassword, "-v1m", "amb909.7z", "data.bin");

            string first = Path.Combine(source, "amb909.7.01");
            File.Move(Path.Combine(source, "amb909.7z.001"), first);

            File.Delete(Path.Combine(source, "amb909.7z.002"));
            File.WriteAllText(Path.Combine(source, "readme.txt"), "说明文件，与这一组分卷无关");

            byte[] before = File.ReadAllBytes(first);

            Harness harness = CreateHarness(new[] { ProbePassword });
            ArchiveTask task = await AddTaskAsync(harness, first);

            await harness.Coordinator.StartExtractAsync();

            Assert.NotEqual(StatusText.ExtractSuccess, task.Status);
            Assert.NotEqual(TaskOutcome.Succeeded, task.Outcome);

            Assert.True(File.Exists(first), "源包被改名或搬走了");
            Assert.Equal(before, File.ReadAllBytes(first));
            Assert.False(File.Exists(Path.Combine(source, "amb909.7z.001")), "试开没成立却把名字改了");
            Assert.True(File.Exists(Path.Combine(source, "readme.txt")), "无关文件被动过");
            Assert.Empty(Directory.GetFiles(harness.OutputRoot, "*", SearchOption.AllDirectories));
        }

        /// <summary>
        /// 反向守门（单卷那一档，加密版）：一个**完整**的、文件名也加密的包被改成 <c>solo.7.01</c> 时，
        /// 单卷试开会报"这是加密归档" —— 那说明那一份头就在这个文件**里**，它本身就是完整的包，
        /// ⛔ 绝不能按内容推顺序把它改名（改完 7-Zip 就找不着它了）。
        ///
        /// <para>所以单卷试开的"加密归档"结论必须**拒绝**，与"多卷试开"里的同一结论含义正好相反
        /// （那边是"头在最后一卷里"= 这一组成立）。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 真七z_完整包文件名也加密_只改了后缀_不许被当成第一卷改名()
        {
            string source = NewDirectory("solo-mhe");
            byte[] payload = MakePayload(source);

            Run7z(source, "a", "-t7z", "-mx0", "-mhe=on", "-p" + ProbePassword, "solo.7z", "data.bin");

            /*
             * 目录里留一个"更短、名字带短数字尾巴"的文件（`data.2`）—— 否则"两卷形状"那张门票
             * 根本不放行，单卷试开也就跑不到，这条用例会白绿。
             */
            File.Move(Path.Combine(source, "data.bin"), Path.Combine(source, "data.2"));

            File.Move(Path.Combine(source, "solo.7z"), Path.Combine(source, "solo.7.01"));

            string renamed = Path.Combine(source, "solo.7.01");

            Harness harness = CreateHarness(new[] { ProbePassword });
            ArchiveTask task = await AddTaskAsync(harness, renamed);

            await harness.Coordinator.StartExtractAsync();

            Assert.True(File.Exists(renamed), "完整的加密包被当成分卷第一卷改掉了名字");
            Assert.False(File.Exists(Path.Combine(source, "solo.7z.001")), "不该按内容推的名字改名");
            Assert.False(File.Exists(Path.Combine(source, "solo.7z.002")), "把无关的 data.2 也一起改了名");
            Assert.False(task.VolumeNameAutoRenamed, "这一单本来就不该走改名那条路");
            Assert.True(File.Exists(Path.Combine(source, "data.2")), "无关文件被动过");

            /*
             * ⚠ 这一单**最终没能解开**（状态「分卷缺失」），而且与本次修复无关 —— 是**既有**的另一处误诊：
             * 文件名末尾那一段是纯数字（`.01`）时，7-Zip 在没有密码的情况下会退化成"通用分片"处理器
             * （清单里只有一条 = 文件自己），管线那道 RawSplitStreamDetector 闸门于是报了「分卷缺失」。
             * 判据本身没错（通用分片正是它要拦的），错的是"没有密码"与"名字被改坏"两件事在那里分不开。
             * 本轮**不动它**（判据只允许一处，且真机那一档是"两卷"而不是"完整单卷"），只把这个事实记下来。
             */
            _output.WriteLine($"完整加密包（名字带纯数字尾巴）这次的状态：[{task.Status}] 原因：[{task.ErrorMessage}]");
        }

        // ── 样本与断言 ──

        /// <summary>
        /// 造一份**不可压缩**的载荷（<c>-v1m</c> 要真的分卷：可压的会被压成一卷，"分卷"就名不副实）。
        /// </summary>
        /// <param name="bytes">
        /// 载荷大小。默认 2.5 MiB（<c>-v1m</c> → 三卷：两个满卷 + 一个尾卷）；
        /// 1.5 MiB 用来造**两卷**（一个满卷 + 一个余量），也就是真机那组的形状。
        /// </param>
        private byte[] MakePayload(string directory, int bytes = PayloadBytes)
        {
            var payload = new byte[bytes];
            new Random(20260928).NextBytes(payload);
            File.WriteAllBytes(Path.Combine(directory, "data.bin"), payload);

            return payload;
        }

        /// <summary>断言"真的解出内容了"：输出目录里找得到 data.bin，而且逐字节等于原样。</summary>
        private static void AssertPayloadExtracted(Harness harness, byte[] payload)
        {
            string[] found = Directory.GetFiles(harness.OutputRoot, "data.bin", SearchOption.AllDirectories);

            Assert.True(found.Length > 0, "输出目录里没有解出来的 data.bin");

            byte[] actual = File.ReadAllBytes(found[0]);

            Assert.Equal(payload.Length, actual.Length);
            Assert.Equal(payload, actual);
        }

        private string NewDirectory(string name)
        {
            string directory = Path.Combine(_root, name);
            Directory.CreateDirectory(directory);

            return directory;
        }

        // ── 真 7z ──

        private void Run7z(string workingDirectory, params string[] args)
        {
            RequireSevenZip();

            var psi = new ProcessStartInfo(_sevenZip!)
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

            Assert.True(process.WaitForExit(120_000), "7z 超时");
            Assert.True(process.ExitCode == 0, $"7z 失败：{stdout}{stderr}");
        }

        private void RequireSevenZip()
        {
            if (string.IsNullOrEmpty(_sevenZip))
            {
                throw new InvalidOperationException("测试机上没有 7z.exe");
            }
        }

        private static string? LocateSevenZip()
        {
            DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ArchiveFixer.slnx")))
                {
                    string candidate = Path.Combine(directory.FullName, "src", "ArchiveFixer", "tools", "7zip", "7z.exe");
                    return File.Exists(candidate) ? candidate : null;
                }

                directory = directory.Parent;
            }

            return null;
        }

        // ── 管线 ──

        private static async Task<ArchiveTask> AddTaskAsync(Harness harness, string path)
        {
            var task = new ArchiveTask(path, harness.Vm.Tasks.Count + 1) { IsSelected = true };

            await new ArchiveDetectService().ApplyDetectResultAsync(task);
            task.CaptureSourceSnapshot();
            harness.Vm.Tasks.Add(task);

            return task;
        }

        private Harness CreateHarness(IReadOnlyList<string>? bookPasswords = null)
        {
            string dataRoot = Path.Combine(_root, "data-" + Guid.NewGuid().ToString("N"));
            string outputRoot = Path.Combine(_root, "out-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(outputRoot);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var settingsService = new SettingsService(pathService);

            AppSettings settings = AppSettings.CreateDefault();
            settings.CacheRootDirectory = dataRoot;
            settings.CustomOutputDirectory = outputRoot;
            settings.ExtractToOriginalDirectory = false;
            settings.KeepArchiveNameFolder = true;
            settings.RecursionMode = "SingleLayer";
            settings.AutoScanAfterDrop = false;
            settings.MaxParallelExtractCount = 1;
            settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
            settings.RestHandlingAfterVerify = RestHandlingModes.Keep;
            settingsService.Save(settings);

            IArchiveEngine engine = new Engines.SevenZip.SevenZipEngine();
            var logService = new LogService(pathService);
            var passwordService = new PasswordService();

            foreach (string password in bookPasswords ?? Array.Empty<string>())
            {
                passwordService.Passwords.Add(new PasswordItem
                {
                    Value = password,
                    Source = "ImportedList",
                    IsEnabled = true,
                    Remark = "分卷形状用例候选"
                });
            }

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
                new ConfirmDialogService());

            RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;
            ToolLocator.Default.CustomSevenZipExePath = previousSevenZipPath;

            var coordinator = new ExtractionCoordinator(
                vm,
                engine,
                passwordService,
                pathService,
                new ConfirmDialogService());

            coordinator.KeepTaskDetailInLog = true;

            return new Harness(vm, coordinator, logService, outputRoot, dataRoot);
        }

        private sealed class ConfirmDialogService : DialogService
        {
            public override bool ShowConfirm(
                string message,
                string optionText,
                bool optionCheckedByDefault,
                string detail,
                out bool optionChecked)
            {
                optionChecked = optionCheckedByDefault;
                return true;
            }
        }

        private sealed class Harness
        {
            public Harness(
                MainViewModel vm,
                ExtractionCoordinator coordinator,
                LogService log,
                string outputRoot,
                string dataRoot)
            {
                Vm = vm;
                Coordinator = coordinator;
                Log = log;
                OutputRoot = outputRoot;
                DataRoot = dataRoot;
            }

            public MainViewModel Vm { get; }

            public ExtractionCoordinator Coordinator { get; }

            public LogService Log { get; }

            public string OutputRoot { get; }

            public string DataRoot { get; }

            public IEnumerable<string> LogTexts => Log.Logs.Select(item => item.DisplayText);
        }
    }
}

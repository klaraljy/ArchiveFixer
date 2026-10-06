using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 「按建议改名并重试」（用户 2026-09-25 第 41 条，他当场同意做）。
    ///
    /// <para>场景：一组分卷的**第一卷**名字被改坏（`set.7z(删掉.001`），程序只能报「分卷缺失」+ 给建议 ——
    /// 源文件它自己一个字节都不能动。这个按钮让用户**显式**点一下，程序替他改**这个名字**再重试。</para>
    ///
    /// <para>钉住四件事：①计划该成立的成立、该拒绝的拒绝（六种拒绝理由各一条）；
    /// ②改名的动作**只改名字**（内容逐字节不变、目标被占就什么都不动）；
    /// ③按钮的可用性由**机器判据**决定（任务上有建议 + 文件系统上计划成立），不看中文状态；
    /// ④真 7z 端到端：改名之后**真的解得开**，而且出来的是真内容（不是那个等大的垃圾文件）。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class VolumeNameRepairTests : IDisposable
    {
        private readonly string _root;
        private readonly string? _sevenZip;

        public VolumeNameRepairTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerVolumeRepair", Guid.NewGuid().ToString("N"));
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
                // 临时目录清不掉不影响结论。
            }
        }

        // ================================================================ 计划（纯逻辑）

        [Fact]
        public void 计划_名字被改坏的第一卷_推出标准名()
        {
            string directory = NewDirectory("plan-ok");

            string mangled = CreateFile(directory, "set.7z(删掉.001", 64);
            CreateFile(directory, "set.7z.002", 64);
            CreateFile(directory, "set.7z.003", 64);

            VolumeNameRepairPlan plan = VolumeNameRepair.Plan(mangled, NamesIn(directory));

            Assert.True(plan.CanRepair, plan.Reason);
            Assert.Equal("set.7z.001", plan.SuggestedFileName);
            Assert.Equal(Path.Combine(directory, "set.7z.001"), plan.TargetPath);
            Assert.Contains("set.7z.002", plan.Siblings);
            Assert.Contains("→", plan.Describe());
        }

        [Fact]
        public void 计划_名字本来就是标准的_不许乱动()
        {
            string directory = NewDirectory("plan-standard");

            string standard = CreateFile(directory, "set.7z.001", 64);
            CreateFile(directory, "set.7z.002", 64);

            VolumeNameRepairPlan plan = VolumeNameRepair.Plan(standard, NamesIn(directory));

            Assert.False(plan.CanRepair);
            Assert.Equal(StatusText.VolumeRepairAlreadyStandard, plan.Reason);
        }

        [Fact]
        public void 计划_同目录没有后续卷_不许瞎猜一个名字()
        {
            string directory = NewDirectory("plan-nosibling");

            string mangled = CreateFile(directory, "set.7z(删掉.001", 64);

            VolumeNameRepairPlan plan = VolumeNameRepair.Plan(mangled, NamesIn(directory));

            Assert.False(plan.CanRepair);
            Assert.Equal(StatusText.VolumeRepairNoSiblings, plan.Reason);
        }

        [Fact]
        public void 计划_名字里没有卷号_不许改()
        {
            string directory = NewDirectory("plan-novolume");

            string plain = CreateFile(directory, "movie.mp4", 64);
            CreateFile(directory, "movie.mp4.002", 64);

            VolumeNameRepairPlan plan = VolumeNameRepair.Plan(plain, NamesIn(directory));

            Assert.False(plan.CanRepair);
            Assert.Equal(StatusText.VolumeRepairNotAVolumeName, plan.Reason);
        }

        [Fact]
        public void 计划_不是第一卷_改名解决不了问题()
        {
            string directory = NewDirectory("plan-notfirst");

            string second = CreateFile(directory, "set.7z(删掉.002", 64);
            CreateFile(directory, "set.7z.003", 64);

            VolumeNameRepairPlan plan = VolumeNameRepair.Plan(second, NamesIn(directory));

            Assert.False(plan.CanRepair);
            Assert.Equal(StatusText.VolumeRepairNotFirstVolume, plan.Reason);
        }

        [Fact]
        public void 计划_目标名被占用_绝不覆盖()
        {
            string directory = NewDirectory("plan-taken");

            string mangled = CreateFile(directory, "set.7z(删掉.001", 64);
            string taken = CreateFile(directory, "set.7z.001", 64);
            CreateFile(directory, "set.7z.002", 64);

            VolumeNameRepairPlan plan = VolumeNameRepair.Plan(mangled, NamesIn(directory));

            Assert.False(plan.CanRepair);
            Assert.Contains("set.7z.001", plan.Reason);

            // 两个文件都还在、内容都没动（计划阶段一个字节都不写）。
            Assert.True(File.Exists(mangled));
            Assert.True(File.Exists(taken));
        }

        // ================================================================ 改名（真的动盘）

        [Fact]
        public void 改名_只改名字_内容逐字节不变()
        {
            string directory = NewDirectory("apply-ok");

            string mangled = CreateFile(directory, "set.7z(删掉.001", 4096, seed: 7);
            CreateFile(directory, "set.7z.002", 64);

            string before = Sha256(mangled);

            VolumeNameRepairPlan plan = VolumeNameRepair.Plan(mangled, NamesIn(directory));
            Assert.True(plan.CanRepair, plan.Reason);

            VolumeNameRepairResult result = VolumeNameRepair.TryApply(plan);

            Assert.True(result.Success, result.Message);
            Assert.Equal(plan.TargetPath, result.NewPath);

            Assert.True(File.Exists(plan.TargetPath), "新名字必须在");
            Assert.False(File.Exists(mangled), "旧名字必须没了");
            Assert.Equal(before, Sha256(plan.TargetPath));
            Assert.Equal(4096, new FileInfo(plan.TargetPath).Length);

            // 同组的后续卷一个字节都没被碰。
            Assert.True(File.Exists(Path.Combine(directory, "set.7z.002")));
        }

        [Fact]
        public void 改名_目标名突然被占用_原地拒绝且什么都不动()
        {
            string directory = NewDirectory("apply-taken");

            string mangled = CreateFile(directory, "set.7z(删掉.001", 128, seed: 11);
            string target = Path.Combine(directory, "set.7z.001");
            CreateFile(directory, "set.7z.001", 128, seed: 12);

            string mangledHash = Sha256(mangled);
            string targetHash = Sha256(target);

            // 手工造一个"计划成立"但目标已被占的状态（模拟两次点击之间别人抢先建了同名文件）。
            var plan = new VolumeNameRepairPlan
            {
                CanRepair = true,
                CurrentPath = mangled,
                CurrentFileName = Path.GetFileName(mangled),
                SuggestedFileName = "set.7z.001",
                TargetPath = target
            };

            VolumeNameRepairResult result = VolumeNameRepair.TryApply(plan);

            Assert.False(result.Success);
            Assert.Contains("set.7z.001", result.Message);
            Assert.Equal(mangledHash, Sha256(mangled));
            Assert.Equal(targetHash, Sha256(target));
        }

        // ================================================================ 端到端（真 7z + 真管线）

        /// <summary>
        /// 真 7z 三卷 → 把第一卷名字改坏 → 手动「只解压」判「分卷缺失」并给出建议 →
        /// 点「按建议改名并重试」→ 名字被改回标准名 → **真的解出真内容**（不是那个等大的垃圾文件）。
        /// </summary>
        [Fact]
        public async Task 真7z_第一卷改名后点按钮_改名成功并解出真内容()
        {
            RequireSevenZip();

            string volumes = NewDirectory("volumes");
            (string payloadName, byte[] payloadBytes) = CreatePayload(volumes, 3 * 1024 * 1024);
            CreateVolumeSet(volumes, "set.7z", payloadName, "1m");

            string first = Path.Combine(volumes, "set.7z.001");
            string mangled = Path.Combine(volumes, "set.7z(删掉.001");
            File.Move(first, mangled);

            Harness harness = CreateHarness();

            var task = new ArchiveTask(mangled, 1) { IsSelected = true };
            await new ArchiveDetectService().ApplyDetectResultAsync(task);
            task.CaptureSourceSnapshot();
            harness.Vm.Tasks.Add(task);

            /*
             * ①手动「只解压」：**现在会在开工前自动把名字改回标准名**（2026-09-28 用户口径：
             * 「一键处理的功能是啥，就是我按一下你全部搞定，这些必要的操作肯定是要的」），
             * 所以这里不再是"报分卷缺失等着用户点按钮"，而是"改好名字 → 直接解出内容"。
             *
             * ⚠ 老断言（判 VolumeMissing、文件一个字节不动、按钮亮起）在 AutoRepairDisguisedVolumeTests
             * 那一批之前是对的；口径变了就要改断言，别把老期望硬留在那儿。
             * 手动按钮那条路仍然在（判据与执行体同一个 VolumeNameRepair），由下面第二个用例覆盖。
             */
            await harness.Coordinator.StartExtractAsync();

            Assert.True(File.Exists(first), "名字应该已经自动改回标准名了");
            Assert.False(File.Exists(mangled), "旧名字该没了");
            Assert.Equal(first, task.CurrentPath);
            Assert.Equal(StatusText.ExtractSuccess, task.Status);

            // ④出来的是**真内容**：与原始字节逐字节一致。
            string output = Path.Combine(harness.OutputRoot, "set", payloadName);
            Assert.True(File.Exists(output), $"没找到产物：{output}");
            Assert.Equal(payloadBytes, File.ReadAllBytes(output));

            // ⑤反向断言：7-Zip 那个"通用分片"垃圾文件不许出现在产物里。
            Assert.False(
                File.Exists(Path.Combine(harness.OutputRoot, "set", "set.7z(删掉")),
                "解出来的不该是「文件自己」那一份垃圾");

            // ⑥源包按默认档留在原地（不变量 1；自动修名只改名字，不搬不删）。
            Assert.True(File.Exists(first));
            Assert.True(File.Exists(Path.Combine(volumes, "set.7z.002")));
            Assert.True(File.Exists(Path.Combine(volumes, "set.7z.003")));
        }

        // ================================================================ 工具

        // ================================================================ 本体干净、只有续卷被改坏（真机 DDD）

        /// <summary>
        /// **真机 DDD（用户 2026-10-01 18:09）**：跨盘 ZIP 分了 **4 片** —— 本体 `222.zip` 是干净的标准名，
        /// 三个续卷的名字里被塞了字（`222.z0删1` / `222.z除02` / `222.z文03`）。
        ///
        /// <para>现场：7-Zip 报 <c>ERROR = Missing volume : 222.z01</c> ⇒ 整包解不开、任务落 Failed，
        /// 源包四个文件一个都没进 `其余物`。用户原话：「我这次将 zip 多分了几个卷你就弄不了了」。</para>
        ///
        /// <para><b>根因</b>：名字那条路的两道门都把这个形状挡在外面 ——
        /// ① 调用方的入口判据是 `IsVolumePartFileName(自己的名字)`，而 `222.zip` **不是**卷标记 ⇒ 名字路压根没跑；
        /// ② 就算跑了，`PlanJunkTailGroup` 要求**自己**的名字里带垃圾（`TrySplitDisguised(222.zip)` = false），
        ///    而 `FindSiblingVolumes` 只按**标准名**找兄弟 ⇒ 认不出 `z0删1` 这种"垃圾塞在卷标记里面"的续卷。
        /// 内容那条路又只支持 **2 片**的跨盘 zip（末片 EOCD 的盘号 + 消去法）⇒ 4 片判不出顺序 ⇒ 两边都不动。</para>
        ///
        /// <para>这一条钉的就是：**本体干净、续卷带垃圾**时，照样要把整组按标准名改好（只改名、不覆盖）。</para>
        /// </summary>
        [Fact]
        public void 计划_本体干净而续卷的名字被改坏_整组按标准名改好()
        {
            string directory = NewDirectory("clean-body-disguised-volumes");

            string body = CreateFile(directory, "222.zip", 64, seed: 1);
            CreateFile(directory, "222.z0删1", 128, seed: 2);
            CreateFile(directory, "222.z除02", 128, seed: 3);
            CreateFile(directory, "222.z文03", 128, seed: 4);

            string bodyHash = Sha256(body);

            VolumeNameRepairPlan? plan = VolumeNameRepair.Plan(body, NamesIn(directory));

            Assert.NotNull(plan);
            Assert.True(plan!.CanRepair, $"本体干净 + 续卷带垃圾必须能改：{plan.Reason}");

            // 要改的是**三个续卷**（本体已经是标准名，不该动它）。
            Assert.Equal(3, plan.Items.Count);
            Assert.Equal(
                new[] { "222.z01", "222.z02", "222.z03" },
                plan.Items.Select(i => i.SuggestedFileName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray());
            Assert.DoesNotContain(plan.Items, i => string.Equals(i.CurrentFileName, "222.zip", StringComparison.OrdinalIgnoreCase));

            VolumeNameRepairResult result = VolumeNameRepair.TryApply(plan);

            Assert.True(result.Success, result.Message);

            // 盘上：三个标准卷名都在、旧名都没了、**本体一个字节没动**。
            Assert.True(File.Exists(Path.Combine(directory, "222.z01")));
            Assert.True(File.Exists(Path.Combine(directory, "222.z02")));
            Assert.True(File.Exists(Path.Combine(directory, "222.z03")));
            Assert.False(File.Exists(Path.Combine(directory, "222.z0删1")));
            Assert.False(File.Exists(Path.Combine(directory, "222.z除02")));
            Assert.False(File.Exists(Path.Combine(directory, "222.z文03")));
            Assert.Equal(bodyHash, Sha256(body));
        }

        /// <summary>
        /// **对照**：续卷的名字本来就标准（`222.z01`…）⇒ 一个名字都不许改（⛔ 别把"没垃圾"也当成要改）。
        /// </summary>
        [Fact]
        public void 计划_本体干净且续卷也标准_一个名字都不改()
        {
            string directory = NewDirectory("clean-body-clean-volumes");

            string body = CreateFile(directory, "222.zip", 64, seed: 1);
            CreateFile(directory, "222.z01", 128, seed: 2);
            CreateFile(directory, "222.z02", 128, seed: 3);

            VolumeNameRepairPlan? plan = VolumeNameRepair.Plan(body, NamesIn(directory));

            Assert.True(plan == null || !plan.CanRepair, "名字本来就标准时不该出改名计划");

            // 名字一个都没动。
            Assert.True(File.Exists(Path.Combine(directory, "222.z01")));
            Assert.True(File.Exists(Path.Combine(directory, "222.z02")));
        }

        /// <summary>
        /// **绝不覆盖**：某个标准卷名已经被别的文件占着 ⇒ 整组不改、一个字节都不动（老红线）。
        /// </summary>
        [Fact]
        public void 计划_本体干净而目标卷名被占_整组不改()
        {
            string directory = NewDirectory("clean-body-target-taken");

            string body = CreateFile(directory, "222.zip", 64, seed: 1);
            CreateFile(directory, "222.z0删1", 128, seed: 2);
            string occupier = CreateFile(directory, "222.z01", 256, seed: 9);

            string occupierHash = Sha256(occupier);

            VolumeNameRepairPlan? plan = VolumeNameRepair.Plan(body, NamesIn(directory));

            Assert.True(plan == null || !plan.CanRepair, "目标卷名被占时必须拒绝");

            // 名字一个都没动、占位者内容不变。
            Assert.True(File.Exists(Path.Combine(directory, "222.z0删1")));
            Assert.Equal(occupierHash, Sha256(occupier));
        }

        // ================================================================ 反向改名（2026-10-04 只读探针量出来的硬缺陷）

        /// <summary>
        /// **入口给干净的第 1 卷**（用户 2026-10-04 点名的第一条）：同一组 <c>x.7z.001</c>（干净）
        /// + <c>x.7z.002.txt</c>（名字被改坏），计划**绝不许**把干净的那个改成脏名字
        /// （探针实测的老行为：`x.7z.001 → x.7z.001.txt` —— 方向反了）。
        ///
        /// <para>两种结果都算过：**只改脏的那一卷**（`x.7z.002.txt → x.7z.002`）或**整份计划不成立**
        /// （判不出就什么都不做）。⛔ 只有"把干净名改成脏名"这一种结果必须红。</para>
        ///
        /// <para>根因链四条（都在这一条里被挡住）：① 建议名从兄弟卷的名字推、**继承了它的脏尾巴**；
        /// ② <c>VolumeNameRepair.Plan</c> 里那道"兄弟基名逐字相等"用**剥标记档**比，跨段形状两边都
        /// 退化成 <c>x</c> ⇒ 放行；③ 缺"**建议名自己必须是规范名**"的第二道闸门；④
        /// <c>PlanDisguisedVolumesBesideStandardSelf</c> 拿**包名档**比**伪装卷档**（跨档）。</para>
        /// </summary>
        [Theory]
        [InlineData("x.7z", ".002.txt")]
        [InlineData("x.7z", ".002.bak")]
        [InlineData("x.7z", ".002删除")]
        public void 反向改名_入口给干净的第1卷_只许改脏的那一卷(string stem, string dirtyTail)
        {
            string directory = NewDirectory("reverse-clean-first-" + dirtyTail.Replace(".", string.Empty));

            string clean = CreateFile(directory, stem + ".001", 64, seed: 1);
            string dirty = CreateFile(directory, stem + dirtyTail, 64, seed: 2);

            VolumeNameRepairPlan plan = VolumeNameRepair.Plan(clean, NamesIn(directory));

            // ⛔ 这一条是被点名的那一格：绝不许把干净的名字改成脏名字。
            Assert.DoesNotContain(".001.txt", plan.SuggestedFileName);
            Assert.DoesNotContain(".001.bak", plan.SuggestedFileName);
            Assert.DoesNotContain(".001删除", plan.SuggestedFileName);
            Assert.DoesNotContain(
                plan.Items,
                item => string.Equals(item.CurrentFileName, stem + ".001", StringComparison.OrdinalIgnoreCase));

            if (plan.CanRepair)
            {
                // 能改时，改的必须是**脏的那一卷**，而且目标名是规范名。
                VolumeRepairItem only = Assert.Single(plan.Items);
                Assert.Equal(stem + dirtyTail, only.CurrentFileName);
                Assert.Equal(stem + ".002", only.SuggestedFileName);
            }

            // 计划阶段一个字节都不写：两个文件都还在原地、名字一个都没变。
            Assert.True(File.Exists(clean));
            Assert.True(File.Exists(dirty));
        }

        /// <summary>
        /// **入口给那个脏兄弟**（探针里本来就正确的那一格，照旧不许回退）：`x.7z.002.txt → x.7z.002`。
        /// </summary>
        [Fact]
        public void 反向改名_入口给脏兄弟_照旧给出正确方向()
        {
            string directory = NewDirectory("reverse-dirty-entry");

            CreateFile(directory, "x.7z.001", 64, seed: 1);
            string dirty = CreateFile(directory, "x.7z.002.txt", 64, seed: 2);

            VolumeNameRepairPlan plan = VolumeNameRepair.Plan(dirty, NamesIn(directory));

            Assert.True(plan.CanRepair, plan.Reason);
            Assert.Equal("x.7z.002", plan.SuggestedFileName);
            Assert.Equal(Path.Combine(directory, "x.7z.002"), plan.TargetPath);
        }

        /// <summary>
        /// **三卷混脏**（`.001` 标准 + `.002.txt` 跨段 + `.003` 标准）：探针实测老行为是
        /// <c>CanRepair=False</c>（"推不出标准名"）—— 同一份判据在不同夹具上时好时坏。
        /// 现在要求：**要么**只把脏的那一卷改成标准名、**要么**如实说不成立；
        /// ⛔ 两个干净卷的名字一个都不许动。
        /// </summary>
        [Fact]
        public void 反向改名_三卷混脏_不许退化成判不出()
        {
            string directory = NewDirectory("reverse-three-volumes");

            string v1 = CreateFile(directory, "111.7z.001", 64, seed: 1);
            string v2 = CreateFile(directory, "111.7z.002.txt", 64, seed: 2);
            string v3 = CreateFile(directory, "111.7z.003", 64, seed: 3);

            VolumeNameRepairPlan plan = VolumeNameRepair.Plan(v1, NamesIn(directory));

            Assert.True(plan.CanRepair, $"三卷里只有一卷名字脏，必须能修：{plan.Reason}");

            VolumeRepairItem only = Assert.Single(plan.Items);
            Assert.Equal("111.7z.002.txt", only.CurrentFileName);
            Assert.Equal("111.7z.002", only.SuggestedFileName);

            // 两个标准名的卷一个都没被列进计划（⛔ 更不许被当成"要改的"）。
            Assert.DoesNotContain(
                plan.Items,
                item => string.Equals(item.CurrentFileName, "111.7z.001", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(item.CurrentFileName, "111.7z.003", StringComparison.OrdinalIgnoreCase));

            Assert.True(File.Exists(v2));
            Assert.True(File.Exists(v3));
        }

        /// <summary>
        /// **第二道闸门的那一格**（"建议名自己必须是规范名"）：这一组里**干净的那一卷**是
        /// <c>x.001</c>（数字族第 1 卷），兄弟卷叫 <c>x.7z.002.txt</c>（名字被改坏）。
        ///
        /// <para>老行为：建议名从兄弟卷的名字推 ⇒ <c>x.7z.001.txt</c>，而"兄弟基名逐字相等"那道闸门
        /// 用剥标记档比时两边都退化成 <c>x</c> ⇒ **放行** ⇒ 计划是 <c>x.001 → x.7z.001.txt</c>
        /// （把干净名改成脏名）。</para>
        ///
        /// <para>⚠ 为什么用这个形状而不是 <c>x.7z.001</c> + <c>x.7z.002.txt</c>：后者现在被
        /// <see cref="VolumeNameRepair.PlanDisguisedVolumesBesideStandardSelf"/> 那条**同档比较**
        /// 当场接住（直接给出正确方向），走不到这条从名字推建议名的老路 —— 两条防线是叠加的，
        /// 这一条专钉**第二道**。</para>
        /// </summary>
        [Fact]
        public void 反向改名_建议名不是规范名时_整份计划不成立()
        {
            string directory = NewDirectory("reverse-gate2");

            string clean = CreateFile(directory, "x.001", 64, seed: 1);
            CreateFile(directory, "x.7z.002.txt", 64, seed: 2);

            VolumeNameRepairPlan plan = VolumeNameRepair.Plan(clean, NamesIn(directory));

            Assert.False(plan.CanRepair, $"建议名 `x.7z.001.txt` 不是规范名 ⇒ 整份计划不成立，实际：{plan.Describe()}");
            Assert.Equal(StatusText.VolumeRepairNoSuggestion, plan.Reason);
            Assert.True(File.Exists(clean));
        }

        // ================================================================ 末尾段本身脏 + 「整组自洽」（2026-10-04 第三轮）

        /// <summary>
        /// ① **末尾段本身脏**（用户点名的 `444.pa8rt1.rar`）+ 同目录真有一组自洽的兄弟卷
        /// （`444.pa8rt2.rar`，末片更小）⇒ 骨架档读出卷标记 `part1` / `part2`，**整组自洽**成立
        /// ⇒ 两卷都改回 `444.part1.rar` / `444.part2.rar`，基名 `444`。
        ///
        /// <para>用户 2026-10-04 原话：「`444.pa8rt1.rar` 和 `444.p1art2.part2.rar`，不不不，你不会觉得这两个
        /// 会放在一起吧……实际情况下那两个东西不可能是同一个包」⇒ 判据换成**整组自洽**（不听形状）。</para>
        /// </summary>
        [Fact]
        public void 整组自洽_末尾段真脏的一对_两卷都改回标准名()
        {
            string directory = NewDirectory("skeleton-part-pair");

            string v1 = CreateFile(directory, "444.pa8rt1.rar", 4096, seed: 1);
            string v2 = CreateFile(directory, "444.pa8rt2.rar", 2048, seed: 2);

            string h1 = Sha256(v1);
            string h2 = Sha256(v2);

            // 名字级：卷标记靠骨架档才读得出来（末段逐字不是合法卷标记）。
            Assert.True(ExtensionHelper.IsPartNumberedMarkBySkeleton("444.pa8rt1.rar", out string mark));
            Assert.Equal("part1", mark);
            Assert.Equal("444", FileNameHelper.StripVolumeMarkers("444.pa8rt1.rar"));
            Assert.Equal("444", OutputPlacement.ResolveArchiveBaseName("444.pa8rt1.rar"));

            VolumeNameRepairPlan plan = VolumeNameRepair.Plan(v1, NamesIn(directory));

            Assert.True(plan.CanRepair, plan.Reason);
            Assert.Equal(2, plan.Items.Count);
            Assert.Equal(
                new[] { "444.part1.rar", "444.part2.rar" },
                plan.Items.Select(i => i.SuggestedFileName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray());

            VolumeNameRepairResult result = VolumeNameRepair.TryApply(plan);

            Assert.True(result.Success, result.Message);
            Assert.True(File.Exists(Path.Combine(directory, "444.part1.rar")));
            Assert.True(File.Exists(Path.Combine(directory, "444.part2.rar")));
            Assert.False(File.Exists(v1));
            Assert.False(File.Exists(v2));

            // 只改名字（内容逐字节不变），基名是 `444`。
            Assert.Equal(h1, Sha256(Path.Combine(directory, "444.part1.rar")));
            Assert.Equal(h2, Sha256(Path.Combine(directory, "444.part2.rar")));
        }

        /// <summary>
        /// ①b 更常见的形状：**只有中间那一卷的名字脏**（`444.part1.rar` 干净 + `444.pa8rt2.rar` 脏）
        /// ⇒ 整组自洽成立 ⇒ **只改脏的那一卷**，干净的那一卷一个字节都不动。
        /// </summary>
        [Fact]
        public void 整组自洽_只有一卷脏_只改那一卷()
        {
            string directory = NewDirectory("skeleton-part-mixed");

            string clean = CreateFile(directory, "444.part1.rar", 4096, seed: 1);
            string dirty = CreateFile(directory, "444.pa8rt2.rar", 2048, seed: 2);

            string cleanHash = Sha256(clean);

            VolumeNameRepairPlan plan = VolumeNameRepair.Plan(dirty, NamesIn(directory));

            Assert.True(plan.CanRepair, plan.Reason);

            VolumeRepairItem only = Assert.Single(plan.Items);
            Assert.Equal("444.pa8rt2.rar", only.CurrentFileName);
            Assert.Equal("444.part2.rar", only.SuggestedFileName);

            Assert.True(VolumeNameRepair.TryApply(plan).Success);

            Assert.True(File.Exists(Path.Combine(directory, "444.part2.rar")));
            Assert.True(File.Exists(clean));
            Assert.Equal(cleanHash, Sha256(clean));
            Assert.False(File.Exists(dirty));
        }

        /// <summary>
        /// ② **对照（防回归的命根）**：`444.p1art2.part2.rar` 的末尾段**逐字就是**合法卷标记
        /// ⇒ 一律走老口径、⛔ 不吃骨架档 ⇒ 基名保持 `444.p1art2`、**一句话都不改**
        /// （哪怕旁边真配着 `444.p1art2.part1.rar`）。
        /// </summary>
        [Fact]
        public void 整组自洽_末尾段逐字干净时_一个字都不改()
        {
            string directory = NewDirectory("skeleton-part-clean-tail");

            string v1 = CreateFile(directory, "444.p1art2.part1.rar", 4096, seed: 1);
            string v2 = CreateFile(directory, "444.p1art2.part2.rar", 2048, seed: 2);

            // 名字级：末尾那段逐字干净 ⇒ 不看骨架档。
            Assert.False(ExtensionHelper.IsPartNumberedMarkBySkeleton("444.p1art2.part2.rar", out _));
            Assert.Equal("444.p1art2", FileNameHelper.StripVolumeMarkers("444.p1art2.part2.rar"));
            Assert.Equal("444.p1art2", OutputPlacement.ResolveArchiveBaseName("444.p1art2.part2.rar"));
            Assert.Equal(2, VolumeGroupDetector.TryGetVolumeIndex("444.p1art2.part2.rar"));
            Assert.Equal("444.p1art2.part1.rar", VolumeGroupDetector.TryGetFirstVolumeName("444.p1art2.part2.rar"));

            // 第一卷第名字本来就标准 ⇒ 不改；后续卷那一单也不改（改名解决不了"缺第一卷"）。
            VolumeNameRepairPlan first = VolumeNameRepair.Plan(v1, NamesIn(directory));
            VolumeNameRepairPlan second = VolumeNameRepair.Plan(v2, NamesIn(directory));

            Assert.False(first.CanRepair, first.Describe());
            Assert.False(second.CanRepair, second.Describe());

            Assert.True(File.Exists(v1));
            Assert.True(File.Exists(v2));
            Assert.False(File.Exists(Path.Combine(directory, "444.part1.rar")));
        }

        /// <summary>
        /// ③ 孤零零一个 `444.pa8rt1.rar`（同目录里没有兄弟）⇒ **也要改名**（用户 2026-10-06 拍板）。
        ///
        /// <para>⚠ <b>本条与旧口径正面冲突，按新指令重写</b>（旧断言是「配不出自洽的一整组 ⇒ 一个字都不改」，
        /// 见 `修改日志.md` 2026-10-04 第六轮）。用户原话：「**只修一卷这是你私自弄的，重大危险**」
        /// 「我们攻破伪装不就是要将其改为标准名字吗」 —— 改名只动**卷标记那一段**：基名不动、族不动、
        /// 卷序不动、内容一个字节不动、可逆，且**绝不覆盖**；这与"缺不缺它那几个兄弟"无关。</para>
        ///
        /// <para>⛔ 真正不许放宽的是另一半：**干净的名字绝不许改成脏名字**（那条守门用例在下一条）。</para>
        /// </summary>
        [Fact]
        public void 续卷改名_孤零零一个脏卷标记_照样改回标准名()
        {
            string directory = NewDirectory("skeleton-part-lone");
            string lone = CreateFile(directory, "444.pa8rt1.rar", 4096, seed: 1);
            string before = Sha256(lone);

            VolumeNameRepairPlan plan = VolumeNameRepair.Plan(lone, NamesIn(directory));

            Assert.True(plan.CanRepair, plan.Describe());
            Assert.Equal("444.part1.rar", plan.SuggestedFileName);

            // 只改这一个名字，而且**只动卷标记那一段**（基名 `444` 一个字不动）。
            Assert.Single(plan.Items);
            Assert.Equal("444", FileNameHelper.GetArchiveBaseName("444.pa8rt1.rar"));

            Assert.True(File.Exists(lone), "计划阶段一个字节都不许动盘上的文件");
            Assert.Equal(before, Sha256(lone));
        }

        /// <summary>
        /// ③b 有兄弟但**卷标记连不成 1..N**（`444.pa8rt2.rar` + `444.pa8rt4.rar`，缺 1 与 3）——
        /// ⚠ <b>按用户 2026-10-06 的新指令改口径</b>：单卷改名只动**卷标记那一段**，
        /// 与"这一组连不连得成 1..N"无关 ⇒ **各自照样改回标准名**（`part2` / `part4`）。
        /// 尺寸不规律同理。⛔ 真正不许放宽的是「干净的名字不许改成脏名字」那一条。
        /// </summary>
        [Fact]
        public void 续卷改名_卷标记不连续或尺寸不规律_照样各改各的()
        {
            string notContiguous = NewDirectory("skeleton-part-not-contiguous");
            string a = CreateFile(notContiguous, "444.pa8rt2.rar", 2048, seed: 1);
            CreateFile(notContiguous, "444.pa8rt4.rar", 1024, seed: 2);

            VolumeNameRepairPlan planA = VolumeNameRepair.Plan(a, NamesIn(notContiguous));

            Assert.True(planA.CanRepair, planA.Describe());
            Assert.Equal("444.part2.rar", planA.SuggestedFileName);

            string irregular = NewDirectory("skeleton-part-irregular-size");
            string b = CreateFile(irregular, "444.pa8rt1.rar", 1024, seed: 1);
            CreateFile(irregular, "444.pa8rt2.rar", 4096, seed: 2);

            VolumeNameRepairPlan planB = VolumeNameRepair.Plan(b, NamesIn(irregular));

            Assert.True(planB.CanRepair, planB.Describe());
            Assert.Equal("444.part1.rar", planB.SuggestedFileName);

            Assert.True(File.Exists(a), "计划阶段一个字节都不许动盘上的文件");
            Assert.True(File.Exists(b));
        }

        // ══════════════════ 闸门扩面：**所有"猜出来的"名字**都要过「整组自洽」（2026-10-04 第六轮） ══════════════════

        /// <summary>
        /// ① **7z 数字族的卷标记段脏**（`set.7z.0a0b1`：卷标记靠容错/骨架档读成 `001`）
        /// **孤零零一个 ⇒ 一个字都不改** —— 与 partN 那一档同一道闸门
        /// （<c>VolumeNameRepair.IsGuessedVolumeName</c> 转调 <c>ExtensionHelper.IsVolumeMarkByDisguise</c>）。
        ///
        /// <para>⚠ <b>本条与旧口径正面冲突，按新指令重写</b>（旧断言是「孤立一个 ⇒ 不改」，
        /// 原话见 `修改日志.md` 2026-10-04 第六轮）。用户 2026-10-06 的新指令覆盖它：
        /// 容忍解析/骨架命中算得出规范卷标记 ⇒ **任何一卷都改回标准名**。</para>
        /// </summary>
        [Fact]
        public void 续卷改名_数字族脏卷标记_孤立一个也改回标准名()
        {
            string directory = NewDirectory("numeric-mark-lone");
            string lone = CreateFile(directory, "set.7z.0a0b1", 4096, seed: 1);

            string before = Sha256(lone);

            VolumeNameRepairPlan plan = VolumeNameRepair.Plan(lone, NamesIn(directory));

            Assert.True(plan.CanRepair, plan.Describe());
            Assert.Equal("set.7z.001", plan.SuggestedFileName);
            Assert.Single(plan.Items);

            Assert.True(File.Exists(lone), "计划阶段一个字节都不许动盘上的文件");
            Assert.Equal(before, Sha256(lone));
        }

        /// <summary>
        /// ② **对照**：`set.7z.001`（干净首卷）+ `set.7z.0a0b2`（卷标记段脏）⇒ 配得出整组
        /// ⇒ 照旧出计划，而且**只改脏的那一卷**，干净的那一卷一个字节都不动。
        /// </summary>
        [Fact]
        public void 整组自洽_数字族脏卷标记_配得出组就只改那一卷()
        {
            string directory = NewDirectory("numeric-mark-pair");

            string clean = CreateFile(directory, "set.7z.001", 1024, seed: 1);
            string dirty = CreateFile(directory, "set.7z.0a0b2", 512, seed: 2);

            string cleanHash = Sha256(clean);

            VolumeNameRepairPlan plan = VolumeNameRepair.Plan(clean, NamesIn(directory));

            Assert.True(plan.CanRepair, $"配得出整组 ⇒ 该改，实际：{plan.Describe()}");

            VolumeRepairItem only = Assert.Single(plan.Items);
            Assert.Equal("set.7z.0a0b2", only.CurrentFileName);
            Assert.Equal("set.7z.002", only.SuggestedFileName);

            Assert.True(VolumeNameRepair.TryApply(plan).Success);

            Assert.True(File.Exists(Path.Combine(directory, "set.7z.002")));
            Assert.False(File.Exists(dirty));
            Assert.True(File.Exists(clean), "干净的那一卷原地不动");
            Assert.Equal(cleanHash, Sha256(clean));
        }

        // ══════════════════ 「只脏归档后缀段」进计划（2026-10-04 第六轮） ══════════════════

        /// <summary>
        /// **只脏归档后缀段**（`set.7aaaaz.002` / `set.7_______z.002` / `333.78a8fuaz.003`）：
        /// 骨架档读出那一段的真实身份 ⇒ 规范名 = <c>&lt;基名&gt;.7z.&lt;卷标记&gt;</c>
        /// （**替换不追加**、基名一个字不动）。
        ///
        /// <para>用户 2026-10-04 第六轮原话：「把这一形状接进改名计划……判据照既有口径、⛔ 不许新造第三把尺子」。
        /// 判据落在 <c>FileNameHelper.TryResolveDisguisedVolume</c> 新增的形状③（转调
        /// <c>ExtensionHelper.TryRecoverDisguisedArchiveBody</c>）。</para>
        /// </summary>
        [Theory]
        [InlineData("set.7z", "set.7aaaaz.002", "set.7z.002", 2)]
        [InlineData("set.7z", "set.7_______z.002", "set.7z.002", 2)]
        [InlineData("333.7z", "333.78a8fuaz.003", "333.7z.003", 3)]
        public void 只脏归档后缀段_给出规范名(string stem, string dirtyName, string expectedName, int dirtyIndex)
        {
            string directory = NewDirectory("archive-segment-" + dirtyName.Replace(".", "_"));

            // 一整组四卷（1..4 连续、除末片外等大）—— 把**中间那一卷**改成脏名。
            string first = CreateFile(directory, stem + ".001", 1024, seed: 1);
            CreateFile(directory, stem + ".002", 1024, seed: 2);
            CreateFile(directory, stem + ".003", 1024, seed: 3);
            string last = CreateFile(directory, stem + ".004", 512, seed: 4);

            string dirty = Path.Combine(directory, dirtyName);
            File.Move(Path.Combine(directory, stem + "." + dirtyIndex.ToString("000", System.Globalization.CultureInfo.InvariantCulture)), dirty);

            Assert.True(
                ExtensionHelper.IsArchiveSegmentByDisguise(dirtyName, out string recovered),
                $"`{dirtyName}` 的归档后缀段应当靠容错/骨架档读得出来");
            Assert.Equal("7z", recovered);

            string dirtyHash = Sha256(dirty);

            VolumeNameRepairPlan plan = VolumeNameRepair.Plan(first, NamesIn(directory));

            Assert.True(plan.CanRepair, $"这一组必须出得来计划，实际：{plan.Describe()}");

            VolumeRepairItem only = Assert.Single(plan.Items);
            Assert.Equal(dirtyName, only.CurrentFileName);
            Assert.Equal(expectedName, only.SuggestedFileName);

            // 包基名一个字不动（`set` / `333`）—— 只换那一段。
            Assert.Equal(
                OutputPlacement.ResolveArchiveBaseName(first),
                OutputPlacement.ResolveArchiveBaseName(only.SuggestedFileName));

            VolumeNameRepairResult applied = VolumeNameRepair.TryApply(plan);

            Assert.True(applied.Success, applied.Message);
            Assert.True(File.Exists(Path.Combine(directory, expectedName)));
            Assert.False(File.Exists(dirty));

            // 只改名字：内容逐字节不变；另外两卷原地不动。
            Assert.Equal(dirtyHash, Sha256(Path.Combine(directory, expectedName)));
            Assert.True(File.Exists(first));
            Assert.True(File.Exists(last));
        }

        /// <summary>
        /// **反例**（用户 2026-10-04 第六轮点名）：`x.rarity` / `x.zipper` 这种"恰好含 rar / zip 三个字母"
        /// 的段**连命中都不算**（骨架没走到段末）⇒ 一个名字都不改。
        ///
        /// <para>与 <c>VolumeSkeletonMatchTests.放宽的代价_含rar或zip字母的普通文件名</c> 同一口径，
        /// 这里补的是**新形状那一格**：`<基名>.rarity.002` / `<基名>.zipper.002` 也不许被当成"脏归档后缀段"。</para>
        /// </summary>
        [Theory]
        [InlineData("set.rarity.002")]
        [InlineData("set.zipper.002")]
        [InlineData("set.7zip.002")]     // 同时命中 7z 与 zip ⇒ 歧义 ⇒ 判不出
        public void 只脏归档后缀段_反例_半个骨架不算命中(string mimicName)
        {
            string directory = NewDirectory("archive-segment-mimic-" + mimicName.Replace(".", "_"));

            string first = CreateFile(directory, "set.7z.001", 1024, seed: 1);
            string mimic = CreateFile(directory, mimicName, 1024, seed: 2);
            CreateFile(directory, "set.7z.003", 512, seed: 3);

            Assert.False(
                ExtensionHelper.IsArchiveSegmentByDisguise(mimicName, out _),
                $"`{mimicName}` 不该被当成「脏归档后缀段」（骨架没走到段末 / 有歧义）");

            VolumeNameRepairPlan plan = VolumeNameRepair.Plan(first, NamesIn(directory));

            // ⛔ 那个仿名一个字节都不许动；计划里也不许出现它。
            Assert.DoesNotContain(
                plan.Items,
                item => string.Equals(item.CurrentFileName, mimicName, StringComparison.OrdinalIgnoreCase));

            Assert.True(File.Exists(mimic));
        }

        // ============================== 续卷的名字被改坏：任何一卷都要能改回标准名（用户 2026-10-06 拍板）

        /// <summary>
        /// **续卷的卷标记段自己粘着垃圾 ⇒ 也要改回标准名**（真机 CCCC 的 `111(3)\111.z0删除2` /
        /// `111(4)\111.z0删除3`）。
        ///
        /// <para>用户原话：「**只修一卷这是你私自弄的，重大危险**」「我们攻破伪装不就是要将其改为标准名字吗」。
        /// 容忍解析/骨架命中**早就认得出**这两片是 `111.z02` / `111.z03`
        /// （<c>ExtensionHelper</c> 的注释里逐字就是这个例子），过去被"必须是第 1 卷"那一行挡回去了。</para>
        ///
        /// <para><b>红检</b>：把 <c>PlanDisguisedRenamedSelf</c> 那一调撤掉（回到"只有第 1 卷能修"）
        /// ⇒ 前两条断言当场变红（`CanRepair=False` + 理由「不是第一卷」）。</para>
        /// </summary>
        [Theory]
        [InlineData("111.z0删除2", "111.z02")]
        [InlineData("111.z0删除3", "111.z03")]
        [InlineData("set.7z.00删2", "set.7z.002")]
        [InlineData("111.part删2.ra除r", "111.part2.rar")]
        public void 计划_续卷的卷标记粘着垃圾_照样推出标准名(string mangledName, string expected)
        {
            string directory = NewDirectory("续卷改名-" + expected.Replace('.', '_') + "-" + Guid.NewGuid().ToString("N")[..6]);

            string mangled = CreateFile(directory, mangledName, 4096);

            VolumeNameRepairPlan plan = VolumeNameRepair.Plan(mangled, NamesIn(directory));

            Assert.True(plan.CanRepair, plan.Reason);
            Assert.Equal(expected, plan.SuggestedFileName);
            Assert.Equal(Path.Combine(directory, expected), plan.TargetPath);

            // 只改这一个名字（⛔ 不许顺手把别人也改一遍）。
            Assert.Single(plan.Items);
            Assert.Equal(mangledName, plan.Items[0].CurrentFileName);
        }

        /// <summary>
        /// ⛔ **不许把已经标准的名字当成"待改"**，⛔ **不许把脏名写回去**，⛔ **目标名被占就什么都不做**
        /// —— 三条兜底都落在"什么都不做"那一档。
        /// </summary>
        [Fact]
        public void 计划_续卷改名_三条兜底都不许动别人的东西()
        {
            // ① 名字本来就标准 ⇒ 没有可修的东西。
            string standardDirectory = NewDirectory("续卷改名-已标准");
            string standard = CreateFile(standardDirectory, "111.z02", 4096);

            VolumeNameRepairPlan already = VolumeNameRepair.Plan(standard, NamesIn(standardDirectory));

            Assert.False(already.CanRepair);

            // ② 目标名被占（`111.z02` 已经在了）⇒ 什么都不做，⛔ 绝不覆盖。
            string takenDirectory = NewDirectory("续卷改名-目标被占");
            string mangled = CreateFile(takenDirectory, "111.z0删除2", 4096, seed: 7);

            CreateFile(takenDirectory, "111.z02", 4096, seed: 8);

            VolumeNameRepairPlan taken = VolumeNameRepair.Plan(mangled, NamesIn(takenDirectory));

            Assert.False(taken.CanRepair);
            Assert.Contains("111.z02", taken.Reason, StringComparison.Ordinal);

            // ③ 普通包（名字里没有卷标记）⇒ 这条路一个字都不改（那是"修正后缀"那条路的事）。
            string plainDirectory = NewDirectory("续卷改名-普通包");
            string plain = CreateFile(plainDirectory, "111.zip", 4096);

            VolumeNameRepairPlan notVolume = VolumeNameRepair.Plan(plain, NamesIn(plainDirectory));

            Assert.False(notVolume.CanRepair);
            Assert.Empty(notVolume.Items);
            Assert.True(File.Exists(plain), "普通包在这条路上一个字节都不许动");
        }

        private string NewDirectory(string name)
        {
            string path = Path.Combine(_root, name);
            Directory.CreateDirectory(path);
            return path;
        }

        private static string CreateFile(string directory, string fileName, int size, int seed = 1)
        {
            string path = Path.Combine(directory, fileName);
            var bytes = new byte[size];
            new Random(seed).NextBytes(bytes);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        private static IEnumerable<string?> NamesIn(string directory) =>
            Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly).Select(Path.GetFileName);

        private static string Sha256(string path) =>
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

        /// <summary>在分卷目录里造一个 3 MiB 的载荷（**只传文件名**给 7z，免得把整条路径存进包里）。</summary>
        private static (string Name, byte[] Bytes) CreatePayload(string directory, int size)
        {
            const string name = "a.bin";

            var bytes = new byte[size];
            new Random(20260925).NextBytes(bytes);
            File.WriteAllBytes(Path.Combine(directory, name), bytes);

            return (name, bytes);
        }

        /// <summary>造一组真 7z 分卷（<c>&lt;name&gt;.001/.002/.003</c>）。</summary>
        private void CreateVolumeSet(string directory, string archiveName, string payloadName, string volumeSize)
        {
            var psi = new ProcessStartInfo(_sevenZip!)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = directory
            };

            psi.ArgumentList.Add("a");
            psi.ArgumentList.Add("-t7z");
            psi.ArgumentList.Add("-mx0");
            psi.ArgumentList.Add("-v" + volumeSize);
            psi.ArgumentList.Add(Path.Combine(directory, archiveName));
            psi.ArgumentList.Add(payloadName);

            using Process process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 7z.exe");

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();

            Assert.True(process.WaitForExit(120_000), "7z 造分卷超时");
            Assert.True(process.ExitCode == 0, $"7z 造分卷失败：{stdout}{stderr}");

            Assert.True(File.Exists(Path.Combine(directory, archiveName + ".001")), "第一卷没造出来");
            Assert.True(File.Exists(Path.Combine(directory, archiveName + ".002")), "第二卷没造出来");
            Assert.True(File.Exists(Path.Combine(directory, archiveName + ".003")), "第三卷没造出来");
        }

        /// <summary>
        /// **协调器那一层的入口判据**（真机 DDD 的另一半）：本体是标准名（`222.zip` —— 它**不是**卷标记）时，
        /// 批首那道"整组改名"必须**照样跑**。
        ///
        /// <para>修之前那个入口是 `IsVolumePartFileName(自己的名字)` ⇒ `.zip` 判 false ⇒ 名字路**压根不跑**
        /// （只靠单元测试钉 `Plan` 是不够的：计划再好，调用方不问也是白搭）。</para>
        ///
        /// <para>同时钉"任务账跟着改"：`SyncTasksAfterVolumeRename` 必须把 `VolumePaths` 里的旧名换成新名
        /// —— 否则这一单后面按旧名字找卷、报「分卷缺失」。</para>
        /// </summary>
        [Fact]
        public async Task 批首整组改名_本体是标准zip时也要跑_盘上与任务账一起改成标准名()
        {
            Harness harness = CreateHarness();
            string directory = NewDirectory("coordinator-disguised-volumes");

            string v1 = CreateFile(directory, "222.z0删1", 128, seed: 2);
            string v2 = CreateFile(directory, "222.z除02", 128, seed: 3);
            string v3 = CreateFile(directory, "222.z文03", 128, seed: 4);
            string body = CreateFile(directory, "222.zip", 64, seed: 1);

            var task = new ArchiveTask(body) { IsVolumeGroup = true };
            task.VolumePaths.Add(v1);
            task.VolumePaths.Add(v2);
            task.VolumePaths.Add(v3);
            task.VolumePaths.Add(body);

            await harness.Coordinator.NormalizeDisguisedVolumeNamesForBatchAsync(new[] { task });

            // 盘上：三个标准卷名都在、旧名都没了、**本体与内容一个字节没动**。
            Assert.True(File.Exists(Path.Combine(directory, "222.z01")), "续卷没被改回标准名");
            Assert.True(File.Exists(Path.Combine(directory, "222.z02")));
            Assert.True(File.Exists(Path.Combine(directory, "222.z03")));
            Assert.False(File.Exists(v1));
            Assert.False(File.Exists(v2));
            Assert.False(File.Exists(v3));
            Assert.True(File.Exists(body));

            // 任务账也跟上了（否则后面按旧名字找卷 ⇒ 假报缺卷）。
            Assert.Contains(Path.Combine(directory, "222.z01"), task.VolumePaths);
            Assert.DoesNotContain(v1, task.VolumePaths);
            Assert.True(task.VolumeNameAutoRenamed);
        }

        /// <summary>
        /// **同一族的另一个形状**（提前钉住，免得用户下次换个样本又踩）：本体干净（`111.part1.rar`）、
        /// 后面几卷的名字里被塞了字（`111.part删2.rar` / `111.part文3.rar`）。
        ///
        /// <para>与 ZIP 那条的区别：RAR 的卷标记是 `partN`（后面还跟着归档后缀 `.rar`），
        /// 而"去杂质"那一步（<c>TrySplitDisguised</c>）只认"卷标记后面没有别的点段"的形状；
        /// 所以这一档**不一定**走得通名字路 —— 走不通时必须老老实实返回"不改"，
        /// 由内容那条路（RAR 的卷号写在内容里）接手。⛔ 无论走哪条，都不许改错名字。</para>
        /// </summary>
        [Fact]
        public void 计划_本体干净的RAR而续卷带垃圾_要么整组改对要么什么都不改()
        {
            string directory = NewDirectory("clean-rar-body-disguised-volumes");

            string first = CreateFile(directory, "111.part1.rar", 64, seed: 1);
            string second = CreateFile(directory, "111.part删2.rar", 128, seed: 2);
            string third = CreateFile(directory, "111.part文3.rar", 128, seed: 3);

            string firstHash = Sha256(first);

            VolumeNameRepairPlan? plan = VolumeNameRepair.Plan(first, NamesIn(directory));

            if (plan == null || !plan.CanRepair)
            {
                // 判不出 ⇒ 一个名字都不许改（后面交给内容那条路）。
                Assert.True(File.Exists(second));
                Assert.True(File.Exists(third));
                Assert.Equal(firstHash, Sha256(first));
                return;
            }

            // 走通了就必须**改对**：每一卷的目标名都等于 基名 + 自己的标准卷段。
            Assert.All(
                plan.Items,
                item => Assert.Matches(@"^111\.part[1-9][0-9]*\.rar$", item.SuggestedFileName));

            VolumeNameRepairResult result = VolumeNameRepair.TryApply(plan);

            Assert.True(result.Success, result.Message);
            Assert.True(File.Exists(Path.Combine(directory, "111.part1.rar")));
            Assert.True(File.Exists(Path.Combine(directory, "111.part2.rar")));
            Assert.True(File.Exists(Path.Combine(directory, "111.part3.rar")));
            Assert.False(File.Exists(second));
            Assert.False(File.Exists(third));
            Assert.Equal(firstHash, Sha256(first));
        }

        private Harness CreateHarness()
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
            settings.RecursionMode = "SingleLayer";
            settings.AutoScanAfterDrop = false;
            settings.MaxParallelExtractCount = 1;

            // 源包档位固定"留在原地"：这一组测的是改名，不该顺带测源包搬运 / 删除。
            settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
            settings.RestHandlingAfterVerify = RestHandlingModes.Keep;

            settingsService.Save(settings);

            var engine = new Engines.SevenZip.SevenZipEngine();
            var logService = new LogService(pathService);

            string? previousWorkspaceRoot = RecursiveExtractor.ConfiguredWorkspaceRoot;

            var vm = new MainViewModel(
                new FileScanService(),
                new ArchiveDetectService(),
                new RenameService(),
                engine,
                new PasswordService(),
                logService,
                settingsService,
                pathService,
                new TaskSummaryService(),
                new ClipboardService(),
                new ConfirmingDialogService());

            RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;

            var coordinator = new ExtractionCoordinator(
                vm,
                engine,
                new PasswordService(),
                pathService,
                new ConfirmingDialogService());

            return new Harness(vm, coordinator, logService, outputRoot);
        }

        private void RequireSevenZip()
        {
            if (string.IsNullOrEmpty(_sevenZip))
            {
                throw new InvalidOperationException(
                    "测试机上没有 7z.exe（ArchiveFixer/tools/7zip/7z.exe），本用例无法运行。");
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

        /// <summary>无界面宿主里默认是"没人点过 = 不确认"，改名那条路就走不到了 —— 这里注入"用户点了确定"。</summary>
        private sealed class ConfirmingDialogService : DialogService
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
            public Harness(MainViewModel vm, ExtractionCoordinator coordinator, LogService log, string outputRoot)
            {
                Vm = vm;
                Coordinator = coordinator;
                Log = log;
                OutputRoot = outputRoot;
            }

            public MainViewModel Vm { get; }

            public ExtractionCoordinator Coordinator { get; }

            public LogService Log { get; }

            public string OutputRoot { get; }

            public IEnumerable<string> LogTexts => Log.Logs.Select(item => item.DisplayText);
        }
    }
}

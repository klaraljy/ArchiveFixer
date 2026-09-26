using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **第 42 条的全自动拼装**（用户 2026-09-25 选的方案 A：做，**默认关**）：
    /// 容器里装的是分卷第 1 卷、后续卷在容器外面时，在工作区里接出一套"名字成套、同在一个目录"的卷。
    ///
    /// <para>他确认时的原话两段：①"分卷多了不要紧，你只要对着 001 使用解压就行了，因为 001 如果扫描到
    /// 同目录的数据包就解压，扫不到就报错"（→ 族过滤：只接与容器里那一段**同一族**的那一组）；
    /// ②"同盘硬链接优先也就是想同一个目录里面有一组，而且仅仅只有一组而且数据包完善就可以解压" ——
    /// 硬链接的要点不是"更快"，而是"**在不动源文件的前提下把名字与目录凑齐**"。</para>
    ///
    /// <para>这一组钉四件事：①判据（族 / 唯一 / 0 字节 / 目标目录）；②硬链接真的不复制字节
    /// （用"改一个名字能看到另一个名字的变化"来证明是同一份数据）；③跨盘复制**先查空间**，
    /// 不够就一个字都不写；④真 7z 端到端：开关关 = 与今天逐字相同（如实报「分卷缺失」），
    /// 开关开 = 解开**真内容**且源目录一个字节不变。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class SplitVolumeAssemblyTests : IDisposable
    {
        private readonly string _root;
        private readonly string? _sevenZip;

        public SplitVolumeAssemblyTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerSplitAssemble", Guid.NewGuid().ToString("N"));
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
            }
        }

        // ================================================================ 判据（纯逻辑）

        [Fact]
        public void 族判定_魔数与首卷名两条路()
        {
            string directory = Path.Combine(_root, "family-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            string sevenZip = Path.Combine(directory, "a.7z");
            string rar = Path.Combine(directory, "a.rar");
            string zip = Path.Combine(directory, "a.zip");
            string other = Path.Combine(directory, "a.bin");

            File.WriteAllBytes(sevenZip, new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x04 });
            File.WriteAllBytes(rar, new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 });
            File.WriteAllBytes(zip, new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x00, 0x00 });
            File.WriteAllBytes(other, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

            Assert.Equal(SplitVolumeFamily.SevenZip, SplitVolumeAssembler.DetectFamilyByMagic(sevenZip));
            Assert.Equal(SplitVolumeFamily.Rar, SplitVolumeAssembler.DetectFamilyByMagic(rar));
            Assert.Equal(SplitVolumeFamily.Zip, SplitVolumeAssembler.DetectFamilyByMagic(zip));
            Assert.Equal(SplitVolumeFamily.Unknown, SplitVolumeAssembler.DetectFamilyByMagic(other));
            Assert.Equal(SplitVolumeFamily.Unknown, SplitVolumeAssembler.DetectFamilyByMagic(null));

            Assert.Equal(SplitVolumeFamily.SevenZip, SplitVolumeAssembler.FamilyOfFirstVolumeName("set.7z.001"));
            Assert.Equal(SplitVolumeFamily.Rar, SplitVolumeAssembler.FamilyOfFirstVolumeName("set.part1.rar"));
            Assert.Equal(SplitVolumeFamily.Rar, SplitVolumeAssembler.FamilyOfFirstVolumeName("set.rar"));
            Assert.Equal(SplitVolumeFamily.Zip, SplitVolumeAssembler.FamilyOfFirstVolumeName("set.zip"));

            // ⚠ 末段是纯数字（archive.001）时推不出族：通用分片也是这个名字，而它拼起来不是解压的输入。
            Assert.Equal(SplitVolumeFamily.Unknown, SplitVolumeAssembler.FamilyOfFirstVolumeName("archive.001"));
        }

        [Fact]
        public void 计划_同族且唯一一组时成立_首卷用标准名()
        {
            (string carved, string sourceDirectory) = BuildCarved(1024);

            WriteVolumes(sourceDirectory, "set.7z", 2, 3);

            SplitVolumeAssemblyPlan plan = SplitVolumeAssembler.Plan(
                carved,
                SplitVolumeFamily.SevenZip,
                sourceDirectory,
                Path.Combine(_root, "assemble-ok"),
                new[] { "set.7z.002", "set.7z.003", "说明.txt" });

            Assert.True(plan.CanAssemble, plan.Reason);
            Assert.Equal("set.7z.001", plan.FirstVolumeName);
            Assert.Equal(3, plan.Entries.Count);
            Assert.True(plan.Entries[0].IsFirstVolume);
            Assert.Equal(carved, plan.Entries[0].SourcePath);
            Assert.Equal(new[] { "set.7z.002", "set.7z.003" }, plan.Entries.Skip(1).Select(entry => entry.TargetName));
        }

        /// <summary>
        /// 用户原话："分卷多了不要紧" —— 目录里有别的族的分卷**不影响**（引擎自己会找同组的）。
        /// 只有**同族**还两组以上时才不动。
        /// </summary>
        [Fact]
        public void 计划_别的族的组不算干扰_同族两组才拒绝()
        {
            (string carved, string sourceDirectory) = BuildCarved(1024);

            WriteVolumes(sourceDirectory, "set.7z", 2, 3);
            WriteVolumes(sourceDirectory, "别的包.part", 2, 3, suffix: ".rar");

            // 目录里另有一组 RAR 的分卷（缺首卷），而容器里那一段是 7z → 不影响。
            SplitVolumeAssemblyPlan mixed = SplitVolumeAssembler.Plan(
                carved,
                SplitVolumeFamily.SevenZip,
                sourceDirectory,
                Path.Combine(_root, "assemble-mixed"),
                new[] { "set.7z.002", "set.7z.003", "别的包.part2.rar", "别的包.part3.rar" });

            Assert.True(mixed.CanAssemble, mixed.Reason);
            Assert.Equal("set.7z.001", mixed.FirstVolumeName);

            WriteVolumes(sourceDirectory, "另一个.7z", 2, 3);

            // 同族两组都缺首卷 → 无法确定容器里是哪一组，⛔ 不猜。
            SplitVolumeAssemblyPlan ambiguous = SplitVolumeAssembler.Plan(
                carved,
                SplitVolumeFamily.SevenZip,
                sourceDirectory,
                Path.Combine(_root, "assemble-ambiguous"),
                new[] { "set.7z.002", "set.7z.003", "另一个.7z.002", "另一个.7z.003" });

            Assert.False(ambiguous.CanAssemble);
            Assert.Contains("同一族", ambiguous.Reason);
            Assert.Contains("不接", ambiguous.Reason);
        }

        [Fact]
        public void 计划_族对不上_没有缺首卷的组_后续卷0字节_一律不做()
        {
            (string carved, string sourceDirectory) = BuildCarved(1024);

            SplitVolumeAssemblyPlan wrongFamily = SplitVolumeAssembler.Plan(
                carved,
                SplitVolumeFamily.Zip,
                sourceDirectory,
                Path.Combine(_root, "assemble-family"),
                new[] { "set.7z.002" });

            Assert.False(wrongFamily.CanAssemble);
            Assert.Contains("对得上", wrongFamily.Reason);

            SplitVolumeAssemblyPlan nothingMissing = SplitVolumeAssembler.Plan(
                carved,
                SplitVolumeFamily.SevenZip,
                sourceDirectory,
                Path.Combine(_root, "assemble-none"),
                new[] { "set.7z.001", "set.7z.002" });

            Assert.False(nothingMissing.CanAssemble);
            Assert.Contains("不需要拼", nothingMissing.Reason);

            string zeroByte = Path.Combine(sourceDirectory, "空的.7z.002");
            File.WriteAllBytes(zeroByte, Array.Empty<byte>());

            SplitVolumeAssemblyPlan hasZero = SplitVolumeAssembler.Plan(
                carved,
                SplitVolumeFamily.Unknown,
                sourceDirectory,
                Path.Combine(_root, "assemble-zero"),
                new[] { "空的.7z.002" });

            Assert.False(hasZero.CanAssemble);
            Assert.Contains("0 字节", hasZero.Reason);
        }

        // ================================================================ 拼装本身（文件系统）

        /// <summary>
        /// 同盘走**硬链接**：零字节复制，而且真的是"同一份数据"——
        /// 用"改一个名字、另一个名字也变"来证明（复制做不到这一点）。
        /// </summary>
        [Fact]
        public void 拼装_同盘走硬链接_零复制_源文件随之可见即同一份数据()
        {
            (string carved, string sourceDirectory) = BuildCarved(4096);

            string sibling = Path.Combine(sourceDirectory, "set.7z.002");
            File.WriteAllBytes(sibling, new byte[8192]);

            string assemblyDirectory = Path.Combine(_root, "assemble-link");
            string carvedHashBefore = Hash(carved);

            SplitVolumeAssemblyPlan plan = SplitVolumeAssembler.Plan(
                carved,
                SplitVolumeFamily.SevenZip,
                sourceDirectory,
                assemblyDirectory,
                new[] { "set.7z.002" });

            SplitVolumeAssemblyResult result = SplitVolumeAssembler.TryAssemble(plan);

            Assert.True(result.Success, result.Reason);
            Assert.Equal(2, result.HardLinkCount);
            Assert.Equal(0, result.CopyCount);
            Assert.Equal(0, result.CopiedBytes);

            string assembledFirst = Path.Combine(assemblyDirectory, "set.7z.001");
            string assembledSecond = Path.Combine(assemblyDirectory, "set.7z.002");

            Assert.True(File.Exists(assembledFirst));
            Assert.Equal(new FileInfo(carved).Length, new FileInfo(assembledFirst).Length);

            // 硬链接的证明：往"接进来的名字"写一个字节，**源文件也跟着变**（同一份数据）。
            long originalLength = new FileInfo(sibling).Length;
            File.AppendAllText(sibling, "x");

            Assert.Equal(originalLength + 1, new FileInfo(assembledSecond).Length);

            // 拼装本身从不写源文件：那个从容器里抠出来的一段，哈希一个字节都没变。
            Assert.Equal(carvedHashBefore, Hash(carved));
        }

        /// <summary>
        /// 跨盘（这里用测试口子强制走复制）：**先查空间**，不够就一个字都不写；
        /// 够才复制，并且复制出来的目标与源一个字节不差、源文件不动。
        /// </summary>
        [Fact]
        public void 拼装_复制前先查空间_不够就不写_够才复制()
        {
            SplitVolumeAssembler.ForceCopyForTests = true;

            try
            {
                (string carved, string sourceDirectory) = BuildCarved(4096);

                string sibling = Path.Combine(sourceDirectory, "set.7z.002");
                File.WriteAllBytes(sibling, new byte[64 * 1024]);

                string assemblyDirectory = Path.Combine(_root, "assemble-copy-notenough");
                string carvedHashBefore = Hash(carved);
                string siblingHashBefore = Hash(sibling);

                SplitVolumeAssemblyPlan plan = SplitVolumeAssembler.Plan(
                    carved,
                    SplitVolumeFamily.SevenZip,
                    sourceDirectory,
                    assemblyDirectory,
                    new[] { "set.7z.002" });

                Assert.True(plan.CanAssemble, plan.Reason);

                // 可用空间只给 1 MiB：连"复制 + 余量"都不够 → 不做，而且**目录里一个文件都不许有**。
                SplitVolumeAssemblyResult noSpace = SplitVolumeAssembler.TryAssemble(
                    plan,
                    availableSpace: _ => 1024L * 1024);

                Assert.False(noSpace.Success);
                Assert.Contains("空间不够", noSpace.Reason);
                Assert.False(Directory.Exists(assemblyDirectory) && Directory.EnumerateFiles(assemblyDirectory).Any());

                // 空间够 → 复制成功，字节一致，源文件一个字节没动。
                string assemblyDirectory2 = Path.Combine(_root, "assemble-copy-ok");

                SplitVolumeAssemblyPlan plan2 = SplitVolumeAssembler.Plan(
                    carved,
                    SplitVolumeFamily.SevenZip,
                    sourceDirectory,
                    assemblyDirectory2,
                    new[] { "set.7z.002" });

                SplitVolumeAssemblyResult copied = SplitVolumeAssembler.TryAssemble(
                    plan2,
                    availableSpace: _ => 1024L * 1024 * 1024);

                Assert.True(copied.Success, copied.Reason);
                Assert.Equal(0, copied.HardLinkCount);
                Assert.Equal(2, copied.CopyCount);
                Assert.Equal(new FileInfo(carved).Length + new FileInfo(sibling).Length, copied.CopiedBytes);

                Assert.Equal(Hash(carved), Hash(Path.Combine(assemblyDirectory2, "set.7z.001")));
                Assert.Equal(Hash(sibling), Hash(Path.Combine(assemblyDirectory2, "set.7z.002")));

                Assert.Equal(carvedHashBefore, Hash(carved));
                Assert.Equal(siblingHashBefore, Hash(sibling));

                // 复制出来的那两个是**独立**的文件（改一个不会影响另一个）。
                long assembledLength = new FileInfo(Path.Combine(assemblyDirectory2, "set.7z.002")).Length;
                File.AppendAllText(sibling, "y");

                Assert.Equal(assembledLength, new FileInfo(Path.Combine(assemblyDirectory2, "set.7z.002")).Length);
            }
            finally
            {
                SplitVolumeAssembler.ForceCopyForTests = false;
            }
        }

        /// <summary>拼装目录里已经有东西（不是我们造的一次干净拼装）→ 一个字都不动。</summary>
        [Fact]
        public void 拼装_目标目录里已有东西时_不删不覆盖()
        {
            (string carved, string sourceDirectory) = BuildCarved(2048);

            File.WriteAllBytes(Path.Combine(sourceDirectory, "set.7z.002"), new byte[2048]);

            string assemblyDirectory = Path.Combine(_root, "assemble-dirty");
            Directory.CreateDirectory(assemblyDirectory);

            string stranger = Path.Combine(assemblyDirectory, "别人的东西.txt");
            File.WriteAllText(stranger, "不要动我");

            SplitVolumeAssemblyPlan plan = SplitVolumeAssembler.Plan(
                carved,
                SplitVolumeFamily.SevenZip,
                sourceDirectory,
                assemblyDirectory,
                new[] { "set.7z.002" });

            SplitVolumeAssemblyResult result = SplitVolumeAssembler.TryAssemble(plan);

            Assert.False(result.Success);
            Assert.Contains("已经有东西", result.Reason);
            Assert.True(File.Exists(stranger), "不是我们造的东西一个都不许删");
        }

        // ================================================================ 端到端：真 7z + 真管线

        /// <summary>开关**关**（默认）：与今天逐字相同 —— 如实报「分卷缺失」，两边不接。</summary>
        [Fact]
        public async Task 真7z_开关关_照旧报分卷缺失_不拼()
        {
            RequireSevenZip();

            string work = Path.Combine(_root, "e2e-off");
            Directory.CreateDirectory(work);

            (string container, _) = BuildContainerWithFirstVolume(work);

            Dictionary<string, string> before = HashAllFiles(work);

            Harness harness = CreateHarness(settings => settings.AssembleSplitVolumesFromContainer = false);
            ArchiveTask task = await AddTaskAsync(harness, container);

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.VolumeMissing, task.Status);
            Assert.DoesNotContain(harness.LogTexts, line => line.Contains("接上后续卷", StringComparison.Ordinal));

            // 源目录一个字节没动（连大小与内容一起核）。
            Assert.Equal(before, HashAllFiles(work));
        }

        /// <summary>
        /// 开关**开**：程序在工作区接出一套（硬链接），引擎自己认了之后**解出真内容**——
        /// 而且源目录（容器 + 外面那几个卷）一个字节都没变。
        /// </summary>
        [Fact]
        public async Task 真7z_开关开_接上后续卷后解出真内容_源目录一个字节不变()
        {
            RequireSevenZip();

            string work = Path.Combine(_root, "e2e-on");
            Directory.CreateDirectory(work);

            (string container, byte[] payload) = BuildContainerWithFirstVolume(work);

            Dictionary<string, string> before = HashAllFiles(work);

            Harness harness = CreateHarness(settings => settings.AssembleSplitVolumesFromContainer = true);
            ArchiveTask task = await AddTaskAsync(harness, container);

            await harness.Coordinator.StartExtractAsync();

            Assert.True(
                task.Status == StatusText.ExtractSuccess,
                $"应该解开，实际状态={task.Status}；原因={task.ErrorMessage}\n日志：\n"
                + string.Join("\n", harness.LogTexts.Where(line => line.Contains("封面", StringComparison.Ordinal))));

            // ①日志必须说清"怎么接的"（硬链接几个 / 复制几个）：那一行摘要是**默认档**下唯一留着的一行
            //   （成功的任务细节会被丢掉），所以这条事实必须挂在摘要上。
            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("封面.jpg：", StringComparison.Ordinal)
                        && line.Contains("接上分卷", StringComparison.Ordinal)
                        && line.Contains("硬链接", StringComparison.Ordinal));

            // 而且**没有**走"接起来也打不开、退回原路"那一支（那是 WARN，默认档不会被丢掉，所以这条断言有效）。
            Assert.DoesNotContain(harness.LogTexts, line => line.Contains("退回原路", StringComparison.Ordinal));

            // ②解出来的就是原始字节（真内容，不是垃圾）。
            string? extracted = FindFile(harness.OutputRoot, "a.bin");
            Assert.NotNull(extracted);
            Assert.Equal(payload, File.ReadAllBytes(extracted!));

            // ③源目录一个字节没动（不变量 1：容器与外面那一组卷都只读）。
            Assert.Equal(before, HashAllFiles(work));

            // ④拼装产物是**中间件**：任务成功后工作区整份清掉，不留残留。
            string workRoot = Path.Combine(harness.DataRoot, "work");

            List<string> leftovers = Directory.Exists(workRoot)
                ? Directory
                    .EnumerateDirectories(workRoot, SplitVolumeAssembler.AssemblyDirectoryName, SearchOption.AllDirectories)
                    .ToList()
                : new List<string>();

            Assert.True(
                leftovers.Count == 0,
                "拼装目录应该跟着成功路径的工作区清理一起没，实际还在：" + string.Join("、", leftovers));
        }

        // ================================================================ 造样本与工具

        /// <summary>造一个"垃圾前缀 + 真 7z 第一卷"的容器（不造外面那几个卷，由各用例自己决定）。</summary>
        private (string Carved, string SourceDirectory) BuildCarved(int bytes)
        {
            string sourceDirectory = Path.Combine(_root, "src-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sourceDirectory);

            string carved = Path.Combine(sourceDirectory, "容器里抠出来的.zip");

            byte[] head = { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C };
            var content = new byte[Math.Max(bytes, head.Length)];
            Array.Copy(head, content, head.Length);
            new Random(42).NextBytes(content.AsSpan(head.Length));

            File.WriteAllBytes(carved, content);

            return (carved, sourceDirectory);
        }

        /// <summary>
        /// 真 7z 造样本：3 MiB 数据 → 分卷（每卷 1 MiB）→ 容器 = 2 MiB 垃圾 + `set.7z.001` 的字节，
        /// 后续卷 `set.7z.002/.003` 放在**同一个目录**里（就是这一档的现场）。
        /// </summary>
        private (string Container, byte[] Payload) BuildContainerWithFirstVolume(string directory)
        {
            string source = Path.Combine(_root, "pack-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(source);

            var payload = new byte[3 * 1024 * 1024];
            new Random(20260926).NextBytes(payload);
            File.WriteAllBytes(Path.Combine(source, "a.bin"), payload);

            Run7z(source, "a", "-t7z", "-mx0", "-v1m", "set.7z", "a.bin");

            string first = Path.Combine(source, "set.7z.001");
            Assert.True(File.Exists(first), "第一卷没造出来");

            byte[] firstBytes = File.ReadAllBytes(first);
            var junk = new byte[2 * 1024 * 1024];
            new Random(9).NextBytes(junk);

            string container = Path.Combine(directory, "封面.jpg");

            using (var stream = File.Create(container))
            {
                stream.Write(junk);
                stream.Write(firstBytes);
            }

            /*
             * ⚠ 后续卷要**全部**搬过来（`-v1m` 切 3 MiB 会得到 4 卷）：只搬前两个的话，
             * 拼装出来的那一套本身就缺最后一卷，引擎照样打不开 —— 那样测的就不是我们的实现了。
             */
            foreach (string volume in Directory
                         .EnumerateFiles(source, "set.7z.*", SearchOption.TopDirectoryOnly)
                         .Where(path => VolumeGroupDetector.TryGetVolumeIndex(Path.GetFileName(path)) > 1))
            {
                File.Copy(volume, Path.Combine(directory, Path.GetFileName(volume)));
            }

            Assert.True(File.Exists(Path.Combine(directory, "set.7z.002")), "后续卷没搬过来");

            return (container, payload);
        }

        /// <summary>整个目录（含子目录）的"文件 → SHA-256"快照 —— 用来钉"源目录一个字节没动"。</summary>
        private static Dictionary<string, string> HashAllFiles(string directory)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                result[Path.GetRelativePath(directory, file)] = Hash(file);
            }

            return result;
        }

        /// <summary>造一组"缺首卷"的真文件（<c>base.002</c>/<c>base.003</c>…）—— Plan 会核对存在性。</summary>
        private static void WriteVolumes(string directory, string baseName, int from, int to, string suffix = "")
        {
            for (int index = from; index <= to; index++)
            {
                string name = suffix.Length == 0
                    ? $"{baseName}.{index:D3}"
                    : $"{baseName}{index}{suffix}";

                File.WriteAllBytes(Path.Combine(directory, name), new byte[1024 + index]);
            }
        }

        private static string? FindFile(string root, string fileName)
        {
            try
            {
                return Directory.Exists(root)
                    ? Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories).FirstOrDefault()
                    : null;
            }
            catch
            {
                return null;
            }
        }

        private static string Hash(string path)
        {
            using FileStream stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }

        private static async Task<ArchiveTask> AddTaskAsync(Harness harness, string path)
        {
            var task = new ArchiveTask(path, harness.Vm.Tasks.Count + 1) { IsSelected = true };

            await new ArchiveDetectService().ApplyDetectResultAsync(task);
            task.CaptureSourceSnapshot();
            harness.Vm.Tasks.Add(task);

            return task;
        }

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

        private Harness CreateHarness(Action<AppSettings>? configure = null)
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

            configure?.Invoke(settings);
            settingsService.Save(settings);

            var engine = new Engines.SevenZip.SevenZipEngine();
            var logService = new LogService(pathService);

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
                new ConfirmDialogService());

            var coordinator = new ExtractionCoordinator(
                vm,
                engine,
                new PasswordService(),
                pathService,
                new ConfirmDialogService());

            return new Harness(vm, coordinator, logService, outputRoot, dataRoot);
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

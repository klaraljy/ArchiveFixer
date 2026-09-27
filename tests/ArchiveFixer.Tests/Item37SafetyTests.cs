using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 第 37 条的三处安全修复（用户原话："你应该先试密码再进行解压"、
    /// "一个空密码花了我10分钟……这是非常危险非常危险的bug"、"不是导出失败日志是导出所有日志，所有的"）。
    ///
    /// <list type="number">
    /// <item><description><b>先试密码再解整包</b>：候选只解"最小的一个条目"验密码（<see cref="PasswordProbe"/>），
    /// 错了就不去跑整包；<b>加密包不试空密码</b>（引擎造不出用空密码加密的包）。</description></item>
    /// <item><description><b>链尾删除要先看整条链</b>：链上有失败 / 没跑完的任务时，其余物一个字节都不删
    /// （他的 12.22 GiB 过程物就是这么被删掉的）。</description></item>
    /// <item><description><b>第一卷名字被改坏时，把我们自己的产物摆正</b>（<see cref="BrokenVolumeChainRepair"/>），
    /// 让那 12 GiB 的内容真的解得出来；用户自己给的源文件一个字节都不动。</description></item>
    /// </list>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class Item37SafetyTests : IDisposable
    {
        private readonly string _root;
        private readonly string? _sevenZip;

        public Item37SafetyTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerItem37", Guid.NewGuid().ToString("N"));
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

        // ================================================================ ① 先试密码（探针）

        [Fact]
        public void 探针挑最小的那个文件_大文件与目录都不算()
        {
            ArchiveListResult list = ListingRaw(
                ("大文件.mp4", 400L * 1024 * 1024, false),
                ("目录", 0L, true),
                ("说明.txt", 1024L, false),
                ("小图.jpg", 2048L, false),
                ("通配符[1].txt", 512L, false));

            // 最小的"安全"条目是 说明.txt（1024）；带通配符的那个 512 **不能**当探针（7-Zip 会当模式匹配）。
            Assert.Equal("说明.txt", PasswordProbe.ChooseProbeEntry(list));
        }

        [Fact]
        public void 没有小条目时不硬凑探针()
        {
            ArchiveListResult list = ListingRaw(("a.bin", 5L * 1024 * 1024 * 1024, false), ("b.bin", 4L * 1024 * 1024 * 1024, false));

            Assert.Null(PasswordProbe.ChooseProbeEntry(list));
            Assert.Null(PasswordProbe.ChooseProbeEntry(null));
        }

        [Fact]
        public void 加密包不试空密码()
        {
            Assert.True(PasswordProbe.ShouldSkipEmptyPassword(ListingRaw(new[] { ("a.bin", 10L, false) }, encrypted: true)));
            Assert.False(PasswordProbe.ShouldSkipEmptyPassword(ListingRaw(new[] { ("a.bin", 10L, false) }, encrypted: false)));
            Assert.False(PasswordProbe.ShouldSkipEmptyPassword(null));
        }

        /// <summary>
        /// 真 7z：名字可见（`-mhe=off`）、条目加密的包 —— 正是他那个 12 GiB 包的形状。
        /// 用**只解一个小条目**的办法，一个错密码在**秒级**内就被否掉（而不是解完整包）。
        /// </summary>
        [Fact]
        public async Task 真7z_错密码只解一个小条目就否掉_一个字节的整包数据都不动()
        {
            RequireSevenZip();

            string directory = Path.Combine(_root, "probe");
            Directory.CreateDirectory(directory);

            string big = Path.Combine(directory, "大文件.bin");
            string small = Path.Combine(directory, "说明.txt");

            File.WriteAllBytes(big, new byte[8 * 1024 * 1024]);
            File.WriteAllText(small, "包说明：这是一个测试包。", new System.Text.UTF8Encoding(false));

            string archive = Path.Combine(directory, "encrypted.7z");
            Run7z("a", "-t7z", "-mx0", "-p" + "正确的密码", archive, big, small);

            var engine = new SevenZipEngine();

            ArchiveListResult list = await engine.ListAsync(ArchiveRequest.For(archive), CancellationToken.None);

            Assert.True(list.Success, list.Message);
            Assert.True(list.IsEncrypted, "整包已加密 —— 空密码这一档必须被跳过");
            Assert.Equal("说明.txt", PasswordProbe.ChooseProbeEntry(list));

            string wrongOutput = Path.Combine(directory, "out-wrong");
            Directory.CreateDirectory(wrongOutput);

            var options = new ExtractOptions { IncludeEntries = new[] { "说明.txt" } };

            ArchiveOperationResult wrong = await engine.ExtractAsync(
                new ArchiveRequest { ArchivePath = archive, OutputPath = wrongOutput, Password = "错的密码" },
                options,
                CancellationToken.None);

            Assert.False(wrong.Success);
            Assert.Equal("WrongPassword", wrong.DetectedErrorType);

            /*
             * 关键：**整包那个 8 MiB 的大文件一个字节都没写**。
             *
             * （7-Zip 用错密码时会先把探针那个条目的**垃圾字节**写出来（这里 36 字节）再报错 ——
             * 第 25 条记过它"先建桩文件再报错"的行为；所以判据是"写出量不超过探针条目"，
             * 而不是"目录里一个文件都没有"。老办法会把整包 8 MiB 全解出来。）
             */
            Assert.False(
                File.Exists(Path.Combine(wrongOutput, "大文件.bin")),
                "错误的密码绝不该把整包里的大文件解出来");

            long written = Directory.GetFiles(wrongOutput, "*", SearchOption.AllDirectories)
                .Sum(x => new FileInfo(x).Length);

            Assert.True(
                written <= new FileInfo(small).Length,
                $"错误密码只该动探针那一个条目（≤{new FileInfo(small).Length} 字节），实际写了 {written} 字节");

            string rightOutput = Path.Combine(directory, "out-right");
            Directory.CreateDirectory(rightOutput);

            ArchiveOperationResult right = await engine.ExtractAsync(
                new ArchiveRequest { ArchivePath = archive, OutputPath = rightOutput, Password = "正确的密码" },
                new ExtractOptions { IncludeEntries = new[] { "说明.txt" } },
                CancellationToken.None);

            Assert.True(right.Success, right.Message);
            Assert.True(File.Exists(Path.Combine(rightOutput, "说明.txt")), "对密码要能把探针条目解出来");
            Assert.False(File.Exists(Path.Combine(rightOutput, "大文件.bin")), "探针只解那一个条目");
        }

        /// <summary>
        /// **包里没有小文件时怎么先试密码**（用户 2026-09-25 第 38 条："而且没有小于16MB的文件怎么办"）。
        ///
        /// <para>办法：只读"第一个条目**解密后的开头 64 字节**" —— 密码对，它就是文件真正的开头（魔数）；
        /// 密码错，就是随机字节。代价与包多大无关（一个 5 GiB 的条目也只花零点几秒、一个字节都不落盘）。</para>
        /// </summary>
        [Fact]
        public async Task 真7z_没有小文件时_只读开头64字节就能分辨密码对不对()
        {
            RequireSevenZip();

            string directory = Path.Combine(_root, "prefix");
            Directory.CreateDirectory(directory);

            // 先造一个"内层包"，它的开头就是 7z 魔数（真机那个包里是 .7z.001，形状一致）。
            string innerSource = Path.Combine(directory, "payload.bin");
            File.WriteAllBytes(innerSource, new byte[128 * 1024]);

            string inner = Path.Combine(directory, "inner.7z");
            Run7z("a", "-t7z", "-mx0", inner, innerSource);

            // 外层：密码 + 存（Copy）方式，形状与真机那个"名字可见、条目加密"的包一致。
            string outer = Path.Combine(directory, "encrypted-outer.7z");
            Run7z("a", "-t7z", "-mx0", "-p" + "正确的密码", outer, inner);

            var engine = new SevenZipEngine();

            ArchiveListResult list = await engine.ListAsync(ArchiveRequest.For(outer), CancellationToken.None);

            Assert.True(list.Success, list.Message);
            Assert.True(list.IsEncrypted, "外层是加密包");

            // ①读开头这一档拿得出条目（哪怕包里一个"小文件"都没有）。
            string? prefixEntry = PasswordProbe.ChoosePrefixEntry(list);
            Assert.NotNull(prefixEntry);

            byte[]? right = await engine.TryReadDecryptedPrefixAsync(
                outer, prefixEntry!, "正确的密码", PasswordProbe.PrefixProbeBytes);

            Assert.NotNull(right);
            Assert.True(
                PasswordProbe.LooksLikeFileStart(right),
                "对密码解出来的开头必须是文件真正的开头（内层 7z 的魔数）");

            byte[]? wrong = await engine.TryReadDecryptedPrefixAsync(
                outer, prefixEntry!, "错的密码", PasswordProbe.PrefixProbeBytes);

            Assert.NotNull(wrong);
            Assert.False(
                PasswordProbe.LooksLikeFileStart(wrong),
                "错密码解出来的是随机字节，看不出是文件开头 —— 靠这一点就能把它排到最后");
        }

        // ================================================================ ② 链尾删除要看整条链

        [Fact]
        public void 链上有失败的任务时_链尾不许动其余物()
        {
            ArchiveTask root = Task("容器.7z", continuation: false);
            root.Outcome = TaskOutcome.Succeeded;
            root.OutputVerification = OutputVerificationOutcome.Passed;

            ArchiveTask inner = Task("内层.7z.001", continuation: true);
            inner.Outcome = TaskOutcome.Failed;
            inner.OutputVerification = OutputVerificationOutcome.Failed;

            string? blocker = ChainCompletionGate.DescribeBlocker(root, new[] { root, inner });

            Assert.NotNull(blocker);
            Assert.Contains("内层.7z.001", blocker!, StringComparison.Ordinal);
        }

        [Fact]
        public void 链上还有没跑完的任务时_也不许动其余物()
        {
            ArchiveTask root = Task("容器.7z", continuation: false);
            ArchiveTask pending = Task("下一层.7z.001", continuation: true);

            // 没跑过：Outcome 还是 Pending、校验 NotAttempted —— 链显然没跑完。
            Assert.NotNull(ChainCompletionGate.DescribeBlocker(root, new[] { root, pending }));
        }

        [Fact]
        public void 整条链都成功才放行()
        {
            ArchiveTask root = Task("容器.7z", continuation: false);
            ArchiveTask innerA = Task("内层A.7z.001", continuation: true);
            ArchiveTask innerB = Task("内层B.7z.001", continuation: true);

            foreach (ArchiveTask task in new[] { root, innerA, innerB })
            {
                task.Outcome = TaskOutcome.Succeeded;
                task.OutputVerification = OutputVerificationOutcome.Passed;
            }

            Assert.Null(ChainCompletionGate.DescribeBlocker(root, new[] { root, innerA, innerB }));

            // 根任务自己混在链里也不会被当成"续解任务"重复判（它由调用方那几道门槛管）。
            Assert.Equal(2, ChainCompletionGate.CountContinuations(root, new[] { root, innerA, innerB }));
        }

        // ================================================================ ③ 第一卷名字被改坏 → 摆正我们自己的产物

        [Fact]
        public void 改名计划_用后续卷推出标准名()
        {
            BrokenVolumeChainRepair.RenamePlan? plan = BrokenVolumeChainRepair.TryPlan(
                @"H:\out\其余物\X\Code Complete-BZ.7z(删掉.001",
                new[] { "Code Complete-BZ.7z(删掉.001", "Code Complete-BZ.7z.002", "Code Complete-BZ.7z.003" });

            Assert.NotNull(plan);
            Assert.Equal("Code Complete-BZ.7z.001", Path.GetFileName(plan!.FirstVolumePathAfterRename));
            Assert.Single(plan.Renames);
            Assert.Equal("Code Complete-BZ.7z.001", plan.Renames[0].TargetName);
        }

        [Fact]
        public void 改名计划_跳号或缺后续卷时不做()
        {
            // 只有 .003（跳号）：这一组本来就不全，改了也解不开。
            Assert.Null(BrokenVolumeChainRepair.TryPlan(
                @"H:\out\X.7z(删掉.001",
                new[] { "X.7z(删掉.001", "X.7z.003" }));

            // 一个后续卷都没有：没有任何线索能推出标准名。
            Assert.Null(BrokenVolumeChainRepair.TryPlan(
                @"H:\out\X.7z(删掉.001",
                new[] { "X.7z(删掉.001" }));

            // 名字本来就是标准的（不是"第一卷"）：不动。
            Assert.True(BrokenVolumeChainRepair.IsNoOp(BrokenVolumeChainRepair.TryPlan(
                @"H:\out\X.7z.001",
                new[] { "X.7z.001", "X.7z.002" })));
        }

        [Fact]
        public void 改名计划_目标名被别的文件占着时不做()
        {
            string directory = Path.Combine(_root, "occupied");
            Directory.CreateDirectory(directory);

            File.WriteAllText(Path.Combine(directory, "X.7z(删掉.001"), "a");
            File.WriteAllText(Path.Combine(directory, "X.7z.002"), "b");

            // 已经有一个不相干的 `X.7z.001`：改名会覆盖它 —— 一次都不许发生。
            File.WriteAllText(Path.Combine(directory, "X.7z.001"), "别人的文件");

            Assert.Null(BrokenVolumeChainRepair.TryPlan(
                Path.Combine(directory, "X.7z(删掉.001"),
                new[] { "X.7z(删掉.001", "X.7z.002", "X.7z.001" }));
        }

        /// <summary>
        /// 真 7z 端到端：造一组三卷 → 把第一卷改成"名字被改坏"的样子 → 走修复 → **真内容解出来了**。
        /// 期间**源文件的名字与内容都不许被改**（他给的素材一个字节都不能动）。
        /// </summary>
        [Fact]
        public async Task 真7z_第一卷名字被改坏_修复之后能把真内容解出来()
        {
            RequireSevenZip();

            string directory = Path.Combine(_root, "repair");
            Directory.CreateDirectory(directory);

            string payloadDirectory = Path.Combine(directory, "payload");
            Directory.CreateDirectory(payloadDirectory);

            byte[] payload = new byte[40_000];
            new Random(20260925).NextBytes(payload);
            File.WriteAllBytes(Path.Combine(payloadDirectory, "a.bin"), payload);

            string archive = Path.Combine(directory, "Code Complete-BZ.7z");
            Run7z("a", "-t7z", "-mx0", "-v16k", archive, Path.Combine(payloadDirectory, "a.bin"));

            string first = archive + ".001";
            string mangled = Path.Combine(directory, "Code Complete-BZ.7z(删掉.001");

            File.Move(first, mangled);

            var engine = new SevenZipEngine();

            // 改坏之后：引擎只会把它当"通用分片"（这就是必须修的原因）。
            ArchiveListResult broken = await engine.ListAsync(ArchiveRequest.For(mangled), CancellationToken.None);

            Assert.True(broken.Success, broken.Message);
            Assert.True(broken.IsRawSplitStream);

            // 修复计划 + 执行（只改名，不复制）。
            BrokenVolumeChainRepair.RenamePlan? plan = BrokenVolumeChainRepair.TryPlan(
                mangled,
                Directory.GetFiles(directory).Select(Path.GetFileName));

            Assert.NotNull(plan);
            Assert.True(BrokenVolumeChainRepair.TryApply(plan, out string failure), failure);

            string repaired = plan!.FirstVolumePathAfterRename;

            Assert.True(File.Exists(repaired), "修复后第一卷应当叫标准名字");

            // 修好之后：引擎认得这是一组真分卷。
            ArchiveListResult fixedList = await engine.ListAsync(ArchiveRequest.For(repaired), CancellationToken.None);

            Assert.True(fixedList.Success, fixedList.Message);
            Assert.False(fixedList.IsRawSplitStream);
            Assert.Contains(fixedList.Entries, e => e.Path.EndsWith("a.bin", StringComparison.OrdinalIgnoreCase));

            string output = Path.Combine(directory, "out");
            Directory.CreateDirectory(output);

            ArchiveOperationResult extract = await engine.ExtractAsync(
                new ArchiveRequest { ArchivePath = repaired, OutputPath = output },
                new ExtractOptions(),
                CancellationToken.None);

            Assert.True(extract.Success, extract.Message);

            string produced = Path.Combine(output, "a.bin");

            Assert.True(File.Exists(produced), "修好之后必须解出真内容（而不是一个与卷等大的垃圾文件）");
            Assert.Equal(payload, File.ReadAllBytes(produced));
        }

        // ================================================================ 工具

        private static ArchiveTask Task(string fileName, bool continuation)
        {
            string path = Path.Combine(Path.GetTempPath(), fileName);

            return new ArchiveTask(path, 1)
            {
                IsArchive = true,
                DetectedFormat = "7Z",
                ParentOutputDirectory = continuation ? @"C:\out\parent" : string.Empty,
                ParentTaskName = continuation ? "parent" : string.Empty
            };
        }

        private static ArchiveListResult ListingRaw(params (string Path, long Size, bool IsDirectory)[] entries)
            => ListingRaw(entries, encrypted: false);

        private static ArchiveListResult ListingRaw((string Path, long Size, bool IsDirectory)[] entries, bool encrypted)
        {
            var list = new List<ArchiveEntry>();

            foreach ((string path, long size, bool isDirectory) in entries)
            {
                list.Add(new ArchiveEntry { Path = path, Size = isDirectory ? 0 : size, IsDirectory = isDirectory });
            }

            return new ArchiveListResult
            {
                Success = true,
                FileCount = list.Count(x => !x.IsDirectory),
                TotalUncompressedSize = list.Sum(x => x.Size),
                Entries = list,
                IsEncrypted = encrypted,
                EngineId = "fake",
                EngineVersion = "1.0"
            };
        }

        private void Run7z(params object[] args)
        {
            RequireSevenZip();

            var psi = new ProcessStartInfo(_sevenZip!)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = _root
            };

            foreach (object arg in args)
            {
                psi.ArgumentList.Add(arg?.ToString() ?? string.Empty);
            }

            using Process process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 7z.exe");

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();

            Assert.True(process.WaitForExit(120_000), "7z 超时");
            Assert.True(process.ExitCode == 0, $"7z 失败（{process.ExitCode}）：{stdout}{stderr}");
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
    }
}

using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 核心层修复的回归用例（对应审查报告里的 P0/P1 条目）。
    ///
    /// 覆盖：
    /// - 中文路径/条目名乱码（P1）：必须带 <c>-sccUTF-8</c>，且真的能列出中文条目名；
    /// - 只给非首卷时的错误分类（P1）：必须是"分卷缺失"，并报出缺哪一卷；
    /// - 递归展开的内层包也要过 Security（P1）：危险条目名与越界落点都必须是失败结论；
    /// - 更深的层出现多分支（P2）：不能静默当成"已完成"。
    /// </summary>
    // 碰进程级静态（构造 RecursiveExtractor / 指定 ConfiguredWorkspaceRoot）：
    // 与同类用例串行跑，不与别的集合并行 —— 见 InnerLayerContinuationTests 顶部的 CollectionDefinition。
    [Collection("ArchiveFixerGlobalState")]
    public class CoreFixRegressionTests : IDisposable
    {
        private readonly string _root;
        private readonly string _sevenZip;

        public CoreFixRegressionTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerCoreFix", Guid.NewGuid().ToString("N"));
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
                // 临时目录删不掉不影响结论，下次跑换一个 GUID 目录。
            }
        }

        // ────────────────────────── P1：控制台编码 ──────────────────────────

        [Fact]
        public void 解压参数必须带_sccUTF_8()
        {
            var runner = new SevenZipProcessRunner(new ToolLocator());

            List<string> extract = runner.BuildExtractArguments(
                @"C:\t\中文包.7z",
                @"C:\t\out",
                string.Empty,
                new ExtractOptions());

            List<string> test = runner.BuildTestArguments(@"C:\t\中文包.7z", string.Empty);

            Assert.Contains("-sccUTF-8", extract);
            Assert.Contains("-sccUTF-8", test);
        }

        [Fact]
        public async Task 中文条目名_列目录不乱码且能解出中文文件名()
        {
            RequireSevenZip();

            string source = Path.Combine(_root, "_中文源");
            Directory.CreateDirectory(Path.Combine(source, "子目录"));
            File.WriteAllText(Path.Combine(source, "普通.txt"), "普通\n", Utf8NoBom);
            File.WriteAllText(Path.Combine(source, "子目录", "中文条目.txt"), "中文\n", Utf8NoBom);

            string archive = Path.Combine(_root, "中文包.zip");
            Run7z("a", "-tzip", archive, Path.Combine(source, "普通.txt"), Path.Combine(source, "子目录"));

            var engine = new SevenZipEngine(new SevenZipProcessRunner(new ToolLocator()));

            ArchiveListResult list = await engine.ListAsync(ArchiveRequest.For(archive), CancellationToken.None);

            Assert.True(list.Success, list.Message);
            Assert.Contains(list.Entries, entry => entry.Path.Contains("普通.txt", StringComparison.Ordinal));
            Assert.Contains(list.Entries, entry => entry.Path.Contains("中文条目.txt", StringComparison.Ordinal));

            // 乱码的典型形态：解码失败后的替换字符 U+FFFD。
            Assert.DoesNotContain(list.Entries, entry => entry.Path.Contains('\uFFFD'));

            string output = Path.Combine(_root, "out_中文");
            ArchiveOperationResult extract = await engine.ExtractAsync(
                new ArchiveRequest { ArchivePath = archive, OutputPath = output, Password = string.Empty },
                new ExtractOptions(),
                CancellationToken.None);

            Assert.True(extract.Success, extract.Message);
            Assert.True(
                File.Exists(Path.Combine(output, "普通.txt")),
                "解压后没有中文名的文件：" + string.Join("、", Directory.EnumerateFileSystemEntries(output, "*", SearchOption.AllDirectories)));
        }

        // ────────────────────────── P1：缺卷分类 ──────────────────────────

        [Fact]
        public void 非首卷错误文案_分类为缺首卷()
        {
            const string error = "ERROR: C:\\t\\only\\volume.7z.002 : Cannot open the file as archive";

            string type = SevenZipOutputParser.DetectSevenZipErrorType(2, string.Empty, error, "C:\\t\\only\\volume.7z.002");

            Assert.Equal(SevenZipOutputParser.MissingFirstVolumeErrorType, type);
            Assert.Equal(StatusText.VolumeMissing, SevenZipOutputParser.ErrorTypeToTaskStatus(type));
        }

        [Fact]
        public async Task 只给非首卷_分类为分卷缺失并报出缺哪一卷()
        {
            RequireSevenZip();

            string volumeDirectory = Path.Combine(_root, "vol");
            Directory.CreateDirectory(volumeDirectory);

            string payload = Path.Combine(_root, "big.bin");
            byte[] bytes = new byte[2 * 1024 * 1024 + 512 * 1024];
            new Random(20260921).NextBytes(bytes);
            File.WriteAllBytes(payload, bytes);

            string archive = Path.Combine(volumeDirectory, "volume.7z");
            Run7z("a", "-t7z", archive, "-v1m", payload);

            Assert.True(File.Exists(archive + ".001"), "分卷样本没有生成 .001");

            /*
             * 造出"用户的真实现场"：手上只有非首卷（两个 mp4 各藏一段分卷，
             * 解到两个目录里去了，于是谁都没有首卷）。
             */
            string misplaced = Path.Combine(_root, "only-second", "volume.7z.002");
            Directory.CreateDirectory(Path.GetDirectoryName(misplaced)!);
            File.Copy(archive + ".002", misplaced);

            var engine = new SevenZipEngine(new SevenZipProcessRunner(new ToolLocator()));

            ArchiveOperationResult result = await engine.ExtractAsync(
                new ArchiveRequest
                {
                    ArchivePath = misplaced,
                    OutputPath = Path.Combine(_root, "out_vol"),
                    Password = string.Empty
                },
                new ExtractOptions(),
                CancellationToken.None);

            Assert.False(result.Success);

            /*
             * 分类必须是"分卷缺失"这一族，而不是"不支持该格式"：
             * 引擎的文本分类给出 MissingFirstVolume（文件名本身就是分卷），
             * 运行器在有文件系统可查时会把它判成 VolumeMissing（能列出缺哪几卷）。
             * 两者对用户是同一个结论（同状态、同动作），测试接受任意一种，但必须落在这一族里。
             */
            Assert.Contains(
                result.DetectedErrorType,
                new[] { "VolumeMissing", SevenZipOutputParser.MissingFirstVolumeErrorType });
            Assert.Equal(StatusText.VolumeMissing, result.Status);

            // 不变量 7：必须报缺哪几个，而且名字要跟用户手上的文件同风格（真实名是 volume.7z.001）。
            Assert.Contains("volume.7z.001", result.Message, StringComparison.OrdinalIgnoreCase);
        }

        // ────────────────────────── P1：递归也要过 Security ──────────────────────────

        [Fact]
        public async Task 递归解压_内层包条目名含上级目录_必须拒绝且不算成功()
        {
            RequireSevenZip();

            byte[] unsafeZip = BuildZipWithUnsafeEntryName("..\\..\\evil.txt");

            // 先确认这个手工样本本身是"7z 认得出、且条目名带 .."的（否则下面的断言测不到东西）。
            string probePath = Path.Combine(_root, "unsafe-inner.zip");
            File.WriteAllBytes(probePath, unsafeZip);

            var probeEngine = new SevenZipEngine(new SevenZipProcessRunner(new ToolLocator()));
            ArchiveListResult probe = await probeEngine.ListAsync(
                ArchiveRequest.For(probePath),
                CancellationToken.None);

            Assert.True(
                probe.Success,
                "手工构造的 zip 7z 打不开，样本本身有问题：" + probe.Message);
            Assert.Contains(
                probe.Entries,
                entry => entry.Path.Contains("..", StringComparison.Ordinal));

            // 7z 不会自己生成带 `..` 的条目名，所以这里手工写一个 zip 供外层包转发。
            string outer = BuildArchiveContaining("inner.zip", unsafeZip);

            RecursionResult result = await RunRecursiveAsync(outer, "unsafe-entry");

            Assert.Equal(RecursionStopReason.UnsafeEntry, result.StopReason);
            Assert.False(result.Completed);

            /*
             * 外层的产物是有效的，所以这里必须是"部分完成"而不是"失败"（不变量 6）：
             * 用户拿到的外层内容没问题，坏的是内层那个包。
             */
            Assert.True(result.PartiallyCompleted, result.Summary);
            Assert.Contains(result.Layers, layer => layer.Message.Contains("不安全", StringComparison.Ordinal));
        }

        [Fact]
        public async Task 递归解压_产物落点越界_必须是失败结论而不是解压成功()
        {
            string archivePath = Path.Combine(_root, "escape.7z");
            File.WriteAllBytes(archivePath, new byte[256]);

            // 造一个"产物目录里有个联接点指向目录外"的现场：目录遍历看得见它，
            // 但它的真实落点在产物目录之外 —— 这正是第二道防线该拦下的形态。
            string outside = Path.Combine(_root, "outside");
            Directory.CreateDirectory(outside);

            var engine = new JunctionEngine();

            RecursionResult result = await RunRecursiveAsync(
                archivePath,
                "junction",
                engine,
                Path.Combine(_root, "out_junction"));

            Assert.Equal(RecursionStopReason.UnsafeEntry, result.StopReason);
            Assert.False(result.Completed);
            Assert.False(result.PartiallyCompleted);
            Assert.Contains(result.Layers, layer => layer.Message.Contains("越出本层产物目录", StringComparison.Ordinal));
        }

        // ────────────────────────── P2：更深的层多分支不得静默 ──────────────────────────

        [Fact]
        public async Task 更深的层出现多分支_停下来并说清还有几个没展开()
        {
            string archivePath = Path.Combine(_root, "branches.7z");
            File.WriteAllBytes(archivePath, new byte[256]);

            RecursionResult result = await RunRecursiveAsync(
                archivePath,
                "branch",
                new BranchingEngine(),
                Path.Combine(_root, "out_branch"));

            Assert.Equal(RecursionStopReason.BranchNotExpanded, result.StopReason);
            Assert.False(result.Completed);
            Assert.Contains("未展开", result.Summary, StringComparison.Ordinal);
        }

        // ────────────────────────── 工具方法 ──────────────────────────

        private async Task<RecursionResult> RunRecursiveAsync(
            string archivePath,
            string tag,
            IArchiveEngine? engine = null,
            string? output = null)
        {
            string? previousRoot = RecursiveExtractor.ConfiguredWorkspaceRoot;

            try
            {
                RecursiveExtractor.ConfiguredWorkspaceRoot = Path.Combine(_root, "work");

                IArchiveEngine effectiveEngine = engine ?? new SevenZipEngine(new SevenZipProcessRunner(new ToolLocator()));

                var extractor = new RecursiveExtractor(
                    effectiveEngine,
                    new MagicArchiveProber(),
                    _ => new[] { string.Empty });
                return await extractor.ExtractAsync(
                    new ArchiveTask(archivePath),
                    output ?? Path.Combine(_root, "out_" + tag),
                    RecursionMode.SingleChain,
                    null,
                    CancellationToken.None);
            }
            finally
            {
                RecursiveExtractor.ConfiguredWorkspaceRoot = previousRoot;
            }
        }

        /// <summary>把 <paramref name="innerArchive"/> 放进一个外层 zip 里，返回外层包路径。</summary>
        private string BuildArchiveContaining(string entryName, byte[] innerBytes)
        {
            string source = Path.Combine(_root, "_outer");
            Directory.CreateDirectory(source);
            File.WriteAllBytes(Path.Combine(source, entryName), innerBytes);

            string outer = Path.Combine(_root, "outer_" + Guid.NewGuid().ToString("N") + ".zip");

            Run7z("a", "-tzip", outer, Path.Combine(source, entryName));

            return outer;
        }

        /// <summary>
        /// 造一个**条目名不安全**的 zip。这里刻意直接写 ZIP 结构而不是用 7z 造：
        /// 7z 会清洗 `..` 这类条目名，造不出"危险条目"这个真实攻击形态。
        /// </summary>
        private static byte[] BuildZipWithUnsafeEntryName(string entryName)
        {
            byte[] name = Encoding.UTF8.GetBytes(entryName);
            byte[] content = Encoding.UTF8.GetBytes("evil\n");

            using var memory = new MemoryStream();

            // Local file header
            memory.Write(new byte[] { 0x50, 0x4B, 0x03, 0x04 });
            WriteUInt16(memory, 20);        // version needed
            WriteUInt16(memory, 0x0800);    // flags: UTF-8 名称
            WriteUInt16(memory, 0);         // method: stored
            WriteUInt16(memory, 0);         // time
            WriteUInt16(memory, 0);         // date
            WriteUInt32(memory, 0);         // crc（用 stored 且内容不校验，7z 只在测试时校验）
            WriteUInt32(memory, (uint)content.Length);
            WriteUInt32(memory, (uint)content.Length);
            WriteUInt16(memory, (ushort)name.Length);
            WriteUInt16(memory, 0);
            memory.Write(name);
            memory.Write(content);

            int centralDirectoryOffset = (int)memory.Length;

            // Central directory header
            memory.Write(new byte[] { 0x50, 0x4B, 0x01, 0x02 });
            WriteUInt16(memory, 20);
            WriteUInt16(memory, 20);
            WriteUInt16(memory, 0x0800);
            WriteUInt16(memory, 0);
            WriteUInt16(memory, 0);
            WriteUInt16(memory, 0);
            WriteUInt32(memory, 0);
            WriteUInt32(memory, (uint)content.Length);
            WriteUInt32(memory, (uint)content.Length);
            WriteUInt16(memory, (ushort)name.Length);
            WriteUInt16(memory, 0);
            WriteUInt16(memory, 0);
            WriteUInt16(memory, 0);
            WriteUInt16(memory, 0);
            WriteUInt32(memory, 0);
            WriteUInt32(memory, 0);
            memory.Write(name);

            int centralDirectorySize = (int)memory.Length - centralDirectoryOffset;

            // End of central directory
            memory.Write(new byte[] { 0x50, 0x4B, 0x05, 0x06 });
            WriteUInt16(memory, 0);
            WriteUInt16(memory, 0);
            WriteUInt16(memory, 1);
            WriteUInt16(memory, 1);
            WriteUInt32(memory, (uint)centralDirectorySize);
            WriteUInt32(memory, (uint)centralDirectoryOffset);
            WriteUInt16(memory, 0);

            return memory.ToArray();
        }

        private static void WriteUInt16(Stream stream, ushort value)
        {
            stream.WriteByte((byte)(value & 0xFF));
            stream.WriteByte((byte)((value >> 8) & 0xFF));
        }

        private static void WriteUInt32(Stream stream, uint value)
        {
            stream.WriteByte((byte)(value & 0xFF));
            stream.WriteByte((byte)((value >> 8) & 0xFF));
            stream.WriteByte((byte)((value >> 16) & 0xFF));
            stream.WriteByte((byte)((value >> 24) & 0xFF));
        }

        private static UTF8Encoding Utf8NoBom => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        private void RequireSevenZip()
        {
            if (string.IsNullOrEmpty(_sevenZip))
            {
                throw new InvalidOperationException(
                    "测试机上没有 7z.exe（ArchiveFixer/tools/7zip/7z.exe），本用例无法运行。");
            }
        }

        private static string LocateSevenZip()
        {
            DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ArchiveFixer.slnx")))
                {
                    string candidate = Path.Combine(
                        directory.FullName,
                        "src", "ArchiveFixer",
                        "tools",
                        "7zip",
                        "7z.exe");

                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }

                directory = directory.Parent;
            }

            string local = Path.Combine(AppContext.BaseDirectory, "tools", "7zip", "7z.exe");

            return File.Exists(local) ? local : string.Empty;
        }

        private void Run7z(params object[] args)
        {
            RequireSevenZip();

            var psi = new ProcessStartInfo(_sevenZip)
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

            using Process process = Process.Start(psi)
                ?? throw new InvalidOperationException("无法启动 7z.exe");

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

                throw new InvalidOperationException("7z 造样本超时：" + string.Join(' ', psi.ArgumentList));
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"7z 失败（exit {process.ExitCode}）：{string.Join(' ', psi.ArgumentList)}\n{stdout}\n{stderr}");
            }
        }

        /// <summary>
        /// 假引擎：在产物目录里造一个**指向目录外的联接点**，再从它里面写出文件。
        ///
        /// 为什么用这个形态：只检查"条目名"（第一道预检）看不出联接点有什么问题 ——
        /// 名字干干净净。而产物目录里的东西真实落点在目录之外，属于"解压后发现越界"这一类，
        /// 正是第二道落点校验存在的理由（不变量 4）。
        /// 用联接点（mklink /J）而不是符号链接：前者不需要管理员权限。
        /// </summary>
        private sealed class JunctionEngine : IArchiveEngine
        {
            public string Id => "junction";

            public string DisplayName => "联接点假引擎（仅测试用）";

            public string Version => "0.0";

            public bool IsAvailable => true;

            public EngineCapabilities Capabilities { get; } = new();

            public Task<ArchiveProbeResult> ProbeAsync(ArchiveRequest request, CancellationToken cancellationToken = default) =>
                Task.FromResult(new ArchiveProbeResult { IsArchive = true, Format = "7Z" });

            public Task<ArchiveListResult> ListAsync(ArchiveRequest request, CancellationToken cancellationToken = default) =>
                Task.FromResult(new ArchiveListResult
                {
                    Success = true,
                    FileCount = 1,
                    TotalUncompressedSize = 8,
                    EngineId = Id,
                    EngineVersion = Version
                });

            public Task<ArchiveOperationResult> TestAsync(ArchiveRequest request, CancellationToken cancellationToken = default) =>
                Task.FromResult(ArchiveOperationResult.CreateSuccess(0, "OK", string.Empty, TimeSpan.Zero));

            public Task<ArchiveOperationResult> ExtractAsync(
                ArchiveRequest request,
                ExtractOptions options,
                CancellationToken cancellationToken = default)
            {
                string outputPath = request.OutputPath ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(outputPath))
                {
                    Directory.CreateDirectory(outputPath);
                    File.WriteAllText(Path.Combine(outputPath, "innocent.txt"), "正常产物\n", Utf8NoBom);

                    string outside = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(outputPath)!)!, "outside");
                    Directory.CreateDirectory(outside);

                    string link = Path.Combine(outputPath, "link-out");

                    if (!Directory.Exists(link))
                    {
                        CreateJunction(link, outside);
                    }

                    File.WriteAllText(Path.Combine(link, "escaped.txt"), "越界产物\n", Utf8NoBom);
                }

                return Task.FromResult(ArchiveOperationResult.CreateSuccess(0, "OK", string.Empty, TimeSpan.Zero));
            }

            /// <summary>用 mklink /J 造目录联接点（不需要管理员权限，也不改注册表）。</summary>
            private static void CreateJunction(string linkPath, string targetPath)
            {
                var psi = new ProcessStartInfo("cmd.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add("mklink");
                psi.ArgumentList.Add("/J");
                psi.ArgumentList.Add(linkPath);
                psi.ArgumentList.Add(targetPath);

                using Process process = Process.Start(psi)
                    ?? throw new InvalidOperationException("无法启动 cmd.exe 建联接点");

                process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                process.WaitForExit(30_000);
            }
        }

        /// <summary>
        /// 假引擎：第 0 层解出 1 个内层包（单链 → 自动展开），第 1 层解出 2 个内层包
        /// （更深层的多分支 → 必须停下并报告，而不是静默收尾）。
        /// </summary>
        private sealed class BranchingEngine : IArchiveEngine
        {
            public string Id => "branching";

            public string DisplayName => "多分支假引擎（仅测试用）";

            public string Version => "0.0";

            public bool IsAvailable => true;

            public EngineCapabilities Capabilities { get; } = new();

            public Task<ArchiveProbeResult> ProbeAsync(ArchiveRequest request, CancellationToken cancellationToken = default) =>
                Task.FromResult(new ArchiveProbeResult { IsArchive = true, Format = "7Z" });

            public Task<ArchiveListResult> ListAsync(ArchiveRequest request, CancellationToken cancellationToken = default) =>
                Task.FromResult(new ArchiveListResult
                {
                    Success = true,
                    FileCount = 1,
                    TotalUncompressedSize = 16,
                    EngineId = Id,
                    EngineVersion = Version
                });

            public Task<ArchiveOperationResult> TestAsync(ArchiveRequest request, CancellationToken cancellationToken = default) =>
                Task.FromResult(ArchiveOperationResult.CreateSuccess(0, "OK", string.Empty, TimeSpan.Zero));

            public Task<ArchiveOperationResult> ExtractAsync(
                ArchiveRequest request,
                ExtractOptions options,
                CancellationToken cancellationToken = default)
            {
                string outputPath = request.OutputPath ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(outputPath))
                {
                    Directory.CreateDirectory(outputPath);

                    if (request.ArchivePath.EndsWith("branches.7z", StringComparison.OrdinalIgnoreCase))
                    {
                        WriteFake7z(Path.Combine(outputPath, "level1.7z"));
                    }
                    else
                    {
                        WriteFake7z(Path.Combine(outputPath, "a.7z"));
                        WriteFake7z(Path.Combine(outputPath, "b.7z"));
                        File.WriteAllText(Path.Combine(outputPath, "note.txt"), "两个内层包\n", Utf8NoBom);
                    }
                }

                return Task.FromResult(ArchiveOperationResult.CreateSuccess(0, "OK", string.Empty, TimeSpan.Zero));
            }

            /// <summary>写一个只有 7z 魔数开头的文件：够探测层认出"这是内层归档"，不需要真能解开。</summary>
            private static void WriteFake7z(string path)
            {
                File.WriteAllBytes(path, new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x04 });
            }
        }
    }
}

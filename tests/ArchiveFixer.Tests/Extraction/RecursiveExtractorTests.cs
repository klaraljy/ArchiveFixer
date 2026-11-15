using ArchiveFixer.BindingModels;
using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ArchiveFixer.Tests;

/// <summary>
/// M4「递归解压」的验收测试（AGENTS.md §10 M4）。
///
/// 测试策略（对应用户要求）：
/// - 用**真实文件**跑：临时目录里先造真实归档（真实 7z.exe 生成 zip / 7z），再让
///   <see cref="RecursiveExtractor"/> 去解，断言的是磁盘上的真实产物；
/// - <see cref="IArchiveProber"/> 用本文件里的假实现（魔数 + 后缀两路判断），
///   因为"是不是归档"不是本模块要验的东西，它只该提供一个可控的判据；
/// - <see cref="IArchiveEngine"/> 用真实的 <see cref="SevenZipEngine"/>。
///   这样才测得出"引擎报密码错误 → 换下一个候选"这类真实链路。
///   只有"展开比超限"那条用假引擎 —— 真压缩包很难天然造出 500 倍展开比
///   （真造一个压缩炸弹既慢又没意义），而那条上限本身必须被验证。
///
/// 临时目录全部建在 <see cref="Path.GetTempPath"/> 下，<see cref="Dispose"/> 里删干净。
/// </summary>
// 递归工作区的根是**进程级静态**（<c>RecursiveExtractor.ConfiguredWorkspaceRoot</c>）：
// 本文件只读它，别的用例类会改写它 —— 全量并发跑时被抢走会让工作区落到别的用例的临时目录里、
// 那个目录又被对方 Dispose 掉（现场：`工作区删不掉` 断言 "第 0 层的产物目录不存在"）。
// 所以与改写它的那些类一起**串行**跑，见 InnerLayerContinuationTests 顶部的 CollectionDefinition。
[Collection("ExtractionState")]
[Trait("Category", "Extended")]
public sealed partial class RecursiveExtractorTests : IDisposable
{
    /// <summary>测试专用合成密码；只出现在测试数据里，不是任何真实凭据。</summary>
    private const string TestPass123 = "TestPass123!";

    private readonly string _root;
    private readonly string _sevenZip;

    public RecursiveExtractorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerRecursive", Guid.NewGuid().ToString("N"));
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
            // 临时目录删不掉不影响结论，下次跑测试换一个 GUID 目录。
        }
    }

    // ────────────────────────────── 测试基础设施 ──────────────────────────────

    private async Task<RecursionResult> ExtractAsync(string archivePath, string outputDirectory)
    {
        return await ExtractAsync(archivePath, outputDirectory, logLines: null);
    }

    /// <summary>
    /// 同上，另收一份日志（判据要说清"为什么这么走"时用得上；<paramref name="logLines"/> 传 null = 不记日志，
    /// 与只传两个参数的那个重载**逐字相同**）。
    /// </summary>
    private async Task<RecursionResult> ExtractAsync(
        string archivePath,
        string outputDirectory,
        List<(string Level, string Message)>? logLines)
    {
        var task = new ArchiveTask(archivePath);
        var extractor = new RecursiveExtractor(
            new SevenZipEngine(),
            new MagicAwareProber(),
            _ => new[] { string.Empty },
            limits: null,
            log: logLines == null ? null : (level, message) => logLines.Add((level, message)));

        return await extractor.ExtractAsync(task, outputDirectory, null, CancellationToken.None);
    }

    /// <summary>从结果里反推某一层产物的目录（第 0 层就是工作区里的 layer-000\output）。</summary>
    private static string ExtractWorkspacePath(RecursionResult result)
    {
        return result.Layers[0].OutputPath;
    }

    /// <summary>在目录下递归找指定文件名的文件；找不到返回 null。</summary>
    private static string? FindFileUnder(string root, string fileName)
    {
        if (!Directory.Exists(root))
        {
            return null;
        }

        return Directory
            .GetFiles(root, fileName, SearchOption.AllDirectories)
            .FirstOrDefault();
    }

    /// <summary>造一个单链嵌套包：outer.zip → inner.7z → data.txt（外加一个说明文件）。</summary>
    private (string Outer, string Inner) BuildChainPackage()
    {
        string source = BuildSourceDir(("data.txt", "最内层的内容\n"));

        string inner = Path.Combine(_root, "inner.7z");
        Run7z("a", "-t7z", inner, Path.Combine(source, "*"));

        string outerSource = Path.Combine(_root, "_chain");
        Directory.CreateDirectory(outerSource);
        File.WriteAllText(Path.Combine(outerSource, "说明.txt"), "只有一个内层归档\n", Utf8NoBom);
        File.Copy(inner, Path.Combine(outerSource, "inner.7z"));

        string outer = Path.Combine(_root, "chain.zip");
        Run7z("a", "-tzip", outer, Path.Combine(outerSource, "*"));

        return (outer, inner);
    }

    /// <summary>
    /// 造"1 个内层归档 + 旁边两个**非说明类**文件"的包：<c>siblings.zip</c> 里是
    /// <c>sibling-inner.7z</c>（里面有 data.txt）+ <c>movie.mp4</c> + <c>手册.pdf</c>。
    ///
    /// <para>这正是用户 2026-10-04 点名的那一格（真机 `第6集.7z`）：旧判据把"
    /// 旁边有非说明类文件"当成"多分支"停下，而这一层其实**只有一个**压缩包。</para>
    /// </summary>
    private (string Outer, string Inner) BuildSingleInnerWithNonInformationalSiblings()
    {
        string source = BuildSourceDir(new[] { ("data.txt", "内层包里的内容\n") }, "_sibling_src");

        string inner = Path.Combine(_root, "sibling-inner.7z");
        Run7z("a", "-t7z", inner, Path.Combine(source, "*"));

        string outerSource = Path.Combine(_root, "_sibling_outer");
        Directory.CreateDirectory(outerSource);
        File.Copy(inner, Path.Combine(outerSource, "sibling-inner.7z"));
        File.WriteAllBytes(Path.Combine(outerSource, "movie.mp4"), new byte[4096]);
        File.WriteAllText(Path.Combine(outerSource, "手册.pdf"), "假装是 PDF\n", Utf8NoBom);

        string outer = Path.Combine(_root, "siblings.zip");
        Run7z("a", "-tzip", outer, Path.Combine(outerSource, "*"));

        return (outer, inner);
    }

    /// <summary>造一个双分支包：outer.zip 里同时有 a.7z 与 b.7z（外加一个说明文件）。</summary>
    private (string Outer, string BranchA) BuildTwoBranchPackage()
    {
        string sourceA = BuildSourceDir(("a.txt", "分支 A\n"));
        string sourceB = BuildSourceDir(new[] { ("b.txt", "分支 B\n") }, "_srcb");

        string branchA = Path.Combine(_root, "a.7z");
        string branchB = Path.Combine(_root, "b.7z");
        Run7z("a", "-t7z", branchA, Path.Combine(sourceA, "*"));
        Run7z("a", "-t7z", branchB, Path.Combine(sourceB, "*"));

        string outerSource = Path.Combine(_root, "_branches");
        Directory.CreateDirectory(outerSource);
        File.Copy(branchA, Path.Combine(outerSource, "a.7z"));
        File.Copy(branchB, Path.Combine(outerSource, "b.7z"));
        File.WriteAllText(Path.Combine(outerSource, "note.txt"), "两个内层归档\n", Utf8NoBom);

        string outer = Path.Combine(_root, "branches.zip");
        Run7z("a", "-tzip", outer, Path.Combine(outerSource, "*"));

        return (outer, branchA);
    }


    /// <summary>同步记账的进度接收器（不用 <see cref="Progress{T}"/>：它会把回调甩到线程池，顺序就不好断言了）。</summary>
    private sealed class RecordingProgress : IProgress<ArchiveProgress>
    {
        private readonly List<ArchiveProgress> _sink;

        public RecordingProgress(List<ArchiveProgress> sink) => _sink = sink;

        public void Report(ArchiveProgress? value)
        {
            if (value != null)
            {
                _sink.Add(value);
            }
        }
    }

    /// <summary>
    /// 造一个"外层不加密 zip + 内层加密 7z"的包，用于验证密码重试链路。
    /// </summary>
    /// <param name="encryptFileNames">
    /// 内层包是否**连文件名一起加密**（<c>-mhe=on</c>，默认 true）。
    ///
    /// <para>默认 true 的理由：这样"密码错"与"密码对"的差异没有别的旁路可看。
    /// ⚠ 2026-10-05 起它还决定**结论落在哪一档**：`-mhe` 的包连内容清单都列不出来 ⇒
    /// 收尾是「文件名已加密」（与单层路径对齐）；要验"密码错误 / 达到密码尝试上限"那几档
    /// 必须传 false（头不加密 ⇒ 清单列得出来，失败发生在解压阶段）。
    /// 这也是 `密码_候选全试完仍失败报WrongPassword` 显式传 false 的原因。</para>
    /// </param>
    private string BuildEncryptedInnerPackage(bool encryptFileNames = true)
    {
        string source = BuildSourceDir(("secret.txt", "加密内容\n"));

        string inner = Path.Combine(_root, "secret.7z");

        if (encryptFileNames)
        {
            Run7z("a", "-t7z", inner, "-p" + TestPass123, "-mhe=on", Path.Combine(source, "*"));
        }
        else
        {
            Run7z("a", "-t7z", inner, "-p" + TestPass123, Path.Combine(source, "*"));
        }

        string outerSource = Path.Combine(_root, "_enc");
        Directory.CreateDirectory(outerSource);
        File.Copy(inner, Path.Combine(outerSource, "secret.7z"));

        string outer = Path.Combine(_root, "enc-outer.zip");
        Run7z("a", "-tzip", outer, Path.Combine(outerSource, "*"));

        return outer;
    }

    /// <summary>
    /// 造一个"内容是 <c>内容物\&lt;名字&gt;.bin</c>"的真 7z 包，返回归档路径。
    /// 就地替换那一组用例拿它当内层包（用户例子里每个包解出来都是一层 <c>内容物\</c>）。
    /// </summary>
    private string BuildInnerPackage(string name)
    {
        string payload = Path.Combine(_root, "_payload-" + name, "内容物");
        Directory.CreateDirectory(payload);
        File.WriteAllText(Path.Combine(payload, name + ".bin"), name + " 的内容\n", Utf8NoBom);

        string archive = Path.Combine(_root, name + ".7z");
        Run7z("a", "-t7z", archive, Path.Combine(Path.GetDirectoryName(payload)!, "*"));

        return archive;
    }

    /// <summary>目录树里的全部条目（目录 + 文件），相对路径、序号排序 —— 用来逐字对照整棵树。</summary>
    private static IReadOnlyList<string> Tree(string root)
    {
        return Directory
            .EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
    }

    private string BuildSourceDir(params (string Name, string Content)[] files)
    {
        return BuildSourceDir(files, "_src");
    }

    private string BuildSourceDir((string Name, string Content)[] files, string directoryName)
    {
        string directory = Path.Combine(_root, directoryName);
        Directory.CreateDirectory(directory);

        foreach ((string name, string content) in files)
        {
            File.WriteAllText(Path.Combine(directory, name), content, Utf8NoBom);
        }

        return directory;
    }

    private static UTF8Encoding Utf8NoBom => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// 调真实 7z.exe 造测试数据。
    /// 这里允许直接起进程：它是**测试**，不是产品代码 —— 产品侧拼 7z 参数只允许存在于
    /// <c>Engines/SevenZip/</c> 内部（AGENTS.md §3.1 四条禁止项）。
    /// </summary>
    private void Run7z(params object[] args)
    {
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
            if (arg is IEnumerable<string> many)
            {
                foreach (string item in many)
                {
                    psi.ArgumentList.Add(item);
                }
            }
            else
            {
                psi.ArgumentList.Add(arg?.ToString() ?? string.Empty);
            }
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

            throw new InvalidOperationException($"7z 造样本超时：{string.Join(' ', psi.ArgumentList)}");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"7z 失败（exit {process.ExitCode}）：{string.Join(' ', psi.ArgumentList)}\n{stdout}\n{stderr}");
        }
    }

    private void RequireSevenZip()
    {
        /*
         * 正常路径下不会走到这里：没 7z 时 [SevenZipFact] 已经在发现阶段把这批用例跳过了。
         * 留着是为了防"特性探测到了、fixture 却没拿到"这种不一致 ——
         * 那种情况下必须**响亮地失败**，绝不能悄悄跳过（跳过会让整批用例假绿）。
         */
        if (string.IsNullOrEmpty(_sevenZip))
        {
            throw new InvalidOperationException(
                "找不到内置 7z.exe，且 [SevenZipFact] 没有把它跳过：环境与特性探测结果不一致。");
        }
    }

    /// <summary>
    /// 测试用探测器：先看文件头魔数，再看后缀。
    ///
    /// 为什么这一层要用假的：本模块要验的是"探测到内层归档之后怎么决策"，
    /// 不是"能不能识别格式"。把它做成可控的，才能构造出"恰好 1 个 / 恰好 2 个"的确定局面。
    /// 判断本身仍然按真实规矩来（魔数优先，避免只看后缀把说明文件误判成归档）。
    /// </summary>
    private sealed class MagicAwareProber : IArchiveProber
    {
        public Task<bool> IsArchiveAsync(string filePath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return Task.FromResult(false);
            }

            return Task.FromResult(HasArchiveSignature(filePath) || HasArchiveExtension(filePath));
        }

        private static bool HasArchiveExtension(string filePath)
        {
            string extension = Path.GetExtension(filePath);

            return extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".7z", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".rar", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".tar", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".gz", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>按魔数判断常见归档（0 字节 / 太短的文件一律不是）。</summary>
    private static bool HasArchiveSignature(string filePath)
    {
        try
        {
            Span<byte> header = stackalloc byte[512];

            using FileStream stream = File.OpenRead(filePath);
            int read = stream.Read(header);

            if (read < 6)
            {
                return false;
            }

            ReadOnlySpan<byte> bytes = header[..read];

            // 7z：37 7A BC AF 27 1C
            if (bytes[0] == 0x37 && bytes[1] == 0x7A && bytes[2] == 0xBC &&
                bytes[3] == 0xAF && bytes[4] == 0x27 && bytes[5] == 0x1C)
            {
                return true;
            }

            // zip（含空归档 50 4B 05 06、分卷 50 4B 07 08）
            if (bytes[0] == 0x50 && bytes[1] == 0x4B &&
                (bytes[2] == 0x03 || bytes[2] == 0x05 || bytes[2] == 0x07))
            {
                return true;
            }

            // rar4 / rar5：52 61 72 21 1A 07
            if (bytes[0] == 0x52 && bytes[1] == 0x61 && bytes[2] == 0x72 &&
                bytes[3] == 0x21 && bytes[4] == 0x1A && bytes[5] == 0x07)
            {
                return true;
            }

            // gzip：1F 8B
            if (bytes[0] == 0x1F && bytes[1] == 0x8B)
            {
                return true;
            }

            // tar：偏移 257 处是 "ustar"
            if (bytes.Length >= 262 &&
                bytes[257] == (byte)'u' && bytes[258] == (byte)'s' &&
                bytes[259] == (byte)'t' && bytes[260] == (byte)'a' &&
                bytes[261] == (byte)'r')
            {
                return true;
            }

            return false;
        }
        catch
        {
            // 读不出来（占用 / 权限）时按"不是归档"处理，与 IArchiveProber 的约定一致。
            return false;
        }
    }

    /// <summary>
    /// 只用于"展开比超限"这条：真压缩包很难天然造出 500 倍展开比。
    /// 其余行为（成功判定、错误分类）都尽量走真引擎，不用这个。
    ///
    /// 另外也用于"并发 / 取消"这两条：它们要控制"解压到一半"的时机，
    /// 真 7z 跑得太快、卡不住，用假引擎才能把竞态摆成确定的局面。
    /// </summary>
    private sealed class FakeEngine : IArchiveEngine
    {
        private readonly long _uncompressedSize;

        public FakeEngine(long uncompressedSize = 0)
        {
            _uncompressedSize = uncompressedSize;
        }

        /// <summary>非 null 时接管解压：并发 / 取消用例靠它决定"什么时候返回、返回什么"。</summary>
        public Func<ArchiveRequest, ExtractOptions, Task<ArchiveOperationResult>>? OnExtractAsync { get; set; }

        public string Id => "fake";

        public string DisplayName => "假引擎（仅测试用）";

        public string Version => "0.0";

        public bool IsAvailable => true;

        public EngineCapabilities Capabilities { get; } = new();

        public Task<ArchiveProbeResult> ProbeAsync(
            ArchiveRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ArchiveProbeResult { IsArchive = true, Format = "7Z" });
        }

        public Task<ArchiveListResult> ListAsync(
            ArchiveRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ArchiveListResult
            {
                Success = true,
                FileCount = 1,
                TotalUncompressedSize = _uncompressedSize,
                EngineId = Id,
                EngineVersion = Version
            });
        }

        public Task<ArchiveOperationResult> TestAsync(
            ArchiveRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Succeeded());
        }

        public Task<ArchiveOperationResult> ExtractAsync(
            ArchiveRequest request,
            ExtractOptions options,
            CancellationToken cancellationToken = default)
        {
            if (OnExtractAsync != null)
            {
                return OnExtractAsync(request, options);
            }

            // 假引擎也要产出真实文件：否则后面"量产物"的代码路径根本没跑到。
            WritePayload(request.OutputPath);

            return Task.FromResult(Succeeded());
        }
    }

    /// <summary>假引擎的产物：一个普通文件（不是归档，所以不会被探测成"内层包"）。</summary>
    private static void WritePayload(string? outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return;
        }

        Directory.CreateDirectory(outputPath);
        File.WriteAllText(Path.Combine(outputPath, "payload.bin"), "假引擎产物", Utf8NoBom);
    }

    private static ArchiveOperationResult Succeeded()
    {
        return ArchiveOperationResult.CreateSuccess(0, "OK", string.Empty, TimeSpan.Zero);
    }
}

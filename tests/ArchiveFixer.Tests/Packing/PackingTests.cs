using ArchiveFixer.Engines;
using ArchiveFixer.Models;
using ArchiveFixer.Packing;
using ArchiveFixer.Password;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace ArchiveFixer.Tests;

/// <summary>
/// 打包功能（用户 2026-09-22 需求第 10 条；设计 docs/打包功能.md）。
///
/// <para>这一组钉住的是**判据**，不是实现细节：</para>
/// <list type="bullet">
/// <item><description>分卷怎么算（用户的两个例子 1 GB → 2 卷、1.5 GB → 3 卷）；</description></item>
/// <item><description>密码必填、两次要一致、**明文不许出现在日志里**；</description></item>
/// <item><description>没有 <c>Rar.exe</c> 时明确报错、**一个字节都不产出**、绝不用 7z 假装生成 .rar；</description></item>
/// <item><description>落点非法（B 在 A 里、rar 在 B 里、rar 在 A 里、A 在 B 里）一律拒绝；</description></item>
/// <item><description>空间不够**不开始**，并报出需要 / 可用 / 差多少；</description></item>
/// <item><description>**真 7z 端到端**：1 MiB 分卷 → <c>.7z.001</c>/<c>.002</c>，对密码列得出、错密码列不出；</description></item>
/// <item><description>**外层容器三选一**（2026-09-23）：7z 外层的命令 / 产物名 / `-slt` 校验、
/// 不做外层时一个外层文件都不产、7z 外层没有 `Rar.exe` 也照常成功；</description></item>
/// <item><description>**自选 `Rar.exe` 路径**：合法 → 解析用它；不存在 → 保存被拦下并说明改法；留空 → 回落已装目录；</description></item>
/// <item><description>取消语义：第二步取消 → 状态是"已取消"、没有 rar、**分卷还在**；</description></item>
/// <item><description>产物校验：少一卷 / 混进别的东西 → **不算成功**（不变量 6）。</description></item>
/// </list>
///
/// <para><b>真机依赖</b>：需要真 7z 的用例在缺 <c>tools\7zip\7z.exe</c> 时打印原因后跳过
/// （与 <c>UnRarRealSampleTests</c> 同一套写法）；需要真 <c>Rar.exe</c> 的那一条在没有
/// WinRAR 的机器上同样跳过 —— 但"没有 Rar.exe 时会怎样"那条判据用的是**假的 runner**，
/// 因此在任何机器上都跑得到。</para>
///
/// <para><b>为什么整类串行跑</b>（<c>ArchiveFixerGlobalState</c>，见 <c>InnerLayerContinuationTests</c>
/// 顶部的 CollectionDefinition）：① ⑪ 那一组要断言设置真的推到了进程级的
/// <c>ToolLocator.Default.CustomRarExePath</c>，而别的用例构造 <c>MainViewModel</c> /
/// <c>SettingsViewModel</c> 时会把设置重推一遍（并行跑时实测出现过"刚推完就被别人清成空串"）；
/// ② 这一组会真起 <c>7z.exe</c> / <c>Rar.exe</c> 进程，与别的重活并行只会互相抢 CPU。
/// </para>
/// </summary>
[Collection("GlobalState")]
public partial class PackingTests : IDisposable
{
    /// <summary>测试用密码。**占位符**，不是任何真实密码（AGENTS.md §8）。</summary>
    private const string SamplePassword = "<sample-password>";

    /// <summary>另一个占位符（用来验"外层用另一个密码"）。</summary>
    private const string OuterSamplePassword = "<sample-outer-password>";

    private const long OneMebibyte = 1024L * 1024L;

    private readonly string _root;
    private readonly ITestOutputHelper _output;

    public PackingTests(ITestOutputHelper output)
    {
        _output = output;
        _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerPacking", Guid.NewGuid().ToString("N"));
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
            // 临时目录删不掉不影响结论，下次跑换一个 GUID 目录。
        }
    }

    private SettingsViewModel CreateSettingsViewModel(string dataFolder)
    {
        return new SettingsViewModel(
            AppSettings.CreateDefault(),
            new SettingsService(new PathService
            {
                DataRootDirectory = Path.Combine(_root, dataFolder)
            }));
    }

    // ══════════════════════════ 辅助 ══════════════════════════

    private void Skip(string reason)
    {
        _output.WriteLine("跳过：" + reason);
    }

    /// <summary>造一个源文件夹：<paramref name="fileCount"/> 个**不可压缩**的随机文件。</summary>
    private string CreateSourceFolder(string name, int fileCount, int bytesPerFile)
    {
        string directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);

        var random = new Random(20260922);

        for (int i = 1; i <= fileCount; i++)
        {
            var bytes = new byte[bytesPerFile];
            random.NextBytes(bytes);

            File.WriteAllBytes(Path.Combine(directory, "rand" + i + ".bin"), bytes);
        }

        return directory;
    }

    /// <summary>内置 7z.exe（优先程序输出目录，其次源码树）。</summary>
    private static string LocateSevenZip()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ArchiveFixer.slnx")))
            {
                string candidate = Path.Combine(directory.FullName, "src", "ArchiveFixer", "tools", "7zip", "7z.exe");

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

    /// <summary>文件内容的 SHA-256（用来钉"原文件一个字节都没动"）。</summary>
    private static string Sha256Of(string path)
    {
        using FileStream stream = File.OpenRead(path);

        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    /// <summary>
    /// 一整棵目录的"内容指纹"：递归取每个文件（**相对路径 + 内容的 SHA-256**），
    /// 按相对路径排序后连成一串再哈希一次。
    ///
    /// <para>用来钉"搬进其余物之后内容一个字节没少 / 一个文件没多" —— 只比文件名或只比大小都会被漏掉。</para>
    /// </summary>
    private static string HashDirectory(string directory)
    {
        var entries = new List<string>();

        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(directory, file).Replace('\\', '/');

            entries.Add(relative + "=" + Sha256Of(file));
        }

        entries.Sort(StringComparer.Ordinal);

        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", entries))));
    }

    /// <summary>
    /// 起一次 7z 并收输出。
    ///
    /// ⚠ <b>必须重定向 stdin</b>：加密头（<c>-mhe=on</c>）的包在**没给密码**时 7-Zip 会
    /// 交互式地问密码（实测会一直等），测试进程会挂在那里。所以这里永远传 <c>-p</c>，
    /// 并且把 stdin 立刻关掉。
    /// </summary>
    private static (int ExitCode, string StdOut, string StdErr) RunSevenZip(string sevenZip, params string[] args)
    {
        return RunProcess(sevenZip, args);
    }

    private static (int ExitCode, string StdOut, string StdErr) RunProcess(string exe, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using Process process = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动：" + exe);

        try
        {
            process.StandardInput.Close();
        }
        catch
        {
        }

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
            }

            throw new TimeoutException($"{Path.GetFileName(exe)} 超时：{string.Join(' ', args)}");
        }

        return (process.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// 同步收集进度。<b>刻意不用 <see cref="Progress{T}"/></b>：那个会把回调投到捕获的
    /// 同步上下文上（xUnit 有自己的），断言可能跑在回调之前，测试就变成了看运气。
    /// </summary>
    private sealed class SynchronousProgress : IProgress<PackStepProgress>
    {
        private readonly List<PackStepProgress> _sink;
        private readonly object _lock = new();

        public SynchronousProgress(List<PackStepProgress> sink)
        {
            _sink = sink;
        }

        public void Report(PackStepProgress value)
        {
            lock (_lock)
            {
                _sink.Add(value);
            }
        }
    }

    /// <summary>
    /// 可控的假 runner：验"没有 Rar.exe 时一步都不跑""第二步取消""产物对不上""7z 外层的命令与校验"
    /// 这类判据，不需要真的装 WinRAR / 起进程（验收判据点名要求用假 runner 断言没被调用）。
    /// </summary>
    private sealed class FakePackRunner : IPackProcessRunner
    {
        private readonly IPackProcessRunner? _sevenZipDelegate;
        private readonly Func<IReadOnlyList<string>, PackStepResult>? _sevenZipResult;
        private readonly Func<IReadOnlyList<string>, PackStepResult>? _sevenZipListResult;
        private readonly Func<IReadOnlyList<string>, PackStepResult>? _rarCreateResult;
        private readonly Func<IReadOnlyList<string>, PackStepResult>? _rarListResult;

        public FakePackRunner(
            IPackProcessRunner? sevenZipDelegate = null,
            Func<IReadOnlyList<string>, PackStepResult>? sevenZipResult = null,
            Func<IReadOnlyList<string>, PackStepResult>? rarCreateResult = null,
            Func<IReadOnlyList<string>, PackStepResult>? rarListResult = null,
            Func<IReadOnlyList<string>, PackStepResult>? sevenZipListResult = null)
        {
            _sevenZipDelegate = sevenZipDelegate;
            _sevenZipResult = sevenZipResult;
            _rarCreateResult = rarCreateResult;
            _rarListResult = rarListResult;
            _sevenZipListResult = sevenZipListResult;
        }

        public int TotalCalls { get; private set; }

        public int SevenZipCalls { get; private set; }

        /// <summary>7z 的列条目调用（外层容器 = 7z 时的产物校验）。</summary>
        public int SevenZipListCalls { get; private set; }

        public int RarCreateCalls { get; private set; }

        public int RarListCalls { get; private set; }

        /// <summary>走了 Rar.exe 这条路的调用总数（"没有 Rar.exe 也照常成功"这条判据靠它）。</summary>
        public int RarCalls => RarCreateCalls + RarListCalls;

        public List<IReadOnlyList<string>> Arguments { get; } = new();

        /// <summary>每次调用时那个"在哪个目录里跑"（外层那一步必须是落点目录）。</summary>
        public List<string> WorkingDirectories { get; } = new();

        public async Task<PackStepResult> RunAsync(
            PackToolKind tool,
            IReadOnlyList<string> arguments,
            string usedPassword,
            IProgress<PackStepProgress>? progress,
            CancellationToken cancellationToken,
            string? workingDirectory = null)
        {
            TotalCalls++;
            Arguments.Add(arguments);
            WorkingDirectories.Add(workingDirectory ?? string.Empty);

            if (tool == PackToolKind.SevenZip)
            {
                SevenZipCalls++;

                /*
                 * 7z 也有"列条目"这一步（外层容器 = 7z 时的产物校验）：命令的第一个参数是 l。
                 * 内层分卷与外层容器都是 a（靠有没有 -v 参数区分），所以只能按子命令认。
                 */
                bool isSevenZipListing = arguments.Count > 0 &&
                                         string.Equals(arguments[0], "l", StringComparison.OrdinalIgnoreCase);

                if (isSevenZipListing)
                {
                    SevenZipListCalls++;

                    if (_sevenZipListResult != null)
                    {
                        return _sevenZipListResult(arguments);
                    }

                    if (_sevenZipDelegate != null)
                    {
                        return await _sevenZipDelegate
                            .RunAsync(tool, arguments, usedPassword, progress, cancellationToken, workingDirectory)
                            .ConfigureAwait(false);
                    }

                    return Ok();
                }

                if (_sevenZipDelegate != null)
                {
                    return await _sevenZipDelegate
                        .RunAsync(tool, arguments, usedPassword, progress, cancellationToken)
                        .ConfigureAwait(false);
                }

                return _sevenZipResult?.Invoke(arguments) ?? Ok();
            }

            bool isListing = arguments.Count > 0 &&
                             string.Equals(arguments[0], "lb", StringComparison.OrdinalIgnoreCase);

            if (isListing)
            {
                RarListCalls++;

                return _rarListResult?.Invoke(arguments) ?? Ok();
            }

            RarCreateCalls++;

            return _rarCreateResult?.Invoke(arguments) ?? Ok();
        }

        public static PackStepResult Ok(string output = "")
        {
            return new PackStepResult
            {
                Success = true,
                ExitCode = 0,
                StandardOutput = output
            };
        }

        /// <summary>一次 7z 调用是不是"切分卷"（只有它带 -v）。</summary>
        public static bool IsVolumeCall(IReadOnlyList<string> arguments)
        {
            return arguments.Any(a => a.StartsWith("-v", StringComparison.Ordinal));
        }
    }
}

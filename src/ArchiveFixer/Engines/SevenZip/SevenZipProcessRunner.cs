using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Engines.SevenZip;

/// <summary>
/// 7-Zip 命令行进程的调用器：**公开 API 门面**，只做转发与工具路径暴露。
///
/// <para>原先 1578 行的实现按职责拆到同目录的协作类（同一命名空间，外部调用点不变）：</para>
/// <list type="bullet">
/// <item><description><see cref="SevenZipCommandLine"/> —— 命令参数：拼装、命令/归档识别、脱敏摘要；</description></item>
/// <item><description><see cref="SevenZipProcessLauncher"/> —— 进程启动：StartInfo 形状与"起得来/起不来"；</description></item>
/// <item><description><see cref="SevenZipOutputCapture"/> —— 输出读取：两路缓冲区、输出泵、有上限的收尾等待；</description></item>
/// <item><description><see cref="SevenZipIdleWatch"/> —— 超时取消：连续无输出的硬上限；</description></item>
/// <item><description><see cref="SevenZipProcessTerminator"/> —— 收尸：只杀自己启动的 PID 及其子进程；</description></item>
/// <item><description><see cref="SevenZipResultClassifier"/> —— 错误分类：退出码/输出 → 结果（含缺卷二次判定）；</description></item>
/// <item><description><see cref="SevenZipProcessSession"/> / <see cref="SevenZipArchiveOperations"/> /
/// <see cref="SevenZipDecryptedPrefixReader"/> —— 跑一次运行 / 测试与解压 / 读解密前缀的编排。</description></item>
/// </list>
///
/// <para>边界（AGENTS.md §3.1 的四条禁止项）：参数拼接、7-Zip 文本输出解析、退出码到错误的映射，
/// **全部只能存在于本命名空间内**。核心模块（ViewModel / 调度 / 递归）一律通过
/// <see cref="IArchiveEngine"/> 调用，不得直接碰 7z.exe。</para>
/// </summary>
public sealed class SevenZipProcessRunner
{
    private readonly ToolLocator _tools;

    public SevenZipProcessRunner()
        : this(ToolLocator.Default)
    {
    }

    public SevenZipProcessRunner(ToolLocator tools)
    {
        _tools = tools ?? ToolLocator.Default;
    }

    /// <summary>
    /// 外部工具路径统一由 <see cref="ToolLocator"/> 解析。
    /// 这里不再自己拼路径 —— 以前 App.xaml.cs / PathService / ExtractService 各自拼一遍，
    /// 还带一个写死的 D:\7-Zip 回退，属于必须还的债（AGENTS.md §3.1）。
    /// </summary>
    public ToolLocator Tools => _tools;

    public bool CheckSevenZipExists()
    {
        return _tools.SevenZipExists;
    }

    public bool CheckSevenZipDllExists()
    {
        return _tools.SevenZipDllExists;
    }

    public string GetSevenZipPath()
    {
        return _tools.SevenZipExePath;
    }

    public string GetSevenZipDllPath()
    {
        return _tools.SevenZipDllPath;
    }

    public Task<ArchiveOperationResult> TestArchiveAsync(
        string archivePath,
        string password,
        CancellationToken cancellationToken = default)
    {
        return TestArchiveAsync(archivePath, password, cancellationToken, null);
    }

    /// <summary>带进度 / 卡住提示的测试（<paramref name="progressContext"/> 可空）。</summary>
    public Task<ArchiveOperationResult> TestArchiveAsync(
        string archivePath,
        string password,
        CancellationToken cancellationToken,
        EngineProgressContext? progressContext)
    {
        return SevenZipArchiveOperations.TestAsync(
            _tools,
            archivePath,
            password,
            cancellationToken,
            progressContext);
    }

    public Task<ArchiveOperationResult> ExtractArchiveAsync(
        string archivePath,
        string outputPath,
        string password,
        ExtractOptions options,
        CancellationToken cancellationToken = default)
    {
        return ExtractArchiveAsync(
            archivePath,
            outputPath,
            password,
            options,
            cancellationToken,
            null);
    }

    /// <summary>带进度 / 卡住提示的解压（<paramref name="progressContext"/> 可空）。</summary>
    public Task<ArchiveOperationResult> ExtractArchiveAsync(
        string archivePath,
        string outputPath,
        string password,
        ExtractOptions options,
        CancellationToken cancellationToken,
        EngineProgressContext? progressContext)
    {
        return SevenZipArchiveOperations.ExtractAsync(
            _tools,
            archivePath,
            outputPath,
            password,
            options,
            cancellationToken,
            progressContext);
    }

    public Task<ArchiveOperationResult> RunSevenZipAsync(
        IEnumerable<string> arguments,
        CancellationToken cancellationToken = default)
    {
        return RunSevenZipAsync(arguments, string.Empty, cancellationToken);
    }

    public Task<ArchiveOperationResult> RunSevenZipAsync(
        IEnumerable<string> arguments,
        string usedPassword,
        CancellationToken cancellationToken = default)
    {
        return RunSevenZipAsync(arguments, usedPassword, cancellationToken, null);
    }

    /// <summary>
    /// 起进程、收输出、按退出码与输出分类（实现在 <see cref="SevenZipProcessSession"/>）。
    ///
    /// <para><paramref name="progressContext"/> 非空时，进度行经节流后投递给上层，并在
    /// "很久没有任何输出"时回调 <see cref="EngineProgressContext.Stalled"/>；两条通道都在
    /// 返回之前收口，所以之后不可能再有迟到回调。</para>
    /// </summary>
    public Task<ArchiveOperationResult> RunSevenZipAsync(
        IEnumerable<string> arguments,
        string usedPassword,
        CancellationToken cancellationToken,
        EngineProgressContext? progressContext)
    {
        return SevenZipProcessSession.RunAsync(
            _tools,
            arguments,
            usedPassword,
            cancellationToken,
            progressContext);
    }

    /// <summary>按退出码与两路输出给出结果（实现在 <see cref="SevenZipResultClassifier"/>）。</summary>
    public ArchiveOperationResult AnalyzeResult(int exitCode, string output, string error)
    {
        return SevenZipResultClassifier.AnalyzeResult(exitCode, output, error);
    }

    /// <summary>同上，可带"这次用的密码（脱敏后落进结果）"与耗时。</summary>
    public ArchiveOperationResult AnalyzeResult(
        int exitCode,
        string output,
        string error,
        string usedPassword,
        TimeSpan elapsed)
    {
        return SevenZipResultClassifier.AnalyzeResult(exitCode, output, error, usedPassword, elapsed);
    }

    /// <summary>
    /// **只读出"某个条目解密后的开头 N 字节"**（用户 2026-09-25 第 38 条；密码预检专用）。
    ///
    /// <para>判据不在这里：本方法只负责把开头那些字节拿回来，怎么判由
    /// <c>Extraction/PasswordProbe</c> 决定（实现在 <see cref="SevenZipDecryptedPrefixReader"/>）。</para>
    /// </summary>
    /// <returns>读到的字节（可能少于 <paramref name="maxBytes"/>）；拿不到返回 null。</returns>
    public Task<byte[]?> TryReadDecryptedPrefixAsync(
        string archivePath,
        string entryPath,
        string password,
        int maxBytes,
        CancellationToken cancellationToken = default)
    {
        return SevenZipDecryptedPrefixReader.ReadAsync(
            _tools,
            archivePath,
            entryPath,
            password,
            maxBytes,
            cancellationToken);
    }

    /// <summary>
    /// 解压参数（实现在 <see cref="SevenZipCommandLine"/>）。
    /// 进度开关是 <c>-bsp1</c> 且**不能**同时给 <c>-bd</c>；刻意**不**加 <c>-kb</c>（7-Zip 没有它）。
    /// </summary>
    public List<string> BuildExtractArguments(
        string archivePath,
        string outputPath,
        string password,
        ExtractOptions options)
    {
        return SevenZipCommandLine.BuildExtractArguments(archivePath, outputPath, password, options);
    }

    /// <summary>测试参数（进度开关与解压同一口径，见 <see cref="BuildExtractArguments"/>）。</summary>
    public List<string> BuildTestArguments(string archivePath, string password)
    {
        return SevenZipCommandLine.BuildTestArguments(archivePath, password);
    }

    /// <summary>覆盖策略 → 7z 开关（唯一出口；<see cref="ExtractOptions"/> 不再保留同名映射）。</summary>
    public string GetOverwriteArgument(string overwriteMode)
    {
        return SevenZipCommandLine.GetOverwriteArgument(overwriteMode);
    }

    /// <summary>"这次到底拿什么参数调的 7-Zip"（已脱敏）。实现在 <see cref="SevenZipCommandLine"/>。</summary>
    internal static string BuildCommandSummary(IEnumerable<string>? arguments)
    {
        return SevenZipCommandLine.BuildCommandSummary(arguments);
    }

    /// <summary>从参数表里认出这次跑的是哪个命令。实现在 <see cref="SevenZipCommandLine"/>。</summary>
    internal static EngineOperation? ResolveOperation(IEnumerable<string>? arguments)
    {
        return SevenZipCommandLine.ResolveOperation(arguments);
    }

    /// <summary>
    /// 把"字段上像'非归档'"的结果修正成缺卷/缺首卷（不变量 7）。
    /// 实现在 <see cref="SevenZipResultClassifier"/>。
    /// </summary>
    internal static string ResolveVolumeMissingErrorType(
        string errorType,
        string? archivePath,
        IEnumerable<string>? arguments = null)
    {
        return SevenZipResultClassifier.ResolveVolumeMissingErrorType(errorType, archivePath, arguments);
    }
}

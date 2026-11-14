using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Engines.WinRar;

/// <summary>
/// RARLAB <c>UnRAR.exe</c> 的调用器：**公开 API 门面**，只做转发与工具路径暴露。
///
/// <para>原先 1107 行的实现按职责拆到同目录的协作类（同一命名空间，外部调用点不变）：</para>
/// <list type="bullet">
/// <item><description><see cref="UnRarCommandLine"/> —— 命令参数：拼装、命令/归档识别、脱敏摘要；</description></item>
/// <item><description><see cref="UnRarProcessLauncher"/> —— 进程启动：StartInfo 形状、启动、关标准输入；</description></item>
/// <item><description><see cref="UnRarOutputCapture"/> —— 输出读取：两路缓冲区、输出泵、有上限的收尾等待；</description></item>
/// <item><description><see cref="UnRarIdleWatch"/> —— 超时取消：连续无输出与总运行时长两道上限；</description></item>
/// <item><description><see cref="UnRarProcessTerminator"/> —— 收尸：只杀自己启动的 PID 及其子进程；</description></item>
/// <item><description><see cref="UnRarResultClassifier"/> —— 错误分类：退出码/输出 → 结果，测试与解压的最终修饰；</description></item>
/// <item><description><see cref="UnRarProcessSession"/> / <see cref="UnRarArchiveOperations"/> ——
/// 跑一次运行 / 测试与解压的编排。</description></item>
/// </list>
///
/// <para>
/// 边界（AGENTS.md §3.1 四条禁止项）：参数拼接、输出解析、退出码到错误的映射
/// **全部只能存在于 <c>Engines/WinRar/</c> 内**。核心模块一律通过 <see cref="IArchiveEngine"/> 调用。
/// 7-Zip 那一侧是 <c>SevenZipProcessRunner</c>，两者的协作类结构刻意对称 ——
/// 第二个引擎的价值之一就是证明"换引擎不用改调度器"。
/// </para>
///
/// <para>
/// 参数侧的实测差异（<c>-scf</c> 必加、<c>-p</c> 绝不给裸的、不用 <c>-y</c>、
/// 输出目录走 <c>-op</c>、不加 <c>-idq</c>/<c>-idp</c>）写在
/// <see cref="UnRarCommandLine"/> 的类注释里，改参数前先读那一段。
/// </para>
/// </summary>
public sealed class UnRarProcessRunner
{
    private readonly ToolLocator _tools;

    public UnRarProcessRunner()
        : this(ToolLocator.Default)
    {
    }

    public UnRarProcessRunner(ToolLocator tools)
    {
        _tools = tools ?? ToolLocator.Default;
    }

    /// <summary>外部工具路径统一由 <see cref="ToolLocator"/> 解析（本类不拼路径）。</summary>
    public ToolLocator Tools => _tools;

    public bool CheckUnRarExists() => _tools.UnRarExists;

    public string GetUnRarPath() => _tools.UnRarExePath;

    /// <summary>
    /// 列表：<c>unrar lt -scf -c- -p&lt;密码&gt; &lt;包&gt;</c>（实现在 <see cref="UnRarCommandLine"/>）。
    /// </summary>
    public List<string> BuildListArguments(string archivePath, string? password)
    {
        return UnRarCommandLine.BuildListArguments(archivePath, password);
    }

    /// <summary>测试：<c>unrar t -scf -c- -p&lt;密码&gt; &lt;包&gt;</c>（实现在 <see cref="UnRarCommandLine"/>）。</summary>
    public List<string> BuildTestArguments(string archivePath, string? password)
    {
        return UnRarCommandLine.BuildTestArguments(archivePath, password);
    }

    /// <summary>
    /// 解压参数（实现在 <see cref="UnRarCommandLine"/>）。
    /// <c>x</c> = 保留包内路径（**永不**用 <c>e</c>：摊平必然撞名）。
    /// </summary>
    public List<string> BuildExtractArguments(
        string archivePath,
        string outputPath,
        string? password,
        ExtractOptions options,
        bool keepBrokenFiles)
    {
        return UnRarCommandLine.BuildExtractArguments(
            archivePath,
            outputPath,
            password,
            options,
            keepBrokenFiles);
    }

    /// <summary>覆盖策略 → UnRAR 开关（唯一出口；实现在 <see cref="UnRarCommandLine"/>）。</summary>
    public string GetOverwriteArgument(string? overwriteMode)
    {
        return UnRarCommandLine.GetOverwriteArgument(overwriteMode);
    }

    /// <summary>密码参数（实现在 <see cref="UnRarCommandLine"/>；⛔ 绝不返回裸的 <c>-p</c>）。</summary>
    internal static string BuildPasswordArgument(string? password)
    {
        return UnRarCommandLine.BuildPasswordArgument(password);
    }

    public Task<ArchiveOperationResult> TestArchiveAsync(
        string archivePath,
        string? password,
        CancellationToken cancellationToken = default)
    {
        return TestArchiveAsync(archivePath, password, cancellationToken, null);
    }

    /// <summary>带进度 / 卡住提示的测试（<paramref name="progressContext"/> 可空）。</summary>
    public Task<ArchiveOperationResult> TestArchiveAsync(
        string archivePath,
        string? password,
        CancellationToken cancellationToken,
        EngineProgressContext? progressContext)
    {
        return UnRarArchiveOperations.TestAsync(
            _tools,
            archivePath,
            password,
            cancellationToken,
            progressContext);
    }

    public Task<ArchiveOperationResult> ExtractArchiveAsync(
        string archivePath,
        string outputPath,
        string? password,
        ExtractOptions options,
        bool keepBrokenFiles,
        CancellationToken cancellationToken = default)
    {
        return ExtractArchiveAsync(
            archivePath,
            outputPath,
            password,
            options,
            keepBrokenFiles,
            cancellationToken,
            null);
    }

    /// <summary>带进度 / 卡住提示的解压（<paramref name="progressContext"/> 可空）。</summary>
    public Task<ArchiveOperationResult> ExtractArchiveAsync(
        string archivePath,
        string outputPath,
        string? password,
        ExtractOptions options,
        bool keepBrokenFiles,
        CancellationToken cancellationToken,
        EngineProgressContext? progressContext)
    {
        return UnRarArchiveOperations.ExtractAsync(
            _tools,
            archivePath,
            outputPath,
            password,
            options,
            keepBrokenFiles,
            cancellationToken,
            progressContext);
    }

    public Task<ArchiveOperationResult> RunAsync(
        IEnumerable<string> arguments,
        string? usedPassword,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(arguments, usedPassword, cancellationToken, null);
    }

    /// <summary>
    /// 起进程、收输出、按退出码与输出分类（实现在 <see cref="UnRarProcessSession"/>）。
    ///
    /// <para><paramref name="progressContext"/> 非空时，UnRAR 的百分比进度经节流后投递给上层，
    /// 并在"很久没有任何输出"时回调 <see cref="EngineProgressContext.Stalled"/>；两条通道都在
    /// 返回之前收口，所以之后不可能再有迟到回调。</para>
    /// </summary>
    public Task<ArchiveOperationResult> RunAsync(
        IEnumerable<string> arguments,
        string? usedPassword,
        CancellationToken cancellationToken,
        EngineProgressContext? progressContext)
    {
        return UnRarProcessSession.RunAsync(
            _tools,
            arguments,
            usedPassword,
            cancellationToken,
            progressContext);
    }

    /// <summary>按退出码与两路输出给出结果（实现在 <see cref="UnRarResultClassifier"/>）。</summary>
    public ArchiveOperationResult AnalyzeResult(
        int exitCode,
        string? output,
        string? error,
        string? usedPassword,
        TimeSpan elapsed,
        string? archivePath,
        EngineOperation? operation = null,
        IEnumerable<string>? arguments = null)
    {
        return UnRarResultClassifier.AnalyzeResult(
            exitCode,
            output,
            error,
            usedPassword,
            elapsed,
            archivePath,
            operation,
            arguments);
    }

    /// <summary>"这次到底拿什么参数调的 UnRAR"（已脱敏）。实现在 <see cref="UnRarCommandLine"/>。</summary>
    internal static string BuildCommandSummary(IEnumerable<string>? arguments)
    {
        return UnRarCommandLine.BuildCommandSummary(arguments);
    }

    /// <summary>从参数表里认出这次跑的是哪个命令。实现在 <see cref="UnRarCommandLine"/>。</summary>
    internal static EngineOperation? ResolveOperation(IEnumerable<string>? arguments)
    {
        return UnRarCommandLine.ResolveOperation(arguments);
    }

    /// <summary>从参数表里找出归档路径。实现在 <see cref="UnRarCommandLine"/>。</summary>
    internal static string? FindArchiveArgument(IEnumerable<string>? arguments)
    {
        return UnRarCommandLine.FindArchiveArgument(arguments);
    }
}

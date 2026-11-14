using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Packing;

/// <summary>
/// 打包的**外部步骤执行**（<see cref="PackingService"/> 的协作类，职责：执行 / 起进程）。
///
/// <para>只做三件事：把参数交给 runner、按**这一步**绑定进度出口、把结论原样交回流程。
/// ⛔ 它不做判据（成功 / 取消 / 失败怎么处理由流程决定），也不拼命令行
/// （那是 <see cref="PackingCommandBuilder"/> 的唯一职责）。</para>
/// </summary>
internal sealed class PackingEngineSteps
{
    private readonly IPackProcessRunner _runner;

    internal PackingEngineSteps(IPackProcessRunner runner)
    {
        _runner = runner;
    }

    /// <summary>第一步：跑 7z 加密分卷（进度绑到 <see cref="PackingStep.Volumes"/>）。</summary>
    internal Task<PackStepResult> RunVolumeStepAsync(
        IReadOnlyList<string> arguments,
        string password,
        string stepText,
        PackingRunLog runLog,
        CancellationToken cancellationToken)
    {
        return _runner.RunAsync(
            PackToolKind.SevenZip,
            arguments,
            password,
            runLog.WrapProgress(PackingStep.Volumes, stepText),
            cancellationToken);
    }

    /// <summary>
    /// 第二步：跑外层容器（rar 或 7z）。<paramref name="workingDirectory"/> 是**落点目录** ——
    /// 参数里只给分卷的文件名，进程必须在落点目录里执行，否则那些名字一个都对不上。
    /// </summary>
    internal Task<PackStepResult> RunOuterStepAsync(
        PackToolKind tool,
        IReadOnlyList<string> arguments,
        string password,
        string workingDirectory,
        string stepText,
        PackingRunLog runLog,
        CancellationToken cancellationToken)
    {
        return _runner.RunAsync(
            tool,
            arguments,
            password,
            runLog.WrapProgress(PackingStep.OuterContainer, stepText),
            cancellationToken,
            workingDirectory);
    }

    /// <summary>核对产物：列一遍条目（这一步没有进度槽位，进度传 null）。</summary>
    internal Task<PackStepResult> RunListingStepAsync(
        PackToolKind tool,
        IReadOnlyList<string> arguments,
        string password,
        CancellationToken cancellationToken)
    {
        return _runner.RunAsync(
            tool,
            arguments,
            password,
            null,
            cancellationToken);
    }

    /// <summary>把外部工具输出的最后几行附在失败原因后面（用户与开发者都靠它定位）。</summary>
    internal static string DescribeOutputTail(PackStepResult step)
    {
        string combined = step.CombinedOutput;

        if (string.IsNullOrWhiteSpace(combined))
        {
            return string.Empty;
        }

        string[] lines = combined
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToArray();

        if (lines.Length == 0)
        {
            return string.Empty;
        }

        string tail = string.Join(" / ", lines.TakeLast(3));

        return "工具输出（末几行）：" + tail + "。";
    }
}

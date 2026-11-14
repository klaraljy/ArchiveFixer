using ArchiveFixer.Models;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Services;

/// <summary>
/// 压缩包真实格式识别服务 —— 公开 API 的**薄转发**（用户 2026-11 按职责拆分这个 1,537 行的类）。
///
/// <para>原来写在类里的职责一条没变，只是按职责分到了五个协作类（判据、文案、次序全部原样迁移）：</para>
/// <list type="bullet">
/// <item><description><see cref="ArchiveFormatProbe"/>：**格式探测** —— 只凭文件头字节认容器
/// （魔数表 / TAR / ISO 结构判据 / 媒体前缀 / ZIP 家族判定）；</description></item>
/// <item><description><see cref="ArchiveContentIdentifier"/>：**内容识别** —— 文件身份与缓存、
/// 文件头 → 尾部 → 顺序扫的流水线、内嵌归档的抠取区间与直读探查结论；</description></item>
/// <item><description><see cref="ArchiveEncryptionVerdict"/>：**加密判读唯一出口** ——
/// RAR / ZIP / 7z 的只读头尾判据都只从这一个方法出（识别与准入两条路都走它）；</description></item>
/// <item><description><see cref="ArchiveAdmissionEvaluator"/>：**准入** —— 识别结论落成任务状态 /
/// 后缀状态 / 建议与原因；</description></item>
/// <item><description><see cref="DetectResultFactory"/>：**结果模型** —— <see cref="DetectResult"/> 的
/// 每一种形状（未知 / 文件头命中 / 内嵌 ZIP / 跨盘片 / 尾部 RAR·7z）只在那一处组装。</description></item>
/// </list>
///
/// <para>注意读入量（与拆分前完全一致）：只读取文件头（34 KB）和"认不出来时"的文件尾部（256 KB）；
/// 只有尾部那条路也没命中时才会顺序扫一遍（上限 512 MiB，命中即停），**正常包一个字节都不多读**。</para>
/// </summary>
public class ArchiveDetectService
{
    private readonly ArchiveContentIdentifier _identifier = new();

    /// <summary>诊断计数：实际进入 <see cref="DetectAsync"/> 的次数（缓存命中前就计数）。</summary>
    public static long ProbeCalls => ArchiveContentIdentifier.ProbeCalls;

    public static void ResetProbeCallsForTests() =>
        ArchiveContentIdentifier.ResetProbeCallsForTests();

    /// <summary>
    /// 要不要用识别结论缓存（默认开）。转发到内容识别器上的同一开关。
    ///
    /// <para>留成**实例**开关而不是静态开关：用例要拿"开缓存的一份"与"关缓存的一份"
    /// 在同一批文件上各跑一遍、逐条对照结论（用户 2026-09-29 任务 B 第 3 条：
    /// 改前改后的识别结果必须逐条相同）。静态开关会串到并行跑的别的用例上。</para>
    /// </summary>
    internal bool UseDetectCache
    {
        get => _identifier.UseDetectCache;
        set => _identifier.UseDetectCache = value;
    }

    /// <summary>
    /// 识别文件真实格式（判据与读入量见 <see cref="ArchiveContentIdentifier"/>）。
    /// </summary>
    public Task<DetectResult> DetectAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        return _identifier.IdentifyAsync(filePath, cancellationToken);
    }

    /// <summary>
    /// 识别并应用到任务（判据见 <see cref="ArchiveAdmissionEvaluator.ApplyAsync"/>）。
    /// </summary>
    public Task<DetectResult> ApplyDetectResultAsync(
        ArchiveTask task,
        CancellationToken cancellationToken = default)
    {
        return ArchiveAdmissionEvaluator.ApplyAsync(task, _identifier, cancellationToken);
    }

    /// <summary>
    /// 获取后缀状态（判据与规则见 <see cref="ArchiveAdmissionEvaluator.GetExtensionStatus"/>）。
    /// </summary>
    public string GetExtensionStatus(
        string fileName,
        string currentExtension,
        DetectResult result)
    {
        return ArchiveAdmissionEvaluator.GetExtensionStatus(fileName, currentExtension, result);
    }

    /// <summary>
    /// 判断是否是多重后缀疑似伪装（判据见 <see cref="ArchiveAdmissionEvaluator.IsMultiExtensionSuspicious"/>）。
    /// </summary>
    public bool IsMultiExtensionSuspicious(
        string fileName,
        string suggestedExtension)
    {
        return ArchiveAdmissionEvaluator.IsMultiExtensionSuspicious(fileName, suggestedExtension);
    }

    /// <summary>
    /// 根据格式获取建议后缀（转发到 <see cref="ArchiveFixer.Helpers.ExtensionHelper"/>）。
    /// </summary>
    public string GetSuggestedExtension(string format) =>
        ArchiveAdmissionEvaluator.GetSuggestedExtension(format);
}

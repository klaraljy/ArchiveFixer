using ArchiveFixer.Models;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Engines;

/// <summary>
/// 「这个文件到底是什么格式」—— <see cref="EngineRouter"/> 分派引擎前唯一要问的问题。
///
/// <para><b>为什么要有这条契约（M6 依赖环收口）</b>：门面原来直接把字段类型写成
/// <c>Services.ArchiveDetectService</c> 并在构造里 <c>new</c> 它，于是
/// <c>Engines</c> 反向依赖 <c>Services</c>，与 <c>Services</c> 对 <c>Engines</c> 的既有依赖
/// 拼成一个双环。分派只需要"给我一个格式"，不需要知道对方怎么认（内容识别 / 缓存 /
/// 加密判读都在实现方）；把这一点抽成接口后，方向变成「实现方依赖契约」。</para>
///
/// <para><b>两个实现，分工是刻意的</b>：</para>
/// <list type="bullet">
/// <item><description><see cref="HeaderArchiveDetector"/>（Engines 内，默认）—— 只读文件头魔数，
/// 够分派用，也不把识别服务拖回 <c>Engines</c>；</description></item>
/// <item><description><c>Services.ArchiveDetectService</c>（生产入口注入）—— 完整识别：
/// 文件头 → 尾部内嵌归档 → 顺序扫 + 缓存 + 加密判读，结论与注入前逐条相同。</description></item>
/// </list>
/// </summary>
internal interface IArchiveDetector
{
    /// <summary>
    /// 识别归档的真实格式。判不出来（文件不存在 / 读失败 / 不认识）时返回
    /// <see cref="DetectResult"/> 的 Unknown 形状，<b>不抛异常</b> ——
    /// 识别失败绝不是"这个包打不开"的理由（见 <see cref="EngineRouter"/>）。
    /// </summary>
    Task<DetectResult> DetectAsync(string archivePath, CancellationToken cancellationToken);
}

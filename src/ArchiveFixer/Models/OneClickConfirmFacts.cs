namespace ArchiveFixer.Models;

/// <summary>
/// 一键处理确认框正文里的那几行事实（用户 2026-09-24 第 17 条）。
///
/// <para><b>为什么要单独一个类型</b>：确认框只负责**显示**，它既不算落点、也不扫无用物 ——
/// 这两个结论分别来自落点的唯一实现（<c>PathService.ResolveOutputPlacement</c>）与
/// 无用物的唯一实现（<c>SourceJunkScanner</c>）。把结论装在这里传进去，
/// 窗口就没有任何"自己再算一遍"的机会（§7：界面文案不许出现第二份判据）。</para>
///
/// <para>四行都是**可空的纯文本**：空 = 那一行不显示（比如没有无用物、也没有缺密码的包时，
/// 提醒那一段整块收起，而不是显示一句"没有"）。</para>
/// </summary>
public sealed class OneClickConfirmFacts
{
    /// <summary>「内容物会生成在…」——由落点唯一实现按本次快照算出来（含多包时的后缀说明）。</summary>
    public string DestinationEcho { get; init; } = string.Empty;

    /// <summary>「其余物…」——自动彻底删除 / 不自动删除（危险模式是否真的生效）。</summary>
    public string RestEcho { get; init; } = string.Empty;

    /// <summary>「源包…」——移入其余物 / 留在原地 / 校验通过后删除。</summary>
    public string SourceEcho { get; init; } = string.Empty;

    /// <summary>
    /// 「特定解压：&lt;规则名&gt;」——开了特定解压时才非空（用户 2026-09-24："我以为它按默认跑的"
    /// 是他最恨的一件事，所以这一行必须出现在**动手前**的那一个框里）。
    /// </summary>
    public string SpecificExtractionEcho { get; init; } = string.Empty;

    /// <summary>需要时的提醒两行（疑似无用物 / 没有可用密码的包）；空 = 不显示。</summary>
    public string NoticeEcho { get; init; } = string.Empty;

    /// <summary>
    /// 「源包：」那一行**不许**被确认框按折叠区里的单选按钮重算（用户 2026-09-27 真机逮到）。
    ///
    /// <para>为什么要这个开关：确认框打开时会用折叠区那几个单选框的值**重算**「源包：」那一行
    /// （那是"你在本框里改了这一档"的正常反馈）。可「空间不足」模式是**运行期覆盖**，
    /// 折叠区里的单选框仍然显示设置里的值（源包=留在原地）—— 于是在同一个框里就出现了
    /// "源包：留在原地" 与红字"会自动覆盖为「源包 → 放入其余物」"两句互相矛盾的话，
    /// 而那正是用户最不能接受的一种形态。</para>
    ///
    /// <para>为 true 时：正文那一行用调用方算好的值（= 覆盖后的真实行为），
    /// 折叠区里的单选框照旧只反映设置。</para>
    /// </summary>
    public bool SourceEchoLocked { get; init; }

    /// <summary>
    /// 「空间不足」模式那条**红字**（用户 2026-09-27 要求：这个模式会在动手前覆盖源包/其余物两档，
    /// 而且会永久删源包，必须在点「开始处理」之前看见）。空 = 整行不显示（模式没开）。
    ///
    /// <para>它与 <see cref="NoticeEcho"/> 分开：那一段是灰字提醒（"顺便说一声"），
    /// 这一段是红字（"这次会删你的源包"）—— 两种分量不能共用一个控件。</para>
    /// </summary>
    public string SpaceTightEcho { get; init; } = string.Empty;
}

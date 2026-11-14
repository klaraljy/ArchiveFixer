namespace ArchiveFixer.Extraction;

/// <summary>
/// 一条**计划中**的移动（<c>From</c> → <c>To</c>）。纯数据，本类只规划、绝不执行。
///
/// <para>
/// 用 <c>readonly record struct</c> 而不是匿名元组：测试可以直接比值（值语义），
/// 调用方也能解构成 <c>(From, To)</c>，还能直接喂给"逐个搬"的执行器。
/// </para>
/// </summary>
public readonly record struct PlannedMove(string From, string To);


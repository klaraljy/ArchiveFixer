using System;

namespace ArchiveFixer.Models;

/// <summary>
/// 同名冲突处理档（设置项 <see cref="AppSettings.ConflictAction"/>）的**唯一词表**。
///
/// <para>
/// 为什么要有这个词表：这四个字符串同时出现在三个地方 —— 设置界面的 <c>ComboBox</c> Tag、
/// appsettings.json 里的字符串、以及冲突处理的分支判断。以前它们是各写各的字面量，
/// 于是出现了本项目最不能接受的那类缺陷：界面给了「询问」这一档，
/// 而 <c>PathService</c> 把它和 <c>AutoRename</c> 并到同一个分支 —— **用户选了询问，程序静默自动重命名**。
/// 现在四处都引用这里的常量，字面量只允许在这里出现一次。
/// </para>
///
/// <para>
/// 四档语义（<c>docs/WinRAR功能参考.md</c> §B：照 WinRAR 的六档精神裁剪到我们的批量场景）：
/// <list type="bullet">
/// <item><description><see cref="Skip"/>：同名就不动，已存在的文件一个字节都不改（产物留在暂存目录）。</description></item>
/// <item><description><see cref="Overwrite"/>：顶掉已存在的同名项；走"先挪到临时名 → 落位 → 再删"两阶段（不变量 3）。</description></item>
/// <item><description><see cref="AutoRename"/>：新产物落成 <c>名字(1)</c>，**绝不覆盖**。默认档。</description></item>
/// <item><description><see cref="Ask"/>：第一次遇到同名冲突时**暂停该任务**弹一次聚合询问
/// （覆盖 / 跳过 / 自动重命名，可升级成"整批都照此办理"）；无界面宿主时降级为自动重命名并写日志。</description></item>
/// </list>
/// </para>
/// </summary>
public static class ConflictActions
{
    /// <summary>跳过（不动已存在的文件）。</summary>
    public const string Skip = "Skip";

    /// <summary>覆盖（两阶段落位，先挪开旧的再落位）。</summary>
    public const string Overwrite = "Overwrite";

    /// <summary>自动重命名（<c>名字(1)</c>）。**默认档**：不丢产物、不动旧文件。</summary>
    public const string AutoRename = "AutoRename";

    /// <summary>询问（第一次冲突时暂停该任务，弹一次聚合询问）。</summary>
    public const string Ask = "Ask";

    /// <summary>这个档是不是"询问"（唯一判定处，禁止在别处再写 <c>== "Ask"</c>）。</summary>
    public static bool IsAsk(string? value) =>
        string.Equals(Normalize(value), Ask, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 归一化：空 / 非法一律回落 <see cref="AutoRename"/>。
    ///
    /// 容错口径与 <c>ParseSourceHandling</c> / <c>ParsePlacementMode</c> 一致：
    /// 这个字符串可能来自旧配置（缺字段）、用户手改的 json，或将来改名的枚举。
    /// 读不懂时**退回最不意外、也最不可能丢数据的那一档**，而不是到解压那一刻才报错或猜一个别的行为。
    /// </summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return AutoRename;
        }

        string trimmed = value.Trim();

        if (string.Equals(trimmed, Skip, StringComparison.OrdinalIgnoreCase))
        {
            return Skip;
        }

        if (string.Equals(trimmed, Overwrite, StringComparison.OrdinalIgnoreCase))
        {
            return Overwrite;
        }

        if (string.Equals(trimmed, AutoRename, StringComparison.OrdinalIgnoreCase))
        {
            return AutoRename;
        }

        if (string.Equals(trimmed, Ask, StringComparison.OrdinalIgnoreCase))
        {
            return Ask;
        }

        return AutoRename;
    }
}

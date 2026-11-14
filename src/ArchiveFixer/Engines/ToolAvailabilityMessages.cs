using System;
using System.Collections.Generic;
using System.Linq;

namespace ArchiveFixer.Engines;

/// <summary>
/// "外部工具在哪、可不可用、没有时怎么办"的**失败模型与文案唯一来源**
/// （界面、日志、失败原因都引这里）。
///
/// <para>判据不在这里现场取：可用性、来源、版本都由调用方算好传进来，本类只负责"这句话怎么说"。
/// 这样改措辞不会碰到探测逻辑，改探测判据也不会碰到文案 —— 旧实现里这两件事混在一个类里，
/// 一句话改错就会连带改到"能不能用"的判断。</para>
///
/// <para>⛔ 用户可见字符串不许用 Markdown 加粗，强调用「」；⛔ 不在这里写死任何外部工具路径
/// （路径只有 <see cref="ToolLocator"/> 一个来源，AGENTS.md §3.1）。</para>
/// </summary>
internal static class ToolAvailabilityMessages
{
    /// <summary>7-Zip 的一句话：用了自选还是内置、版本多少；没找到时给期望位置。</summary>
    internal static string DescribeSevenZip(
        bool exists,
        bool usingCustom,
        string executablePath,
        string version)
    {
        if (exists)
        {
            return usingCustom
                ? $"使用自定义 7-Zip：{executablePath}（版本 {version}）"
                : $"使用内置 7-Zip：{executablePath}（版本 {version}）";
        }

        return $"未找到 7-Zip，期望位置：{executablePath}";
    }

    /// <summary>UnRAR 的一句话（界面与报告共用；措辞只做事实性说明，见 docs/引擎与外部工具.md §3）。</summary>
    internal static string DescribeUnRar(
        ToolLocation location,
        bool exists,
        string executablePath,
        string version)
    {
        if (!exists)
        {
            return $"未找到 RAR 解压引擎 UnRAR，期望位置：{executablePath}";
        }

        string source = location.IsCustom
            ? "使用自选的 UnRAR"
            : location.IsWinRarInstallation
                ? "使用本机已装 WinRAR 目录中的 UnRAR"
                : "使用内置 UnRAR";

        return $"{source}：{executablePath}（版本 {version}）";
    }

    /// <summary>
    /// Rar.exe 的一句话（界面与报告共用）。
    /// 出问题时这句话要能回答"到底用的哪一个、从哪来的"（不变量 14）。
    /// </summary>
    internal static string DescribeRar(
        ToolLocation location,
        bool exists,
        string executablePath,
        string version,
        string expectedPath)
    {
        if (!exists)
        {
            return DescribeNoRar(expectedPath);
        }

        string source = location.IsCustom
            ? "使用自选的 Rar.exe（你在设置里填的那一份）"
            : location.IsWinRarGui
                ? "使用本机已装 WinRAR 目录中的 WinRAR.exe（命令行版 Rar.exe 没找到）"
                : "使用本机已装 WinRAR 目录中的 Rar.exe";

        return $"{source}：{executablePath}（版本 {version}）";
    }

    /// <summary>
    /// 没有 Rar.exe 时给用户看的那段话 —— **唯一来源**（⑤页状态格、日志、失败原因都引它）。
    ///
    /// <para>为什么不内置一个：<c>Rar.exe</c> 是共享软件，许可不允许再分发（AGENTS.md §3.1）。
    /// 也**不会**拿 7-Zip 假装做出一个 <c>.rar</c>：7-Zip 建不了 RAR（算法是专有的），
    /// 改名成 <c>.rar</c> 是伪造，用户拿去用时会直接坏掉。</para>
    ///
    /// <para>2026-09-26 第 46 条改口径（用户原话："如果用户没有装 WinRAR，那就弄 7z 吧"）：
    /// <c>PackingService</c> 会**自动**改用 7z 外层，⑤页上"外层容器"那个控件已经不存在了；
    /// 现在只剩"想拿到 <c>.rar</c> 该怎么办"，而那条路只有一处入口 = ②页 →「引擎」里的自选路径。</para>
    /// </summary>
    internal static string DescribeNoRar(string expectedPath)
    {
        return "本机没有 Rar.exe（WinRAR 是共享软件，程序只检测与调用它、绝不随包分发）—— "
             + "打包会自动改用 7z 外层（内置 7-Zip，无需额外安装），产物是 .7z 而不是 .rar。"
             + $"期望位置：{expectedPath}。"
             + "想要 .rar 的话有两条：① 装好 WinRAR 后重试；"
             + "② 在②「解压方式」页 →「引擎」里填你自己那份 Rar.exe 的路径。";
    }

    /// <summary>
    /// WinRAR.exe 的同一句话（兜底那边写日志用："用的是哪一个、从哪来的"）。
    /// 没找到时写清"期望位置"（用户要知道该往哪儿装）。
    /// </summary>
    internal static string DescribeWinRar(
        ToolLocation location,
        bool exists,
        string? executablePath,
        string version,
        string expectedPath)
    {
        if (!exists)
        {
            return "未找到 WinRAR（本机没装 / 没在预期位置）：期望位置 " + expectedPath;
        }

        string source = location.IsCustom
            ? "使用自选的 WinRAR.exe（设置里那一格指向的就是它）"
            : "使用本机已装 WinRAR 目录中的 WinRAR.exe";

        return $"{source}：{executablePath}（版本 {version}）";
    }

    /// <summary>
    /// 两个引擎"在哪、能不能用"的一句话（写日志、排障用）：只把两种事实各自的措辞并成一行，
    /// ⛔ 不在这里另造"可用 / 不可用"的说法。
    /// </summary>
    internal static string DescribeAvailability(string sevenZipText, string unRarText, string priorityText)
    {
        return $"{sevenZipText}；{unRarText}；当前引擎优先级：{priorityText}";
    }

    /// <summary>
    /// "没有可用解压引擎"时给用户看的那段提示 —— **唯一来源**。
    ///
    /// <para>为什么必须现算：可用性判据是"任一引擎可用"，而默认优先级是
    /// <c>WinRAR(UnRAR) → 7-Zip</c>。旧提示写死"未找到 <c>tools\7zip\7z.exe</c>"，后果是
    /// 真正缺 UnRAR（甚至只是没排上队）的用户被引去修一个本来没问题的 7z 目录；
    /// 自己配了外部 7z 的用户更是被指向一个他根本没在用的路径。</para>
    ///
    /// <para>所以这里把两条期望路径都列出来、写明"任装其一即可"，并给出当前优先级顺序 ——
    /// 用户看一眼就知道该往哪儿放文件。路径全部由调用方现算传入，全文不写死任何文件名。</para>
    /// </summary>
    internal static string DescribeNoEngine(
        string sevenZipExecutablePath,
        string unRarExecutablePath,
        string priorityText)
    {
        return "未找到可用的解压引擎：7-Zip 与 UnRAR 都没有找到，两者任装其一即可。"
             + $"7-Zip 期望位置：{sevenZipExecutablePath}；"
             + $"UnRAR 期望位置：{unRarExecutablePath}。"
             + $"当前引擎优先级：{priorityText}（不可用的引擎会自动跳过，顺序可在设置里调整）。";
    }

    /// <summary>
    /// 当前优先级的人读写法（引擎 id → 外部工具名）。
    ///
    /// <para>映射为什么放在这里而不是 <c>EngineIds</c>：这句话回答的是"外部工具按什么顺序找"，
    /// 而 <see cref="ToolLocator"/> 正是外部工具的唯一定位者。认不出的 id 原样列出，
    /// 绝不悄悄丢掉 —— 将来加第三个引擎时，提示里要能看见它。</para>
    /// </summary>
    internal static string DescribePriority()
    {
        IReadOnlyList<string> priority = EngineRuntimeSettings.EnginePriority;

        if (priority == null || priority.Count == 0)
        {
            return DescribeEngineId(ArchiveFixer.Models.EngineIds.SevenZip);
        }

        return string.Join(" → ", priority.Select(DescribeEngineId));
    }

    private static string DescribeEngineId(string id)
    {
        if (string.Equals(id, ArchiveFixer.Models.EngineIds.WinRar, StringComparison.OrdinalIgnoreCase))
        {
            return "UnRAR（RAR 系）";
        }

        if (string.Equals(id, ArchiveFixer.Models.EngineIds.SevenZip, StringComparison.OrdinalIgnoreCase))
        {
            return "7-Zip";
        }

        return id;
    }
}

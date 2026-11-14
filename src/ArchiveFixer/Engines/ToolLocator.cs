using System;
using System.Collections.Generic;
using System.IO;

namespace ArchiveFixer.Engines;

/// <summary>
/// 外部归档工具路径的**唯一**来源（7-Zip、RARLAB UnRAR、打包要用的 Rar.exe、
/// "换引擎再试一遍"兜底用的 WinRAR.exe）。
///
/// <para>本类是**公开 API 门面**，只做"取值 + 转发"；职责在同目录的协作类里：</para>
/// <list type="bullet">
/// <item><description><see cref="ToolDiscovery"/> —— 工具发现：候选（含注册表 / ProgramFiles）、四条解析链的顺序、"本来应该在的位置"；</description></item>
/// <item><description><see cref="ToolPathValidator"/> —— 路径验证：设置值归一化、自选文件是否存在、是不是 GUI 版 WinRAR.exe；</description></item>
/// <item><description><see cref="ToolVersionReader"/> —— 版本：读文件版本资源，取不到一律 <c>"unknown"</c>；</description></item>
/// <item><description><see cref="ToolPathCache"/> —— 缓存：每条链只解析一次，<see cref="Invalidate"/> 之后重来；</description></item>
/// <item><description><see cref="ToolAvailabilityMessages"/> —— 失败模型与文案：可用性、来源、"未找到"时那句话的唯一来源。</description></item>
/// </list>
///
/// <para>⚠ 四条解析顺序原样保留（⛔ 不改）：7-Zip = 自选 → 内置；UnRAR = 自选 → 已装 WinRAR 目录 →
/// 内置；Rar.exe = 自选 → 已装目录的 Rar.exe → 同目录 WinRAR.exe；WinRAR.exe（只给兜底用）=
/// 自选里确实叫 WinRAR.exe 的那份 → 注册表 / ProgramFiles → 没找到返回 <c>null</c>。</para>
///
/// <para>⛔ 全程只读探测：<c>Rar.exe</c> / <c>WinRAR.exe</c> 是共享软件，只检测、只调用，
/// 绝不复制进程序目录、绝不随包分发（AGENTS.md §3.1）。</para>
/// </summary>
public sealed class ToolLocator
{
    /// <summary>默认实例。进程内共用一份，避免各处缓存不一致。</summary>
    public static ToolLocator Default { get; } = new ToolLocator();

    private readonly ToolPathCache _cache = new ToolPathCache();

    private string _customSevenZipExePath = string.Empty;
    private string _customUnRarExePath = string.Empty;
    private string _customRarExePath = string.Empty;

    /// <summary>
    /// 要不要探测"用户已装的 WinRAR 目录"这一档（默认 true）。
    ///
    /// <para>存在这个开关不是为了给用户看，而是为了让**"没装 WinRAR 时会怎样"这件事可被验证**：
    /// 只把自定义路径指向一个不存在的文件不够 —— 下一档（已装目录 / 内置）会把它救回来。
    /// 关掉本项与 <see cref="UseBundledUnRar"/> 才是真正的"这台机器上没有 UnRAR"。
    /// 打包用的 <c>Rar.exe</c> 同样读这一档：关掉它就等于"这台机器上没有 Rar.exe"。</para>
    /// </summary>
    public bool UseWinRarInstallation { get; set; } = true;

    /// <summary>要不要用内置的 <c>tools\unrar\UnRAR.exe</c> 这一档（默认 true）。含义同上。</summary>
    public bool UseBundledUnRar { get; set; } = true;

    /// <summary>用户自定义的 7z.exe 路径（来自设置）。赋值后自动失效缓存，下次读取重新解析。</summary>
    public string CustomSevenZipExePath
    {
        get => _customSevenZipExePath;
        set
        {
            string normalized = ToolPathValidator.Normalize(value);

            if (string.Equals(_customSevenZipExePath, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _customSevenZipExePath = normalized;
            Invalidate();
        }
    }

    /// <summary>用户自定义的 UnRAR.exe 路径（来自设置）；空表示按"已装 WinRAR 目录 → 内置"解析。</summary>
    public string CustomUnRarExePath
    {
        get => _customUnRarExePath;
        set
        {
            string normalized = ToolPathValidator.Normalize(value);

            if (string.Equals(_customUnRarExePath, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _customUnRarExePath = normalized;
            Invalidate();
        }
    }

    /// <summary>
    /// 用户指定的 <c>Rar.exe</c> 路径（来自②「解压方式」页 → 引擎那一格）。
    ///
    /// <para>空 = 按"已装 WinRAR 目录 → <c>WinRAR.exe</c>"自动解析。填了但文件不存在时
    /// **不吃掉后面的档位**（与 UnRAR 同一口径）：回落到下一档并如实说明用的是哪一个。</para>
    /// </summary>
    public string CustomRarExePath
    {
        get => _customRarExePath;
        set
        {
            string normalized = ToolPathValidator.Normalize(value);

            if (string.Equals(_customRarExePath, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _customRarExePath = normalized;
            Invalidate();
        }
    }

    /// <summary>随程序分发的内置 7-Zip 目录。</summary>
    public string BundledDirectory => Path.Combine(AppContext.BaseDirectory, "tools", "7zip");

    /// <summary>随程序分发的内置 UnRAR 目录。</summary>
    public string BundledUnRarDirectory => Path.Combine(AppContext.BaseDirectory, "tools", "unrar");

    /// <summary>实际使用的 7z.exe 路径（即使文件不存在也会返回"应该在哪"）。</summary>
    public string SevenZipExePath => EnsureSevenZipLocation().ExecutablePath;

    /// <summary>实际使用的 7z.dll 路径。</summary>
    public string SevenZipDllPath => EnsureSevenZipLocation().DllPath;

    /// <summary>是否用的是用户自定义路径（而不是内置的）。</summary>
    public bool IsUsingCustomPath => EnsureSevenZipLocation().IsCustom;

    /// <summary>7z.exe 这个文件现在在不在（现查，不缓存判定）。</summary>
    public bool SevenZipExists => File.Exists(SevenZipExePath);

    /// <summary>7z.dll 这个文件现在在不在（现查，不缓存判定）。</summary>
    public bool SevenZipDllExists => File.Exists(SevenZipDllPath);

    /// <summary>7z.exe 的文件版本（取不到返回 "unknown"；实现在 <see cref="ToolVersionReader"/>）。</summary>
    public string SevenZipVersion => ToolVersionReader.ReadFileVersion(SevenZipExePath);

    /// <summary>内置 UnRAR 的"本来应该在的位置"（用于"未找到"提示，不参与是否可用的判定）。</summary>
    public string UnRarExpectedPath => ToolDiscovery.ExpectedUnRarPath(BundledUnRarDirectory);

    /// <summary>
    /// 实际使用的 UnRAR.exe 路径；三档都没命中时返回"本来应该在的位置"（即内置目录），
    /// 由 <see cref="UnRarExists"/> 判否 —— 调用方据此报"未找到"而不是拿到一个空字符串。
    ///
    /// <para>注意：这里**不**校验它自报的版本 —— 用户机器上那份可能是 6.11（WinRAR 自带），
    /// 也可能是我们内置的 7.23。是哪一份要看 <see cref="UnRarVersion"/> 与
    /// <see cref="DescribeUnRarResolution"/>，报告里必须写清楚（不变量 14）。</para>
    /// </summary>
    public string UnRarExePath => EnsureUnRarLocation().ExecutablePath ?? UnRarExpectedPath;

    /// <summary>
    /// 是否真的解析到了一个可用的 UnRAR.exe。
    ///
    /// <para>⚠ **不能用 <c>File.Exists(UnRarExePath)</c> 代替**：当"已装 WinRAR 目录"与"内置"两档
    /// 都被显式关掉时（模拟没装 UnRAR 的那一档验收），解析结果是"没找到"，
    /// 而 <see cref="UnRarExePath"/> 仍然会回落到那个"本来应该在的位置"用于提示 ——
    /// 那台机器上这个文件可能恰好存在，用 <c>File.Exists</c> 判就会把"已禁用"误判成"可用"。</para>
    /// </summary>
    public bool UnRarExists
    {
        get
        {
            string? path = EnsureUnRarLocation().ExecutablePath;
            return path != null && File.Exists(path);
        }
    }

    /// <summary>用的是用户自选路径。</summary>
    public bool IsUsingCustomUnRarPath => EnsureUnRarLocation().IsCustom;

    /// <summary>用的是用户已装 WinRAR 目录里那一个（而不是内置的）。</summary>
    public bool IsUsingWinRarInstallation => EnsureUnRarLocation().IsWinRarInstallation;

    /// <summary>
    /// UnRAR.exe 的文件版本（取不到返回 "unknown"）。
    ///
    /// <para>为什么不解析它输出的 "UNRAR 7.23 x64 freeware" banner：那要起一个进程，
    /// 而设置界面每次刷新都要显示版本 —— 为一个版本号反复起进程是不划算的。
    /// 文件版本资源的写法（<c>7.23.0</c>）与实际 banner（<c>UNRAR 7.23</c>）对得上，
    /// 报告里写 <c>UnRAR 7.23.0</c> 足够追到"当时用的是哪个构建"。</para>
    /// </summary>
    public string UnRarVersion => ToolVersionReader.ReadFileVersion(UnRarExePath);

    /// <summary>让缓存的解析结果失效（设置变更、文件被替换后调用）。</summary>
    public void Invalidate()
    {
        _cache.Invalidate();
    }

    // ================================================================
    // Rar.exe（**只给打包用**）
    // ================================================================
    //
    // ⛔ 与 UnRAR.exe 的性质完全不同：UnRAR 是 freeware，许可明确允许随包分发；
    //    Rar.exe / WinRAR.exe 是**共享软件**，绝不允许再分发（AGENTS.md §3.1）。
    //    所以这里只做"用户在不在本机装了它、装在哪"的只读探测，
    //    绝不把找到的文件复制进程序目录，也不随程序分发。

    /// <summary>
    /// "本来应该在的位置"（只用于"未找到"的提示，不参与可用性判定）：
    /// 用户没自选时就是已装 WinRAR 目录里的 <c>Rar.exe</c>。
    /// </summary>
    public string RarExpectedPath => ToolDiscovery.ExpectedRarPath();

    /// <summary>
    /// 实际使用的 <c>Rar.exe</c>（或退一档的 <c>WinRAR.exe</c>）；没有时返回
    /// <see cref="RarExpectedPath"/>，由 <see cref="RarExists"/> 判否。
    /// </summary>
    public string RarExePath => EnsureRarLocation().ExecutablePath ?? RarExpectedPath;

    /// <summary>
    /// 这台机器上有没有可用的打包命令行工具。
    ///
    /// <para>⚠ 与 <see cref="UnRarExists"/> 同一个写法：不能拿 <c>File.Exists(RarExePath)</c> 代替 ——
    /// <see cref="RarExePath"/> 在"没找到"时会回落到"本来应该在的位置"，那个文件在那台机器上
    /// 可能恰好存在（只是被显式关掉了那一档）。</para>
    /// </summary>
    public bool RarExists
    {
        get
        {
            string? path = EnsureRarLocation().ExecutablePath;
            return path != null && File.Exists(path);
        }
    }

    /// <summary>用的是用户自选的路径。</summary>
    public bool IsUsingCustomRarPath => EnsureRarLocation().IsCustom;

    /// <summary>
    /// 退到了 GUI 版 <c>WinRAR.exe</c>（而不是命令行版 <c>Rar.exe</c>）。
    /// 打包侧据此补 <c>-ibck</c>：不加它，那个 GUI 程序会弹出进度窗口并抢焦点。
    /// </summary>
    public bool IsUsingWinRarGuiForRar => EnsureRarLocation().IsWinRarGui;

    /// <summary>Rar.exe 的文件版本（取不到返回 "unknown"）。</summary>
    public string RarVersion => ToolVersionReader.ReadFileVersion(RarExePath);

    /// <summary>
    /// Rar.exe 的同一句话（界面与报告共用；实现在 <see cref="ToolAvailabilityMessages.DescribeRar"/>）。
    /// 出问题时这句话要能回答"到底用的哪一个、从哪来的"（不变量 14）。
    /// </summary>
    public string DescribeRarResolution()
    {
        return ToolAvailabilityMessages.DescribeRar(
            EnsureRarLocation(),
            RarExists,
            RarExePath,
            RarVersion,
            RarExpectedPath);
    }

    /// <summary>
    /// 没有 Rar.exe 时给用户看的那段话 —— **唯一来源**（⑤页状态格、日志、失败原因都引它；
    /// 实现在 <see cref="ToolAvailabilityMessages.DescribeNoRar"/>）。⛔ 不会拿 7-Zip 假装做出一个 .rar。
    /// </summary>
    public string DescribeNoRarAvailable()
    {
        return ToolAvailabilityMessages.DescribeNoRar(RarExpectedPath);
    }

    /// <summary>
    /// "用户已装的 WinRAR 目录"（目录形态；实现在
    /// <see cref="ToolDiscovery.WinRarInstallationDirectories"/>，不另写一套探测逻辑）。
    /// </summary>
    public IReadOnlyList<string> WinRarInstallationDirectories()
    {
        return ToolDiscovery.WinRarInstallationDirectories();
    }

    // ================================================================
    // WinRAR.exe（**只给"换引擎再试一遍"那一档用**）
    // ================================================================
    //
    // ⛔ 与 Rar.exe 性质一样：共享软件，绝不分发、绝不复制、绝不捆绑。这里只做"本机装没装、装在哪"
    //    的只读探测，调用点只有一处（换引擎兜底，见 Extraction/PasswordEngineFallback）。
    // ⛔ **不参与引擎优先级**：这一份是"主引擎吃不下这一包"时的兜底，不是"Zip 平时也交给 WinRAR 解"
    //    （7-Zip 才有可解析的进度输出）。

    /// <summary>
    /// 实际可用的 <c>WinRAR.exe</c> 路径；**找不到时返回 <c>null</c>**（兜底那边据此什么都不做）。
    /// 解析顺序见 <see cref="ToolDiscovery.ResolveWinRarGui"/>。
    /// </summary>
    public string? WinRarExePath => EnsureWinRarGuiLocation().ExecutablePath;

    /// <summary>
    /// 这台机器上有没有可用的 <c>WinRAR.exe</c>。
    ///
    /// <para>⚠ 与 <see cref="UnRarExists"/> 同一个写法：判据是"解析结果非空且文件真的在"，
    /// ⛔ 不拿 <c>File.Exists(WinRarExePath)</c> 代替（<see cref="UseWinRarInstallation"/> 关掉时
    /// 解析结果就该是"没找到"，哪怕那个路径上恰好有个文件）。</para>
    /// </summary>
    public bool WinRarExeExists
    {
        get
        {
            string? path = EnsureWinRarGuiLocation().ExecutablePath;
            return path != null && File.Exists(path);
        }
    }

    /// <summary>用的是用户自选路径（<see cref="CustomRarExePath"/> 指向的就是 WinRAR.exe）。</summary>
    public bool IsUsingCustomWinRarGuiPath => EnsureWinRarGuiLocation().IsCustom;

    /// <summary>WinRAR.exe 的文件版本（取不到返回 "unknown"，⛔ 不编造）。</summary>
    public string WinRarVersion => ToolVersionReader.ReadFileVersion(WinRarExePath);

    /// <summary>
    /// 兜底那边写日志用的一句话（"用的是哪一个、从哪来的"）。
    /// 没找到时写清"期望位置"（用户要知道该往哪儿装）。
    /// </summary>
    public string DescribeWinRarResolution()
    {
        return ToolAvailabilityMessages.DescribeWinRar(
            EnsureWinRarGuiLocation(),
            WinRarExeExists,
            WinRarExePath,
            WinRarVersion,
            ToolDiscovery.ExpectedWinRarGuiPath());
    }

    /// <summary>WinRAR.exe 的候选（只读探测；实现在 <see cref="ToolDiscovery.WinRarGuiInstallationCandidates"/>）。</summary>
    public IReadOnlyList<string> WinRarGuiInstallationCandidates()
    {
        return ToolDiscovery.WinRarGuiInstallationCandidates();
    }

    /// <summary>
    /// 给日志/报告用的一句话，说清"用的是哪一个、从哪来的"。
    /// 出问题时这句话比任何猜测都有用。
    /// </summary>
    public string DescribeResolution()
    {
        return ToolAvailabilityMessages.DescribeSevenZip(
            SevenZipExists,
            IsUsingCustomPath,
            SevenZipExePath,
            SevenZipVersion);
    }

    /// <summary>UnRAR 的同一句话（界面与报告共用；措辞只做事实性说明，见 docs/引擎与外部工具.md §3）。</summary>
    public string DescribeUnRarResolution()
    {
        return ToolAvailabilityMessages.DescribeUnRar(
            EnsureUnRarLocation(),
            UnRarExists,
            UnRarExePath,
            UnRarVersion);
    }

    /// <summary>
    /// 两个引擎"在哪、能不能用"的一句话（写日志、排障用）：只把两种事实各自的措辞并成一行。
    /// </summary>
    public string DescribeAvailability()
    {
        return ToolAvailabilityMessages.DescribeAvailability(
            DescribeResolution(),
            DescribeUnRarResolution(),
            DescribePriority());
    }

    /// <summary>
    /// "没有可用解压引擎"时给用户看的那段提示 —— **唯一来源**（实现在
    /// <see cref="ToolAvailabilityMessages.DescribeNoEngine"/>）：两条期望路径都现算列出、
    /// 写明"任装其一即可"，并给出当前优先级顺序。
    /// </summary>
    public string DescribeNoEngineAvailable()
    {
        return ToolAvailabilityMessages.DescribeNoEngine(
            SevenZipExePath,
            UnRarExePath,
            DescribePriority());
    }

    /// <summary>当前优先级的人读写法（引擎 id → 外部工具名；实现在 <see cref="ToolAvailabilityMessages.DescribePriority"/>）。</summary>
    public static string DescribePriority()
    {
        return ToolAvailabilityMessages.DescribePriority();
    }

    /// <summary>"用户已装的 WinRAR 目录"候选（只读探测；实现在 <see cref="ToolDiscovery.WinRarInstallationCandidates"/>）。</summary>
    public IReadOnlyList<string> WinRarInstallationCandidates()
    {
        return ToolDiscovery.WinRarInstallationCandidates();
    }

    /// <summary>7-Zip 的解析结果（每条链只解析一次，缓存见 <see cref="ToolPathCache"/>）。</summary>
    private SevenZipLocation EnsureSevenZipLocation()
    {
        return _cache.SevenZip ??= ToolDiscovery.ResolveSevenZip(_customSevenZipExePath, BundledDirectory);
    }

    /// <summary>UnRAR 的解析结果（自选 → 已装 WinRAR 目录 → 内置；两条"模拟没装"开关只影响这一档）。</summary>
    private ToolLocation EnsureUnRarLocation()
    {
        return _cache.UnRar ??= ToolDiscovery.ResolveUnRar(
            _customUnRarExePath,
            UseWinRarInstallation,
            UseBundledUnRar,
            BundledUnRarDirectory);
    }

    /// <summary>Rar.exe 的解析结果（自选 → 已装目录的 Rar.exe → 同目录 WinRAR.exe）。</summary>
    private ToolLocation EnsureRarLocation()
    {
        return _cache.Rar ??= ToolDiscovery.ResolveRar(_customRarExePath, UseWinRarInstallation);
    }

    /// <summary>WinRAR.exe 的解析结果（只给"换引擎再试一遍"兜底用）。</summary>
    private ToolLocation EnsureWinRarGuiLocation()
    {
        return _cache.WinRarGui ??= ToolDiscovery.ResolveWinRarGui(_customRarExePath, UseWinRarInstallation);
    }
}

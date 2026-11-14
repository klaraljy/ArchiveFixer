using System;
using System.Collections.Generic;
using System.IO;

namespace ArchiveFixer.Engines;

/// <summary>
/// 外部工具的**只读发现**：候选从哪来、按什么顺序试、"本来应该在的位置"在哪。
///
/// <para>本类只做两件事：拼候选路径、用 <c>File.Exists</c> 探测。⛔ 不复制、不搬动、不改名
/// （<c>Rar.exe</c> / <c>WinRAR.exe</c> 是共享软件，只允许检测与调用，见 AGENTS.md §3.1）；
/// ⛔ 不缓存 —— 缓存只存在于 <see cref="ToolPathCache"/> 一处；
/// ⛔ 不产生用户可见文案 —— 文案在 <see cref="ToolAvailabilityMessages"/>。</para>
///
/// <para>四条解析链的顺序都写在各自的 <c>Resolve…</c> 方法上，改顺序前先读那一段：
/// 自选路径那一档"填了但文件不存在"时**不吃掉后面的档位**，而是回落到下一档并如实说明
/// 用的是哪一个（这是"模拟这台机器上没装引擎"那组验收要验证的回落链路）。</para>
/// </summary>
internal static class ToolDiscovery
{
    /// <summary>
    /// 7-Zip 解析顺序（⛔ 不改）：
    /// ① 用户自选路径（文件真的存在才用，dll 取同一目录）；
    /// ② 内置 <c>&lt;程序目录&gt;\tools\7zip</c>；
    /// ③ 都没有 → **仍返回内置路径**，由调用方报"未找到"（不再猜别的安装位置）。
    /// </summary>
    internal static SevenZipLocation ResolveSevenZip(string customExePath, string bundledDirectory)
    {
        if (ToolPathValidator.IsUsableCustomPath(customExePath))
        {
            return new SevenZipLocation(
                customExePath,
                Path.Combine(Path.GetDirectoryName(customExePath) ?? bundledDirectory, "7z.dll"),
                true);
        }

        return new SevenZipLocation(
            Path.Combine(bundledDirectory, "7z.exe"),
            Path.Combine(bundledDirectory, "7z.dll"),
            false);
    }

    /// <summary>
    /// UnRAR 解析顺序（⛔ 不改）：
    /// ① 用户自选路径；
    /// ② 用户**已装**的 WinRAR 目录（只读探测、只调用、绝不复制）；
    /// ③ 内置 <c>&lt;程序目录&gt;\tools\unrar\UnRAR.exe</c>；
    /// ④ 都没命中 → <see cref="ToolLocation.NotFound"/>，由引擎报"不可用"、选择器自动跳过它。
    /// </summary>
    internal static ToolLocation ResolveUnRar(
        string customExePath,
        bool useWinRarInstallation,
        bool useBundledUnRar,
        string bundledDirectory)
    {
        if (ToolPathValidator.IsUsableCustomPath(customExePath))
        {
            return ToolLocation.Found(customExePath, ToolLocationSource.Custom);
        }

        if (useWinRarInstallation)
        {
            foreach (string candidate in WinRarInstallationCandidates())
            {
                if (File.Exists(candidate))
                {
                    return ToolLocation.Found(candidate, ToolLocationSource.WinRarInstallation);
                }
            }
        }

        if (useBundledUnRar)
        {
            return ToolLocation.Found(
                Path.Combine(bundledDirectory, "UnRAR.exe"),
                ToolLocationSource.Bundled);
        }

        // 三档都不允许（只有"模拟这台机器上没装 UnRAR"的验证会走到这）：显式"没找到"，
        // ⛔ 绝不悄悄回落到一个存在的路径上 —— 那样测的就不是回落链路，而是"其实还能用"。
        return ToolLocation.NotFound();
    }

    /// <summary>
    /// Rar.exe 解析顺序（⛔ 不改；打包功能 docs/打包功能.md §5）：
    /// ① 用户自选路径；
    /// ② 用户**已装** WinRAR 目录里的 <c>Rar.exe</c>；
    /// ③ 同一批目录里的 <c>WinRAR.exe</c>（GUI 版，调用方据此补 <c>-ibck</c>）；
    /// ④ 都没有 → <see cref="ToolLocation.NotFound"/>：打包侧自动改用内置 7-Zip 做外层。
    /// </summary>
    internal static ToolLocation ResolveRar(string customExePath, bool useWinRarInstallation)
    {
        if (ToolPathValidator.IsUsableCustomPath(customExePath))
        {
            return ToolLocation.Found(customExePath, ToolLocationSource.Custom);
        }

        if (useWinRarInstallation)
        {
            foreach (string directory in WinRarInstallationDirectories())
            {
                string rar = Path.Combine(directory, "Rar.exe");

                if (File.Exists(rar))
                {
                    return ToolLocation.Found(rar, ToolLocationSource.WinRarInstallation);
                }
            }

            foreach (string directory in WinRarInstallationDirectories())
            {
                string winRar = Path.Combine(directory, "WinRAR.exe");

                if (File.Exists(winRar))
                {
                    return ToolLocation.Found(winRar, ToolLocationSource.WinRarGui);
                }
            }
        }

        // 两档都没命中：显式"没找到"。⛔ 绝不悄悄回落到一个存在的路径上 ——
        // 那会让打包以为能做外层 rar，然后在真正开跑时才炸。
        return ToolLocation.NotFound();
    }

    /// <summary>
    /// WinRAR.exe 解析顺序（⛔ 不改；只给"换引擎再试一遍"那一档兜底用，⛔ 不进引擎优先级表、
    /// ⛔ 平时不参与任何解压）：
    /// ① 用户自选路径里**确实叫 <c>WinRAR.exe</c>** 的那一份；
    /// ② 注册表 <c>HKLM\SOFTWARE\WinRAR</c> / <c>WOW6432Node</c> 的 <c>exe64</c> / <c>exe32</c>，
    ///    再退 <c>%ProgramFiles%\WinRAR\WinRAR.exe</c>、<c>%ProgramFiles(x86)%\WinRAR\WinRAR.exe</c>；
    /// ③ 都没有 → <see cref="ToolLocation.NotFound"/>：兜底落在"什么都不做"，并如实报"本机没找到 WinRAR"。
    /// </summary>
    internal static ToolLocation ResolveWinRarGui(string customExePath, bool useWinRarInstallation)
    {
        // 只认自选路径确实叫 WinRAR.exe 的情形：指向 Rar.exe 时是"打包用的控制台版"，与这条路无关
        // （实测 Rar.exe 见到 -ibck 直接报命令行错误、退出码 7）。
        if (ToolPathValidator.IsWinRarGuiExecutable(customExePath))
        {
            return ToolLocation.Found(customExePath, ToolLocationSource.Custom);
        }

        if (useWinRarInstallation)
        {
            foreach (string candidate in WinRarGuiInstallationCandidates())
            {
                if (File.Exists(candidate))
                {
                    return ToolLocation.Found(candidate, ToolLocationSource.WinRarInstallation);
                }
            }
        }

        return ToolLocation.NotFound();
    }

    /// <summary>
    /// "用户已装的 WinRAR 目录"候选（只读探测）。
    /// 取不到环境变量时跳过对应项；同一路径不重复探测。
    /// </summary>
    internal static IReadOnlyList<string> WinRarInstallationCandidates()
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? programFiles)
        {
            if (string.IsNullOrWhiteSpace(programFiles))
            {
                return;
            }

            string path;

            try
            {
                path = Path.Combine(programFiles, "WinRAR", "UnRAR.exe");
            }
            catch
            {
                return;
            }

            if (seen.Add(path))
            {
                candidates.Add(path);
            }
        }

        Add(Environment.GetEnvironmentVariable("ProgramFiles"));
        Add(Environment.GetEnvironmentVariable("ProgramW6432"));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));

        // 32 位系统上的 x86 目录（同一台机器上两份都装的情况极少，但探测是只读的、便宜）。
        Add(Environment.GetEnvironmentVariable("ProgramFiles(x86)"));

        return candidates;
    }

    /// <summary>
    /// "用户已装的 WinRAR 目录"（目录形态；从 <see cref="WinRarInstallationCandidates"/> 推出来，
    /// 不另写一套探测逻辑）。
    /// </summary>
    internal static IReadOnlyList<string> WinRarInstallationDirectories()
    {
        var directories = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string candidate in WinRarInstallationCandidates())
        {
            string? directory = null;

            try
            {
                directory = Path.GetDirectoryName(candidate);
            }
            catch
            {
                directory = null;
            }

            if (!string.IsNullOrWhiteSpace(directory) && seen.Add(directory))
            {
                directories.Add(directory);
            }
        }

        return directories;
    }

    /// <summary>
    /// WinRAR.exe 的候选（只读探测，按优先级排好）：
    /// 注册表 <c>HKLM\SOFTWARE\WinRAR</c> → <c>HKLM\SOFTWARE\WOW6432Node\WinRAR</c>（各取
    /// <c>exe64</c> / <c>exe32</c>）→ <c>%ProgramFiles%\WinRAR\WinRAR.exe</c> →
    /// <c>%ProgramFiles(x86)%\WinRAR\WinRAR.exe</c>。
    ///
    /// <para>注册表整段都在 try 里：读注册表可能因策略 / 权限抛异常，而那**不是**"这个包解不开"
    /// 的理由 —— 抛了就跳过这一档，继续按 ProgramFiles 找（大不了返回"没找到"，兜底什么都不做）。</para>
    /// </summary>
    internal static IReadOnlyList<string> WinRarGuiInstallationCandidates()
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            if (seen.Add(path))
            {
                candidates.Add(path);
            }
        }

        Add(ReadWinRarRegistryValue(@"SOFTWARE\WinRAR", "exe64"));
        Add(ReadWinRarRegistryValue(@"SOFTWARE\WinRAR", "exe32"));
        Add(ReadWinRarRegistryValue(@"SOFTWARE\WOW6432Node\WinRAR", "exe64"));
        Add(ReadWinRarRegistryValue(@"SOFTWARE\WOW6432Node\WinRAR", "exe32"));

        foreach (string? programFiles in ProgramFilesRoots())
        {
            try
            {
                Add(Path.Combine(programFiles!, "WinRAR", "WinRAR.exe"));
            }
            catch
            {
                // 路径拼不出来（畸形环境变量）就跳过这一档。
            }
        }

        return candidates;
    }

    /// <summary>内置 UnRAR 的"本来应该在的位置"（只用于"未找到"提示，不参与可用性判定）。</summary>
    internal static string ExpectedUnRarPath(string bundledUnRarDirectory)
    {
        return Path.Combine(bundledUnRarDirectory, "UnRAR.exe");
    }

    /// <summary>
    /// Rar.exe 的"本来应该在的位置"（只用于"未找到"提示，不参与可用性判定）：
    /// 用户没自选时就是已装 WinRAR 目录里的 <c>Rar.exe</c>；一个目录都没探测到时回落 ProgramFiles。
    /// </summary>
    internal static string ExpectedRarPath()
    {
        IReadOnlyList<string> directories = WinRarInstallationDirectories();

        return directories.Count > 0
            ? Path.Combine(directories[0], "Rar.exe")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "WinRAR",
                "Rar.exe");
    }

    /// <summary>WinRAR.exe 的"本来应该在的位置"（只用于"未找到"提示，不参与可用性判定）。</summary>
    internal static string ExpectedWinRarGuiPath()
    {
        return Path.Combine(ExpectedProgramFilesRoot(), "WinRAR", "WinRAR.exe");
    }

    private static string? ReadWinRarRegistryValue(string subKey, string valueName)
    {
        try
        {
            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(subKey);

            return key?.GetValue(valueName) as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>ProgramFiles 的两个来源（64 位优先；取不到的跳过）。</summary>
    private static IEnumerable<string?> ProgramFilesRoots()
    {
        yield return Environment.GetEnvironmentVariable("ProgramW6432");
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Environment.GetEnvironmentVariable("ProgramFiles");
        yield return Environment.GetEnvironmentVariable("ProgramFiles(x86)");
    }

    /// <summary>"本来应该在的位置"：ProgramFiles 的第一个可用根（只用于"未找到"的提示）。</summary>
    private static string ExpectedProgramFilesRoot()
    {
        foreach (string? root in ProgramFilesRoots())
        {
            if (!string.IsNullOrWhiteSpace(root))
            {
                return root;
            }
        }

        return string.Empty;
    }
}

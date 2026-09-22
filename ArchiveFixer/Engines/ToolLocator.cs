using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Engines
{
    /// <summary>
    /// 外部归档工具路径的**唯一**来源（7-Zip 与 RARLAB UnRAR 两条都在这里）。
    ///
    /// 为什么要单独一个类（AGENTS.md §3.1）：
    /// 以前 7z.exe 的路径在 App.xaml.cs、PathService、ExtractService 三个文件里各拼了一遍，
    /// 而且还写死了开发机的 <c>D:\7-Zip\7z.exe</c> 回退 —— 换台机器、换个安装位置就会行为不一致，
    /// 排查时也说不清"到底用的是哪一个 7z"。现在只允许从这里取，UnRAR 同理。
    ///
    /// <para><b>7-Zip 解析顺序</b>：
    /// 1. 用户在设置里指定的路径（<see cref="CustomSevenZipExePath"/>）
    /// 2. 随程序分发的内置路径 <c>&lt;程序目录&gt;\tools\7zip\7z.exe</c>
    /// 3. 都没有 → 返回内置路径，由调用方报"未找到"（**不再猜别的安装位置**）</para>
    ///
    /// <para><b>UnRAR 解析顺序</b>（AGENTS.md §3.1 / 用户 2026-09-22 指示）：
    /// 1. 用户在设置里指定的路径（<see cref="CustomUnRarExePath"/>）
    /// 2. 用户**已装**的 WinRAR 目录（<c>%ProgramFiles%\WinRAR\UnRAR.exe</c> 等，只读探测、只调用、绝不复制）
    /// 3. 随程序分发的内置路径 <c>&lt;程序目录&gt;\tools\unrar\UnRAR.exe</c>
    /// 4. 都没有 → 返回"本来应该在的位置"，由引擎报"不可用"，选择器自动跳过它</para>
    ///
    /// ⚠ 第 2 档只找 RARLAB 的**免费件** <c>UnRAR.exe</c>，绝不碰同一目录里的
    /// <c>Rar.exe</c> / <c>WinRAR.exe</c>（共享软件，见 docs/引擎与外部工具.md §4）。
    /// </summary>
    public sealed class ToolLocator
    {
        /// <summary>默认实例。进程内共用一份，避免各处缓存不一致。</summary>
        public static ToolLocator Default { get; } = new ToolLocator();

        private string _customSevenZipExePath = string.Empty;
        private string _customUnRarExePath = string.Empty;

        private string? _resolvedExePath;
        private string? _resolvedDllPath;
        private bool _usingCustomPath;

        private string? _resolvedUnRarPath;
        private bool _unRarResolved;
        private bool _usingCustomUnRarPath;
        private bool _usingWinRarInstallation;

        /// <summary>
        /// 要不要探测"用户已装的 WinRAR 目录"这一档（默认 true）。
        ///
        /// 存在这个开关不是为了给用户看，而是为了让**"没装 UnRAR 时会怎样"这件事可被验证**：
        /// 验收要求模拟"机器上没有 UnRAR"，只把自定义路径指向一个不存在的文件是不够的 ——
        /// 那样下一档（已装 WinRAR 目录 / 内置）会把它救回来，测的就不是回落路径了。
        /// 关掉这两档（本项与 <see cref="UseBundledUnRar"/>）才是真正的"这台机器上没有 UnRAR"。
        /// </summary>
        public bool UseWinRarInstallation { get; set; } = true;

        /// <summary>要不要用内置的 <c>tools\unrar\UnRAR.exe</c> 这一档（默认 true）。含义同上。</summary>
        public bool UseBundledUnRar { get; set; } = true;

        /// <summary>
        /// 用户自定义的 7z.exe 路径（来自设置）。
        /// 赋值后会自动失效缓存，下次读取重新解析。
        /// </summary>
        public string CustomSevenZipExePath
        {
            get => _customSevenZipExePath;
            set
            {
                string normalized = value?.Trim() ?? string.Empty;

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
                string normalized = value?.Trim() ?? string.Empty;

                if (string.Equals(_customUnRarExePath, normalized, StringComparison.Ordinal))
                {
                    return;
                }

                _customUnRarExePath = normalized;
                Invalidate();
            }
        }

        /// <summary>随程序分发的内置 7-Zip 目录。</summary>
        public string BundledDirectory => Path.Combine(AppContext.BaseDirectory, "tools", "7zip");

        /// <summary>随程序分发的内置 UnRAR 目录。</summary>
        public string BundledUnRarDirectory => Path.Combine(AppContext.BaseDirectory, "tools", "unrar");

        /// <summary>实际使用的 7z.exe 路径（即使文件不存在也会返回"应该在哪"）。</summary>
        public string SevenZipExePath
        {
            get
            {
                EnsureResolved();
                return _resolvedExePath!;
            }
        }

        /// <summary>实际使用的 7z.dll 路径。</summary>
        public string SevenZipDllPath
        {
            get
            {
                EnsureResolved();
                return _resolvedDllPath!;
            }
        }

        /// <summary>是否用的是用户自定义路径（而不是内置的）。</summary>
        public bool IsUsingCustomPath
        {
            get
            {
                EnsureResolved();
                return _usingCustomPath;
            }
        }

        public bool SevenZipExists => File.Exists(SevenZipExePath);

        public bool SevenZipDllExists => File.Exists(SevenZipDllPath);

        /// <summary>内置 UnRAR 的"本来应该在的位置"（用于"未找到"提示，不参与是否可用的判定）。</summary>
        public string UnRarExpectedPath => Path.Combine(BundledUnRarDirectory, "UnRAR.exe");

        /// <summary>
        /// 实际使用的 UnRAR.exe 路径；三档都没命中时返回"本来应该在的位置"（即内置目录），
        /// 由 <see cref="UnRarExists"/> 判否 —— 调用方据此报"未找到"而不是拿到一个空字符串。
        ///
        /// 注意：这里**不**校验它自报的版本 —— 用户机器上那份可能是 6.11（WinRAR 自带），
        /// 也可能是我们内置的 7.23。是哪一份要看 <see cref="UnRarVersion"/> 与
        /// <see cref="DescribeUnRarResolution"/>，报告里必须写清楚（不变量 14）。
        /// </summary>
        public string UnRarExePath
        {
            get
            {
                EnsureUnRarResolved();
                return _resolvedUnRarPath ?? UnRarExpectedPath;
            }
        }

        /// <summary>
        /// 是否真的解析到了一个可用的 UnRAR.exe。
        ///
        /// ⚠ **不能用 <c>File.Exists(UnRarExePath)</c> 代替**：当"已装 WinRAR 目录"与"内置"两档
        /// 都被显式关掉时（模拟没装 UnRAR 的那一档测试），解析结果是"没找到"，
        /// 而 <see cref="UnRarExePath"/> 仍然会回落到那个"本来应该在的位置"用于提示 ——
        /// 那台机器上这个文件可能恰好存在，用 File.Exists 判就会把"已禁用"误判成"可用"。
        /// </summary>
        public bool UnRarExists
        {
            get
            {
                EnsureUnRarResolved();
                return _resolvedUnRarPath != null && File.Exists(_resolvedUnRarPath);
            }
        }

        /// <summary>用的是用户自选路径。</summary>
        public bool IsUsingCustomUnRarPath
        {
            get
            {
                EnsureUnRarResolved();
                return _usingCustomUnRarPath;
            }
        }

        /// <summary>用的是用户已装 WinRAR 目录里那一个（而不是内置的）。</summary>
        public bool IsUsingWinRarInstallation
        {
            get
            {
                EnsureUnRarResolved();
                return _usingWinRarInstallation;
            }
        }

        /// <summary>7z.exe 的文件版本（取不到返回 "unknown"）。</summary>
        public string SevenZipVersion
        {
            get
            {
                try
                {
                    string path = SevenZipExePath;

                    if (!File.Exists(path))
                    {
                        return "unknown";
                    }

                    string? version = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileVersion;

                    return string.IsNullOrWhiteSpace(version) ? "unknown" : version;
                }
                catch
                {
                    return "unknown";
                }
            }
        }

        /// <summary>
        /// UnRAR.exe 的文件版本（取不到返回 "unknown"）。
        ///
        /// 为什么不解析它输出的 "UNRAR 7.23 x64 freeware" banner：那要起一个进程，
        /// 而设置界面每次刷新都要显示版本 —— 为一个版本号反复起进程是不划算的。
        /// 文件版本资源的写法（<c>7.23.0</c>）与实际 banner（<c>UNRAR 7.23</c>）对得上，
        /// 报告里写 <c>UnRAR 7.23.0</c> 足够追到"当时用的是哪个构建"。
        /// </summary>
        public string UnRarVersion
        {
            get
            {
                try
                {
                    string path = UnRarExePath;

                    if (!File.Exists(path))
                    {
                        return "unknown";
                    }

                    string? version = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileVersion;

                    return string.IsNullOrWhiteSpace(version) ? "unknown" : version;
                }
                catch
                {
                    return "unknown";
                }
            }
        }

        /// <summary>让缓存的解析结果失效（设置变更、文件被替换后调用）。</summary>
        public void Invalidate()
        {
            _resolvedExePath = null;
            _resolvedDllPath = null;
            _resolvedUnRarPath = null;
            _unRarResolved = false;
        }

        /// <summary>
        /// 给日志/报告用的一句话，说清"用的是哪一个、从哪来的"。
        /// 出问题时这句话比任何猜测都有用。
        /// </summary>
        public string DescribeResolution()
        {
            if (SevenZipExists)
            {
                return IsUsingCustomPath
                    ? $"使用自定义 7-Zip：{SevenZipExePath}（版本 {SevenZipVersion}）"
                    : $"使用内置 7-Zip：{SevenZipExePath}（版本 {SevenZipVersion}）";
            }

            return $"未找到 7-Zip，期望位置：{SevenZipExePath}";
        }

        /// <summary>UnRAR 的同一句话（界面与报告共用；措辞只做事实性说明，见 docs/引擎与外部工具.md §3）。</summary>
        public string DescribeUnRarResolution()
        {
            if (!UnRarExists)
            {
                return $"未找到 RAR 解压引擎 UnRAR，期望位置：{UnRarExePath}";
            }

            string source = IsUsingCustomUnRarPath
                ? "使用自选的 UnRAR"
                : IsUsingWinRarInstallation
                    ? "使用本机已装 WinRAR 目录中的 UnRAR"
                    : "使用内置 UnRAR";

            return $"{source}：{UnRarExePath}（版本 {UnRarVersion}）";
        }

        /// <summary>
        /// 两个引擎"在哪、能不能用"的一句话（写日志、排障用）。
        ///
        /// 为什么不在这里另造一套"可用 / 不可用"的说法：<see cref="DescribeResolution"/> 与
        /// <see cref="DescribeUnRarResolution"/> 早就把两种事实各自的措辞定死了，这里只把它们并成一行。
        /// </summary>
        public string DescribeAvailability()
        {
            return $"{DescribeResolution()}；{DescribeUnRarResolution()}；当前引擎优先级：{DescribePriority()}";
        }

        /// <summary>
        /// "没有可用解压引擎"时给用户看的那段提示 —— **唯一来源**。
        ///
        /// <para>
        /// 为什么必须由本类现算（体检报告《audit-code.md》§2 第 5 条 / §4 第 1 条）：
        /// 判定用的是 <c>EngineRouter.IsAvailable</c> = <b>任一</b>引擎可用，而默认优先级是
        /// <c>WinRAR(UnRAR) → 7-Zip</c>。旧提示写死"未找到 <c>tools\7zip\7z.exe</c>"，后果是
        /// 真正缺 UnRAR（甚至只是没排上队）的用户被引去修一个本来没问题的 7z 目录；
        /// 自己配了外部 7z 的用户更是被指向一个他根本没在用的路径。
        /// </para>
        ///
        /// <para>
        /// 所以这里把**两条期望路径都列出来**、写明"任装其一即可"，并给出当前优先级顺序 ——
        /// 用户看一眼就知道该往哪儿放文件。路径全部现算，全文不写死任何文件名
        /// （外部工具路径只有本类一个来源，AGENTS.md §3.1）。
        /// </para>
        /// </summary>
        public string DescribeNoEngineAvailable()
        {
            return "未找到可用的解压引擎：7-Zip 与 UnRAR 都没有找到，两者任装其一即可。"
                 + $"7-Zip 期望位置：{SevenZipExePath}；"
                 + $"UnRAR 期望位置：{UnRarExePath}。"
                 + $"当前引擎优先级：{DescribePriority()}（不可用的引擎会自动跳过，顺序可在设置里调整）。";
        }

        /// <summary>
        /// 当前优先级的人读写法（引擎 id → 外部工具名）。
        ///
        /// 映射为什么放在这里而不是 <c>EngineIds</c>：这句话回答的是"**外部工具**按什么顺序找"，
        /// 而本类正是外部工具的唯一定位者，"引擎 id ↔ 工具名"的对应关系只有在这件事上才有意义。
        /// 认不出的 id 原样列出，绝不悄悄丢掉 —— 将来加第三个引擎时，提示里要能看见它。
        /// </summary>
        public static string DescribePriority()
        {
            IReadOnlyList<string> priority = EngineRuntimeSettings.EnginePriority;

            if (priority == null || priority.Count == 0)
            {
                return DescribeEngineId(EngineIds.SevenZip);
            }

            return string.Join(" → ", priority.Select(DescribeEngineId));
        }

        private static string DescribeEngineId(string id)
        {
            if (string.Equals(id, EngineIds.WinRar, StringComparison.OrdinalIgnoreCase))
            {
                return "UnRAR（RAR 系）";
            }

            if (string.Equals(id, EngineIds.SevenZip, StringComparison.OrdinalIgnoreCase))
            {
                return "7-Zip";
            }

            return id;
        }

        /// <summary>
        /// "用户已装的 WinRAR 目录"候选（只读探测）。
        /// 取不到环境变量时跳过对应项；同一路径不重复探测。
        /// </summary>
        public IReadOnlyList<string> WinRarInstallationCandidates()
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

        private void EnsureResolved()
        {
            if (_resolvedExePath != null && _resolvedDllPath != null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(_customSevenZipExePath) && File.Exists(_customSevenZipExePath))
            {
                _resolvedExePath = _customSevenZipExePath;
                _resolvedDllPath = Path.Combine(
                    Path.GetDirectoryName(_customSevenZipExePath) ?? BundledDirectory,
                    "7z.dll");
                _usingCustomPath = true;
                return;
            }

            _resolvedExePath = Path.Combine(BundledDirectory, "7z.exe");
            _resolvedDllPath = Path.Combine(BundledDirectory, "7z.dll");
            _usingCustomPath = false;
        }

        /// <summary>
        /// UnRAR 的三级解析（用户自选 → 已装 WinRAR → 内置）。
        ///
        /// 与 7-Zip 那一段的差别：**自选路径填了但不存在的文件，不会把后面的档位吃掉**——
        /// 用户自选一个已经删掉的 UnRAR，正确的行为是"回落到下一档并如实说明用的是哪一个"，
        /// 而不是"引擎整个不可用"。这也是验收第 4 条要验证的回落链路的第一环。
        /// </summary>
        private void EnsureUnRarResolved()
        {
            if (_unRarResolved)
            {
                return;
            }

            _usingCustomUnRarPath = false;
            _usingWinRarInstallation = false;

            if (!string.IsNullOrWhiteSpace(_customUnRarExePath) && File.Exists(_customUnRarExePath))
            {
                _resolvedUnRarPath = _customUnRarExePath;
                _usingCustomUnRarPath = true;
                _unRarResolved = true;
                return;
            }

            if (UseWinRarInstallation)
            {
                foreach (string candidate in WinRarInstallationCandidates())
                {
                    if (File.Exists(candidate))
                    {
                        _resolvedUnRarPath = candidate;
                        _usingWinRarInstallation = true;
                        _unRarResolved = true;
                        return;
                    }
                }
            }

            if (UseBundledUnRar)
            {
                _resolvedUnRarPath = Path.Combine(BundledUnRarDirectory, "UnRAR.exe");
                _unRarResolved = true;
                return;
            }

            /*
             * 三档都不允许（只有"模拟这台机器上没装 UnRAR"的验证会走到这）：
             * 解析结果显式置空 = "没找到"，绝不悄悄回落到一个存在的路径上 ——
             * 那样测的就不是回落链路，而是"其实还能用"。
             */
            _resolvedUnRarPath = null;
            _unRarResolved = true;
        }
    }
}

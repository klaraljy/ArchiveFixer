using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Engines
{
    /// <summary>
    /// 外部归档工具路径的**唯一**来源（7-Zip、RARLAB UnRAR、以及打包要用的 Rar.exe 三条都在这里）。
    ///
    /// 为什么要单独一个类（AGENTS.md §3.1）：
    /// 以前 7z.exe 的路径在 App.xaml.cs、PathService、ExtractService 三个文件里各拼了一遍，
    /// 而且还写死了开发机的 <c>D:\7-Zip\7z.exe</c> 回退 —— 换台机器、换个安装位置就会行为不一致，
    /// 排查时也说不清"到底用的是哪一个 7z"。现在只允许从这里取，UnRAR 与 Rar.exe 同理。
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
    /// <para><b>Rar.exe 解析顺序</b>（打包功能，docs/打包功能.md §5）：
    /// 1. 用户指定的路径（<see cref="CustomRarExePath"/>，来自②「解压方式」页 → 引擎那一格）
    /// 2. 用户**已装**的 WinRAR 目录里的 <c>Rar.exe</c>，再退 <c>WinRAR.exe</c>
    /// 3. 都没有 → <see cref="RarExists"/> = false，打包侧**明确报错**并给出三条出路
    /// （装 WinRAR / 外层容器改 7z / 不做外层）。
    /// ⛔ <c>Rar.exe</c> / <c>WinRAR.exe</c> 是**共享软件**：只检测、只调用，**绝不打包、绝不复制**
    /// （AGENTS.md §3.1）。自选那一格指向的永远是**用户自己装的**那一份。</para>
    ///
    /// ⚠ 第 2 档只找 RARLAB 的**免费件** <c>UnRAR.exe</c>（解压引擎）；同一目录里的
    /// <c>Rar.exe</c> / <c>WinRAR.exe</c> 只在**打包**这一条路上被调用（见
    /// docs/引擎与外部工具.md §4）。
    /// </summary>
    public sealed class ToolLocator
    {
        /// <summary>默认实例。进程内共用一份，避免各处缓存不一致。</summary>
        public static ToolLocator Default { get; } = new ToolLocator();

        private string _customSevenZipExePath = string.Empty;
        private string _customUnRarExePath = string.Empty;
        private string _customRarExePath = string.Empty;

        private string? _resolvedExePath;
        private string? _resolvedDllPath;
        private bool _usingCustomPath;

        private string? _resolvedUnRarPath;
        private bool _unRarResolved;
        private bool _usingCustomUnRarPath;
        private bool _usingWinRarInstallation;

        private string? _resolvedRarPath;
        private bool _rarResolved;
        private bool _usingCustomRarPath;
        private bool _usingWinRarGuiForRar;

        /// <summary>
        /// 要不要探测"用户已装的 WinRAR 目录"这一档（默认 true）。
        ///
        /// 存在这个开关不是为了给用户看，而是为了让**"没装 WinRAR 时会怎样"这件事可被验证**：
        /// 验收要求模拟"机器上没有 UnRAR"，只把自定义路径指向一个不存在的文件是不够的 ——
        /// 那样下一档（已装 WinRAR 目录 / 内置）会把它救回来，测的就不是回落路径了。
        /// 关掉这两档（本项与 <see cref="UseBundledUnRar"/>）才是真正的"这台机器上没有 UnRAR"。
        ///
        /// 打包用的 <c>Rar.exe</c> 同样读这一档：关掉它就等于"这台机器上没有 Rar.exe"
        /// （打包侧据此走"明确报错 + 只做 7z 分卷"那条路）。
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

        /// <summary>
        /// 用户指定的 <c>Rar.exe</c> 路径（来自②「解压方式」页 → 引擎那一格；用户 2026-09-23 决定加这一格）。
        /// 赋值后自动失效缓存。
        ///
        /// <para>空 = 按"已装 WinRAR 目录 → <c>WinRAR.exe</c>"自动解析。填了但文件不存在时
        /// **不吃掉后面的档位**（与 UnRAR 同一口径）：回落到下一档并如实说明用的是哪一个。</para>
        /// </summary>
        public string CustomRarExePath
        {
            get => _customRarExePath;
            set
            {
                string normalized = value?.Trim() ?? string.Empty;

                if (string.Equals(_customRarExePath, normalized, StringComparison.Ordinal))
                {
                    return;
                }

                _customRarExePath = normalized;
                Invalidate();
            }
        }

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
            _resolvedRarPath = null;
            _rarResolved = false;
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
        public string RarExpectedPath
        {
            get
            {
                IReadOnlyList<string> directories = WinRarInstallationDirectories();

                return directories.Count > 0
                    ? Path.Combine(directories[0], "Rar.exe")
                    : Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                        "WinRAR",
                        "Rar.exe");
            }
        }

        /// <summary>
        /// 实际使用的 <c>Rar.exe</c>（或退一档的 <c>WinRAR.exe</c>）；没有时返回
        /// <see cref="RarExpectedPath"/>，由 <see cref="RarExists"/> 判否。
        /// </summary>
        public string RarExePath
        {
            get
            {
                EnsureRarResolved();
                return _resolvedRarPath ?? RarExpectedPath;
            }
        }

        /// <summary>
        /// 这台机器上有没有可用的打包命令行工具。
        ///
        /// ⚠ 与 <see cref="UnRarExists"/> 同一个写法：不能拿 <c>File.Exists(RarExePath)</c> 代替 ——
        /// <see cref="RarExePath"/> 在"没找到"时会回落到"本来应该在的位置"，那个文件在那台机器上
        /// 可能恰好存在（只是被显式关掉了那一档）。
        /// </summary>
        public bool RarExists
        {
            get
            {
                EnsureRarResolved();
                return _resolvedRarPath != null && File.Exists(_resolvedRarPath);
            }
        }

        /// <summary>用的是用户自选的路径。</summary>
        public bool IsUsingCustomRarPath
        {
            get
            {
                EnsureRarResolved();
                return _usingCustomRarPath;
            }
        }

        /// <summary>
        /// 退到了 GUI 版 <c>WinRAR.exe</c>（而不是命令行版 <c>Rar.exe</c>）。
        /// 打包侧据此补 <c>-ibck</c>：不加它，那个 GUI 程序会弹出进度窗口并抢焦点。
        /// </summary>
        public bool IsUsingWinRarGuiForRar
        {
            get
            {
                EnsureRarResolved();
                return _usingWinRarGuiForRar;
            }
        }

        /// <summary>Rar.exe 的文件版本（取不到返回 "unknown"）。</summary>
        public string RarVersion
        {
            get
            {
                try
                {
                    string path = RarExePath;

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
        /// Rar.exe 的同一句话（界面与报告共用）。
        /// 出问题时这句话要能回答"到底用的哪一个、从哪来的"（不变量 14）。
        /// </summary>
        public string DescribeRarResolution()
        {
            if (!RarExists)
            {
                return DescribeNoRarAvailable();
            }

            string source = IsUsingCustomRarPath
                ? "使用自选的 Rar.exe（你在设置里填的那一份）"
                : IsUsingWinRarGuiForRar
                    ? "使用本机已装 WinRAR 目录中的 WinRAR.exe（命令行版 Rar.exe 没找到）"
                    : "使用本机已装 WinRAR 目录中的 Rar.exe";

            return $"{source}：{RarExePath}（版本 {RarVersion}）";
        }

        /// <summary>
        /// 没有 Rar.exe 时给用户看的那段话 —— **唯一来源**（界面、日志、失败原因都引它）。
        ///
        /// <para>为什么不内置一个：<c>Rar.exe</c> 是共享软件，许可不允许再分发（AGENTS.md §3.1）。
        /// 也**不会**拿 7-Zip 假装做出一个 <c>.rar</c>：7-Zip 建不了 RAR（算法是专有的），
        /// 改名成 <c>.rar</c> 是伪造，用户拿去用时会直接坏掉。</para>
        /// </summary>
        public string DescribeNoRarAvailable()
        {
            return "这一步需要本机已安装 WinRAR（要 Rar.exe / WinRAR.exe）—— 程序不会替你装、也不会随包分发它"
                 + "（WinRAR 是共享软件，许可不允许随其它软件包分发）。"
                 + $"期望位置：{RarExpectedPath}。"
                 + "在②「解压方式」页 →「引擎」里可以填你自己装的那一份 Rar.exe 的路径；"
                 + "如果这台机器上确实没有，也可以把⑤「打包」页的外层容器改成 7z（无需额外安装）或选「不做外层容器」。";
        }

        /// <summary>
        /// "用户已装的 WinRAR 目录"（目录形态；从 <see cref="WinRarInstallationCandidates"/> 推出来，
        /// 不另写一套探测逻辑）。
        /// </summary>
        public IReadOnlyList<string> WinRarInstallationDirectories()
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
        /// Rar 的两级解析（自选 → 已装 WinRAR 目录里的 Rar.exe，再退 WinRAR.exe）。
        ///
        /// 与 UnRAR 那一段同一口径：**自选路径填了但文件不存在时不吃掉后面的档位**，
        /// 而是回落到下一档并如实说明用的是哪一个。
        /// </summary>
        private void EnsureRarResolved()
        {
            if (_rarResolved)
            {
                return;
            }

            _usingCustomRarPath = false;
            _usingWinRarGuiForRar = false;

            if (!string.IsNullOrWhiteSpace(_customRarExePath) && File.Exists(_customRarExePath))
            {
                _resolvedRarPath = _customRarExePath;
                _usingCustomRarPath = true;
                _rarResolved = true;
                return;
            }

            if (UseWinRarInstallation)
            {
                foreach (string directory in WinRarInstallationDirectories())
                {
                    string rar = Path.Combine(directory, "Rar.exe");

                    if (File.Exists(rar))
                    {
                        _resolvedRarPath = rar;
                        _rarResolved = true;
                        return;
                    }
                }

                foreach (string directory in WinRarInstallationDirectories())
                {
                    string winRar = Path.Combine(directory, "WinRAR.exe");

                    if (File.Exists(winRar))
                    {
                        _resolvedRarPath = winRar;
                        _usingWinRarGuiForRar = true;
                        _rarResolved = true;
                        return;
                    }
                }
            }

            /*
             * 两档都没命中：显式"没找到"。绝不悄悄回落到一个存在的路径上 ——
             * 那会让打包以为能做外层 rar，然后在真正开跑时才炸。
             */
            _resolvedRarPath = null;
            _rarResolved = true;
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

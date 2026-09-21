using System;
using System.IO;

namespace ArchiveFixer.Engines
{
    /// <summary>
    /// 外部归档工具路径的**唯一**来源。
    ///
    /// 为什么要单独一个类（AGENTS.md §3.1）：
    /// 以前 7z.exe 的路径在 App.xaml.cs、PathService、ExtractService 三个文件里各拼了一遍，
    /// 而且还写死了开发机的 <c>D:\7-Zip\7z.exe</c> 回退 —— 换台机器、换个安装位置就会行为不一致，
    /// 排查时也说不清"到底用的是哪一个 7z"。现在只允许从这里取。
    ///
    /// 解析顺序：
    /// 1. 用户在设置里指定的路径（<see cref="CustomSevenZipExePath"/>）
    /// 2. 随程序分发的内置路径 <c>&lt;程序目录&gt;\tools\7zip\7z.exe</c>
    /// 3. 都没有 → 返回内置路径，由调用方报"未找到"（**不再猜别的安装位置**）
    /// </summary>
    public sealed class ToolLocator
    {
        /// <summary>默认实例。进程内共用一份，避免各处缓存不一致。</summary>
        public static ToolLocator Default { get; } = new ToolLocator();

        private string _customSevenZipExePath = string.Empty;
        private string? _resolvedExePath;
        private string? _resolvedDllPath;
        private bool _usingCustomPath;

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

        /// <summary>随程序分发的内置 7-Zip 目录。</summary>
        public string BundledDirectory => Path.Combine(AppContext.BaseDirectory, "tools", "7zip");

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

        /// <summary>让缓存的解析结果失效（设置变更、文件被替换后调用）。</summary>
        public void Invalidate()
        {
            _resolvedExePath = null;
            _resolvedDllPath = null;
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
    }
}

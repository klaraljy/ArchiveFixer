using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace ArchiveFixer.Helpers
{
    /// <summary>
    /// 安全路径工具。
    /// </summary>
    public static class SafePathHelper
    {
        /// <summary>
        /// 获取程序基目录。
        /// </summary>
        public static string AppBaseDirectory => AppContext.BaseDirectory;

        /// <summary>
        /// 组合路径。
        /// </summary>
        public static string Combine(params string[] parts)
        {
            try
            {
                return Path.Combine(parts);
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// 判断文件是否存在。
        /// </summary>
        public static bool FileExists(string? path)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 判断目录是否存在。
        /// </summary>
        public static bool DirectoryExists(string? path)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 确保目录存在。
        /// </summary>
        public static bool EnsureDirectoryExists(string? path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return false;
                }

                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 获取完整路径。
        /// </summary>
        public static string GetFullPathSafe(string? path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return string.Empty;
                }

                return Path.GetFullPath(path);
            }
            catch
            {
                return path ?? string.Empty;
            }
        }

        /// <summary>
        /// 清理非法文件名。
        /// </summary>
        public static string SanitizeFileName(string? name)
        {
            return FileNameHelper.SanitizeFileName(name);
        }

        /// <summary>
        /// 自动重命名文件路径。
        /// 例如：
        /// test.7z -> test(1).7z
        /// </summary>
        public static string AutoRenameFilePath(string targetPath)
        {
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                return targetPath;
            }

            if (!File.Exists(targetPath) && !Directory.Exists(targetPath))
            {
                return targetPath;
            }

            string? dir = Path.GetDirectoryName(targetPath);
            string fileName = Path.GetFileNameWithoutExtension(targetPath);
            string ext = Path.GetExtension(targetPath);

            if (string.IsNullOrWhiteSpace(dir))
            {
                dir = Directory.GetCurrentDirectory();
            }

            for (int i = 1; i < 10000; i++)
            {
                string candidate = Path.Combine(dir, $"{fileName}({i}){ext}");

                if (!File.Exists(candidate) && !Directory.Exists(candidate))
                {
                    return candidate;
                }
            }

            string randomName = $"{fileName}_{DateTime.Now:yyyyMMddHHmmssfff}{ext}";
            return Path.Combine(dir, randomName);
        }

        /// <summary>
        /// 自动重命名目录路径。
        /// 例如：
        /// test -> test(1)
        /// </summary>
        public static string AutoRenameDirectoryPath(string targetPath)
        {
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                return targetPath;
            }

            if (!Directory.Exists(targetPath) && !File.Exists(targetPath))
            {
                return targetPath;
            }

            string? parent = Path.GetDirectoryName(targetPath);
            string name = Path.GetFileName(targetPath);

            if (string.IsNullOrWhiteSpace(parent))
            {
                parent = Directory.GetCurrentDirectory();
            }

            for (int i = 1; i < 10000; i++)
            {
                string candidate = Path.Combine(parent, $"{name}({i})");

                if (!Directory.Exists(candidate) && !File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return Path.Combine(parent, $"{name}_{DateTime.Now:yyyyMMddHHmmssfff}");
        }

        /// <summary>
        /// 判断路径是否可能过长。
        /// </summary>
        public static bool IsPathTooLong(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            return path.Length >= 240;
        }

        /// <summary>
        /// 尝试打开目录。
        /// </summary>
        public static bool OpenDirectory(string? path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return false;
                }

                if (File.Exists(path))
                {
                    path = Path.GetDirectoryName(path);
                }

                if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                {
                    return false;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    UseShellExecute = true
                };

                psi.ArgumentList.Add(path);
                Process.Start(psi);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 在资源管理器中选中文件。
        /// </summary>
        public static bool OpenFileInExplorer(string? filePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                {
                    return false;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    UseShellExecute = true
                };

                psi.ArgumentList.Add("/select,");
                psi.ArgumentList.Add(filePath);

                Process.Start(psi);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 允许交给默认浏览器打开的**外部网址白名单**（用户 2026-10-05：设置里那句"推荐另外装一个 WinRAR"
        /// 要能一键打开发布方官网）。
        ///
        /// <para><b>为什么必须白名单</b>：<c>UseShellExecute = true</c> 是把字符串直接交给 shell 的出口 ——
        /// 拿它开任意 URL 等于给程序开一个"什么都能执行"的后门。这里只认 <c>https</c>，且主机名必须**正好是**
        /// 白名单里的那一个（⛔ 不用 <c>EndsWith</c> 判：`rarlab.com.evil.tld` 会跟着过）。</para>
        /// </summary>
        private static readonly string[] AllowedExternalLinkHosts =
        {
            "rarlab.com",
            "www.rarlab.com"
        };

        /// <summary>这个网址允不允许交给浏览器（纯判据，测试直接喂它，不起进程）。</summary>
        public static bool IsAllowedExternalLink(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)
                || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
                || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return AllowedExternalLinkHosts.Any(
                host => string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 用默认浏览器打开一个**白名单里**的网址；不在白名单 / 起不来 ⇒ 返回 false（⛔ 静默失败由调用方说出来）。
        /// </summary>
        public static bool OpenExternalLink(string? url)
        {
            try
            {
                if (!IsAllowedExternalLink(url))
                {
                    return false;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                };

                using Process? process = Process.Start(psi);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 将字节数组转十六进制字符串。
        /// </summary>
        public static string ToHexString(byte[]? bytes, int maxLength = 64)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return string.Empty;
            }

            int length = Math.Min(bytes.Length, maxLength);
            var builder = new StringBuilder(length * 3);

            for (int i = 0; i < length; i++)
            {
                if (i > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(bytes[i].ToString("X2"));
            }

            return builder.ToString();
        }

        /// <summary>
        /// 判断两个路径是否相同。
        /// Windows 下忽略大小写。
        /// </summary>
        public static bool PathEquals(string? a, string? b)
        {
            string fa = GetFullPathSafe(a);
            string fb = GetFullPathSafe(b);

            return string.Equals(fa, fb, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 判断路径是否可写。
        /// </summary>
        public static bool CanWriteToDirectory(string? directory)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(directory))
                {
                    return false;
                }

                EnsureDirectoryExists(directory);

                string testFile = Path.Combine(directory, $".write_test_{Guid.NewGuid():N}.tmp");
                File.WriteAllText(testFile, "test");
                File.Delete(testFile);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}

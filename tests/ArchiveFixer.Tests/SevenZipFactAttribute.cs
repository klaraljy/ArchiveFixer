using System;
using System.IO;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// "需要真实 7z.exe"的用例标记：环境不满足时在发现阶段跳过并说明，而不是染红整条测试链。
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class SevenZipFactAttribute : FactAttribute
    {
        private static readonly Lazy<string> LocatedSevenZip = new(LocateSevenZipPath);

        public SevenZipFactAttribute()
        {
            if (string.IsNullOrEmpty(LocatedSevenZip.Value))
            {
                Skip = "测试机上没有可用的 7z.exe（ArchiveFixer/tools/7zip/7z.exe），跳过需要真实引擎的用例。";
            }
        }

        /// <summary>定位内置 7z.exe：从测试程序集目录向上找含 ArchiveFixer.slnx 的目录，再取 tools/7zip/7z.exe。</summary>
        internal static string LocateSevenZipPath()
        {
            DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ArchiveFixer.slnx")))
                {
                    string candidate = Path.Combine(directory.FullName, "src", "ArchiveFixer", "tools", "7zip", "7z.exe");

                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }

                directory = directory.Parent;
            }

            string local = Path.Combine(AppContext.BaseDirectory, "tools", "7zip", "7z.exe");

            return File.Exists(local) ? local : string.Empty;
        }
    }
}

using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace ArchiveFixer.Services
{
    /// <summary>
    /// 构建身份：诊断包必须能回答“这份日志对应哪次提交、什么配置”。
    /// 打包脚本通过 <c>SourceRevisionId</c> 把 git commit 写进 AssemblyInformationalVersion；
    /// 本地无 git 时如实写 unknown，⛔ 不编造。
    /// </summary>
    public static class BuildInfo
    {
        private static readonly Assembly AppAssembly = typeof(BuildInfo).Assembly;

        public static string Commit
        {
            get
            {
                AssemblyMetadataAttribute? metadata = AppAssembly
                    .GetCustomAttributes<AssemblyMetadataAttribute>()
                    .FirstOrDefault(attribute => string.Equals(attribute.Key, "GitCommit", StringComparison.Ordinal));

                if (metadata != null && !string.IsNullOrWhiteSpace(metadata.Value))
                {
                    return metadata.Value!;
                }

                string? informational = AppAssembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                    .InformationalVersion;

                if (!string.IsNullOrWhiteSpace(informational))
                {
                    int plus = informational.IndexOf('+');

                    if (plus >= 0 && plus + 1 < informational.Length)
                    {
                        return informational[(plus + 1)..];
                    }
                }

                return "unknown";
            }
        }

        public static string Configuration =>
            AppAssembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? string.Empty;

        public static string BuildTime
        {
            get
            {
                try
                {
                    /*
                     * 单文件发布里 Assembly.Location 恒为空（IL3000，开了 TreatWarningsAsErrors 会直接编译失败）。
                     * 先读程序目录里的 ArchiveFixer.dll（框架依赖/开发期），读不到再退回进程路径（单文件 exe）。
                     */
                    foreach (string candidate in new[]
                             {
                                 Path.Combine(AppContext.BaseDirectory, "ArchiveFixer.dll"),
                                 Environment.ProcessPath ?? string.Empty
                             })
                    {
                        if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                        {
                            return File.GetLastWriteTimeUtc(candidate)
                                .ToString("O", System.Globalization.CultureInfo.InvariantCulture);
                        }
                    }
                }
                catch
                {
                    // 单文件/受限环境读不到就如实空着。
                }

                return string.Empty;
            }
        }
    }
}

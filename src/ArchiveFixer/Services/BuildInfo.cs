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
                    string? location = AppAssembly.Location;

                    if (!string.IsNullOrWhiteSpace(location) && File.Exists(location))
                    {
                        return File.GetLastWriteTimeUtc(location).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
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

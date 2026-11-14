using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Storage;
using ArchiveFixer.Extraction;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 阶段 7.1 清点门禁：先钉住“已死的旧符号”和“活跃的删除/卷名路径”，
    /// 再按切片删除。⛔ 本测试不直接删任何东西。
    /// </summary>
    public sealed class LegacySymbolInventoryTests
    {
        [Fact]
        public void 已下线的旧类型保持不存在()
        {
            Type[] types = typeof(RecycleBinService).Assembly.GetTypes();

            foreach (string name in new[]
                     {
                         "SpecialExtractionRules",
                         "SecondStepResidueState",
                         "GroupAnalysis",
                         "AutoConcurrencyController"
                     })
            {
                Assert.DoesNotContain(types, type => type.Name == name);
            }
        }

        [Fact]
        public void 仍在用的回收站与卷名修路径必须存在()
        {
            Assert.NotNull(typeof(RecycleBinService));
            Assert.NotNull(typeof(VolumeNameRepair));
        }

        [Fact]
        public void 直接删除移动调用点只允许清单内文件新增()
        {
            string repoRoot = FindRepoRoot();
            string sourceRoot = Path.Combine(repoRoot, "src", "ArchiveFixer");

            HashSet<string> allowed = new(StringComparer.OrdinalIgnoreCase);

            var violations = new List<string>();

            foreach (string file in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string text = File.ReadAllText(file);
                string code = System.Text.RegularExpressions.Regex.Replace(
                    text,
                    @"/\*.*?\*/",
                    string.Empty,
                    System.Text.RegularExpressions.RegexOptions.Singleline);
                code = System.Text.RegularExpressions.Regex.Replace(
                    code,
                    @"//.*$",
                    string.Empty,
                    System.Text.RegularExpressions.RegexOptions.Multiline);

                if (!code.Contains("File.Delete", StringComparison.Ordinal)
                    && !code.Contains("File.Move", StringComparison.Ordinal))
                {
                    continue;
                }

                string relative = Path.GetRelativePath(sourceRoot, file).Replace('\\', '/');

                if (relative.StartsWith("Storage/", StringComparison.OrdinalIgnoreCase)
                    || relative.StartsWith("Services/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!allowed.Contains(relative))
                {
                    violations.Add(relative);
                }
            }

            Assert.True(
                violations.Count == 0,
                "新增了直接 File.Delete/File.Move，必须走 Storage/Services 统一入口：" + string.Join("；", violations));
        }

        private static string FindRepoRoot()
        {
            DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);

            for (int i = 0; i < 10 && directory != null; i++, directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ArchiveFixer.slnx")))
                {
                    return directory.FullName;
                }
            }

            throw new DirectoryNotFoundException("找不到 ArchiveFixer.slnx，无法定位仓库根。");
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 结构守卫：不让新文件重新长成“多类型大杂烩”，也不让已登记的大文件继续变胖。
    ///
    /// <para>现有违规在 <c>docs/开发/结构基线.json</c> 中登记；拆分完成后从基线删除。
    /// 这里不追求把老文件一次改完，只保证新债务不再增加。</para>
    /// </summary>
    public sealed class CodeStructureGuardTests
    {
        private const int NewFileLineLimit = 800;
        private const int NewFileTypeLimit = 1;

        private static readonly Regex TopLevelTypePattern = new(
            @"^\s{4}(public|internal|private|protected|file)?\s*(sealed\s+|static\s+|abstract\s+|readonly\s+|partial\s+)*(class|record|record\s+struct|struct|enum|interface)\s+([A-Za-z0-9_]+)",
            RegexOptions.Compiled);

        private static readonly string[] CoreLayerDirectories =
        {
            "Detection", "Engines", "Extraction", "Security", "Storage", "Packing"
        };

        [Fact]
        public void 源文件不能超过已登记行数()
        {
            StructureBaseline baseline = LoadBaseline();
            var failures = new List<string>();

            foreach (string file in EnumerateSourceFiles())
            {
                string relative = RelativeSourcePath(file);
                int lines = File.ReadLines(file).Count();

                if (baseline.LargeFiles.TryGetValue(relative, out int allowed))
                {
                    if (lines > allowed)
                    {
                        failures.Add($"{relative}: {lines} 行 > 基线 {allowed} 行");
                    }
                }
                else if (lines > NewFileLineLimit)
                {
                    failures.Add($"{relative}: 新文件 {lines} 行 > {NewFileLineLimit} 行；请拆分或登记基线（需理由）");
                }
            }

            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }

        [Fact]
        public void 源文件不能超过已登记顶层类型数()
        {
            StructureBaseline baseline = LoadBaseline();
            var failures = new List<string>();

            foreach (string file in EnumerateSourceFiles())
            {
                string relative = RelativeSourcePath(file);
                int types = CountTopLevelTypes(file);

                if (baseline.MultiTypeFiles.TryGetValue(relative, out int allowed))
                {
                    if (types > allowed)
                    {
                        failures.Add($"{relative}: {types} 个顶层类型 > 基线 {allowed} 个");
                    }
                }
                else if (types > NewFileTypeLimit)
                {
                    failures.Add($"{relative}: 新文件有 {types} 个顶层类型；请一类型一文件或登记基线");
                }
            }

            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }

        [Fact]
        public void 核心层不能引用SystemWindows()
        {
            var failures = new List<string>();

            foreach (string directory in CoreLayerDirectories)
            {
                string path = Path.Combine(FindRepoRoot(), "src", "ArchiveFixer", directory);

                foreach (string file in Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories))
                {
                    string text = File.ReadAllText(file);

                    if (text.Contains("using System.Windows", StringComparison.Ordinal))
                    {
                        failures.Add(RelativeSourcePath(file));
                    }
                }
            }

            Assert.True(failures.Count == 0, "核心层不得引用 WPF：" + string.Join("；", failures));
        }

        [Fact]
        public void 基线不能登记不存在的文件()
        {
            StructureBaseline baseline = LoadBaseline();
            var missing = new List<string>();

            foreach (string relative in baseline.LargeFiles.Keys.Concat(baseline.MultiTypeFiles.Keys).Distinct())
            {
                string path = Path.Combine(FindRepoRoot(), "src", "ArchiveFixer", relative.Replace('/', Path.DirectorySeparatorChar));

                if (!File.Exists(path))
                {
                    missing.Add(relative);
                }
            }

            Assert.True(missing.Count == 0, "结构基线有过期条目，请删除：" + string.Join("；", missing));
        }

        private static IEnumerable<string> EnumerateSourceFiles()
        {
            string root = Path.Combine(FindRepoRoot(), "src", "ArchiveFixer");

            foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (!file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && !file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    yield return file;
                }
            }
        }

        private static string RelativeSourcePath(string file)
        {
            string root = Path.Combine(FindRepoRoot(), "src", "ArchiveFixer");
            return Path.GetRelativePath(root, file).Replace('\\', '/');
        }

        private static int CountTopLevelTypes(string file)
        {
            int count = 0;

            foreach (string line in File.ReadLines(file))
            {
                if (TopLevelTypePattern.IsMatch(line))
                {
                    count++;
                }
            }

            return count;
        }

        private static StructureBaseline LoadBaseline()
        {
            string path = Path.Combine(FindRepoRoot(), "docs", "开发", "结构基线.json");
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = document.RootElement;

            var large = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var multi = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (JsonProperty property in root.GetProperty("large_files").EnumerateObject())
            {
                large[property.Name] = property.Value.GetInt32();
            }

            foreach (JsonProperty property in root.GetProperty("multi_type_files").EnumerateObject())
            {
                multi[property.Name] = property.Value.GetInt32();
            }

            return new StructureBaseline(large, multi);
        }

        private static string FindRepoRoot()
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);

            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ArchiveFixer.slnx")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("找不到 ArchiveFixer.slnx");
        }

        private sealed record StructureBaseline(
            IReadOnlyDictionary<string, int> LargeFiles,
            IReadOnlyDictionary<string, int> MultiTypeFiles);
    }
}

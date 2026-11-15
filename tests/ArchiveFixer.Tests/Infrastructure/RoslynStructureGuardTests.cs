using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;
using static ArchiveFixer.Tests.RoslynStructureAnalysis;

namespace ArchiveFixer.Tests;

/// <summary>
/// M3 结构守卫（Roslyn 版）：用语法树检查结构，而不是数 4 空格缩进的正则。
///
/// <para>门禁口径：每文件顶层类型 ≤1；文件名 = 主类型名（partial 允许 <c>Type.后缀</c>）；
/// namespace 与目录一致；partial 类型必须在 <c>docs/开发/结构基线.json</c> 的 <c>partials</c> 登记。
/// 现行结构里的存量违规登记在 <c>multi_type_files</c> / <c>file_name_exceptions</c> /
/// <c>namespace_exceptions</c> / <c>partials</c>，守卫只挡“不新增”。
/// 方法 &gt;150 行只作报告项，用 <c>method_length_report</c> 的“文件+类型+方法”键判“不新增”。</para>
///
/// <para>每条守卫都有内存片段红检（<c>红检_</c> 开头的用例）：分析函数必须真的对违规返回
/// <see cref="Finding"/>，否则守卫本身失效。</para>
/// </summary>
public sealed class RoslynStructureGuardTests
{
    [Fact]
    public void 源文件顶层类型数不超过一()
    {
        StructureBaseline baseline = LoadStructureBaseline();
        IReadOnlyList<ParsedFile> files = LoadSourceFilesForGuard();
        List<Finding> findings = files.SelectMany(file => AnalyzeTypeCount(file, baseline)).ToList();

        Assert.True(findings.Count == 0, Describe("顶层类型数", findings));
    }

    [Fact]
    public void 文件名必须等于主类型名()
    {
        StructureBaseline baseline = LoadStructureBaseline();
        IReadOnlyList<ParsedFile> files = LoadSourceFilesForGuard();
        List<Finding> findings = files.SelectMany(file => AnalyzeFileNames(file, baseline)).ToList();

        Assert.True(findings.Count == 0, Describe("文件名=类型名", findings));
    }

    [Fact]
    public void 命名空间必须与目录一致()
    {
        StructureBaseline baseline = LoadStructureBaseline();
        IReadOnlyList<ParsedFile> files = LoadSourceFilesForGuard();
        List<Finding> findings = files.SelectMany(file => AnalyzeNamespace(file, baseline)).ToList();

        Assert.True(findings.Count == 0, Describe("namespace=目录", findings));
    }

    [Fact]
    public void partial类型必须在基线登记()
    {
        StructureBaseline baseline = LoadStructureBaseline();
        IReadOnlyList<Finding> findings = AnalyzePartialRegistration(LoadSourceFilesForGuard(), baseline);

        Assert.True(findings.Count == 0, Describe("partial 登记", findings));
    }

    [Fact]
    public void 方法长度报告项不得新增()
    {
        StructureBaseline baseline = LoadStructureBaseline();
        IReadOnlyList<MethodLengthItem> current = FindLongMethods(LoadSourceFilesForGuard());
        List<MethodLengthItem> added = current
            .Where(item => !baseline.MethodLengthReport.ContainsKey(item.Key))
            .ToList();

        string detail = string.Join(
            "\n",
            added.Select(item => $"{item.Key} = {item.Lines} 行"));

        Assert.True(
            added.Count == 0,
            $"新增 {added.Count} 个 >150 行方法（基线只允许减少）：\n{detail}\n当前 {current.Count} 个；基线 {baseline.MethodLengthReport.Count} 个");
    }

    [Fact]
    public void 基线新段落必须写明owner原因与退出阶段()
    {
        StructureBaseline baseline = LoadStructureBaseline();
        List<string> missing = new();

        foreach (KeyValuePair<string, PartialRegistration> pair in baseline.Partials)
        {
            if (string.IsNullOrWhiteSpace(pair.Value.Owner))
            {
                missing.Add($"partials.{pair.Key}.owner");
            }

            if (string.IsNullOrWhiteSpace(pair.Value.Reason))
            {
                missing.Add($"partials.{pair.Key}.reason");
            }

            if (string.IsNullOrWhiteSpace(pair.Value.ExitPhase))
            {
                missing.Add($"partials.{pair.Key}.exit_phase");
            }
        }

        foreach (KeyValuePair<string, NamespaceException> pair in baseline.NamespaceExceptions)
        {
            if (string.IsNullOrWhiteSpace(pair.Value.Namespace))
            {
                missing.Add($"namespace_exceptions.{pair.Key}.namespace");
            }

            if (string.IsNullOrWhiteSpace(pair.Value.Owner))
            {
                missing.Add($"namespace_exceptions.{pair.Key}.owner");
            }

            if (string.IsNullOrWhiteSpace(pair.Value.Reason))
            {
                missing.Add($"namespace_exceptions.{pair.Key}.reason");
            }

            if (string.IsNullOrWhiteSpace(pair.Value.ExitPhase))
            {
                missing.Add($"namespace_exceptions.{pair.Key}.exit_phase");
            }
        }

        foreach (KeyValuePair<string, FileNameException> pair in baseline.FileNameExceptions)
        {
            if (string.IsNullOrWhiteSpace(pair.Value.Owner))
            {
                missing.Add($"file_name_exceptions.{pair.Key}.owner");
            }

            if (string.IsNullOrWhiteSpace(pair.Value.Reason))
            {
                missing.Add($"file_name_exceptions.{pair.Key}.reason");
            }

            if (string.IsNullOrWhiteSpace(pair.Value.ExitPhase))
            {
                missing.Add($"file_name_exceptions.{pair.Key}.exit_phase");
            }

            if (pair.Value.Types.Count == 0)
            {
                missing.Add($"file_name_exceptions.{pair.Key}.types");
            }
        }

        Assert.True(missing.Count == 0, "基线条目缺 owner/原因/退出阶段：" + string.Join("；", missing));
    }

    [Fact]
    public void 红检_双顶层类型必须报违规()
    {
        ParsedFile violate = Parse(
            "TwoTypes.cs",
            "namespace ArchiveFixer;\n\ninternal sealed class Alpha\n{\n}\n\ninternal sealed class Beta\n{\n}\n");
        IReadOnlyList<Finding> findings = AnalyzeTypeCount(violate, StructureBaseline.Empty);

        Assert.Contains(findings, finding => finding.Rule == RuleTopLevelTypeCount);

        ParsedFile compliant = Parse(
            "SingleType.cs",
            "namespace ArchiveFixer;\n\ninternal sealed class SingleType\n{\n}\n");
        Assert.Empty(AnalyzeTypeCount(compliant, StructureBaseline.Empty));
    }

    [Fact]
    public void 红检_文件名与类型名不一致必须报违规()
    {
        StructureBaseline empty = StructureBaseline.Empty;
        ParsedFile violate = Parse(
            "Alpha.cs",
            "namespace ArchiveFixer;\n\ninternal sealed class Beta\n{\n}\n");
        IReadOnlyList<Finding> findings = AnalyzeFileNames(violate, empty);

        Assert.Contains(findings, finding => finding.Rule == RuleFileName);

        ParsedFile compliant = Parse(
            "Beta.cs",
            "namespace ArchiveFixer;\n\ninternal sealed class Beta\n{\n}\n");
        Assert.Empty(AnalyzeFileNames(compliant, empty));

        ParsedFile partialSuffix = Parse(
            "Beta.Part.cs",
            "namespace ArchiveFixer;\n\ninternal sealed partial class Beta\n{\n}\n");
        Assert.Empty(AnalyzeFileNames(partialSuffix, empty));

        ParsedFile nonPartialSuffix = Parse(
            "Beta.Part.cs",
            "namespace ArchiveFixer;\n\ninternal sealed class Beta\n{\n}\n");
        Assert.Contains(AnalyzeFileNames(nonPartialSuffix, empty), finding => finding.Rule == RuleFileName);

        // 登记的文件名例外只放行名单里的类型；同一文件里再冒出一个错名类型仍然判红。
        StructureBaseline registered = StructureBaseline.Empty with
        {
            FileNameExceptions = new Dictionary<string, FileNameException>(StringComparer.Ordinal)
            {
                ["Alpha.cs"] = new("测试", "红检登记", "M3", new[] { "Beta" })
            }
        };
        Assert.Empty(AnalyzeFileNames(violate, registered));

        ParsedFile extraType = Parse(
            "Alpha.cs",
            "namespace ArchiveFixer;\n\ninternal sealed class Beta\n{\n}\n\ninternal sealed class Gamma\n{\n}\n");
        Assert.Contains(AnalyzeFileNames(extraType, registered), finding => finding.Rule == RuleFileName);
    }

    [Fact]
    public void 红检_命名空间与目录不一致必须报违规()
    {
        ParsedFile violate = Parse(
            "Models/Widget.cs",
            "namespace ArchiveFixer.Wrong;\n\ninternal sealed class Widget\n{\n}\n");
        IReadOnlyList<Finding> findings = AnalyzeNamespace(violate, StructureBaseline.Empty);

        Assert.Contains(findings, finding => finding.Rule == RuleNamespace);

        ParsedFile compliant = Parse(
            "Models/Widget.cs",
            "namespace ArchiveFixer.Models;\n\ninternal sealed class Widget\n{\n}\n");
        Assert.Empty(AnalyzeNamespace(compliant, StructureBaseline.Empty));
    }

    [Fact]
    public void 红检_已登记目录例外只放行登记值()
    {
        ParsedFile excepted = Parse(
            "Extraction/Artifacts/Widget.cs",
            "namespace ArchiveFixer.Extraction;\n\ninternal sealed class Widget\n{\n}\n");
        Assert.NotEmpty(AnalyzeNamespace(excepted, StructureBaseline.Empty));

        StructureBaseline registered = StructureBaseline.Empty with
        {
            NamespaceExceptions = new Dictionary<string, NamespaceException>(StringComparer.Ordinal)
            {
                ["Extraction/Artifacts/"] = new("ArchiveFixer.Extraction", "测试", "红检登记", "M3")
            }
        };
        Assert.Empty(AnalyzeNamespace(excepted, registered));

        ParsedFile other = Parse(
            "Extraction/Artifacts/Widget.cs",
            "namespace ArchiveFixer.Other;\n\ninternal sealed class Widget\n{\n}\n");
        Assert.NotEmpty(AnalyzeNamespace(other, registered));
    }

    [Fact]
    public void 红检_未登记partial必须报违规()
    {
        ParsedFile violate = Parse(
            "Widget.cs",
            "namespace ArchiveFixer;\n\ninternal sealed partial class Widget\n{\n}\n");
        IReadOnlyList<Finding> findings = AnalyzePartialRegistration(new[] { violate }, StructureBaseline.Empty);

        Assert.Contains(findings, finding => finding.Rule == RulePartialUnregistered);

        StructureBaseline registered = StructureBaseline.Empty with
        {
            Partials = new Dictionary<string, PartialRegistration>(StringComparer.Ordinal)
            {
                ["ArchiveFixer.Widget"] = new("测试", "红检登记", "M3")
            }
        };
        Assert.Empty(AnalyzePartialRegistration(new[] { violate }, registered));
    }

    [Fact]
    public void 红检_151行方法必须进入报告()
    {
        ParsedFile violate = Parse("Long.cs", BuildMethodSource(statementCount: 147));
        IReadOnlyList<MethodLengthItem> items = FindLongMethods(new[] { violate });
        MethodLengthItem item = Assert.Single(items);

        Assert.Equal(151, item.Lines);
        Assert.Equal("ArchiveFixer.Widget", item.TypeName);
        Assert.Equal("Long", item.MethodName);
        Assert.Equal("Long.cs::ArchiveFixer.Widget.Long", item.Key);

        ParsedFile boundary = Parse("Boundary.cs", BuildMethodSource(statementCount: 146));
        Assert.Empty(FindLongMethods(new[] { boundary }));
    }

    private static string BuildMethodSource(int statementCount)
    {
        StringBuilder builder = new();
        builder.Append("namespace ArchiveFixer;\n\ninternal sealed class Widget\n{\n    internal void Long()\n    {\n        int value = 0;\n");
        builder.Append(string.Concat(Enumerable.Repeat("        value++;\n", statementCount)));
        builder.Append("    }\n}\n");
        return builder.ToString();
    }

    private static string Describe(string rule, IReadOnlyList<Finding> findings)
    {
        StringBuilder builder = new();
        builder.Append(rule).Append('：').Append(findings.Count).Append(" 条违规");

        foreach (Finding finding in findings)
        {
            builder.Append('\n').Append(finding.FilePath).Append(" | ").Append(finding.Detail);
        }

        return builder.ToString();
    }

    private static IReadOnlyList<ParsedFile> LoadSourceFilesForGuard()
    {
        return RoslynStructureAnalysis.LoadSourceFiles(Path.Combine(FindRepoRoot(), "src", "ArchiveFixer"));
    }

    private static StructureBaseline LoadStructureBaseline()
    {
        return RoslynStructureAnalysis.LoadBaseline(Path.Combine(FindRepoRoot(), "docs", "开发", "结构基线.json"));
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
}

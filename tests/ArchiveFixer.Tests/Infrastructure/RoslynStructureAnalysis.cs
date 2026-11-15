using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ArchiveFixer.Tests;

/// <summary>
/// Roslyn 结构分析的纯函数部分：输入源码文本/文件，输出违规或报告项。
///
/// <para>守卫测试（<see cref="RoslynStructureGuardTests"/>）用内存片段做红检、用
/// <c>src/ArchiveFixer</c> 全量做绿色门禁；这里不读测试上下文、不写文件，便于单独调用。
/// 基线需要整体重算时，可在测试项目之外链接本文件跑一遍扫描（口径与守卫完全一致）。</para>
/// </summary>
internal static class RoslynStructureAnalysis
{
    internal const int TopLevelTypeLimit = 1;
    internal const int MethodLineLimit = 150;

    internal const string RuleTopLevelTypeCount = "top_level_type_count";
    internal const string RuleFileName = "file_name";
    internal const string RuleNamespace = "namespace";
    internal const string RulePartialUnregistered = "partial_unregistered";

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.CSharp12);

    /// <summary>单个源文件的相对路径（<c>/</c> 分隔，相对 <c>src/ArchiveFixer</c>）与语法树。</summary>
    internal sealed record ParsedFile(string RelativePath, SyntaxTree Tree);

    /// <summary>一条结构违规；<see cref="Rule"/> 取上面的 Rule* 常量。</summary>
    internal sealed record Finding(string Rule, string FilePath, string Detail);

    /// <summary>一个 &gt;150 行方法的报告项；<see cref="Key"/> 是基线的“文件+类型+方法”键。</summary>
    internal sealed record MethodLengthItem(string FilePath, string TypeName, string MethodName, int Lines)
    {
        internal string Key => string.Concat(FilePath, "::", TypeName, ".", MethodName);
    }

    /// <summary>partial 类型登记项：owner/原因/退出阶段。</summary>
    internal sealed record PartialRegistration(string Owner, string Reason, string ExitPhase);

    /// <summary>命名空间例外登记项：该目录允许的命名空间 + owner/原因/退出阶段。</summary>
    internal sealed record NamespaceException(string Namespace, string Owner, string Reason, string ExitPhase);

    /// <summary>
    /// 文件名例外登记项：该文件里允许“文件名 ≠ 类型名”的类型名 + owner/原因/退出阶段。
    /// ⛔ 只放行名单里的类型：同一文件新增一个名字不匹配的类型仍然判红（“不新增”而不是“整个文件免检”）。
    /// </summary>
    internal sealed record FileNameException(string Owner, string Reason, string ExitPhase, IReadOnlyList<string> Types);

    /// <summary>源码里实际出现的 partial 类型统计（用于更新基线）。</summary>
    internal sealed record PartialTypeUse(string FullName, int Parts, IReadOnlyList<string> Files);

    /// <summary><c>docs/开发/结构基线.json</c> 的只读视图。</summary>
    internal sealed record StructureBaseline(
        IReadOnlyDictionary<string, int> LargeFiles,
        IReadOnlyDictionary<string, int> MultiTypeFiles,
        IReadOnlyDictionary<string, PartialRegistration> Partials,
        IReadOnlyDictionary<string, NamespaceException> NamespaceExceptions,
        IReadOnlyDictionary<string, FileNameException> FileNameExceptions,
        IReadOnlyDictionary<string, int> MethodLengthReport)
    {
        internal static StructureBaseline Empty { get; } = new(
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, PartialRegistration>(StringComparer.Ordinal),
            new Dictionary<string, NamespaceException>(StringComparer.Ordinal),
            new Dictionary<string, FileNameException>(StringComparer.Ordinal),
            new Dictionary<string, int>(StringComparer.Ordinal));
    }

    internal static ParsedFile Parse(string relativePath, string source) =>
        new(relativePath, CSharpSyntaxTree.ParseText(source, ParseOptions, relativePath));

    /// <summary>枚举 <c>src/ArchiveFixer</c> 下的手写源文件（跳过 <c>obj</c>/<c>bin</c>），按路径排序保证稳定。</summary>
    internal static IReadOnlyList<ParsedFile> LoadSourceFiles(string sourceRoot)
    {
        List<ParsedFile> files = new();

        foreach (string path in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
                     .OrderBy(value => value, StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(sourceRoot, path).Replace('\\', '/');
            string[] segments = relative.Split('/');

            if (segments.Any(segment => string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase)
                                        || string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            files.Add(Parse(relative, File.ReadAllText(path)));
        }

        return files;
    }

    internal static StructureBaseline LoadBaseline(string baselinePath)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(baselinePath));
        JsonElement root = document.RootElement;

        Dictionary<string, int> largeFiles = ReadIntMap(root, "large_files");
        Dictionary<string, int> multiTypeFiles = ReadIntMap(root, "multi_type_files");
        Dictionary<string, PartialRegistration> partials = new(StringComparer.Ordinal);
        Dictionary<string, NamespaceException> namespaceExceptions = new(StringComparer.Ordinal);
        Dictionary<string, FileNameException> fileNameExceptions = new(StringComparer.Ordinal);
        Dictionary<string, int> methodLengthReport = new(StringComparer.Ordinal);

        if (root.TryGetProperty("partials", out JsonElement partialsElement)
            && partialsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in partialsElement.EnumerateObject())
            {
                partials[property.Name] = new PartialRegistration(
                    ReadString(property.Value, "owner"),
                    ReadString(property.Value, "reason"),
                    ReadString(property.Value, "exit_phase"));
            }
        }

        if (root.TryGetProperty("namespace_exceptions", out JsonElement namespaceElement)
            && namespaceElement.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in namespaceElement.EnumerateObject())
            {
                namespaceExceptions[property.Name] = new NamespaceException(
                    ReadString(property.Value, "namespace"),
                    ReadString(property.Value, "owner"),
                    ReadString(property.Value, "reason"),
                    ReadString(property.Value, "exit_phase"));
            }
        }

        if (root.TryGetProperty("file_name_exceptions", out JsonElement fileNameElement)
            && fileNameElement.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in fileNameElement.EnumerateObject())
            {
                fileNameExceptions[property.Name] = new FileNameException(
                    ReadString(property.Value, "owner"),
                    ReadString(property.Value, "reason"),
                    ReadString(property.Value, "exit_phase"),
                    ReadStringArray(property.Value, "types"));
            }
        }

        if (root.TryGetProperty("method_length_report", out JsonElement reportElement)
            && reportElement.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in reportElement.EnumerateObject())
            {
                methodLengthReport[property.Name] = property.Value.GetInt32();
            }
        }

        return new StructureBaseline(largeFiles, multiTypeFiles, partials, namespaceExceptions, fileNameExceptions, methodLengthReport);
    }

    /// <summary>每个文件顶层类型数 ≤1；<c>multi_type_files</c> 里登记的文件按登记值放行（只降不升）。</summary>
    internal static IReadOnlyList<Finding> AnalyzeTypeCount(ParsedFile file, StructureBaseline baseline)
    {
        int count = EnumerateTopLevelTypes(file.Tree).Count();

        if (baseline.MultiTypeFiles.TryGetValue(file.RelativePath, out int allowed))
        {
            return count <= allowed
                ? Array.Empty<Finding>()
                : new[] { new Finding(RuleTopLevelTypeCount, file.RelativePath, $"{count} 个顶层类型 > 基线 {allowed} 个") };
        }

        return count <= TopLevelTypeLimit
            ? Array.Empty<Finding>()
            : new[] { new Finding(RuleTopLevelTypeCount, file.RelativePath, $"新文件有 {count} 个顶层类型 > {TopLevelTypeLimit}；请一类型一文件或登记 multi_type_files") };
    }

    /// <summary>
    /// 文件名 = 主类型名；partial 允许 <c>Type.后缀.cs</c>，非 partial 不允许点号后缀。
    /// <c>file_name_exceptions</c> 登记的文件只放行名单里的类型名，名单外的名字不匹配仍然判红。
    /// </summary>
    internal static IReadOnlyList<Finding> AnalyzeFileNames(ParsedFile file, StructureBaseline baseline)
    {
        IReadOnlyList<string> mismatches = CollectFileNameMismatchTypes(file);

        if (mismatches.Count == 0)
        {
            return Array.Empty<Finding>();
        }

        string stem = FileStem(file.RelativePath);
        IReadOnlyList<string> allowed = baseline.FileNameExceptions.TryGetValue(file.RelativePath, out FileNameException? exception)
            ? exception.Types
            : Array.Empty<string>();

        List<Finding> findings = new();

        foreach (string typeName in mismatches)
        {
            if (allowed.Contains(typeName, StringComparer.Ordinal))
            {
                continue;
            }

            string hint = stem.StartsWith(typeName + ".", StringComparison.Ordinal)
                ? "（.后缀只允许 partial 类型）"
                : string.Empty;

            findings.Add(new Finding(RuleFileName, file.RelativePath, $"文件名 {stem} ≠ 主类型 {typeName}{hint}"));
        }

        return findings;
    }

    /// <summary>该文件里“文件名 ≠ 类型名”的类型名列表（已排除 partial 的 <c>Type.后缀</c> 合法形态）。</summary>
    internal static IReadOnlyList<string> CollectFileNameMismatchTypes(ParsedFile file)
    {
        List<string> mismatch = new();
        string stem = FileStem(file.RelativePath);

        foreach (SyntaxNode node in EnumerateTopLevelTypes(file.Tree))
        {
            string typeName = TypeNameOf(node);

            if (string.Equals(stem, typeName, StringComparison.Ordinal))
            {
                continue;
            }

            bool partialSuffix = IsPartial(node)
                                 && stem.StartsWith(typeName + ".", StringComparison.Ordinal);

            if (!partialSuffix)
            {
                mismatch.Add(typeName);
            }
        }

        return mismatch;
    }

    /// <summary>
    /// namespace 与目录一致（<c>ArchiveFixer.&lt;dir&gt;</c>）。<c>namespace_exceptions</c> 里登记的目录
    /// 按登记值放行（登记值本身也要逐文件核对，防止例外目录里再长出错命名空间）。
    /// </summary>
    internal static IReadOnlyList<Finding> AnalyzeNamespace(ParsedFile file, StructureBaseline baseline)
    {
        IReadOnlyList<string> declared = GetDeclaredNamespaces(file.Tree);

        if (declared.Count == 0)
        {
            // AssemblyInfo / global usings 这类没有命名空间的文件不参与目录一致性检查。
            return Array.Empty<Finding>();
        }

        string directory = GetDirectory(file.RelativePath);
        string key = NamespaceExceptionKey(directory);

        if (baseline.NamespaceExceptions.TryGetValue(key, out NamespaceException? exception))
        {
            return declared
                .Where(name => !string.Equals(name, exception.Namespace, StringComparison.Ordinal))
                .Select(name => new Finding(RuleNamespace, file.RelativePath, $"已登记目录 {directory} 只允许 {exception.Namespace}，实际 {name}"))
                .ToList();
        }

        string expected = ExpectedNamespace(directory);
        return declared
            .Where(name => !string.Equals(name, expected, StringComparison.Ordinal))
            .Select(name => new Finding(RuleNamespace, file.RelativePath, $"namespace {name} ≠ 目录要求 {expected}"))
            .ToList();
    }

    /// <summary>partial 类型必须在 <c>partials</c> 登记；同一类型只在第一条违规文件上报一次。</summary>
    internal static IReadOnlyList<Finding> AnalyzePartialRegistration(IEnumerable<ParsedFile> files, StructureBaseline baseline)
    {
        List<Finding> findings = new();
        HashSet<string> reported = new(StringComparer.Ordinal);

        foreach (ParsedFile file in files)
        {
            foreach (TypeDeclarationSyntax type in file.Tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                if (!type.Modifiers.Any(SyntaxKind.PartialKeyword))
                {
                    continue;
                }

                string fullName = FullTypeName(type);

                if (baseline.Partials.ContainsKey(fullName) || !reported.Add(fullName))
                {
                    continue;
                }

                findings.Add(new Finding(RulePartialUnregistered, file.RelativePath, $"partial 类型 {fullName} 未在 partials 登记（需 owner/原因/退出阶段）"));
            }
        }

        return findings;
    }

    internal static IReadOnlyList<PartialTypeUse> CollectPartialTypes(IEnumerable<ParsedFile> files)
    {
        Dictionary<string, List<string>> byName = new(StringComparer.Ordinal);

        foreach (ParsedFile file in files)
        {
            foreach (TypeDeclarationSyntax type in file.Tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                if (!type.Modifiers.Any(SyntaxKind.PartialKeyword))
                {
                    continue;
                }

                string fullName = FullTypeName(type);

                if (!byName.TryGetValue(fullName, out List<string>? fileList))
                {
                    fileList = new List<string>();
                    byName[fullName] = fileList;
                }

                if (!fileList.Contains(file.RelativePath, StringComparer.Ordinal))
                {
                    fileList.Add(file.RelativePath);
                }
            }
        }

        return byName
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new PartialTypeUse(pair.Key, pair.Value.Count, pair.Value.OrderBy(value => value, StringComparer.Ordinal).ToList()))
            .ToList();
    }

    /// <summary>&gt;150 行方法：只产报告项，不判红；是否新增由守卫测试对照基线判定。</summary>
    internal static IReadOnlyList<MethodLengthItem> FindLongMethods(IEnumerable<ParsedFile> files)
    {
        List<MethodLengthItem> items = new();

        foreach (ParsedFile file in files)
        {
            foreach (MethodDeclarationSyntax method in file.Tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                FileLinePositionSpan span = method.GetLocation().GetLineSpan();
                int lines = span.EndLinePosition.Line - span.StartLinePosition.Line + 1;

                if (lines <= MethodLineLimit)
                {
                    continue;
                }

                TypeDeclarationSyntax? containingType = method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
                string typeName = containingType is null ? string.Empty : FullTypeName(containingType);
                items.Add(new MethodLengthItem(file.RelativePath, typeName, method.Identifier.ValueText, lines));
            }
        }

        return items
            .OrderBy(item => item.FilePath, StringComparer.Ordinal)
            .ThenBy(item => item.TypeName, StringComparer.Ordinal)
            .ThenBy(item => item.MethodName, StringComparer.Ordinal)
            .ThenBy(item => item.Lines)
            .ToList();
    }

    internal static string GetDeclaredNamespaceOrEmpty(SyntaxTree tree)
    {
        IReadOnlyList<string> declared = GetDeclaredNamespaces(tree);
        return declared.Count == 0 ? string.Empty : declared[0];
    }

    internal static IReadOnlyList<string> GetDeclaredNamespaces(SyntaxTree tree)
    {
        return tree.GetRoot()
            .DescendantNodes()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .Where(declaration => declaration.Parent is CompilationUnitSyntax)
            .Select(declaration => declaration.Name.ToString())
            .ToList();
    }

    internal static string GetDirectory(string relativePath)
    {
        string normalized = relativePath.Replace('\\', '/');
        int slash = normalized.LastIndexOf('/');
        return slash < 0 ? string.Empty : normalized[..slash];
    }

    internal static string ExpectedNamespace(string directory)
    {
        return directory.Length == 0 ? "ArchiveFixer" : "ArchiveFixer." + directory.Replace('/', '.');
    }

    internal static string NamespaceExceptionKey(string directory)
    {
        return directory.Length == 0 ? string.Empty : directory + "/";
    }

    internal static string FileStem(string relativePath)
    {
        string normalized = relativePath.Replace('\\', '/');
        string name = normalized[(normalized.LastIndexOf('/') + 1)..];

        if (name.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase))
        {
            return name[..^".xaml.cs".Length];
        }

        return name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? name[..^".cs".Length] : name;
    }

    internal static string FullTypeName(TypeDeclarationSyntax type)
    {
        List<string> parts = new() { type.Identifier.ValueText };

        for (SyntaxNode? node = type.Parent; node is not null; node = node.Parent)
        {
            if (node is TypeDeclarationSyntax outerType)
            {
                parts.Insert(0, outerType.Identifier.ValueText);
            }
            else if (node is BaseNamespaceDeclarationSyntax namespaceDeclaration)
            {
                parts.Insert(0, namespaceDeclaration.Name.ToString());
            }
        }

        return string.Join(".", parts);
    }

    private static IEnumerable<SyntaxNode> EnumerateTopLevelTypes(SyntaxTree tree)
    {
        return tree.GetRoot()
            .DescendantNodes()
            .Where(node => node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax)
            .Where(node => node.Parent is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax);
    }

    private static string TypeNameOf(SyntaxNode node)
    {
        return node switch
        {
            BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
            DelegateDeclarationSyntax declaration => declaration.Identifier.ValueText,
            _ => throw new InvalidOperationException("不是顶层类型节点：" + node.Kind())
        };
    }

    private static bool IsPartial(SyntaxNode node)
    {
        return node is BaseTypeDeclarationSyntax type && type.Modifiers.Any(SyntaxKind.PartialKeyword);
    }

    private static Dictionary<string, int> ReadIntMap(JsonElement root, string propertyName)
    {
        Dictionary<string, int> map = new(StringComparer.OrdinalIgnoreCase);

        if (root.TryGetProperty(propertyName, out JsonElement element) && element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                map[property.Name] = property.Value.GetInt32();
            }
        }

        return map;
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property) || property.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return property.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty)
            .ToList();
    }
}

using ArchiveFixer.Models;
using ArchiveFixer.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Xml.Linq;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 「默认双向绑定」这件事的**廉价静态检查**（缺陷 3 的补充手段，2026-09-22）。
    ///
    /// <para><b>要防的是什么</b>：WPF 里有些依赖属性的元数据写着
    /// <c>BindsTwoWayByDefault</c> —— 最出名的就是 <see cref="Run.TextProperty"/>。
    /// 于是 XAML 里写 <c>&lt;Run Text="{Binding TimeText}"/&gt;</c> 而 <c>TimeText</c> 是只读计算属性时，
    /// **绑定一建立就抛</c>XamlParseException</c>（"无法对只读属性进行 TwoWay 绑定"）。
    /// 这类错误的可怕之处在于它**只在模板/控件实例化时才发作**：
    /// <c>dotnet build</c> 全绿，不实例化的"无界面 XAML 校验宿主"也全绿 —— 2026-09-22 的
    /// Release 版就是这么"启动即崩"的（提交 <c>6838940</c> 把那些绑定改成了 <c>Mode=OneWay</c>）。</para>
    ///
    /// <para><b>这条检查怎么判</b>：扫描所有 <c>.xaml</c>，对每一个 <c>{Binding …}</c>：</para>
    /// <list type="number">
    /// <item><description>目标属性是不是"默认双向"—— 用**WPF 自己的元数据**判断
    /// （<c>DependencyProperty.GetMetadata(type).BindsTwoWayByDefault</c>），不维护容易过期的白名单；</description></item>
    /// <item><description>绑定里有没有显式 <c>Mode=</c>；</description></item>
    /// <item><description>绑定路径的**最后一段**在应用自己的类型里是不是只读（没有任何公开 setter）。</description></item>
    /// </list>
    /// <para>三条同时成立才是问题。所以它是"零误报"口径：正常写法（只读属性 + 显式 OneWay）不会报。</para>
    ///
    /// <para><b>它不会静默通过</b>：仓库/源码目录找不到、XAML 读不了、一条绑定都没扫到，
    /// 都会直接让用例变红（见下面几个 <c>Assert</c>），而不是"扫了个寂寞然后报绿"。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class XamlBindingSafetyTests
    {
        [Fact]
        public void 所有XAML里都没有_只读属性上的默认双向绑定()
        {
            IReadOnlyList<string> findings = XamlBindingScan.ScanRepository(out int xamlFiles, out int bindings);

            Assert.True(xamlFiles > 0, "一份 .xaml 都没扫到 —— 检查器失去意义，必须当成失败");
            Assert.True(bindings > 0, "一条绑定都没扫到 —— 检查器失去意义，必须当成失败");

            Assert.True(
                findings.Count == 0,
                "发现只读属性上的默认双向绑定（程序会在**模板实例化时**崩）："
                + Environment.NewLine
                + string.Join(Environment.NewLine, findings));
        }

        [Fact]
        public void 检查器本身有效_能抓到Run点Text绑到只读属性()
        {
            /*
             * 正向对照：把 2026-09-22 那次崩溃的原样搬进来（Mode 故意不写）。
             * 没有这条对照，"零发现"既可能是真的干净，也可能是检查器根本没在工作。
             */
            const string bad = """
                <TextBlock xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                    <Run Text="{Binding TimeText}" />
                </TextBlock>
                """;

            IReadOnlyList<string> findings = XamlBindingScan.ScanText("对照样本.xaml", bad, out int scanned);

            Assert.Equal(1, scanned);
            Assert.Single(findings);
            Assert.Contains("TimeText", findings[0], StringComparison.Ordinal);
            Assert.Contains("Mode=OneWay", findings[0], StringComparison.Ordinal);
        }

        [Fact]
        public void 检查器本身有效_写了Mode就不再报()
        {
            const string good = """
                <TextBlock xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                    <Run Text="{Binding TimeText, Mode=OneWay, StringFormat=[{0}]}" />
                    <Run Text="{Binding Level}" />
                </TextBlock>
                """;

            IReadOnlyList<string> findings = XamlBindingScan.ScanText("对照样本.xaml", good, out int scanned);

            // Level 是可写的，TimeText 有显式 Mode → 两条都不该报。
            Assert.Equal(2, scanned);
            Assert.Empty(findings);
        }

        [Fact]
        public void 检查器只认真正的默认双向属性_普通OneWay目标不报()
        {
            /*
             * TextBlock.Text 默认是 OneWay（不是 Run.Text 那种坑）：
             * 即使绑到只读属性也不该报 —— 否则这条检查会因为满仓的只读展示属性而变成噪音，
             * 噪音最后一定会被"关掉"，那才是真正的损失。
             */
            const string textBlockBinding = """
                <TextBlock xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                           Text="{Binding TimeText}" />
                """;

            IReadOnlyList<string> findings = XamlBindingScan.ScanText("对照样本.xaml", textBlockBinding, out _);

            Assert.Empty(findings);
        }

        [Fact]
        public void 检查器覆盖到了日志模板里每一条Run点Text绑定()
        {
            /*
             * 这条是"覆盖度"断言：2026-09-22 崩的就是 `<Run Text="{Binding TimeText}"/>`，
             * 所以检查器必须真的把这类写法一条不落地看进去。只断言"零发现"是不够的 ——
             * 一个悄悄失效的检查器同样会报绿。
             *
             * 2026-09-24 第 11 条之后日志模板搬进了①任务页（Views\Tabs\TaskTab.xaml），
             * 独立日志窗口（Views\LogWindow.xaml）里还有一份同样的模板 —— 两处都要扫。
             */
            foreach ((string relativePath, int expectedMinimum) in new[]
                     {
                         // ①任务页的日志模板（3 条）+ 「后缀状态」列的 ToolTip（4 条）
                         (Path.Combine("src", "ArchiveFixer", "Views", "Tabs", "TaskTab.xaml"), 7),

                         // 独立日志窗口（第 18 条）：时间 / 级别 / 正文
                         (Path.Combine("src", "ArchiveFixer", "Views", "LogWindow.xaml"), 3)
                     })
            {
                string path = Path.Combine(XamlBindingScan.RepositoryRoot, relativePath);
                string text = File.ReadAllText(path);

                int runBindings = Regex.Matches(text, "<Run[^>]*Text=\"\\{Binding").Count;

                Assert.True(
                    runBindings >= expectedMinimum,
                    $"{relativePath} 里的 Run.Text 绑定少得反常（{runBindings} 条，至少应有 {expectedMinimum} 条）—— 检查器可能已经扫不到它们了");

                IReadOnlyList<string> findings = XamlBindingScan.ScanText(relativePath, text, out int scanned);

                Assert.Empty(findings);
                Assert.True(
                    scanned >= runBindings,
                    $"扫到的默认双向绑定（{scanned} 条）少于 Run.Text 绑定数（{runBindings} 条）—— Run.Text 的元数据判定失效了");
            }
        }

        [Fact]
        public void 界面蓝字与命令提示共用同一个文案来源()
        {
            string taskTab = File.ReadAllText(
                Path.Combine(XamlBindingScan.RepositoryRoot, "src", "ArchiveFixer", "Views", "Tabs", "TaskTab.xaml"));

            // 蓝字不许再写死：它必须引用 Models/StatusText.SelectionScopeHint（任务列表在①任务页里）。
            Assert.Contains("StatusText.SelectionScopeHint", taskTab, StringComparison.Ordinal);

            Assert.Contains("勾选", StatusText.SelectionScopeHint, StringComparison.Ordinal);

            /*
             * 口径必须写成"一律只认勾选"（用户 2026-09-24 第 12 条亲自拍板）。
             * ⛔ 这里曾经断言过"点中"二字 —— 那两个字属于已经被否掉的"没勾就按当前行办"兜底，
             * 现在必须彻底消失，免得界面又在教用户一条不存在的规则。
             */
            Assert.Contains("只认勾选", StatusText.SelectionScopeHint, StringComparison.Ordinal);
            Assert.DoesNotContain("按当前点中的那一行办", StatusText.SelectionScopeHint, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 「只读 + 默认双向 + 没写 Mode」的扫描器。
    ///
    /// <para>
    /// 独立成一个类是为了能被<b>两条路</b>用到：静态检查用例（扫仓库），
    /// 以及窗口显示用例在"这台机器显示不了窗口"时的补偿检查 —— 环境退化时也不能完全没有覆盖。
    /// </para>
    /// </summary>
    internal static class XamlBindingScan
    {
        /// <summary>显式写了 Mode= 的绑定：这一条就轮不到"默认值"来决定了。</summary>
        private static readonly Regex ExplicitModePattern = new(
            @"(?<![A-Za-z])Mode\s*=",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly string[] WpfNamespaces =
        {
            "System.Windows",
            "System.Windows.Controls",
            "System.Windows.Controls.Primitives",
            "System.Windows.Documents",
            "System.Windows.Shapes",
            "System.Windows.Media"
        };

        private static string? _repositoryRoot;

        /// <summary>仓库根目录（找不到就抛 —— 检查器不许"找不到就认为没问题"）。</summary>
        internal static string RepositoryRoot => _repositoryRoot ??= ResolveRepositoryRoot();

        /// <summary>扫描仓库里的全部 XAML。</summary>
        internal static IReadOnlyList<string> ScanRepository(out int xamlFileCount, out int bindingCount)
        {
            string appDirectory = Path.Combine(RepositoryRoot, "src", "ArchiveFixer");

            List<string> files = Directory.Exists(appDirectory)
                ? Directory.EnumerateFiles(appDirectory, "*.xaml", SearchOption.AllDirectories)
                    .Where(path => !IsBuildOutput(path))
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : new List<string>();

            var findings = new List<string>();
            int bindings = 0;

            foreach (string file in files)
            {
                string text;

                try
                {
                    text = File.ReadAllText(file, Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    findings.Add($"读不了 {Relative(file)}：{ex.Message}");
                    continue;
                }

                findings.AddRange(ScanText(Relative(file), text, out int scanned));
                bindings += scanned;
            }

            xamlFileCount = files.Count;
            bindingCount = bindings;

            return findings;
        }

        /// <summary>
        /// 扫一段 XAML 文本。<paramref name="bindingCount"/> 返回**被真正检查过**的绑定条数
        /// （目标属性是默认双向的那些），用于让"一条都没扫到"变成可见的失败而不是静默通过。
        /// </summary>
        internal static IReadOnlyList<string> ScanText(string displayName, string xaml, out int bindingCount)
        {
            var findings = new List<string>();
            bindingCount = 0;

            XDocument document;

            try
            {
                document = XDocument.Parse(xaml);
            }
            catch (Exception ex)
            {
                findings.Add($"{displayName}：XAML 解析失败（{ex.Message}）");
                return findings;
            }

            IReadOnlySet<string> readOnlyOnly = ReadOnlyOnlyPropertyNames;

            foreach (XElement element in document.Descendants())
            {
                foreach (XAttribute attribute in element.Attributes())
                {
                    if (attribute.IsNamespaceDeclaration)
                    {
                        continue;
                    }

                    string value = attribute.Value;

                    if (value.IndexOf("{Binding", StringComparison.Ordinal) < 0)
                    {
                        continue;
                    }

                    if (!IsTwoWayByDefault(element.Name.LocalName, attribute.Name.LocalName))
                    {
                        continue;
                    }

                    bindingCount++;

                    if (ExplicitModePattern.IsMatch(value))
                    {
                        continue;
                    }

                    string path = ExtractBindingPath(value);
                    string lastSegment = LastSegment(path);

                    if (lastSegment.Length == 0 || !readOnlyOnly.Contains(lastSegment))
                    {
                        continue;
                    }

                    findings.Add(
                        $"{displayName}：<{element.Name.LocalName} {attribute.Name.LocalName}=\"{{Binding {path}}}\">"
                        + $" —— {element.Name.LocalName}.{attribute.Name.LocalName} 默认就是 TwoWay，"
                        + $"而 {lastSegment} 是只读属性：模板实例化时会抛 XamlParseException（请写 Mode=OneWay）");
                }
            }

            return findings;
        }

        /// <summary>
        /// 目标属性在 WPF 元数据里是不是"默认双向"。
        ///
        /// 用 <see cref="DependencyProperty.GetMetadata(Type)"/> 直接问 WPF，而不是维护一张
        /// "哪些属性是 TwoWay"的白名单 —— 白名单会过期，而元数据不会说谎。
        /// 类型认不出来时返回 false（宁可漏报也不误报：误报会把这条检查逼成噪音）。
        /// </summary>
        private static bool IsTwoWayByDefault(string elementName, string propertyName)
        {
            Type? elementType = ResolveWpfType(elementName);

            if (elementType == null)
            {
                return false;
            }

            FieldInfo? field = elementType.GetField(
                propertyName + "Property",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);

            if (field?.GetValue(null) is not DependencyProperty property)
            {
                return false;
            }

            return property.GetMetadata(elementType) is FrameworkPropertyMetadata metadata
                   && metadata.BindsTwoWayByDefault;
        }

        private static Type? ResolveWpfType(string elementName)
        {
            foreach (string ns in WpfNamespaces)
            {
                Type? type = Type.GetType($"{ns}.{elementName}, PresentationFramework", throwOnError: false)
                              ?? Type.GetType($"{ns}.{elementName}, PresentationCore", throwOnError: false);

                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }

        /// <summary>
        /// 应用自己的类型里"只读、且没有任何类型把它写成可写"的属性名。
        ///
        /// 后半个条件是防误报：同名属性只要在**某处**可写，就不能断言"绑定到它一定不合法"。
        /// </summary>
        private static IReadOnlySet<string> ReadOnlyOnlyPropertyNames
        {
            get
            {
                if (_readOnlyOnly != null)
                {
                    return _readOnlyOnly;
                }

                var readOnly = new HashSet<string>(StringComparer.Ordinal);
                var writable = new HashSet<string>(StringComparer.Ordinal);

                foreach (Type type in typeof(MainViewModel).Assembly.GetTypes())
                {
                    foreach (PropertyInfo property in type.GetProperties(
                                 BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
                    {
                        if (property.SetMethod is { IsPublic: true })
                        {
                            writable.Add(property.Name);
                        }
                        else
                        {
                            readOnly.Add(property.Name);
                        }
                    }
                }

                readOnly.ExceptWith(writable);

                _readOnlyOnly = readOnly;
                return _readOnlyOnly;
            }
        }

        private static IReadOnlySet<string>? _readOnlyOnly;

        /// <summary>从 <c>{Binding …}</c> 里取出绑定路径（支持 <c>{Binding X}</c> 与 <c>{Binding Path=X}</c>）。</summary>
        internal static string ExtractBindingPath(string markupExtension)
        {
            const string head = "{Binding";

            int start = markupExtension.IndexOf(head, StringComparison.Ordinal);

            if (start < 0)
            {
                return string.Empty;
            }

            int depth = 0;
            int end = -1;

            for (int i = start; i < markupExtension.Length; i++)
            {
                if (markupExtension[i] == '{')
                {
                    depth++;
                }
                else if (markupExtension[i] == '}')
                {
                    depth--;

                    if (depth == 0)
                    {
                        end = i;
                        break;
                    }
                }
            }

            if (end < 0)
            {
                return string.Empty;
            }

            string body = markupExtension.Substring(start + head.Length, end - start - head.Length);

            foreach (string part in SplitTopLevel(body))
            {
                string trimmed = part.Trim();

                if (trimmed.Length == 0)
                {
                    continue;
                }

                int equals = trimmed.IndexOf('=');

                if (equals < 0)
                {
                    // {Binding TimeText}
                    return trimmed;
                }

                if (trimmed[..equals].Trim().Equals("Path", StringComparison.OrdinalIgnoreCase))
                {
                    return trimmed[(equals + 1)..].Trim();
                }
            }

            return string.Empty;
        }

        /// <summary>按顶层逗号切分（忽略嵌套 <c>{…}</c> 里的逗号，例如 RelativeSource）。</summary>
        private static IEnumerable<string> SplitTopLevel(string body)
        {
            var current = new StringBuilder();
            int depth = 0;

            foreach (char c in body)
            {
                if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                }

                if (c == ',' && depth == 0)
                {
                    yield return current.ToString();
                    current.Clear();
                    continue;
                }

                current.Append(c);
            }

            yield return current.ToString();
        }

        private static string LastSegment(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            string cleaned = path.Trim();

            int bracket = cleaned.IndexOf('[');

            if (bracket >= 0)
            {
                cleaned = cleaned[..bracket];
            }

            int dot = cleaned.LastIndexOf('.');

            return dot >= 0 ? cleaned[(dot + 1)..].Trim() : cleaned.Trim();
        }

        private static bool IsBuildOutput(string path)
        {
            string normalized = path.Replace('\\', '/');

            return normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
                   || normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase);
        }

        private static string Relative(string path)
        {
            string root = RepositoryRoot;

            return path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? path[(root.Length + 1)..]
                : path;
        }

        /// <summary>
        /// 仓库根：优先用**编译期就写下来的源码路径**（本文件就在 <c>&lt;repo&gt;\ArchiveFixer.Tests\</c> 下），
        /// 再退回"从输出目录往上找 ArchiveFixer.slnx"。两条都失败就抛 —— 找不到就必须是红的。
        /// </summary>
        private static string ResolveRepositoryRoot()
        {
            string? sourceFile = ThisSourceFile();

            if (!string.IsNullOrWhiteSpace(sourceFile))
            {
                string? testsDirectory = Path.GetDirectoryName(sourceFile);
                string? root = testsDirectory == null ? null : Path.GetDirectoryName(testsDirectory);

                if (!string.IsNullOrWhiteSpace(root) && File.Exists(Path.Combine(root, "ArchiveFixer.slnx")))
                {
                    return root;
                }
            }

            DirectoryInfo? current = new(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));

            while (current != null)
            {
                if (File.Exists(Path.Combine(current.FullName, "ArchiveFixer.slnx")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new InvalidOperationException(
                "找不到仓库根目录（既没有可用的编译期源码路径，也没能从输出目录往上找到 ArchiveFixer.slnx）——"
                + "静态绑定检查无法进行，不允许静默通过。");
        }

        private static string? ThisSourceFile([CallerFilePath] string path = "") => path;
    }
}

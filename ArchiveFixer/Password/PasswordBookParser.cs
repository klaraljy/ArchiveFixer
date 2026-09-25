using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using ArchiveFixer.Helpers;

namespace ArchiveFixer.Password
{
    /// <summary>
    /// 密码本条目的类型。
    /// </summary>
    public enum PasswordEntryKind
    {
        /// <summary>列表式：一行一个密码，没有"名称"这一说。</summary>
        List,

        /// <summary>映射式：<c>名称:密码</c>，可以用归档名去匹配"名称"。</summary>
        Mapped,

        /// <summary>显式的空密码行（文件里写了"空"/"空密码"这类标记）。</summary>
        Empty
    }

    /// <summary>
    /// 密码本里的一条候选密码。
    ///
    /// 隐私约定（AGENTS.md §8）：<see cref="Password"/> 是明文，只能在内存里流转；
    /// 日志、报告、异常信息里一律不得输出它，也不要依赖默认的 ToString 打印本类型。
    /// </summary>
    public sealed class PasswordEntry
    {
        /// <summary>明文密码（仅内存）。列表式是整行原文，映射式是冒号右侧原文。</summary>
        public string Password { get; init; } = string.Empty;

        /// <summary>映射式的"名称"；列表式为 null。名称已 Trim（名称前后空格是排版噪音）。</summary>
        public string? Name { get; init; }

        /// <summary>条目类型。</summary>
        public PasswordEntryKind Kind { get; init; }

        /// <summary>在密码本文件里的行号，1 起。Parse 无行号概念时按拆分顺序编号。</summary>
        public int LineNumber { get; init; }

        /// <summary>给界面看的说明，例如"来自第 12 行"。不含密码内容。</summary>
        public string? Remark { get; init; }

        /// <summary>
        /// 映射式条目的**整行原文**（例：<c>abc:123</c>）；列表式与空密码标记为 null。
        ///
        /// <para><b>为什么必须留着它</b>（2026-09-25 第 30 条）：「映射式还是列表式」只能靠"这一行里有没有冒号"
        /// 来猜，而**列表式的密码本身完全可能带冒号** —— <c>abc:123</c>、<c>www.xxx.com:8888</c>
        /// 这类资源站密码很常见。那种情况下冒号右侧根本不是密码，**整行才是**。
        /// 于是候选链要"两种理解都试"（见 <c>PasswordService.GetPasswordCandidates</c> 的 BookRawLine 一档），
        /// 而密码列表界面上一条都不多（整行只在候选链里出现）。</para>
        /// </summary>
        public string? RawLine { get; init; }

        /// <summary>
        /// 脱敏显示，避免手滑把本对象丢进日志或绑定到界面时泄露明文。
        /// </summary>
        public override string ToString()
        {
            string body = Password.Length == 0 ? "空密码" : "******";

            return Name == null
                ? $"{Kind} 第 {LineNumber} 行：{body}"
                : $"{Kind} 第 {LineNumber} 行（{Name}）：{body}";
        }
    }

    /// <summary>
    /// 密码本解析结果。
    /// </summary>
    public sealed class PasswordBookParseResult
    {
        /// <summary>解析出来的条目，按文件中出现顺序。</summary>
        public IReadOnlyList<PasswordEntry> Entries { get; init; } = Array.Empty<PasswordEntry>();

        /// <summary>给用户看的警告（空密码行、重复行、编码回退等）。警告里不得出现密码原文。</summary>
        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

        /// <summary>被跳过的行数（重复、空密码等）。注释行与空行不计入。</summary>
        public int SkippedLineCount { get; init; }
    }

    /// <summary>
    /// 密码本文件解析器（AGENTS.md §9.1：列表式与映射式两种写法都必须支持）。
    ///
    /// 两条最容易踩的规则，先说清楚为什么：
    ///
    /// 1. <b>密码绝不 Trim</b>。密码本身可能真的以空格开头或结尾（" abc" 和 "abc" 是两个不同的密码）。
    ///    密码本里唯一允许被吃掉空白的只有映射式的"名称"——名称是给人看的排版内容，不是密钥。
    ///    代价是纯空格行会被当成"只有空白字符的行"跳过（见规则 2），这是有意的取舍：
    ///    宁可让用户用"空"/"空密码"标记显式表达空密码，也不要把一次误判变成全表 Trim。
    ///
    /// 2. <b>映射式只按第一个冒号切分</b>。密码里本身可能带冒号（如 <c>账号:口令:123</c>，密码是 <c>口令:123</c>），
    ///    按最后一个冒号或全部冒号切分都会把密码切坏。
    /// </summary>
    public static class PasswordBookParser
    {
        /// <summary>同时认 \r\n、\n、\r 三种换行。</summary>
        private static readonly Regex LineSplitter = new Regex("\r\n|\n|\r", RegexOptions.Compiled);

        /// <summary>显式空密码标记：忽略大小写、忽略首尾空白。</summary>
        private static readonly HashSet<string> EmptyPasswordMarkers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "空",
            "空密码",
            "<空>",
            "<empty>",
        };

        /// <summary>空密码标记命中的条目说明。</summary>
        private const string EmptyPasswordRemark = "空密码（按文件内容）";

        /// <summary>文件不存在时给出的警告。</summary>
        private const string FileMissingWarning = "密码本文件不存在，已跳过。";

        /// <summary>UTF-8 解码失败并回退到 GB18030 时的警告。</summary>
        private const string Gb18030FallbackWarning = "密码本文件不是有效的 UTF-8，已按 GB18030（GBK 超集）重新读取。";

        /// <summary>连 GB18030 都取不到时的警告。</summary>
        private const string DefaultEncodingFallbackWarning = "未能识别编码，已按系统默认编码读取。";

        /// <summary>
        /// 从文本内容解析密码本。
        /// </summary>
        /// <param name="content">整份文件内容。null / 空串都返回空结果，不抛异常。</param>
        public static PasswordBookParseResult Parse(string? content)
        {
            var entries = new List<PasswordEntry>();
            var warnings = new List<string>();
            var seenPasswords = new HashSet<string>(StringComparer.Ordinal);
            int skipped = 0;

            if (string.IsNullOrEmpty(content))
            {
                return new PasswordBookParseResult
                {
                    Entries = entries,
                    Warnings = warnings,
                    SkippedLineCount = skipped
                };
            }

            string[] lines = LineSplitter.Split(content);

            for (int i = 0; i < lines.Length; i++)
            {
                int lineNumber = i + 1;
                string line = lines[i];

                // 规则 2：只有空白字符的行、以及 # 开头的注释行（# 前允许空白）直接跳过。
                // 注意这里用的是纯空白判断，不是 Trim 后判断内容——密码行本身不 Trim。
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (line.TrimStart().StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                // 规则 5：显式空密码标记。必须放在映射式/列表式判定之前。
                if (IsEmptyPasswordMarker(line))
                {
                    AppendEntry(new PasswordEntry
                    {
                        Password = string.Empty,
                        Name = null,
                        Kind = PasswordEntryKind.Empty,
                        LineNumber = lineNumber,
                        Remark = EmptyPasswordRemark
                    });
                    continue;
                }

                // 规则 3：映射式判定。冒号在第 1 个字符时（如 ":abc"）不算映射，按列表式处理。
                if (TryParseMapped(line, lineNumber, out PasswordEntry? mapped))
                {
                    AppendEntry(mapped);
                    continue;
                }

                // 规则 4：列表式——整行原样就是密码，绝不 Trim。
                AppendEntry(new PasswordEntry
                {
                    Password = line,
                    Name = null,
                    Kind = PasswordEntryKind.List,
                    LineNumber = lineNumber,
                    Remark = $"来自第 {lineNumber} 行"
                });
            }

            return new PasswordBookParseResult
            {
                Entries = entries,
                Warnings = warnings,
                SkippedLineCount = skipped
            };

            // 规则 6：按密码原文精确去重（Ordinal）。不同 Name 但密码相同也算重复——
            // 密码候选只需要一份，重复尝试同一个密码纯属浪费尝试次数。
            void AppendEntry(PasswordEntry entry)
            {
                if (seenPasswords.Add(entry.Password))
                {
                    entries.Add(entry);
                    return;
                }

                skipped++;
                warnings.Add($"第 {entry.LineNumber} 行被忽略：密码重复（与前面出现过的候选相同）。");
            }
        }

        /// <summary>
        /// 从文件解析密码本。
        ///
        /// 编码策略：先用 <see cref="Encoding.UTF8"/>（自动跳过 BOM）读；
        /// 若解码结果里出现替换字符 U+FFFD，说明不是 UTF-8，再用 GB18030 重读一次
        /// （中文 Windows 下的 txt 常见 GBK/GB18030）。
        ///
        /// ⚠ 代码页编码（GB18030）在 .NET Core 上**必须先注册**才拿得到，否则
        /// <see cref="Encoding.GetEncoding(string)"/> 直接抛异常、这条回退会**静默降级**成
        /// "按系统默认编码读"（= UTF-8），中文 Windows 的 GBK 密码本会被读成乱码，用户只会看到"密码全不对"。
        /// 所以这里先调一次 <see cref="CodePageEncodingBootstrap.EnsureRegistered"/>（幂等）——
        /// 不依赖"启动路径恰好注册过"，单测/未来的 CLI 直接调本方法也是对的。
        /// 取不到时仍然退回 <see cref="Encoding.Default"/> 并把这件事写进 Warnings —— 不静默丢行。
        /// </summary>
        /// <param name="filePath">密码本文件路径。文件不存在、路径为空都不抛异常，只返回空结果 + 警告。</param>
        public static PasswordBookParseResult ParseFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return CreateFailureResult(FileMissingWarning);
            }

            if (!File.Exists(filePath))
            {
                return CreateFailureResult(FileMissingWarning);
            }

            string content;

            try
            {
                content = File.ReadAllText(filePath, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                // 密码本读不了不是崩溃点：返回空结果 + 警告，让上层决定怎么提示用户。
                // 警告里只放路径与异常类型，不放文件内容。
                return CreateFailureResult($"密码本文件读取失败（{ex.GetType().Name}），已跳过。");
            }

            if (!ContainsReplacementCharacter(content))
            {
                return Parse(content);
            }

            Encoding fallbackEncoding;
            var extraWarnings = new List<string> { Gb18030FallbackWarning };

            // 先确保代码页编码可用（幂等）：拿不到时下面那条 catch 仍然会把降级写进警告。
            Helpers.CodePageEncodingBootstrap.EnsureRegistered();

            try
            {
                fallbackEncoding = Encoding.GetEncoding("GB18030");
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                fallbackEncoding = Encoding.Default;
                extraWarnings.Add(DefaultEncodingFallbackWarning);
            }

            string fallbackContent;

            try
            {
                fallbackContent = File.ReadAllText(filePath, fallbackEncoding);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                // 回退编码也读不了：宁可交回 UTF-8 的结果（至少行结构是对的），并说明这一点。
                extraWarnings.Add($"密码本按备用编码重读失败（{ex.GetType().Name}），已使用 UTF-8 结果。");

                return MergeWarnings(Parse(content), extraWarnings);
            }

            return MergeWarnings(Parse(fallbackContent), extraWarnings);
        }

        /// <summary>
        /// 用归档文件名 / 基名去匹配映射式条目，返回命中项（可能多个，按输入顺序）。
        ///
        /// 命中优先级：① 规范化后完全相等 ② 互相包含（长度 ≥ 2 才做包含匹配）。
        /// 只要存在完全相等的命中，就<b>只</b>返回它们 —— 精确命中的可信度远高于模糊包含，
        /// 混在一起返回会让"命中优先于遍历全表"这条规则形同虚设。
        /// </summary>
        public static IReadOnlyList<PasswordEntry> MatchByName(
            IEnumerable<PasswordEntry> entries,
            string archiveFileName)
        {
            var matched = new List<PasswordEntry>();

            if (entries == null)
            {
                return matched;
            }

            string archiveName = NormalizeName(archiveFileName);

            if (archiveName.Length == 0)
            {
                return matched;
            }

            var exactMatches = new List<PasswordEntry>();
            var containsMatches = new List<PasswordEntry>();

            foreach (PasswordEntry entry in entries)
            {
                if (entry == null || entry.Kind != PasswordEntryKind.Mapped)
                {
                    continue;
                }

                string entryName = NormalizeName(entry.Name);

                if (entryName.Length == 0)
                {
                    continue;
                }

                if (string.Equals(entryName, archiveName, StringComparison.Ordinal))
                {
                    exactMatches.Add(entry);
                    continue;
                }

                // 长度 ≥ 2 才做包含匹配：单字名称包含命中率极低、误伤率极高
                // （一个叫"a"的名称会命中任何含 a 的归档名），所以直接不参与。
                if (entryName.Length >= 2 &&
                    archiveName.Length >= 2 &&
                    (archiveName.Contains(entryName, StringComparison.Ordinal) ||
                     entryName.Contains(archiveName, StringComparison.Ordinal)))
                {
                    containsMatches.Add(entry);
                }
            }

            return exactMatches.Count > 0 ? exactMatches : containsMatches;
        }

        /// <summary>
        /// 规范化名称，便于"完全相等 / 包含"两种匹配：
        /// 去扩展名（用 <see cref="FileNameHelper.GetArchiveBaseName"/> 的语义，x.tar.gz → x）
        /// → 去首尾空白 → 统一小写。
        ///
        /// 不要在这里补 Trim 之外的处理：这里处理的是<b>名称</b>，不是密码。
        /// </summary>
        public static string NormalizeName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            string baseName;

            try
            {
                baseName = FileNameHelper.GetArchiveBaseName(name);
            }
            catch (ArgumentException)
            {
                // 含非法路径字符时 GetArchiveBaseName 可能抛，退化成原串处理。
                baseName = name;
            }

            return baseName.Trim().ToLowerInvariant();
        }

        /// <summary>
        /// 判断整行是不是显式空密码标记。
        /// 只认整行匹配，不认"行尾带标记"——避免把 <c>abc空</c> 这类真实密码误判成空密码。
        /// </summary>
        private static bool IsEmptyPasswordMarker(string line)
        {
            if (line.Length == 0)
            {
                return false;
            }

            return EmptyPasswordMarkers.Contains(line.Trim());
        }

        /// <summary>
        /// 尝试按映射式解析一行。
        ///
        /// 判定条件（全部满足才算映射式）：
        /// ① 行内含 <c>:</c> 或 <c>：</c>；② 冒号不是第 1 个字符；
        /// ③ 冒号左侧存在非空白内容；④ 冒号右侧内容非空。
        /// 切分点固定在<b>第一个</b>冒号，因为密码里可能还有冒号。
        /// </summary>
        private static bool TryParseMapped(string line, int lineNumber, [NotNullWhen(true)] out PasswordEntry? entry)
        {
            entry = null;

            int colon = IndexOfColon(line);

            if (colon <= 0)
            {
                return false;
            }

            string name = line[..colon];
            string password = line[(colon + 1)..];

            if (name.Trim().Length == 0)
            {
                return false;
            }

            if (password.Length == 0)
            {
                return false;
            }

            // 名称 Trim（排版噪音），密码原样保留（见类注释第 1 条）。
            string trimmedName = name.Trim();

            entry = new PasswordEntry
            {
                Password = password,
                Name = trimmedName,
                Kind = PasswordEntryKind.Mapped,
                LineNumber = lineNumber,
                RawLine = line,
                Remark = $"来自第 {lineNumber} 行，名称：{trimmedName}"
            };

            return true;
        }

        /// <summary>取行内第一个冒号（半角或全角）的下标；没有则返回 -1。</summary>
        private static int IndexOfColon(string line)
        {
            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];

                if (ch == ':' || ch == '\uFF1A')
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// 判断解码结果里是否出现 U+FFFD 替换字符。
        ///
        /// 注意：这个判据只在"文件内容本来不该有 U+FFFD"的前提下成立。
        /// 真正的 UTF-8 文本里如果本来就写了替换字符，会被误判成编码错误并回退一次；
        /// 回退后的结果同样带 U+FFFD 时不会无限回退（只回退一次），影响可接受。
        /// </summary>
        private static bool ContainsReplacementCharacter(string content)
        {
            return content.IndexOf('\uFFFD') >= 0;
        }

        private static PasswordBookParseResult MergeWarnings(PasswordBookParseResult result, IReadOnlyList<string> extraWarnings)
        {
            if (extraWarnings.Count == 0)
            {
                return result;
            }

            var merged = new List<string>(extraWarnings);

            if (result.Warnings != null)
            {
                merged.AddRange(result.Warnings);
            }

            return new PasswordBookParseResult
            {
                Entries = result.Entries,
                Warnings = merged,
                SkippedLineCount = result.SkippedLineCount
            };
        }

        private static PasswordBookParseResult CreateFailureResult(string warning)
        {
            return new PasswordBookParseResult
            {
                Entries = Array.Empty<PasswordEntry>(),
                Warnings = new[] { warning },
                SkippedLineCount = 0
            };
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using ArchiveFixer.Helpers;

namespace ArchiveFixer.Password
{
    /// <summary>
    /// 从归档旁边的说明文件里抽出来的一个密码候选。
    ///
    /// 隐私约定（AGENTS.md §8）：<see cref="Password"/> 是明文，只能在内存里流转；
    /// 日志、报告、异常信息里一律不得输出它，也不要依赖默认的 ToString 打印本类型。
    /// </summary>
    public sealed class SidecarCandidate
    {
        /// <summary>明文密码（仅内存）。</summary>
        public string Password { get; init; } = string.Empty;

        /// <summary>来源说明文件的完整路径。</summary>
        public string SourceFile { get; init; } = string.Empty;

        /// <summary>在说明文件里的行号，1 起。</summary>
        public int LineNumber { get; init; }

        /// <summary>给界面看的说明，例如"来自 archive.7z 同目录的 说明.txt 第 3 行"。不写文件内容。</summary>
        public string? Remark { get; init; }

        /// <summary>
        /// 脱敏显示，避免手滑把本对象丢进日志或绑定到界面时泄露明文。
        /// </summary>
        public override string ToString()
        {
            return $"******（来自 {FileNameHelper.GetFileName(SourceFile)} 第 {LineNumber} 行）";
        }
    }

    /// <summary>
    /// 旁路说明文件读取器（AGENTS.md §9.4）。
    ///
    /// 背景：真实资源包常把密码直接写在归档旁边的 .txt / .bat 里
    /// （<c>set "password=xxx"</c>、<c>-pxxx</c>、<c>解压密码：xxx</c> 等等）。
    ///
    /// 三条设计约束，先说清楚为什么：
    ///
    /// 1. <b>只读归档所在目录，不递归</b>。递归扫描等于把用户的整个目录树翻一遍，
    ///    既慢又越权；说明文件按习惯就放在包旁边，同目录足够覆盖真实场景。
    ///    这个能力还必须由用户显式开启（默认关闭），本类只负责"读同目录"这一件事。
    ///
    /// 2. <b>本类不是日志</b>。抽出来的明文只放进返回值，绝不写进日志或异常信息，
    ///    来源信息也只写文件名/行号，不写文件内容。
    ///
    /// 3. <b>抽不准就不抽</b>。说明文件里九成内容都是"解压说明""注意事项"这类噪声，
    ///    因此自由文本行的判定刻意收得很紧：宁可漏掉一个密码候选（用户还能手输），
    ///    也不要把一整句话当成密码塞进尝试列表、白白消耗尝试上限。
    /// </summary>
    public static class SidecarPasswordReader
    {
        /// <summary>同时认 \r\n、\n、\r 三种换行。</summary>
        private static readonly Regex LineSplitter = new Regex("\r\n|\n|\r", RegexOptions.Compiled);

        /// <summary>
        /// <c>set "password=xxx"</c> / <c>set password=xxx</c>（忽略大小写）。
        /// 有引号时取引号内内容，此时引号内的首尾空白属于排版噪声，Trim；
        /// 引号内为空（<c>set "password="</c>）视为空密码候选。
        /// </summary>
        private static readonly Regex SetPasswordPattern = new Regex(
            "^\\s*set\\s+\"\\s*password\\s*=\\s*(?<pw>[^\"]*?)\\s*\"\\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary><c>set password=xxx</c>（无引号，密码原样保留，含首尾空格）。</summary>
        private static readonly Regex SetPasswordBarePattern = new Regex(
            "^\\s*set\\s+password\\s*=\\s*(?<pw>.+?)\\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// 7z 参数式 <c>-p&lt;密码&gt;</c>：要求 <c>-p</c> 前是行首或空白、后面紧跟非空白内容。
        /// 用"取其后非空白部分"而不是整行切分，是为了兼容 <c>7z x a.7z -pTestPass123!</c> 这种写法。
        /// </summary>
        private static readonly Regex SevenZipPasswordPattern = new Regex(
            "(?:^|\\s)-p(?<pw>\\S+)",
            RegexOptions.Compiled);

        /// <summary>
        /// 标签式：<c>密码：xxx</c> / <c>密码:xxx</c> / <c>解压密码是 xxx</c> /
        /// <c>password:xxx</c> / <c>pass=xxx</c> / <c>pwd:xxx</c>（忽略大小写）。
        ///
        /// 注意：分隔符（<c>:</c> / <c>：</c> / <c>=</c>）或中文的"是/为"**必须有**。
        /// 早先的写法把分隔符做成可选、还允许标签后跟两个任意字符，
        /// 结果整行就是密码的 <c>pass1</c> 会被切成密码 <c>1</c>（<c>password123</c> 会变成 <c>3</c>）——
        /// 这类文件里"一行一个密码"很常见，切错等于把用户的密码改坏，所以宁可不认这种含糊写法。
        /// 认不出来会落到下面的"整行就是一个密码"规则，那条规则能正确收下 <c>pass1</c>。
        /// </summary>
        private static readonly Regex LabeledPasswordPattern = new Regex(
            "^(?:解压|压缩|文件)?(?:密码|pass(?:word)?|pwd)\\s*(?:[:：=]|是|为)\\s*(?<pw>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// 自由文本行的拒收词表：命中就说明这行在讲事情，不是在给密码。
        /// 只做子串匹配（大小写无关）。
        /// </summary>
        private static readonly string[] NoiseWords =
        {
            "说明", "注意", "解压", "密码", "教程", "下载", "http", "www",
            "转存", "在线", "安装", "运行", "如果", "请", "版权", "资源", "链接",
        };

        /// <summary>"整行就是一个密码"的长度上限。</summary>
        private const int BarePasswordMaxLength = 64;

        /// <summary>只认这些后缀的说明文件。</summary>
        private static readonly string[] SidecarExtensions = { ".txt", ".bat", ".cmd", ".ini", ".md" };

        /// <summary>
        /// 在归档同目录里找 .txt/.bat/.cmd/.ini/.md 说明文件，抽出密码候选。
        /// </summary>
        /// <param name="archivePath">归档文件路径。为空、目录不存在都返回空列表，不抛异常。</param>
        /// <param name="maxFileBytes">
        /// 单个说明文件的大小上限，默认 256KB。超过就跳过该文件，避免把一个几百 MB 的
        /// 文本（或伪装成 .txt 的别的东西）整个读进内存。
        /// </param>
        /// <param name="maxFiles">最多看几个说明文件，默认 20（按文件名字典序取前 N 个，保证结果稳定可复现）。</param>
        /// <param name="maxCandidates">最多返回几个候选，默认 50（试密码是有上限的，候选再多也没用）。</param>
        public static IReadOnlyList<SidecarCandidate> ReadCandidates(
            string archivePath,
            int maxFileBytes = 256 * 1024,
            int maxFiles = 20,
            int maxCandidates = 50)
        {
            var results = new List<SidecarCandidate>();

            if (string.IsNullOrWhiteSpace(archivePath) ||
                maxFileBytes <= 0 ||
                maxFiles <= 0 ||
                maxCandidates <= 0)
            {
                return results;
            }

            string? directory = GetDirectoryOrNull(archivePath);

            if (directory == null || !Directory.Exists(directory))
            {
                return results;
            }

            List<string> candidates;

            try
            {
                candidates = CollectSidecarFiles(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 目录列不出来不是崩溃点：返回空列表，由上层决定怎么提示。
                return results;
            }

            // 按文件名字典序（大小写无关、稳定）取前 maxFiles 个。
            // 不依赖 Directory.GetFiles 的返回顺序——它由文件系统决定，同一目录两次跑可能不一样，
            // 那会让"同一批文件两次尝试顺序不同"，排查问题时无法复现。
            candidates.Sort(StringComparer.OrdinalIgnoreCase);

            if (candidates.Count > maxFiles)
            {
                candidates = candidates.GetRange(0, maxFiles);
            }

            string archiveFileName = FileNameHelper.GetFileName(archivePath);

            if (string.IsNullOrWhiteSpace(archiveFileName))
            {
                archiveFileName = "压缩包";
            }

            var seenPasswords = new HashSet<string>(StringComparer.Ordinal);

            foreach (string file in candidates)
            {
                if (results.Count >= maxCandidates)
                {
                    break;
                }

                AppendFileCandidates(file, archiveFileName, maxFileBytes, seenPasswords, results, maxCandidates);
            }

            return results;
        }

        /// <summary>
        /// 逐个抽取一个说明文件里的候选。
        /// 读不了就整份跳过——一个坏掉的 .txt 不能连累同目录其它说明文件。
        /// </summary>
        private static void AppendFileCandidates(
            string filePath,
            string archiveFileName,
            int maxFileBytes,
            HashSet<string> seenPasswords,
            List<SidecarCandidate> results,
            int maxCandidates)
        {
            try
            {
                var info = new FileInfo(filePath);

                if (!info.Exists || info.Length > maxFileBytes)
                {
                    return;
                }

                string content = File.ReadAllText(filePath, Encoding.UTF8);
                string[] lines = LineSplitter.Split(content);
                string sourceName = FileNameHelper.GetFileName(filePath);

                for (int i = 0; i < lines.Length; i++)
                {
                    if (results.Count >= maxCandidates)
                    {
                        return;
                    }

                    int lineNumber = i + 1;

                    foreach (string password in ExtractLine(lines[i]))
                    {
                        if (results.Count >= maxCandidates)
                        {
                            return;
                        }

                        // 按密码原文精确去重（Ordinal）：同一份说明文件里重复写、或几个文件写同一个密码，
                        // 都只留第一次出现的那条（连同它的来源，便于用户核对）。
                        if (!seenPasswords.Add(password))
                        {
                            continue;
                        }

                        results.Add(new SidecarCandidate
                        {
                            Password = password,
                            SourceFile = filePath,
                            LineNumber = lineNumber,
                            Remark = $"来自 {archiveFileName} 同目录的 {sourceName} 第 {lineNumber} 行"
                        });
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                // 读不了（占用、权限、路径非法）就跳过；不记文件内容，也不把异常往上抛。
            }
        }

        /// <summary>
        /// 从一行里抽出密码候选。
        ///
        /// 按"最具体 → 最宽松"的顺序尝试，命中一个就返回：
        /// 带引号的 set、无引号的 set、-p 参数、中文/英文标签、最后才是"整行看起来就是密码"。
        /// </summary>
        private static IEnumerable<string> ExtractLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                yield break;
            }

            Match match = SetPasswordPattern.Match(line);

            if (match.Success)
            {
                // 有引号包裹：引号内的首尾空白是排版噪声，Trim。
                // 这里不做"去引号后再剥一层"的处理——引号里是什么就是什么。
                yield return match.Groups["pw"].Value.Trim();
                yield break;
            }

            match = SetPasswordBarePattern.Match(line);

            if (match.Success)
            {
                // 无引号写法：值里的空格无法与排版空格区分，按"密码可能带空格"的既定取舍原样保留。
                yield return match.Groups["pw"].Value;
                yield break;
            }

            match = SevenZipPasswordPattern.Match(line);

            if (match.Success)
            {
                yield return TrimWrappingQuotes(match.Groups["pw"].Value);
                yield break;
            }

            match = LabeledPasswordPattern.Match(line);

            if (match.Success)
            {
                string labeled = NormalizeLabeledValue(match.Groups["pw"].Value);

                if (labeled.Length > 0)
                {
                    yield return labeled;
                    yield break;
                }

                // 标签右边是空的（如"解压密码："单独一行）：不产出候选，
                // 但也不要掉进"整行就是密码"那条规则——那行明显是说明文字。
                yield break;
            }

            string bare = TrimWrappingQuotes(line.Trim());

            if (LooksLikeBarePassword(bare))
            {
                yield return bare;
            }
        }

        /// <summary>
        /// 标签右侧内容的规范化：Trim，外层成对引号去掉。
        /// 不再往下切分——像"解压密码是 abc 或者试试空密码"这种句子，
        /// 多切一刀只会把说明文字切进候选列表。
        /// </summary>
        private static string NormalizeLabeledValue(string value)
        {
            string trimmed = value.Trim();

            if (trimmed.Length == 0)
            {
                return string.Empty;
            }

            if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
            {
                return trimmed[1..^1].Trim();
            }

            return trimmed;
        }

        /// <summary>
        /// 去掉把整个 token 包起来的一对引号（<c>-p"abc"</c> 这种写法）。
        /// 只处理成对包裹的情况，不处理引号在中间的写法。
        /// </summary>
        private static string TrimWrappingQuotes(string value)
        {
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                return value[1..^1];
            }

            return value;
        }

        /// <summary>
        /// 判断"整行看起来就是一个密码"。
        ///
        /// 判定条件（全满足才收）：
        /// ① 长度 1..64；
        /// ② 不含任何空白字符（密码带空格必须写成 <c>key=value</c> 形式，自由行无从判断哪部分是密码）；
        /// ③ 不含 <c>\</c>、<c>/</c>、<c>:</c> —— 这些字符出现在自由行里基本意味着这行是路径或句子；
        /// ④ 不命中常见说明词表。
        ///
        /// 第 ③ 条只是启发式，不是安全边界：它挡住的是"一整行说明文字"这类噪声，
        /// 代价是含冒号/斜杠的真实密码会漏掉（用户仍可手输，或让说明文件写成 key=value 形式）。
        /// </summary>
        private static bool LooksLikeBarePassword(string candidate)
        {
            if (candidate.Length < 1 || candidate.Length > BarePasswordMaxLength)
            {
                return false;
            }

            foreach (char ch in candidate)
            {
                if (char.IsWhiteSpace(ch) || ch == '\\' || ch == '/' || ch == ':')
                {
                    return false;
                }
            }

            foreach (string word in NoiseWords)
            {
                if (candidate.Contains(word, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 列出同目录下后缀命中 <see cref="SidecarExtensions"/> 的文件。
        /// </summary>
        private static List<string> CollectSidecarFiles(string directory)
        {
            var result = new List<string>();

            foreach (string file in Directory.GetFiles(directory))
            {
                string fileName = Path.GetFileName(file);

                if (fileName.Length == 0)
                {
                    continue;
                }

                foreach (string extension in SidecarExtensions)
                {
                    if (fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(file);
                        break;
                    }
                }
            }

            return result;
        }

        /// <summary>取归档文件所在目录；路径为空或调不动 Path API 时返回 null。</summary>
        private static string? GetDirectoryOrNull(string archivePath)
        {
            try
            {
                string? directory = Path.GetDirectoryName(archivePath);

                if (!string.IsNullOrEmpty(directory))
                {
                    return directory;
                }

                // 只给了文件名（相对路径）时，视作当前目录。
                return Directory.GetCurrentDirectory();
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }
        }
    }
}

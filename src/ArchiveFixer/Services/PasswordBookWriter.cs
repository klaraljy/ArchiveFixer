using ArchiveFixer.Helpers;
using ArchiveFixer.Password;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ArchiveFixer.Services
{
    /// <summary>
    /// 一次「写回密码本」的结论。
    ///
    /// <para>
    /// <b>这里绝不携带密码原文</b>（AGENTS.md §6 不变量 5）：调用方拿这个对象去写日志、写界面提示条、
    /// 拼失败原因，都不该、也不需要知道写了哪些密码。数量、文件、备份路径足够说清发生了什么。
    /// </para>
    /// </summary>
    public sealed class PasswordBookWriteBackResult
    {
        /// <summary>这次是不是成功了。失败时 <see cref="FailureReason"/> 一定有内容。</summary>
        public bool Success { get; init; }

        /// <summary>实际追加进文件的行数。</summary>
        public int AppendedCount { get; init; }

        /// <summary>没被写进去的条数（空密码、文件里已经有了、批内重复）。</summary>
        public int SkippedCount { get; init; }

        /// <summary>其中"因为文件里已经有同样的值"而没写的条数。</summary>
        public int AlreadyPresentCount { get; init; }

        /// <summary>其中"空密码"（长度为 0）被跳过的条数。</summary>
        public int SkippedEmptyCount { get; init; }

        /// <summary>
        /// 其中"只由空白字符组成"被跳过的条数。
        ///
        /// <para>为什么它们也不能写：本类写完会用 <see cref="PasswordBookParser.ParseFile"/> 自检，
        /// 而解析器把**只有空白字符的行**当成排版空行跳过（AGENTS.md §9.1 里那条取舍）——
        /// 写进去 = 立刻自检失败，用户会看到一条莫名其妙的失败。
        /// 与其写一条"下次启动读不回来"的内容，不如现在就如实说清它被跳过了。</para>
        /// </summary>
        public int SkippedBlankCount { get; init; }

        /// <summary>其中"同一次里重复出现"被跳过的条数。</summary>
        public int SkippedDuplicateCount { get; init; }

        /// <summary>写入的目标密码本文件（失败时是原本打算写的那个）。</summary>
        public string TargetPath { get; init; } = string.Empty;

        /// <summary>写前备份文件路径；一条都没写时为空串（没有备份，也没有改动）。</summary>
        public string BackupPath { get; init; } = string.Empty;

        /// <summary>失败原因（给用户看的一句话，**不含密码原文**）；成功时为空串。</summary>
        public string FailureReason { get; init; } = string.Empty;

        /// <summary>是否需要告诉用户"原文件一个字节都没变"。</summary>
        public bool IsNoOp => AppendedCount == 0 && Success;

        public static PasswordBookWriteBackResult Failure(string targetPath, string reason)
        {
            return new PasswordBookWriteBackResult
            {
                Success = false,
                TargetPath = targetPath ?? string.Empty,
                FailureReason = reason ?? string.Empty
            };
        }
    }

    /// <summary>
    /// 把手工密码**追加**进用户自己的密码本 txt（用户 2026-09-24 拍板的功能）。
    ///
    /// <para><b>它解决的是哪一个现象</b>：用户在「密码列表管理」里手动加的条目、手动调的顺序，
    /// 以前**重启即丢**（那时列表只在内存里、启动由密码本 txt 重建）。2026-09-24 用户拍板后，
    /// 列表本身已经按本机 DPAPI（机器范围）加密落盘、能跨重启保留（AGENTS.md §6 不变量 5）——
    /// 但那份记忆**换机器 / 重装系统就解不开**、关掉「记住密码列表」也没有。所以这条写回**照旧重要**：
    /// 只有写进<b>用户自己的那份 txt</b>（他明确点的按钮）才是真正带得走的长期载体，
    /// ⛔ 绝不写进 <c>appsettings.json</c>、也绝不写进程序自己的 <c>data\</c> 下任何文件。</para>
    ///
    /// <para><b>六条硬约束</b>（每一条都有测试钉住）：</para>
    /// <list type="number">
    /// <item><description><b>写前先备份</b>：同目录 <c>&lt;原名&gt;.bak-yyyyMMdd-HHmmss</c>。
    /// 备份失败 = 中止，绝不"没备份也照写"。</description></item>
    /// <item><description><b>保持原编码</b>：UTF-8 带 BOM / UTF-8 无 BOM / GB18030 三档，
    /// 与 <see cref="PasswordBookParser"/> 的探测口径一致（同一个判据、同一个回退顺序），
    /// 追加时用同一种编码写回去。</description></item>
    /// <item><description><b>保持原行尾</b>：文件里有 <c>\r\n</c> 就 CRLF，否则有 <c>\n</c> 就 LF，
    /// 两者都没有（单行文件）时按平台默认。</description></item>
    /// <item><description><b>只追加、绝不重写</b>：文件末尾若没有换行先补一个，然后每条一行；
    /// 密码<b>不 Trim</b>（首尾空格是密码的一部分）。</description></item>
    /// <item><description><b>按值去重</b>：文件里已经出现过的值不再追加（同一文件内逐行精确比较）。</description></item>
    /// <item><description><b>写完自检</b>：用 <see cref="PasswordBookParser.ParseFile"/> 重新解析一遍，
    /// 新写入的值必须都在；不在就判失败并指出备份在哪。</description></item>
    /// </list>
    ///
    /// <para><b>失败时原文件一个字节都不许变</b>：唯一会改动原文件的地方是"打开 → 追加 → 自检"，
    /// 任何一步出事都按打开前的长度 <see cref="FileStream.SetLength(long)"/> 截回去；
    /// 连回滚都失败时如实写进 <see cref="PasswordBookWriteBackResult.FailureReason"/>，
    /// 不假装没发生过。</para>
    /// </summary>
    public class PasswordBookWriter
    {
        /// <summary>UTF-8 BOM 的三个字节。</summary>
        private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };

        /// <summary>GB18030（GBK 的超集）的代码页名。</summary>
        private const string Gb18030CodePageName = "GB18030";

        /// <summary>
        /// 备份文件名里的时间戳由谁提供。
        ///
        /// <para>做成可注入的：测试造不出"真正同一秒"的两次写回，只能从这里把时间钉住 ——
        /// 否则"同秒撞名要退到 <c>.1</c>/<c>.2</c>"这条分支永远只能靠读代码相信。</para>
        /// </summary>
        public Func<DateTime> Clock { get; set; } = () => DateTime.Now;

        /// <summary>
        /// 从一批候选里挑出"还没写回"的那些（纯查询，不碰文件）。
        ///
        /// <para>界面用它决定「写回密码本…」按钮可不可点，以及确认框里该列哪几条。
        /// 判据与真正写回时**完全同一份实现**（<see cref="Classify"/>），
        /// 免得"按钮说 3 条、点下去写了 2 条"这种自相矛盾。</para>
        /// </summary>
        /// <param name="filePath">密码本文件。不存在 / 读不出来时，**所有非空值都算待写**（它们没在文件里出现过）。</param>
        /// <param name="values">候选值（可以是密码列表里手工添加的那些）。</param>
        public IReadOnlyList<string> FindPending(string filePath, IEnumerable<string>? values)
        {
            Plan plan = Classify(filePath, values);

            // Pending 里不会有空密码 / 纯空白（它们在 Classify 就被分走了），所以直接取值。
            return plan.Pending.Select(item => item.Value).ToArray();
        }

        /// <summary>
        /// 把一批密码追加进指定的密码本 txt。**绝不重写、绝不重排已有内容。**
        /// </summary>
        /// <param name="filePath">目标密码本文件；不存在时失败（不新建一个"看起来像密码本"的空文件）。</param>
        /// <param name="values">要写的密码（不 Trim，首尾空格原样保留）。null / 空集合 = 什么都不做，返回成功 0 条。</param>
        public PasswordBookWriteBackResult WriteBack(string filePath, IEnumerable<string>? values)
        {
            string target = filePath ?? string.Empty;

            if (string.IsNullOrWhiteSpace(target))
            {
                return PasswordBookWriteBackResult.Failure(target, "没有指定密码本文件，没有写任何东西。");
            }

            if (!File.Exists(target))
            {
                return PasswordBookWriteBackResult.Failure(
                    target,
                    $"密码本文件不存在（{Path.GetFileName(target)}），没有写任何东西。");
            }

            Plan plan = Classify(target, values);

            if (plan.Pending.Count == 0)
            {
                // 一条都不用写：不备份、不打开、一个字节都不碰。
                // （哪怕是"全是空密码"也一样：写进去没有意义。）
                return new PasswordBookWriteBackResult
                {
                    Success = true,
                    AppendedCount = 0,
                    SkippedCount = plan.SkippedCount,
                    AlreadyPresentCount = plan.AlreadyPresentCount,
                    SkippedEmptyCount = plan.SkippedEmptyCount,
                    SkippedBlankCount = plan.SkippedBlankCount,
                    SkippedDuplicateCount = plan.SkippedDuplicateCount,
                    TargetPath = target
                };
            }

            string backupPath;

            try
            {
                backupPath = CreateBackup(target);
            }
            catch (Exception ex) when (IsExpectedFileFailure(ex))
            {
                // 约束 ①：没有备份就绝不往下写。
                return PasswordBookWriteBackResult.Failure(
                    target,
                    "写前备份失败（" + Describe(ex) + "），原文件一个字节都没有改动，也没有写入任何密码。");
            }

            var pendingValues = plan.Pending.Select(item => item.Value).ToArray();

            try
            {
                AppendValues(target, plan, pendingValues);
            }
            catch (Exception ex) when (IsExpectedFileFailure(ex))
            {
                string rollbackNote = TryRollback(target, plan.OriginalContent, out string rollbackFailure);

                // 只读 / 被占用 / 权限不足 / 磁盘满都走到这里：**明确失败**，
                // 并且把"原文件到底动没动"如实说清（回滚失败时绝不假装没事）。
                return PasswordBookWriteBackResult.Failure(
                    target,
                    "写入失败（" + Describe(ex) + "）。"
                    + (rollbackFailure.Length == 0
                        ? "原文件已回滚到写入前的大小，内容没有变化。"
                        : "⚠ 原文件可能留下了半截内容，回滚也没成功：" + rollbackFailure + "。")
                    + "备份文件在：" + backupPath);
            }

            // 约束 ⑥：自己再解析一遍，确认新写入的值真的都在文件里。
            string missing = VerifyWritten(target, pendingValues);

            if (missing.Length > 0)
            {
                return PasswordBookWriteBackResult.Failure(
                    target,
                    "写入后自检没通过（" + missing + "）。备份文件在：" + backupPath
                    + "；请用这个备份核对密码本内容。");
            }

            return new PasswordBookWriteBackResult
            {
                Success = true,
                AppendedCount = plan.Pending.Count,
                SkippedCount = plan.SkippedCount,
                AlreadyPresentCount = plan.AlreadyPresentCount,
                SkippedEmptyCount = plan.SkippedEmptyCount,
                SkippedBlankCount = plan.SkippedBlankCount,
                SkippedDuplicateCount = plan.SkippedDuplicateCount,
                TargetPath = target,
                BackupPath = backupPath
            };
        }

        // ------------------------------------------------------------------ 判定：谁要写、谁不写

        /// <summary>归类后的一项：一个真的会被写进去的值。</summary>
        private readonly struct PlanItem
        {
            public PlanItem(string value)
            {
                Value = value;
            }

            /// <summary>密码原文（只在内存里流转，绝不进日志）。</summary>
            public string Value { get; }
        }

        /// <summary>一次写回的计划：写哪些、跳哪些、原文件长什么样。</summary>
        private sealed class Plan
        {
            /// <summary>真正要追加的值（按输入顺序）。</summary>
            public List<PlanItem> Pending { get; } = new();

            /// <summary>打开前的原始内容（用于失败时回滚成逐字节一致）。</summary>
            public byte[] OriginalContent { get; set; } = Array.Empty<byte>();

            /// <summary>原文件里有没有内容、末尾是不是换行（决定要不要先补一个换行）。</summary>
            public bool EndsWithNewline { get; set; }

            /// <summary>追加时用的编码（文本 + BOM 策略与原文件一致）。</summary>
            public Encoding Encoding { get; set; } = new UTF8Encoding(false);

            /// <summary>追加时用的行尾。</summary>
            public string Newline { get; set; } = Environment.NewLine;

            public int SkippedEmptyCount { get; set; }

            public int SkippedBlankCount { get; set; }

            public int SkippedDuplicateCount { get; set; }

            public int AlreadyPresentCount { get; set; }

            public int SkippedCount => SkippedEmptyCount + SkippedBlankCount + SkippedDuplicateCount + AlreadyPresentCount;
        }

        /// <summary>
        /// 归类：哪些值文件里已经有了、哪些是空密码、哪些同一次里重复了、哪些真要写。
        ///
        /// <para>文件读不出来时（不存在 / 被占用）按"文件里什么都没有"处理 —— 这条路上
        /// 上层还没打算写，先给用户一个"有 N 条待写"的诚实估计；真写的时候读不出来会走失败分支。</para>
        /// </summary>
        private Plan Classify(string filePath, IEnumerable<string>? values)
        {
            var plan = new Plan();

            HashSet<string> existing;
            byte[]? content = TryReadAllBytes(filePath);

            if (content == null)
            {
                existing = new HashSet<string>(StringComparer.Ordinal);
            }
            else
            {
                existing = BuildExistingValueSet(content, out Encoding encoding, out string newline);

                plan.OriginalContent = content;
                plan.Encoding = encoding;
                plan.Newline = newline;
                plan.EndsWithNewline = EndsWithNewline(content);
            }

            if (values == null)
            {
                return plan;
            }

            var seenInBatch = new HashSet<string>(StringComparer.Ordinal);

            foreach (string? raw in values)
            {
                string value = raw ?? string.Empty;

                if (value.Length == 0)
                {
                    // 约束：空密码默认跳过（写进去没有意义，而且很容易被误用成"随便试试"）。
                    plan.SkippedEmptyCount++;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(value))
                {
                    // 只含空白：解析器会把这种行当空行跳过，写进去等于写了一条读不回来的内容。
                    plan.SkippedBlankCount++;
                    continue;
                }

                if (!seenInBatch.Add(value))
                {
                    plan.SkippedDuplicateCount++;
                    continue;
                }

                if (existing.Contains(value))
                {
                    plan.AlreadyPresentCount++;
                    continue;
                }

                plan.Pending.Add(new PlanItem(value));
            }

            return plan;
        }

        /// <summary>
        /// 从原文件内容里取出"已经出现过的值"，并顺手定下追加要用的编码与行尾。
        ///
        /// <para>逐行精确比较（Ordinal，不 Trim）：一行本身算一个值；映射式的行
        /// （<c>名称:密码</c>）**额外**把冒号右侧也算一个值 —— 否则手工加一条已经在映射式里写过的密码，
        /// 会被重复追加成一行列表式，同一个密码在文件里出现两次。</para>
        /// </summary>
        private static HashSet<string> BuildExistingValueSet(byte[] content, out Encoding encoding, out string newline)
        {
            encoding = DetectEncoding(content, out int bomLength);
            newline = DetectNewline(content);

            string text = encoding.GetString(content, bomLength, content.Length - bomLength);

            var set = new HashSet<string>(StringComparer.Ordinal);

            foreach (string line in SplitLines(text))
            {
                if (line.Length == 0)
                {
                    continue;
                }

                set.Add(line);

                int colon = IndexOfColon(line);

                if (colon > 0 && colon + 1 < line.Length)
                {
                    set.Add(line[(colon + 1)..]);
                }
            }

            return set;
        }

        // ------------------------------------------------------------------ 编码 / 行尾 / 换行（与解析器同一口径）

        /// <summary>
        /// 探测原文件编码。判据与回退顺序**照抄 <see cref="PasswordBookParser.ParseFile"/> 的口径**：
        /// UTF-8 带 BOM → UTF-8 无 BOM → GB18030；再取不到就用 UTF-8（无 BOM）。
        ///
        /// <para>与解析器唯一的差别是"凭什么判定不是 UTF-8"：解析器用的是"解出来有没有 U+FFFD"，
        /// 这里用的是<b>严格解码会不会抛</b>。用严格解码是因为本方法还要把文件<b>原样写回去</b> ——
        /// 内容里本来就带 U+FFFD 的合法 UTF-8 文件（解析器会误判成 GBK 并回退一次）在这里必须
        /// 按 UTF-8 原样追加，否则一次写回就会把整个文件重新编一遍。</para>
        /// </summary>
        private static Encoding DetectEncoding(byte[] content, out int bomLength)
        {
            if (StartsWithBom(content))
            {
                bomLength = Utf8Bom.Length;
                return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            }

            bomLength = 0;

            if (IsValidUtf8(content))
            {
                return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            }

            // 代码页编码在 .NET Core 上必须先注册（幂等），否则这里会**静默降级**成 UTF-8，
            // 于是 GBK 密码本被追加一段乱码 —— 与解析器那边同一个坑，同一处兜底。
            CodePageEncodingBootstrap.EnsureRegistered();

            if (CodePageEncodingBootstrap.IsRegistered)
            {
                try
                {
                    return Encoding.GetEncoding(Gb18030CodePageName);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
                {
                    // 落到下面的 UTF-8。
                }
            }

            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }

        /// <summary>严格试一遍 UTF-8：能完整解出来才算。</summary>
        private static bool IsValidUtf8(byte[] content)
        {
            if (content.Length == 0)
            {
                return true;
            }

            try
            {
                // throwOnInvalidBytes: true —— 这是与"看有没有 U+FFFD"的关键差别。
                var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

                strict.GetString(content);
                return true;
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
        }

        /// <summary>
        /// 探测行尾：文件里有 <c>\r\n</c> 就 CRLF，否则有 <c>\n</c> 就 LF，
        /// 两者都没有（单行文件 / 空文件）时用平台默认。
        ///
        /// <para>判据走**原始字节**而不是解码后的文本：编码可能多字节，但 CR(0x0D)/LF(0x0A)
        /// 在所有受支持的编码里都是 ASCII 的这两个字节，不会被别的字符借用。</para>
        /// </summary>
        private static string DetectNewline(byte[] content)
        {
            bool hasCarriageReturn = false;

            foreach (byte b in content)
            {
                if (b == (byte)'\n')
                {
                    return hasCarriageReturn ? "\r\n" : "\n";
                }

                if (b == (byte)'\r')
                {
                    hasCarriageReturn = true;
                }
            }

            return Environment.NewLine;
        }

        private static bool EndsWithNewline(byte[] content)
        {
            return content.Length > 0 && content[^1] == (byte)'\n';
        }

        private static bool StartsWithBom(byte[] content)
        {
            return content.Length >= Utf8Bom.Length
                && content[0] == Utf8Bom[0]
                && content[1] == Utf8Bom[1]
                && content[2] == Utf8Bom[2];
        }

        /// <summary>按 <c>\r\n</c> / <c>\n</c> / <c>\r</c> 切行（与解析器同一个口径）。</summary>
        private static IEnumerable<string> SplitLines(string text)
        {
            int start = 0;
            int index = 0;

            while (index < text.Length)
            {
                char ch = text[index];

                if (ch != '\r' && ch != '\n')
                {
                    index++;
                    continue;
                }

                yield return text[start..index];

                index += ch == '\r' && index + 1 < text.Length && text[index + 1] == '\n' ? 2 : 1;
                start = index;
            }

            yield return text[start..];
        }

        private static int IndexOfColon(string line)
        {
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == ':' || line[i] == '\uFF1A')
                {
                    return i;
                }
            }

            return -1;
        }

        // ------------------------------------------------------------------ 备份 / 追加 / 回滚 / 自检

        /// <summary>
        /// 约束 ①：写前先备份成同目录的 <c>&lt;原名&gt;.bak-yyyyMMdd-HHmmss</c>。
        ///
        /// <para>同一秒里第二次写回会撞名：那时退到 <c>.1</c> / <c>.2</c> …
        /// —— <b>绝不覆盖上一份备份</b>（上一份备份是上一次写回唯一的后悔药）。</para>
        /// </summary>
        public string CreateBackup(string filePath)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? string.Empty;

            if (directory.Length > 0 && !Directory.Exists(directory))
            {
                throw new DirectoryNotFoundException("目录不存在：" + directory);
            }

            string candidate = BuildBackupPath(filePath, Clock());

            for (int suffix = 1; File.Exists(candidate); suffix++)
            {
                if (suffix > 1000)
                {
                    throw new IOException("备份文件名连续 1000 次都撞名，已放弃（没有写入任何内容）。");
                }

                candidate = BuildBackupPath(filePath, Clock()) + "." + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            File.Copy(filePath, candidate, overwrite: false);

            return candidate;
        }

        /// <summary>备份路径：<c>&lt;目录&gt;\&lt;原名&gt;.bak-yyyyMMdd-HHmmss</c>。</summary>
        private static string BuildBackupPath(string filePath, DateTime timestamp)
        {
            string full = Path.GetFullPath(filePath);

            return full + ".bak-" + timestamp.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 约束 ④：**只追加**。文件末尾不是换行就先补一个，然后每条一行，
        /// 密码一个字符都不改（不 Trim、不做任何转义）。
        /// </summary>
        private static void AppendValues(string filePath, Plan plan, IReadOnlyList<string> values)
        {
            using var stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read);

            var writer = new StreamWriter(stream, plan.Encoding);

            try
            {
                // 原文件末尾没有换行时先补一个：否则新写的第一条会和最后一行粘成一行。
                if (plan.OriginalContent.Length > 0 && !plan.EndsWithNewline)
                {
                    writer.Write(plan.Newline);
                }

                foreach (string value in values)
                {
                    writer.Write(value);
                    writer.Write(plan.Newline);
                }

                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            finally
            {
                // StreamWriter 的 Dispose 会再 Flush 一次；这里不能让它的异常盖掉真正的失败原因。
                try
                {
                    writer.Dispose();
                }
                catch (Exception ex) when (IsExpectedFileFailure(ex))
                {
                }
            }
        }

        /// <summary>
        /// 失败时把文件截回打开前的长度，保证"原文件一个字节都不许变"。
        /// 返回给用户看的一句话（回滚没成功时由调用方再补一句）。
        /// </summary>
        private static string TryRollback(string filePath, byte[] originalContent, out string failure)
        {
            failure = string.Empty;

            try
            {
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.Read);
                stream.SetLength(originalContent.LongLength);
                stream.Flush(flushToDisk: true);

                return "已回滚。";
            }
            catch (Exception ex) when (IsExpectedFileFailure(ex))
            {
                failure = Describe(ex);
                return "原文件可能留下了半截内容（回滚失败）。";
            }
        }

        /// <summary>
        /// 约束 ⑥：写完自己解析一遍，确认新写入的值都在。
        /// 用**解析器本人**（<see cref="PasswordBookParser.ParseFile"/>）而不是本类自己的读取逻辑 ——
        /// 这样"我们按这个编码写的"与"程序下次启动按哪个编码读"必须当场对齐，对不上就判失败。
        /// </summary>
        /// <returns>缺失的值数量描述（空串 = 全都读得到；**不回显密码原文**）。</returns>
        private static string VerifyWritten(string filePath, IReadOnlyList<string> writtenValues)
        {
            PasswordBookParseResult parsed;

            try
            {
                parsed = PasswordBookParser.ParseFile(filePath);
            }
            catch (Exception ex) when (IsExpectedFileFailure(ex))
            {
                return "重新解析失败（" + Describe(ex) + "）";
            }

            // 多集合比较：同一个值写两次也只算两次，不会因为"文件里另有一处相同的值"而蒙混过去。
            var remaining = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (PasswordEntry entry in parsed.Entries)
            {
                string value = entry.Password ?? string.Empty;

                if (value.Length == 0)
                {
                    // 空密码不参与：解析器对"空"的表示方式（显式标记 / 空行跳过）与写入内容不是一一对应。
                    continue;
                }

                remaining[value] = remaining.TryGetValue(value, out int count) ? count + 1 : 1;
            }

            int missing = 0;

            foreach (string value in writtenValues)
            {
                if (value.Length == 0)
                {
                    continue;
                }

                if (remaining.TryGetValue(value, out int count) && count > 0)
                {
                    remaining[value] = count - 1;
                    continue;
                }

                missing++;
            }

            return missing == 0
                ? string.Empty
                : $"有 {missing} 条密码写进去之后没能重新读出来";
        }

        private static byte[]? TryReadAllBytes(string filePath)
        {
            try
            {
                return File.Exists(filePath) ? File.ReadAllBytes(filePath) : null;
            }
            catch (Exception ex) when (IsExpectedFileFailure(ex))
            {
                return null;
            }
        }

        /// <summary>
        /// "可预期的文件层失败"。用白名单而不是 <c>catch (Exception)</c>：
        /// 只读 / 被占用 / 权限不足 / 空间不足 / 路径过长这些都该变成一条"诚实的失败"，
        /// 而 <see cref="OutOfMemoryException"/> 这类真异常不该被这里吞掉。
        /// </summary>
        private static bool IsExpectedFileFailure(Exception ex)
        {
            return ex is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or ArgumentException
                or System.Security.SecurityException;
        }

        private static string Describe(Exception ex)
        {
            return ex.GetType().Name + "：" + ex.Message;
        }
    }
}

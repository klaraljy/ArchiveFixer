using System;

namespace ArchiveFixer.Helpers
{
    /// <summary>
    /// 长路径的**中间省略**（用户 2026-09-25 第 27 条）。
    ///
    /// <para><b>用户原话</b>：「如果选择解压到位置名字太长，可以简写，这只是个比方
    /// <c>E:\DeepSeekProjects\ArchiveFixer\ArchiveFixer.Tests\obj\Release\net8.0-windows\ref</c>
    /// 可以写成这样 <c>E:\DeepSeekProjects\ArchiveFixer\...\net8.0-windows\ref</c>，
    /// 反正我要求的就是我们最好能够看到完整的解压地址」。</para>
    ///
    /// <para><b>规则</b>：保留**前三段**（普通路径 = 盘符 + 两层目录，UNC = <c>\\服务器\共享</c> + 一层目录）
    /// 与**末两段**，中间用 <c>\...\</c> 连起来；段数 ≤ <see cref="ShortPathSegmentLimit"/> 时
    /// **原样返回**（首尾会重叠，省略反而更长），省略之后不比原来短的也原样返回。
    /// 完整路径不进这里 —— 它在界面的 ToolTip 与右键菜单「复制完整路径」里
    /// （省略只发生在**显示**这一层）。</para>
    ///
    /// <para><b>为什么是"中间省略"而不是尾省略</b>：路径的尾部（最后一个目录名）才是用户在找的东西，
    /// 头部告诉他这是哪块盘 / 哪个项目 —— 尾巴被截掉就变成了"看不出是哪一个"。
    /// 这也是它必须是一个**纯函数**、单独测的原因：省略规则错了只会让人找不到目录，不会报错。</para>
    ///
    /// <para><b>边界</b>：空 / 空白 / 盘根 / 短路径 / UNC / 全是分隔符的怪形状一律不抛异常
    /// （最差也只是原样返回）；不碰磁盘、不解析符号链接、不做任何 IO。</para>
    /// </summary>
    public static class PathMiddleEllipsis
    {
        /// <summary>段数不超过它就不省略（首 3 + 末 2 会重叠，省略反而更长）。</summary>
        public const int ShortPathSegmentLimit = 4;

        /// <summary>
        /// 头部保留几段。普通路径 = 盘符 + 两层目录（<c>E:\DeepSeekProjects\ArchiveFixer</c>）、
        /// UNC = 服务器 + 共享 + 一层目录 —— 与用户给的例子逐字一致，见类型注释里的原话。
        /// </summary>
        private const int HeadSegmentCount = 3;

        /// <summary>中间那一截的写法（与用户给的例子逐字一致）。</summary>
        public const string Ellipsis = @"\...\";

        /// <summary>目录分隔符（Windows 口径；正斜杠先归一成它再分段）。</summary>
        private const char Separator = '\\';

        /// <summary>
        /// 把路径中间省略成「头部三段 … 末两段」。空 / 短路径 / 认不出的形状原样返回，
        /// 而**省略之后不比原来短**时也原样返回（5–6 段的路径省下来的还不如省略号长，
        /// 那种情况下原样更清楚、也不会让人以为路径真的变了）。
        /// </summary>
        public static string Elide(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            string original = path.Trim();

            // 正斜杠归一：用户从别处粘过来的路径可能是 /，分段必须认它（显示时统一成 \）。
            string normalized = original.Replace('/', Separator);

            bool unc = normalized.StartsWith(@"\\", StringComparison.Ordinal);

            // UNC 前缀那两个反斜杠**不是**空段（去掉之后按 \ 分段，最后再补回去）。
            string body = unc ? normalized.Substring(2) : normalized;

            string[] segments = body.Split(Separator, StringSplitOptions.RemoveEmptyEntries);

            if (segments.Length <= ShortPathSegmentLimit)
            {
                // 短路径（含盘根 "E:\" / 纯 UNC 根 "\\server\share"）：一个字都不改。
                return original;
            }

            string head = (unc ? @"\\" : string.Empty)
                + string.Join(Separator, segments, 0, Math.Min(HeadSegmentCount, segments.Length));

            string tail = segments[segments.Length - 2] + Separator + segments[segments.Length - 1];

            string elided = head + Ellipsis + tail;

            return elided.Length < original.Length ? elided : original;
        }
    }
}

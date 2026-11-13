using System;
using System.Collections.Generic;

namespace ArchiveFixer.Password
{
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
}

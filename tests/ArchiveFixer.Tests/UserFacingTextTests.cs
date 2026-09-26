using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 面向用户的文案体检（2026-09-26 审计）。
    ///
    /// <para><b>为什么要有这一组</b>：程序里积累了几十处把 Markdown 的"加粗"写进用户字符串的写法
    /// （<c>"**一律只认勾选**"</c>）—— WPF 不认 Markdown，界面上原样显示成星号：
    /// 任务列表上方那条蓝提示、无用物提醒、打包弹窗里的红字、失败清单都会出现 <c>**</c>。
    /// 这类问题代码不会报错、测试不会变红，只有用户看着别扭 —— 所以钉一条扫描测试。</para>
    /// </summary>
    public class UserFacingTextTests
    {
        /// <summary>C# 字符串字面量（含转义；不含跨行的逐字字符串 —— 本项目没在文案里用过）。</summary>
        private static readonly Regex StringLiteral = new(
            "\"([^\"\\\\\\r\\n]*(?:\\\\.[^\"\\\\\\r\\n]*)*)\"",
            RegexOptions.Compiled);

        /// <summary>恰好两个星号 = Markdown 的加粗标记（<c>******</c> 这种掩码不算）。</summary>
        private static readonly Regex BoldMarker = new("(?<!\\*)\\*\\*(?!\\*)", RegexOptions.Compiled);

        [Fact]
        public void 面向用户的字符串里不许出现_Markdown_的星号加粗()
        {
            string appDirectory = Path.Combine(XamlBindingScan.RepositoryRoot, "src", "ArchiveFixer");
            var findings = new List<string>();

            foreach (string file in Directory
                         .EnumerateFiles(appDirectory, "*.cs", SearchOption.AllDirectories)
                         .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
            {
                string[] lines = File.ReadAllLines(file, Encoding.UTF8);

                for (int index = 0; index < lines.Length; index++)
                {
                    string trimmed = lines[index].TrimStart();

                    // 注释行不算（说明里写 Markdown 是给读代码的人看的，不上界面）。
                    if (trimmed.StartsWith("//", StringComparison.Ordinal) ||
                        trimmed.StartsWith("*", StringComparison.Ordinal) ||
                        trimmed.StartsWith("/*", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    foreach (Match match in StringLiteral.Matches(lines[index]))
                    {
                        if (BoldMarker.IsMatch(match.Groups[1].Value))
                        {
                            findings.Add(
                                $"{Path.GetFileName(file)}:{index + 1}：{trimmed[..Math.Min(120, trimmed.Length)]}");
                        }
                    }
                }
            }

            Assert.True(
                findings.Count == 0,
                "下面这些字符串会被**原样**显示到界面上（WPF 不认 Markdown）：" +
                Environment.NewLine + string.Join(Environment.NewLine, findings));
        }
    }
}

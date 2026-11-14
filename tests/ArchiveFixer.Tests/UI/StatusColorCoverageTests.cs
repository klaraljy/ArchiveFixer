using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using ArchiveFixer.Converters;
using ArchiveFixer.Models;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **每一个能落进①页状态格的常量，都必须有颜色**（用户 2026-10-10 原话：
    /// 「为什么解压失败都能显示成蓝色」「为什么第一行是绿色的，其他的都是蓝色的」）。
    ///
    /// <para><b>病灶</b>：<c>StatusToBrushConverter</c> 拿状态串跟一张**固定常量表**比，
    /// 不在表里就落到 <c>DefaultBrush</c>。所以"新加一个状态忘了登记配色"是**静默**的 ——
    /// 编译过、测试绿、界面上一行黑（或旧色）而已，没人会发现。</para>
    ///
    /// <para><b>这把守门怎么判</b>（⛔ 不靠人工维护一张名单，名单会烂）：
    /// 直接扫产品源码，把**能写进 <c>ArchiveTask.Status</c> 的那几种写法**里的常量名全收出来 ——
    /// ① <c>Status = StatusText.X</c>；② <c>MarkStarted/MarkSuccess/MarkFailed(…, StatusText.X, …)</c>。
    /// 然后逐个用反射取到字符串值，问配色表：它落在哪一档？
    /// 落在 <c>DefaultBrush</c>（= 哪一档都不是）就报出来，并点名是哪个常量。</para>
    ///
    /// <para><b>红检</b>：把 <c>RenameReady</c> 从 <c>IsInfoStatus</c> 里撤掉 ⇒ 本条当场变红。</para>
    /// </summary>
    public class StatusColorCoverageTests
    {
        /// <summary><c>Status = StatusText.X</c>（含 <c>this.Status = …</c>）。</summary>
        private static readonly Regex StatusAssignment = new(
            @"\bStatus\s*=\s*StatusText\.([A-Za-z0-9_]+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary><c>MarkStarted/MarkSuccess/MarkFailed(…, StatusText.X, …)</c> —— 这三个是拿状态当参数传的入口。</summary>
        private static readonly Regex MarkCallStatusArgument = new(
            @"\bMark(?:Started|Success|Failed)\s*\([^)]*?StatusText\.([A-Za-z0-9_]+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        [Fact]
        public void 能落进状态格的常量_每一个都要在配色表里有着落()
        {
            string appDirectory = Path.Combine(XamlBindingScan.RepositoryRoot, "src", "ArchiveFixer");

            Assert.True(Directory.Exists(appDirectory), "找不到产品源码目录：" + appDirectory);

            var names = new SortedSet<string>(StringComparer.Ordinal);

            foreach (string file in Directory.GetFiles(appDirectory, "*.cs", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(file);

                foreach (Match match in StatusAssignment.Matches(text))
                {
                    names.Add(match.Groups[1].Value);
                }

                foreach (Match match in MarkCallStatusArgument.Matches(text))
                {
                    names.Add(match.Groups[1].Value);
                }
            }

            // 收不到东西说明正则与源码脱节了（⛔ 不许把"扫不到"读成"全都合格"）。
            Assert.True(names.Count >= 20, $"只从源码里收到 {names.Count} 个状态常量，正则八成失效了。");

            var converter = new StatusToBrushConverter();
            var unmapped = new List<string>();

            foreach (string name in names)
            {
                FieldInfo? field = typeof(StatusText).GetField(
                    name,
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);

                if (field == null || field.FieldType != typeof(string) || !field.IsLiteral)
                {
                    unmapped.Add($"{name}（StatusText 里没有这个常量）");
                    continue;
                }

                string value = (string)field.GetRawConstantValue()!;
                object brush = converter.Convert(value, typeof(object), null!, null!);

                if (ReferenceEquals(brush, converter.DefaultBrush))
                {
                    unmapped.Add($"{name} = 「{value}」");
                }
            }

            Assert.True(
                unmapped.Count == 0,
                "这些状态在配色表里哪一档都不是 ⇒ 状态格会显示成默认色（用户会觉得「颜色跟文字对不上」）："
                    + Environment.NewLine
                    + string.Join(Environment.NewLine, unmapped));
        }
    }
}

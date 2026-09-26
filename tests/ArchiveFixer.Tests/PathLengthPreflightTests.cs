using System.Collections.Generic;
using System.IO;
using ArchiveFixer.Engines;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 解压前的**路径长度预检**（规格 §3 第 4 条：同一份 list 只取一次，顺带把"路径过长"算出来）。
    ///
    /// 为什么要有：7-Zip 报的是英文错（<c>The filename or extension is too long</c>），
    /// 而且往往解到一半才报 —— 本项目此前只能在失败之后才告诉用户"路径过长"
    /// （`SafePathHelper.IsPathTooLong` 一直没有调用方）。现在提前一句中文提示。
    ///
    /// 阈值只有一处定义：<see cref="ArchiveFixer.Helpers.SafePathHelper.IsPathTooLong"/>（≥240 字符）。
    /// 这里刻意按"完整路径正好 240 / 239"两个边界各钉一条，防止有人把阈值改成 260 或改成 &gt; 而没人发现。
    /// </summary>
    public class PathLengthPreflightTests
    {
        private const int RootLength = 6; // "C:\out"

        private const string Root = @"C:\out";

        private static ArchiveEntry File(string path) => new ArchiveEntry { Path = path, Size = 10 };

        private static string RelativeOfFullLength(int fullLength)
        {
            // 完整路径 = 根(6) + 分隔符(1) + 相对路径
            int relativeLength = fullLength - RootLength - 1;

            return new string('a', relativeLength - 4) + ".txt";
        }

        [Fact]
        public void 完整路径正好到阈值_给出提示并点名最长条目()
        {
            string relative = RelativeOfFullLength(240);

            PathLengthPreflightResult result = PathLengthPreflight.Check(new[] { File(relative) }, Root);

            Assert.True(result.HasWarning);
            Assert.Equal(240, result.LongestFullPathLength);
            Assert.Equal(240 - RootLength - 1, result.LongestRelativePathLength);
            Assert.Equal(RootLength, result.TargetRootLength);
            Assert.Equal(1, result.OverLimitEntryCount);
            Assert.Contains(relative, result.Warning);
            Assert.Contains(StatusText.PathTooLong, result.Warning);
        }

        [Fact]
        public void 差一个字符就不到阈值_不提示()
        {
            string relative = RelativeOfFullLength(239);

            PathLengthPreflightResult result = PathLengthPreflight.Check(new[] { File(relative) }, Root);

            Assert.False(result.HasWarning);
            Assert.Equal(string.Empty, result.Warning);
            Assert.Equal(239, result.LongestFullPathLength);
        }

        [Fact]
        public void 提示里带的是最长的那一条_并给出超限条目数()
        {
            string longOne = RelativeOfFullLength(300);
            string alsoLong = RelativeOfFullLength(260);

            PathLengthPreflightResult result = PathLengthPreflight.Check(
                new[] { File("short.txt"), File(alsoLong), File(longOne) },
                Root);

            Assert.True(result.HasWarning);
            Assert.Equal(2, result.OverLimitEntryCount);
            Assert.Equal(300, result.LongestFullPathLength);
            Assert.Equal(longOne, result.LongestRelativePath);
            Assert.Contains(longOne, result.Warning);
            Assert.Contains("2 个条目", result.Warning);
        }

        [Fact]
        public void 目录条目也算_目录建不出来时它下面的文件全落不了盘()
        {
            string longDirectory = new string('a', 300 - RootLength - 1) + "\\";

            PathLengthPreflightResult result = PathLengthPreflight.Check(
                new[] { new ArchiveEntry { Path = longDirectory, IsDirectory = true } },
                Root);

            Assert.True(result.HasWarning);
            Assert.Equal(1, result.OverLimitEntryCount);
            Assert.Equal(longDirectory, result.LongestRelativePath);
        }

        [Fact]
        public void 没有清单时不下结论_不假装安全也不假装有问题()
        {
            PathLengthPreflightResult result = PathLengthPreflight.Check(null, Root);

            Assert.False(result.HasWarning);
            Assert.Equal(string.Empty, result.Warning);
            Assert.Equal(0, result.LongestFullPathLength);
        }

        [Fact]
        public void 没有目标根时只按条目名算_仍然能发现超长条目()
        {
            PathLengthPreflightResult result = PathLengthPreflight.Check(
                new[] { File(new string('b', 300)) },
                null);

            Assert.True(result.HasWarning);
            Assert.Equal(0, result.TargetRootLength);
            Assert.Contains("未提供目标根", result.Warning);
        }

        [Fact]
        public void 空条目被跳过_不会被当成一条超长路径()
        {
            PathLengthPreflightResult result = PathLengthPreflight.Check(
                new[] { File(string.Empty), null!, File("   ") },
                Root);

            Assert.False(result.HasWarning);
            Assert.Equal(0, result.LongestFullPathLength);
            Assert.Equal(0, result.OverLimitEntryCount);
        }

        // ---------------------------------------------------------------- 接线：预检挂在既有的那一次 list 上

        [Fact]
        public void 资源预算里顺带算出路径提示_并拼进给用户看的原因()
        {
            /*
             * 接线点：解压前那一遍 list 的预检里，资源预算已经拿着"完整清单 + 引擎真正写盘的目标根"，
             * 调用方也已经在把非空的 Reason 当提示打日志 —— 所以路径提示拼在 Reason 上，
             * 不需要为了它再列一次目录、也不需要新增一个没人显示的字段。
             */
            string root = Path.Combine(Path.GetTempPath(), "af-pathlen");
            int relativeLength = 240 - root.Length - 1;
            string relative = new string('a', relativeLength - 4) + ".txt";

            var list = new ArchiveListResult
            {
                Success = true,
                Entries = new List<ArchiveEntry> { File(relative) },
                FileCount = 1,
                TotalUncompressedSize = 10
            };

            BudgetCheckResult result = new ResourceBudget().CheckBeforeExtract(list, 1000, root);

            Assert.True(result.Allowed);
            Assert.False(string.IsNullOrWhiteSpace(result.PathLengthWarning));
            Assert.Contains(StatusText.PathTooLong, result.PathLengthWarning);
            Assert.Contains(StatusText.PathTooLong, result.Reason);
            Assert.Equal(root.Length + 1 + relative.Length, result.LongestPathLength);
        }

        [Fact]
        public void 路径不长时资源预算不多说一句()
        {
            var list = new ArchiveListResult
            {
                Success = true,
                Entries = new List<ArchiveEntry> { File("docs/readme.txt") },
                FileCount = 1,
                TotalUncompressedSize = 10
            };

            BudgetCheckResult result = new ResourceBudget().CheckBeforeExtract(list, 1000, Root);

            Assert.True(result.Allowed);
            Assert.Equal(string.Empty, result.PathLengthWarning);
            Assert.DoesNotContain(StatusText.PathTooLong, result.Reason);
        }

        [Fact]
        public void 拿不到清单时资源预算不给路径结论()
        {
            BudgetCheckResult result = new ResourceBudget().CheckBeforeExtract(null, 1000, Root);

            Assert.True(result.Allowed);
            Assert.Equal(string.Empty, result.PathLengthWarning);
            Assert.Equal(0, result.LongestPathLength);
        }
    }
}

using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 「某个文件被搬走了」之后账上两处路径要一起改 —— 唯一出口 <see cref="TaskPathSync"/>。
    ///
    /// <para>为什么值得单测：任务上指向同一个文件的地方有 <c>CurrentPath</c> 与 <c>VolumePaths</c> 两处，
    /// **只改一处**就会留下"盘上已不存在"的旧名字 ⇒ 分卷组搬运判"清单非空、却不含自己"
    /// ⇒ 整组一份都不搬（AGENTS.md §38 真机缺陷的同一形状；内层包那一处 2026-10-02 才补齐）。</para>
    /// </summary>
    public class TaskPathSyncTests
    {
        [Fact]
        public void 搬走之后_当前路径与分卷清单里的旧名字一起改()
        {
            var task = new ArchiveTask(@"C:\src\222.7z.001", 1) { FileName = "222.7z.001" };
            task.VolumePaths.Add(@"C:\src\222.7z.001");
            task.VolumePaths.Add(@"C:\src\222.7z.002");
            task.VolumePaths.Add(@"C:\src\other.7z.001");

            var moves = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [@"C:\src\222.7z.001"] = @"C:\out\222\其余物\222.7z.001"
            };

            TaskPathSync.ApplyMove(task, moves);

            Assert.Equal(@"C:\out\222\其余物\222.7z.001", task.CurrentPath);
            Assert.Equal(@"C:\out\222\其余物\222.7z.001", task.VolumePaths[0]);
            Assert.Equal(@"C:\src\222.7z.002", task.VolumePaths[1]);
            Assert.Equal(@"C:\src\other.7z.001", task.VolumePaths[2]);
        }

        /// <summary>大小写不同也算同一个文件（Windows 上就是同一个，判据口径与别处一致）。</summary>
        [Fact]
        public void 大小写不同也算同一个文件()
        {
            var task = new ArchiveTask(@"c:\src\A.7Z", 1) { FileName = "A.7Z" };
            task.VolumePaths.Add(@"C:\SRC\a.7z");

            TaskPathSync.ApplyMove(
                task,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [@"C:\src\a.7z"] = @"C:\out\a\其余物\a.7z"
                });

            Assert.Equal(@"C:\out\a\其余物\a.7z", task.CurrentPath);
            Assert.Equal(@"C:\out\a\其余物\a.7z", task.VolumePaths[0]);
        }

        /// <summary>一次改一批（内层包搬运那条路是一批兄弟任务共用一份搬迁表）。</summary>
        [Fact]
        public void 一次改一批任务()
        {
            var first = new ArchiveTask(@"C:\src\inner.7z.001", 1) { FileName = "inner.7z.001" };
            var second = new ArchiveTask(@"C:\src\other.7z", 2) { FileName = "other.7z" };
            second.VolumePaths.Add(@"C:\src\inner.7z.001");

            TaskPathSync.ApplyMoves(
                new[] { first, second },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [@"C:\src\inner.7z.001"] = @"C:\out\o\其余物\inner.7z.001"
                });

            Assert.Equal(@"C:\out\o\其余物\inner.7z.001", first.CurrentPath);
            Assert.Equal(@"C:\out\o\其余物\inner.7z.001", second.VolumePaths[0]);
            Assert.Equal(@"C:\src\other.7z", second.CurrentPath);
        }

        /// <summary>没搬家 / 空表 ⇒ 一个字段都不动（⛔ 不许顺手把别人的路径改掉）。</summary>
        [Fact]
        public void 没有搬家时_一个字段都不动()
        {
            var task = new ArchiveTask(@"C:\src\a.7z", 1) { FileName = "a.7z" };
            task.VolumePaths.Add(@"C:\src\a.7z");

            TaskPathSync.ApplyMove(task, new Dictionary<string, string>());
            TaskPathSync.ApplyMove(task, null);
            TaskPathSync.ApplySingleMove(task, @"C:\src\other.7z", @"C:\out\other.7z");

            Assert.Equal(@"C:\src\a.7z", task.CurrentPath);
            Assert.Equal(new[] { @"C:\src\a.7z" }, task.VolumePaths.ToArray());
        }
    }
}

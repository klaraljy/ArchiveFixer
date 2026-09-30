using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 链尾「删除操作」的门槛必须**按链**判，⛔ 不能按名字。
    /// <para>真机现场（2026-09-30，用户 876 行日志）：
    /// <c>333-Rar4.part1.rar：链尾的其余物不处理（链上的「111.part1.rar」没有成功（机器终态：Failed））</c>
    /// —— 那句里的 <c>111.part1.rar</c> 是**另一个目录里的另一个包**。调用方传进来的是整批任务清单，
    /// 旧判据只看"是不是续解任务"，于是 A 链被 B 链的失败挡住。</para>
    /// </summary>
    public sealed class ChainIdentityGateTests
    {
        [Fact]
        public void 另一条链上的同名包失败_不许挡住本链的链尾处理()
        {
            var rootA = new ArchiveTask
            {
                FileName = "111.part1.rar",
                CurrentPath = @"X:\aaa\111\111.part1.rar"
            };

            var otherChainInner = new ArchiveTask
            {
                FileName = "111.part1.rar",
                CurrentPath = @"X:\bbb\333-Rar4\111.part1.rar",
                ParentOutputDirectory = @"X:\bbb\333-Rar4",
                RootSourcePath = @"X:\bbb\333-Rar4.part1.rar"
            };

            otherChainInner.Outcome = TaskOutcome.Failed;

            // 整批清单里同时有本链根任务与**别的链**的内层任务：B 链失败不许影响 A 链。
            Assert.Null(ChainCompletionGate.DescribeBlocker(rootA, new[] { rootA, otherChainInner }));
        }

        [Fact]
        public void 本链上的内层任务失败_仍然必须挡住链尾处理()
        {
            var rootA = new ArchiveTask
            {
                FileName = "111.part1.rar",
                CurrentPath = @"X:\aaa\111\111.part1.rar"
            };

            var ownChainInner = new ArchiveTask
            {
                FileName = "111.part2.rar",
                CurrentPath = @"X:\aaa\111\111.part2.rar",
                ParentOutputDirectory = @"X:\aaa\111",
                RootSourcePath = rootA.CurrentPath
            };

            ownChainInner.Outcome = TaskOutcome.Failed;

            string? blocker = ChainCompletionGate.DescribeBlocker(rootA, new[] { rootA, ownChainInner });

            Assert.NotNull(blocker);
            Assert.Contains("111.part2.rar", blocker!);
        }

        [Fact]
        public void 链身份为空的内层任务_一律按本链处理_宁可不删()
        {
            var rootA = new ArchiveTask
            {
                FileName = "111.part1.rar",
                CurrentPath = @"X:\aaa\111\111.part1.rar"
            };

            // 旧路径 / 直接构造的任务没有链身份 ⇒ 必须当成本链成员（漏掉就是 12 GiB 那次事故）。
            var legacyInner = new ArchiveTask
            {
                FileName = "111.part2.rar",
                CurrentPath = @"X:\aaa\111\111.part2.rar",
                ParentOutputDirectory = @"X:\aaa\111"
            };

            legacyInner.Outcome = TaskOutcome.Failed;

            Assert.NotNull(ChainCompletionGate.DescribeBlocker(rootA, new[] { rootA, legacyInner }));
        }
    }
}

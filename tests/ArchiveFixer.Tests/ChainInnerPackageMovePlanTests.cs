using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 链尾把**内层包收进「其余物」**的搬运计划（<c>ExtractionCoordinator.PlanChainInnerPackageMoves</c>）。
    ///
    /// <para><b>用户 2026-10-01 真机 BBBB / CCCC 两批报的现场</b>（原话："这些压缩包如果没有随着其余物一同删掉的话
    /// 应该是在 <c>…\其余物</c> 里面一起放着同其余物一同删去的"、"111 和 222 里面都残留着，而且残留的都没有在外面
    /// 都在内容物里面"）：三层压缩跑完之后，<c>…\111\111\111\111.part1.rar</c> 与
    /// <c>…\222\222\222\222.zip</c> 这类内层包**留在成品目录里**，而 333 那一组是干净的。</para>
    ///
    /// <para><b>根因（日志原话）</b>：<c>内层包没能移入其余物：… —— The file '…\其余物\111.part1(1).rar' already exists.
    /// （它留在原地）</c> —— 计划里**两个同名内层包算出了同一个目标名**：让位只看文件系统（那一刻两边都还不存在），
    /// 第一条搬成功、第二条就撞名失败。所以这里钉的是"**同一份计划内的撞名也要让位**"。</para>
    /// </summary>
    public class ChainInnerPackageMovePlanTests : IDisposable
    {
        private readonly string _root;

        public ChainInnerPackageMovePlanTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerChainPlan", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch
            {
                // 临时目录清不掉不影响结论。
            }
        }

        private string WriteFile(string relativePath)
        {
            string path = Path.Combine(_root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x");
            return path;
        }

        private static ArchiveTask Continuation(string path, bool succeeded = true)
        {
            // 「是不是续解任务」的判据是 ParentOutputDirectory 非空（ArchiveTask.cs 的只读属性）。
            var task = new ArchiveTask(path) { ParentOutputDirectory = Path.GetDirectoryName(path) ?? string.Empty };
            task.Outcome = succeeded ? TaskOutcome.Succeeded : TaskOutcome.Failed;
            task.OutputVerification = succeeded ? OutputVerificationOutcome.Passed : OutputVerificationOutcome.Failed;
            return task;
        }

        private static ArchiveTask Root(string path, bool succeeded = true)
        {
            var task = new ArchiveTask(path);
            task.Outcome = succeeded ? TaskOutcome.Succeeded : TaskOutcome.Failed;
            task.OutputVerification = succeeded ? OutputVerificationOutcome.Passed : OutputVerificationOutcome.Failed;
            return task;
        }

        /// <summary>
        /// **真机形状**：同一条链里两个内层包**同名**（不同目录），其余物目录里还已经躺着同名的一份
        /// —— 三个名字必须互不相同、且一个都不许指向已存在的那个文件。
        ///
        /// <para>修之前：两条都算出 <c>222(1).zip</c>（让位只问文件系统）⇒ 第一条搬成功、第二条
        /// <c>already exists</c> 留在原地 —— 这正是用户看到的那两个残留。</para>
        /// </summary>
        [Fact]
        public void 同名内层包_计划里的目标名必须互不相同且绝不覆盖()
        {
            string restDirectory = Path.Combine(_root, "222", "其余物");
            Directory.CreateDirectory(restDirectory);

            // 其余物里已经有一份同名的（根任务的源包，早就搬进来了）。
            string existing = Path.Combine(restDirectory, "222.zip");
            File.WriteAllText(existing, "源包");

            string rootOutput = Path.Combine(_root, "222", "222");
            string innerA = WriteFile(@"222\222\222\222.zip");
            string innerB = WriteFile(@"222\222\其他层\222.zip");

            ArchiveTask root = Root(Path.Combine(_root, "222", "222.zip"));
            var chain = new List<ArchiveTask> { root, Continuation(innerA), Continuation(innerB) };

            List<(string From, string To)> moves = ExtractionCoordinator.PlanChainInnerPackageMoves(
                root,
                chain,
                restDirectory,
                rootOutput,
                ContentKeepRules.Empty,
                out List<string> warnings);

            Assert.Empty(warnings);
            Assert.Equal(2, moves.Count);

            // 两个源都要搬。
            Assert.Contains(innerA, moves.Select(m => m.From));
            Assert.Contains(innerB, moves.Select(m => m.From));

            List<string> targets = moves.Select(m => m.To).ToList();

            // ⛔ 目标名互不相同（原来的 bug：两条都是 …\222(1).zip）。
            Assert.Equal(targets.Count, targets.Distinct(StringComparer.OrdinalIgnoreCase).Count());

            // ⛔ 绝不覆盖：谁也不许指到已经存在的那一份上，也不许指到对方身上。
            Assert.DoesNotContain(existing, targets, StringComparer.OrdinalIgnoreCase);

            foreach (string target in targets)
            {
                Assert.False(File.Exists(target), $"目标名不该指向盘上已有的文件：{target}");
                Assert.StartsWith(restDirectory, target, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// 分卷组**整组一起搬**：一个任务的 <c>VolumePaths</c> 有几卷就排几条，且各卷名字互不相同。
        /// （老红线：只搬 <c>.001</c> 会把一套完整的包拆成废件。）
        /// </summary>
        [Fact]
        public void 分卷组内层包_整组一起搬且名字互不相同()
        {
            string restDirectory = Path.Combine(_root, "111", "其余物");
            Directory.CreateDirectory(restDirectory);

            string rootOutput = Path.Combine(_root, "111", "111");
            string v1 = WriteFile(@"111\111\111\111.part1.rar");
            string v2 = WriteFile(@"111\111\111\111.part2.rar");

            ArchiveTask root = Root(Path.Combine(_root, "111", "111.part1.rar"));
            ArchiveTask group = Continuation(v1);
            group.IsVolumeGroup = true;
            group.VolumePaths.Add(v1);
            group.VolumePaths.Add(v2);

            var chain = new List<ArchiveTask> { root, group };

            List<(string From, string To)> moves = ExtractionCoordinator.PlanChainInnerPackageMoves(
                root,
                chain,
                restDirectory,
                rootOutput,
                ContentKeepRules.Empty,
                out _);

            Assert.Equal(2, moves.Count);
            Assert.Contains(v1, moves.Select(m => m.From));
            Assert.Contains(v2, moves.Select(m => m.From));
            Assert.Equal(2, moves.Select(m => m.To).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }

        /// <summary>
        /// **根任务没成功 ⇒ 一个字节都不搬**（不变量 1：失败 / 部分完成 / 取消时源包与过程物原地不动），
        /// 并且要**如实点名**它们还留着（那是给人看的下一步指路，⛔ 不许静默）。
        /// </summary>
        [Fact]
        public void 根任务失败_什么都不搬()
        {
            string restDirectory = Path.Combine(_root, "222", "其余物");
            Directory.CreateDirectory(restDirectory);

            string inner = WriteFile(@"222\222\222\222.zip");

            ArchiveTask root = Root(Path.Combine(_root, "222", "222.zip"), succeeded: false);
            var chain = new List<ArchiveTask> { root, Continuation(inner, succeeded: false) };

            List<(string From, string To)> moves = ExtractionCoordinator.PlanChainInnerPackageMoves(
                root,
                chain,
                restDirectory,
                Path.Combine(_root, "222", "222"),
                ContentKeepRules.Empty,
                out _);

            Assert.Empty(moves);
        }

        /// <summary>
        /// 根任务成功、但链上层那一单**没成功** ⇒ 照旧搬（用户 2026-09-28 拍板：内层包本来就是外层解出来的过程物，
        /// 外层成功了它就该跟源包一起进其余物 —— "成功了就刚好是我们要达到的地方，失败了也不会删除"）。
        /// </summary>
        [Fact]
        public void 根任务成功而链上那一单没成功_照旧搬()
        {
            string restDirectory = Path.Combine(_root, "222", "其余物");
            Directory.CreateDirectory(restDirectory);

            string inner = WriteFile(@"222\222\222\222.zip");

            ArchiveTask root = Root(Path.Combine(_root, "222", "222.zip"));
            var chain = new List<ArchiveTask> { root, Continuation(inner, succeeded: false) };

            List<(string From, string To)> moves = ExtractionCoordinator.PlanChainInnerPackageMoves(
                root,
                chain,
                restDirectory,
                Path.Combine(_root, "222", "222"),
                ContentKeepRules.Empty,
                out _);

            Assert.Single(moves);
            Assert.Equal(inner, moves[0].From);
        }
    }
}

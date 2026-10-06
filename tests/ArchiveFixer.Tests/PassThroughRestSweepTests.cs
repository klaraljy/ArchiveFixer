using System;
using System.Collections.Generic;
using System.IO;
using ArchiveFixer.Models;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **"过路层的其余物"该不该放行**（真机 CCCC 2026-10-06，用户原话：
    /// 「**其余物会在每层的过程中会删掉**，我无法理解为什么还会有其余物留着」）。
    ///
    /// <para><b>现场</b>：`111.rar` 只出过程物（它解出来的入口包 `111.zip` 被**采纳进别的层**），
    /// 于是它自己那条链停在中途、终态是**「部分完成」**⇒ 老判据（只放行"成功"）**永远把它挡在外面**
    /// ⇒ 那份 `111\111\其余物\111.zip`（48.35 MB）一直留在盘上。</para>
    ///
    /// <para><b>判据</b>（唯一出口 <see cref="ExtractionCoordinator.CanSweepPassThroughRest"/>）：
    /// ① 没失败、没取消；② 其余物目录在盘上且不是空的；③ **其余物里每一个文件都已在
    /// "借来的源片"账里**（= 已被下游接手）。三条同时成立才放行；⛔ 只差一条就什么都不做。</para>
    /// </summary>
    public class PassThroughRestSweepTests : IDisposable
    {
        private readonly string _root;

        public PassThroughRestSweepTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "af-passthrough-rest-" + Guid.NewGuid().ToString("N"));
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

        /// <summary>
        /// **真机那一格正是"部分完成"** ⇒ 必须放行（这正是本轮修的）。
        ///
        /// <para><b>红检</b>：把判据换回"只放行 `Outcome == Succeeded`" ⇒ 本条变红
        /// （真机那份 `111\111\其余物\111.zip` 就是被它挡住的）。</para>
        /// </summary>
        [Fact]
        public void 过路层是部分完成_但其余物里的东西已被下游接手_放行()
        {
            string rest = Path.Combine(_root, "111", "111", "其余物");

            Directory.CreateDirectory(rest);

            string piece = Path.Combine(rest, "111.zip");

            File.WriteAllText(piece, "入口包");

            var task = new ArchiveTask(Path.Combine(_root, "111", "111.rar"), 1)
            {
                RestDirectoryPath = rest,
                Outcome = TaskOutcome.PartiallyCompleted
            };

            var consumed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [piece] = "111.zip"
            };

            Assert.True(ExtractionCoordinator.CanSweepPassThroughRest(task, consumed));
        }

        /// <summary>
        /// ⛔ **红线三连**：失败 / 取消 / 其余物里还有没被下游接手的东西 ⇒ **一律什么都不做**
        /// （那是失败现场的凭证，删掉就再也查不出为什么没做成）。
        /// </summary>
        [Fact]
        public void 失败或取消或还有没接手的东西_一律不放行()
        {
            string rest = Path.Combine(_root, "pack", "其余物");

            Directory.CreateDirectory(rest);

            string adopted = Path.Combine(rest, "inner.zip");
            string notAdopted = Path.Combine(rest, "leftover.bin");

            File.WriteAllText(adopted, "已接手");
            File.WriteAllText(notAdopted, "没人接手");

            var consumed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [adopted] = "consumer.zip"
            };

            // ① 失败 ⇒ 不放行。
            var failed = new ArchiveTask(Path.Combine(_root, "pack.7z"), 1)
            {
                RestDirectoryPath = rest,
                Outcome = TaskOutcome.Failed
            };

            Assert.False(ExtractionCoordinator.CanSweepPassThroughRest(failed, consumed));

            // ② 取消 ⇒ 不放行。
            var cancelled = new ArchiveTask(Path.Combine(_root, "pack.7z"), 1)
            {
                RestDirectoryPath = rest,
                Outcome = TaskOutcome.Cancelled
            };

            Assert.False(ExtractionCoordinator.CanSweepPassThroughRest(cancelled, consumed));

            // ③ 其余物里**还有没被接手的** ⇒ 不放行（哪怕同一单是"部分完成"）。
            var partial = new ArchiveTask(Path.Combine(_root, "pack.7z"), 1)
            {
                RestDirectoryPath = rest,
                Outcome = TaskOutcome.PartiallyCompleted
            };

            Assert.False(ExtractionCoordinator.CanSweepPassThroughRest(partial, consumed));

            // ④ 账是空的 ⇒ 判不出 ⇒ 不放行。
            var onlyAdopted = Path.Combine(_root, "pack2", "其余物");

            Directory.CreateDirectory(onlyAdopted);
            File.WriteAllText(Path.Combine(onlyAdopted, "inner.zip"), "x");

            var task2 = new ArchiveTask(Path.Combine(_root, "pack2.7z"), 1)
            {
                RestDirectoryPath = onlyAdopted,
                Outcome = TaskOutcome.Succeeded
            };

            Assert.False(ExtractionCoordinator.CanSweepPassThroughRest(
                task2,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));

            // ⑤ 其余物目录不在盘上 ⇒ 不抢那一档的活。
            var missing = new ArchiveTask(Path.Combine(_root, "gone.7z"), 1)
            {
                RestDirectoryPath = Path.Combine(_root, "不存在的其余物"),
                Outcome = TaskOutcome.Succeeded
            };

            Assert.False(ExtractionCoordinator.CanSweepPassThroughRest(missing, consumed));
        }

        /// <summary>
        /// **成功那一档照旧放行**（别把原来就走得通的路一起挡住）；空目录由既有"空壳"那一档处理。
        /// </summary>
        [Fact]
        public void 成功且已全部接手_放行_空目录不在这条路上()
        {
            string rest = Path.Combine(_root, "ok", "其余物");

            Directory.CreateDirectory(rest);

            string piece = Path.Combine(rest, "inner.zip");

            File.WriteAllText(piece, "x");

            var task = new ArchiveTask(Path.Combine(_root, "ok.7z"), 1)
            {
                RestDirectoryPath = rest,
                Outcome = TaskOutcome.Succeeded
            };

            var consumed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [piece] = "consumer.zip"
            };

            Assert.True(ExtractionCoordinator.CanSweepPassThroughRest(task, consumed));

            // 空目录：`any == false` ⇒ 不在这条路上（空壳由既有的 `RestItemPurger` 那一档处理）。
            string emptyRest = Path.Combine(_root, "empty", "其余物");

            Directory.CreateDirectory(emptyRest);

            var emptyTask = new ArchiveTask(Path.Combine(_root, "empty.7z"), 1)
            {
                RestDirectoryPath = emptyRest,
                Outcome = TaskOutcome.Succeeded
            };

            Assert.False(ExtractionCoordinator.CanSweepPassThroughRest(emptyTask, consumed));
        }
    }
}

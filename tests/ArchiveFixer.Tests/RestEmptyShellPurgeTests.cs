using System;
using System.Collections.Generic;
using System.IO;
using ArchiveFixer.Models;
using ArchiveFixer.Storage;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **空壳不进回收站**（用户 2026-10-03 真机反馈）：「其余物」里递归地一个文件都没有（只剩空目录结构）时，
    /// <see cref="RestItemPurger.Purge"/> 与 <see cref="RestItemPurger.PurgeExcept"/> 一律**就地永久删掉**，
    /// ⛔ 不再把整个 `其余物\` 目录送进回收站 —— 回收站里那堆 1 KB 的同名「其余物」文件夹就是这么堆起来的。
    ///
    /// <para>判据唯一出口 = <c>RestItemPurger.IsFileFreeDirectoryTree</c>（只读文件系统事实：有没有文件）。
    /// 四条用例钉四件事：① 空壳就地删 + 回收站零调用；② 有真文件照旧走回收站（防回归对照，断言不放宽）；
    /// ③ 目录不存在 / 形状不像「其余物」照旧 Skip（空壳判据排在六道门槛之后，不许抢跑）；
    /// ④ 部分完成那一档（<c>PurgeExcept</c>）走同一条路。</para>
    ///
    /// <para>与 <see cref="RecycleBinServiceTests"/> 同一套自我约束：**绝不往用户系统回收站里塞东西** ——
    /// 回收站那一档全部走注入的假执行器；只在 <see cref="Path.GetTempPath"/> 下的独立临时目录里动真实文件系统，
    /// <see cref="Dispose"/> 里清干净。测试数据一律合成值（`222.7z` / `payload.bin`），不含真实用户数据（AGENTS.md §8）。</para>
    /// </summary>
    public class RestEmptyShellPurgeTests : IDisposable
    {
        private readonly string _root;

        public RestEmptyShellPurgeTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerRestShellTests", Guid.NewGuid().ToString("N"));
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
                // 临时目录清不掉不影响测试结论。
            }
        }

        // ================================================================ ① 只剩空目录结构 ⇒ 就地删掉

        /// <summary>
        /// 其余物里**只剩空目录结构**（两层嵌套空目录，一个文件都没有）⇒ 把那整个空壳目录就地删掉，
        /// **回收站一次都没碰**（假执行器零调用），并返回成功（释放 0 字节、0 个条目 —— 空壳本来就不占空间）。
        /// </summary>
        [Fact]
        public void 其余物里只剩空目录结构_就地删掉空壳_回收站零调用()
        {
            (ArchiveTask task, string restDirectory) = CreatePurgeScenario();

            // 空壳现场：`其余物\a\b\` 两层嵌套空目录，判据必须递归地看到"没有文件"。
            Assert.True(Directory.Exists(Path.Combine(restDirectory, "a", "b")));
            Assert.Empty(Directory.GetFiles(restDirectory, "*", SearchOption.AllDirectories));

            var executor = new FakeDeleteExecutor();

            // 走「移入回收站」那一档 —— 用户真机上正是这一档把回收站塞满的。
            RestPurgeOutcome outcome = new RestItemPurger(executor)
                .Purge(task, cancelled: false, DeleteMode.RecycleBin);

            Assert.True(outcome.Succeeded, outcome.Message);

            // 回收站零调用（连"彻底删除"那条也没走执行器）。
            Assert.Empty(executor.RecycleCalls);
            Assert.Empty(executor.PermanentCalls);

            Assert.False(Directory.Exists(restDirectory), "只剩空壳的其余物必须就地删掉（嵌套空目录一起），不是送回收站");
            Assert.Equal(0L, outcome.FreedBytes);
            Assert.Equal(0, outcome.EntryCount);
            Assert.True(outcome.RemovedAsEmptyShell, "空壳这一档必须留下事实位（调用方据此选文案，⛔ 不许按数字猜）");
            Assert.Contains("空壳", outcome.Message, StringComparison.Ordinal);
            Assert.Contains("没有送进回收站", outcome.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// **空壳那一行的措辞**（用户 2026-10-03）：走「移入回收站」档也必须说"没有进回收站"，
        /// ⛔ 不许复用"档名 + 条目数"那句 —— 否则真机上会写成
        /// 「移入回收站：…\其余物（0 项 / 0 B）」，而空壳**一个字节都没进回收站**。
        /// </summary>
        [Fact]
        public void 空壳那一行_按事实位选文案_回收站档也不谎称进了回收站()
        {
            var shell = new RestPurgeOutcome
            {
                Attempted = true,
                Succeeded = true,
                Directory = Path.Combine(_root, "out", "222", "其余物"),
                RemovedAsEmptyShell = true
            };

            string shellLine = ExtractionCoordinator.DescribeRestPurgedLine(shell, DeleteMode.RecycleBin);

            Assert.Contains("没有进回收站", shellLine, StringComparison.Ordinal);
            Assert.DoesNotContain(StatusText.RestActionRecycleBin, shellLine, StringComparison.Ordinal);

            // 对照：普通档照旧是"档名 + 条目数"那句（⛔ 这一档一个字都没改）。
            var normal = new RestPurgeOutcome
            {
                Attempted = true,
                Succeeded = true,
                Directory = Path.Combine(_root, "out", "222", "其余物"),
                EntryCount = 3,
                FreedBytes = 2048
            };

            string normalLine = ExtractionCoordinator.DescribeRestPurgedLine(normal, DeleteMode.RecycleBin);

            Assert.Contains(StatusText.RestActionRecycleBin, normalLine, StringComparison.Ordinal);
            Assert.Contains("3 项", normalLine, StringComparison.Ordinal);
        }

        // ================================================================ ② 有真文件 ⇒ 照旧走回收站（对照）

        /// <summary>
        /// **防回归对照**：其余物里**有一个真文件**（摆在最里面那一层）⇒ 判据必须看不见"空"，照旧整份走回收站
        /// （假执行器恰好被调用 1 次、参数就是那个目录）。⛔ 断言不许放宽 —— 这一条钉的就是
        /// "只有空壳才就地删，别的一律按用户选的档走"。
        /// </summary>
        [Fact]
        public void 其余物里有一个真文件_照旧走回收站_执行器恰好一次()
        {
            (ArchiveTask task, string restDirectory) = CreatePurgeScenario();

            string deepFile = Path.Combine(restDirectory, "a", "b", "payload.bin");
            File.WriteAllText(deepFile, "真文件（钻进最里面那一层，看判据是不是真的递归）");

            var executor = new FakeDeleteExecutor();
            RestPurgeOutcome outcome = new RestItemPurger(executor)
                .Purge(task, cancelled: false, DeleteMode.RecycleBin);

            Assert.Equal(restDirectory, Assert.Single(executor.RecycleCalls));
            Assert.Empty(executor.PermanentCalls);
            Assert.True(outcome.Succeeded, outcome.Message);
            Assert.False(File.Exists(deepFile), "照旧走回收站（假执行器把整个目录搬走）");
            Assert.Contains(
                RestItemPurger.AutoRecycleReason,
                string.Join(" | ", outcome.LogLines),
                StringComparison.Ordinal);
        }

        // ================================================================ ③ 老的门槛照旧

        /// <summary>
        /// 老门槛一个字没放宽：**目录不存在**、**形状不像「其余物」**（哪怕它就是个空壳）⇒ 照旧 <c>Skip</c>
        /// （<c>Attempted == false</c>、回收站零调用、盘上一个字节都不动）。
        ///
        /// <para>第二条还兼着钉顺序：空壳判据必须排在**六道门槛之后** —— 排在前面的话，
        /// 形状不像的目录只要是个空壳就会被就地删掉。</para>
        /// </summary>
        [Fact]
        public void 目录不存在或形状不像其余物_照旧跳过()
        {
            // ① 其余物目录不存在。
            (ArchiveTask missingTask, string missingRest) = CreatePurgeScenario(createRestDirectory: false);
            var missingExecutor = new FakeDeleteExecutor();

            RestPurgeOutcome missing = new RestItemPurger(missingExecutor)
                .Purge(missingTask, cancelled: false, DeleteMode.RecycleBin);

            Assert.False(missing.Attempted);
            Assert.False(missing.Succeeded);
            Assert.Empty(missingExecutor.RecycleCalls);
            Assert.Contains("不存在", missing.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(missingRest));

            // ② 在自己输出根之内、但形状不像「其余物」的**空壳**目录。
            (ArchiveTask shapedTask, string restDirectory) = CreatePurgeScenario();
            string contentDirectory = Path.Combine(shapedTask.OutputPath!, "内容物");
            Directory.CreateDirectory(Path.Combine(contentDirectory, "还没有内容"));
            shapedTask.RestDirectoryPath = contentDirectory;

            var shapedExecutor = new FakeDeleteExecutor();
            RestPurgeOutcome shaped = new RestItemPurger(shapedExecutor)
                .Purge(shapedTask, cancelled: false, DeleteMode.RecycleBin);

            Assert.False(shaped.Attempted);
            Assert.Empty(shapedExecutor.RecycleCalls);
            Assert.Contains("形状不像", shaped.Message, StringComparison.Ordinal);
            Assert.True(Directory.Exists(contentDirectory), "形状不像其余物 ⇒ 一个字节都不许动（哪怕它是空壳）");
            Assert.True(Directory.Exists(Path.Combine(contentDirectory, "还没有内容")));
            Assert.True(Directory.Exists(restDirectory), "被拒绝的那一次不许顺手把真的其余物也删掉");
        }

        // ================================================================ ④ 部分完成那一档（PurgeExcept）

        /// <summary>
        /// 部分完成那一档（<see cref="RestItemPurger.PurgeExcept"/>）：按 <c>keepPaths</c> 排除之后
        /// **剩下的全是空目录** ⇒ 走同一条"就地删掉"的路（回收站零调用）。
        ///
        /// <para>⛔ 要保留的那一份（这条链的最外层源包）怎么算、怎么留，一个字没变：它还在盘上、
        /// 其余物目录本身照旧留着（只删里面的项）。</para>
        /// </summary>
        [Fact]
        public void 部分完成那一档_剩下的全是空目录_同样就地删掉()
        {
            (ArchiveTask task, string restDirectory) = CreatePurgeScenario();

            string sourcePackage = Path.Combine(_root, "src", "222.7z");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePackage)!);
            File.WriteAllText(sourcePackage, "最外层源包（必须留住）");

            var executor = new FakeDeleteExecutor();
            RestPurgeOutcome outcome = new RestItemPurger(executor)
                .PurgeExcept(task, new[] { sourcePackage }, DeleteMode.Permanent);

            Assert.True(outcome.Succeeded, outcome.Message);
            Assert.Empty(executor.RecycleCalls);
            Assert.Empty(executor.PermanentCalls);
            Assert.True(File.Exists(sourcePackage), "要保留的那一份（最外层源包）一个字节都不许动");
            Assert.Empty(Directory.GetFileSystemEntries(restDirectory));
            Assert.True(Directory.Exists(restDirectory), "只删掉里面的空目录，其余物目录本身照旧留着（老口径不变）");
            Assert.True(outcome.RemovedAsEmptyShell, "PurgeExcept 的空壳支同样要留事实位");
            Assert.Contains("没有送进回收站", outcome.Message, StringComparison.Ordinal);
        }

        // ================================================================ 装配

        /// <summary>
        /// 造一个"其余物该动手"的现场：完成 + **可证完整** + `<输出>\222\其余物` 里**只有两层嵌套空目录**。
        /// 形状与 <c>DeleteOptionsClarityTests.CreatePurgeScenario</c> 同一套，区别只是里面一个文件都没有。
        /// </summary>
        private (ArchiveTask Task, string RestDirectory) CreatePurgeScenario(bool createRestDirectory = true)
        {
            string outputPath = Path.Combine(_root, "out", "222");
            string restDirectory = Path.Combine(outputPath, "其余物");

            Directory.CreateDirectory(outputPath);
            File.WriteAllText(Path.Combine(outputPath, "payload.mp4"), "内容物");

            if (createRestDirectory)
            {
                Directory.CreateDirectory(Path.Combine(restDirectory, "a", "b"));
            }

            var task = new ArchiveTask(Path.Combine(_root, "src", "222.7z"), 1)
            {
                FileName = "222.7z",
                OutputPath = outputPath,
                Status = StatusText.ExtractSuccess,
                IsOutputVerified = true,

                // L4：只有"拿清单核对过"才算可证完整，也才允许删源包（见 ResultCompletenessTests）。
                OutputManifestCrossChecked = true,
                Outcome = TaskOutcome.Succeeded,
                RestDirectoryPath = restDirectory,
                VolumeGroupKey = "222"
            };

            return (task, restDirectory);
        }

        /// <summary>记录调用、可脚本化结果的假执行器：全程不碰系统回收站（照抄 RecycleBinServiceTests 那份）。</summary>
        private sealed class FakeDeleteExecutor : IDeleteExecutor
        {
            public RecycleAttemptResult RecycleResult { get; set; } = RecycleAttemptResult.Recycled;

            public string RecycleMessage { get; set; } = "已移入回收站（假执行器）";

            /// <summary>报"回收站成功"时是否真的把目标从临时目录里移走（模拟"搬进回收站"）。</summary>
            public bool SimulateRemovalOnRecycle { get; set; } = true;

            public List<string> RecycleCalls { get; } = new();

            public List<string> PermanentCalls { get; } = new();

            public RecycleAttemptResult TryMoveToRecycleBin(string path, bool isDirectory, out string message)
            {
                RecycleCalls.Add(path);
                message = RecycleMessage;

                if (RecycleResult == RecycleAttemptResult.Recycled && SimulateRemovalOnRecycle)
                {
                    if (isDirectory)
                    {
                        Directory.Delete(path, recursive: true);
                    }
                    else
                    {
                        File.Delete(path);
                    }
                }

                return RecycleResult;
            }

            public void DeletePermanently(string path, bool isDirectory)
            {
                PermanentCalls.Add(path);
                Directory.Delete(path, recursive: true);
            }
        }
    }
}

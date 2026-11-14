using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using ArchiveFixer.Engines;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using Xunit;
using Xunit.Abstractions;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 失败清单的**两级结构**（抄 WinRAR <c>-log[AF]</c> 的 A/F 分组）与"结果可追溯"。
    ///
    /// 旧实现只有一行 <c>文件名 - 原因</c>：一个包里有 3 个条目失败、另 200 个成功时根本说不清
    /// （M3 的验收判据就是"能说清每个为什么失败"）。现在的形态：
    ///
    /// <code>
    /// [归档] 333.7z - 密码错误
    ///   引擎：7-Zip 命令行 26.01.0.0
    ///   层级：第 0 层（用户给的源包）
    ///   校验：校验未通过：预期 200 个文件 / …，实际 197 个 / …
    ///   条目：docs\a.bin - CRC Failed
    ///   位置：E:\x\333.7z
    /// </code>
    ///
    /// 第二级里"引擎 / 层级 / 校验 / 分卷 / 位置"都来自任务自身已有的结构化字段；
    /// "条目"由 <see cref="TaskSummaryService.EntryDetailProvider"/> 提供（有就写，没有就不编）。
    /// </summary>
    public class FailedListFormatTests
    {
        private readonly ITestOutputHelper _output;

        public FailedListFormatTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private static ArchiveTask Failed(string fileName, string status, string message)
        {
            return new ArchiveTask($@"E:\x\{fileName}")
            {
                Status = status,
                ErrorMessage = message,
                IsSelected = true,
                IsArchive = true,
                DetectedFormat = "7Z",
                ExtensionStatus = StatusText.ExtensionNormal
            };
        }

        [Fact]
        public void 失败清单是两级_归档一行加缩进的第二级()
        {
            var service = new TaskSummaryService();
            ArchiveTask task = Failed("333.7z", StatusText.Corrupted, "压缩包可能损坏或下载不完整");

            string text = service.BuildFailedListText(new[] { task });
            string[] lines = text.Split(Environment.NewLine);

            int archiveIndex = Array.FindIndex(lines, l => l.Contains("[归档] 333.7z"));

            Assert.True(archiveIndex >= 0, "没有归档那一行");
            Assert.Contains("压缩包可能损坏或下载不完整", lines[archiveIndex]);

            // 第二级：紧跟着的缩进行（引擎 / 层级 / 位置），且都缩进。
            Assert.StartsWith(TaskSummaryService.DetailIndent, lines[archiveIndex + 1]);
            Assert.Contains("引擎：", lines[archiveIndex + 1]);
            Assert.Contains("层级：", lines[archiveIndex + 2]);
            Assert.Contains("位置：", text);
        }

        [Fact]
        public void 失败清单带引擎名与版本_不变量14()
        {
            var service = new TaskSummaryService();
            ArchiveTask task = Failed("444.rar", StatusText.ExtractFailed, "解压失败");

            string text = service.BuildFailedListText(new[] { task });

            // 表头一行、每个归档的第二级一行 —— 两处都要有引擎名 + 版本。
            Assert.Contains("引擎：" + service.EngineIdentity.Describe(), text);

            // 版本不许是编的：解析不到时只写引擎名（Describe 已经处理），这里确认 Describe 有内容。
            Assert.False(string.IsNullOrWhiteSpace(service.EngineIdentity.Describe()));
            Assert.DoesNotContain(EngineIdentity.UnknownEngineText, text);
        }

        [Fact]
        public void 条目级明细由提供方给出_并缩进在归档之下()
        {
            var service = new TaskSummaryService
            {
                EntryDetailProvider = _ => new[] { @"docs\a.bin - CRC Failed", @"docs\b.bin - Data Error" }
            };

            ArchiveTask task = Failed("555.7z", StatusText.PartiallyCompleted, "7-Zip 报告非致命错误（退出码 1）");

            string text = service.BuildFailedListText(new[] { task });

            Assert.Contains(TaskSummaryService.DetailIndent + "条目：" + @"docs\a.bin - CRC Failed", text);
            Assert.Contains(TaskSummaryService.DetailIndent + "条目：" + @"docs\b.bin - Data Error", text);
        }

        [Fact]
        public void 没有条目明细时不编条目_第二级仍然完整()
        {
            var service = new TaskSummaryService();
            ArchiveTask task = Failed("666.7z", StatusText.ExtractFailed, "解压失败");

            string text = service.BuildFailedListText(new[] { task });

            Assert.DoesNotContain("条目：", text);
            Assert.Contains("引擎：", text);
        }

        [Fact]
        public void 明细来源抛异常不能把整份清单带崩()
        {
            var service = new TaskSummaryService
            {
                EntryDetailProvider = _ => throw new InvalidOperationException("明细来源坏了")
            };

            ArchiveTask task = Failed("777.7z", StatusText.ExtractFailed, "解压失败");

            string text = service.BuildFailedListText(new[] { task });

            Assert.Contains("[归档] 777.7z", text);
        }

        [Fact]
        public void 校验结论与引擎结论原样带出_里面有条目数落差()
        {
            var service = new TaskSummaryService();
            ArchiveTask task = Failed("888.7z", StatusText.PartiallyCompleted, "部分完成");
            task.VerifyMessage = "校验未通过：预期 200 个文件 / 1000 字节，实际 197 个 / 900 字节";
            task.EngineVerdict = "7-Zip 命令行 26.01 能打开：200 个文件";

            string text = service.BuildFailedListText(new[] { task });

            Assert.Contains(TaskSummaryService.DetailIndent + "校验：" + task.VerifyMessage, text);
            Assert.Contains(TaskSummaryService.DetailIndent + "引擎结论：" + task.EngineVerdict, text);
        }

        [Fact]
        public void 分卷失败要报缺哪几个_不变量7()
        {
            var service = new TaskSummaryService();
            ArchiveTask task = Failed("999.7z.001", StatusText.VolumeMissing, "分卷压缩包缺少必要分卷");
            task.IsVolumeGroup = true;
            task.MissingVolumeNames.Add("999.7z.002");
            task.MissingVolumeNames.Add("999.7z.003");

            string text = service.BuildFailedListText(new[] { task });

            Assert.Contains("分卷：缺少 999.7z.002、999.7z.003", text);
        }

        [Fact]
        public void 内层包要说清属于哪个父包_这就是失败层()
        {
            var service = new TaskSummaryService();
            ArchiveTask task = Failed("inner.7z", StatusText.Corrupted, "压缩包可能损坏");
            task.ParentOutputDirectory = @"E:\x\outer\内容物";
            task.ParentTaskName = "outer.7z";

            string text = service.BuildFailedListText(new[] { task });

            Assert.Contains("层级：内层包（父包：outer.7z）", text);
        }

        [Fact]
        public void 表头带固定格式的生成时间与失败计数()
        {
            var service = new TaskSummaryService();

            ArchiveTask a = Failed("a.7z", StatusText.ExtractFailed, "解压失败");
            ArchiveTask b = Failed("b.7z", StatusText.Corrupted, "文件损坏");
            ArchiveTask c = Failed("c.7z", StatusText.ExtractSuccess, string.Empty);

            a.Outcome = TaskOutcome.Failed;
            b.Outcome = TaskOutcome.Failed;
            c.Outcome = TaskOutcome.Succeeded;

            string text = service.BuildFailedListText(new[] { a, b, c });

            // 生成时间是固定格式（不随区域设置变）；数法**转调 BatchOutcomeTally**（用户 2026-10-04 真机）。
            Assert.Matches(@"生成时间：\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}", text);
            Assert.Contains("本次逐条列出：2 个（全批 3 个 —— 成功 1 / 失败 2 / 跳过 0）", text);
        }

        /// <summary>
        /// **表头口径与批末汇总必须是同一套数法**（用户 2026-10-04 真机）：
        /// 那份清单过去写「失败：2 个任务」，可其中 `第6集.7z` 的机器终态是**部分完成**
        /// （批末汇总是"部分完成 1"）—— 用户拿清单去对批末那行怎么都对不上。
        ///
        /// <para><b>红检</b>：把表头改回自己 `failedTasks.Count` 那一套
        /// （不转调 <see cref="BatchOutcomeTally"/>）⇒ 本用例当场红（会写成"失败：2 个任务"）。</para>
        /// </summary>
        [Fact]
        public void 表头按机器终态数_部分完成不算失败()
        {
            var service = new TaskSummaryService();

            ArchiveTask ok = Failed("ok.7z", StatusText.ExtractSuccess, string.Empty);
            ArchiveTask partial = Failed("partial.7z", StatusText.PartiallyCompleted, "部分完成");
            ArchiveTask broken = Failed("broken.7z", StatusText.ExtractFailed, "解压失败");

            ok.Outcome = TaskOutcome.Succeeded;
            partial.Outcome = TaskOutcome.PartiallyCompleted;
            broken.Outcome = TaskOutcome.Failed;

            string text = service.BuildFailedListText(new[] { ok, partial, broken });

            // 逐条列出的是"要看原因的那两个"（失败 + 部分完成），而全批的分项按机器终态数。
            Assert.Contains("本次逐条列出：2 个（全批 3 个 —— 成功 1 / 失败 1 / 跳过 0 / 部分完成 1）", text);

            // ⛔ 不许再把「部分完成」算进"失败"。
            Assert.DoesNotContain("失败：2 个任务", text);
        }

        /// <summary>
        /// **其余物为什么还在要进失败清单**（用户 2026-10-04 真机）：那一句可能落在**成功**的那个任务上
        /// （真机 `Sociology.7z` 解压成功、只是链上另一层没成功 ⇒ 其余物一个字节都不删），
        /// 所以它必须**单独一段**列出来，⛔ 不能塞进逐归档的第二级（塞了就一个字都不会出现）。
        ///
        /// <para><b>红检</b>：撤掉 <c>AppendRestKeptSection</c> 那次调用 ⇒ 本用例当场红。</para>
        /// </summary>
        [Fact]
        public void 其余物没处理要单独一段_成功的那一单也会出现()
        {
            var service = new TaskSummaryService();

            ArchiveTask ok = Failed("outer.7z", StatusText.ExtractSuccess, string.Empty);
            ok.Outcome = TaskOutcome.Succeeded;
            ok.RestDirectoryPath = @"E:\x\outer\其余物";
            ok.RestKeptReason = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.RestKeptNoteFormat,
                ok.RestDirectoryPath,
                "链上的「inner.7z」没有成功（机器终态：Failed）");

            string text = service.BuildFailedListText(new[] { ok });

            Assert.Contains("其余物没有处理（1 条）", text);
            Assert.Contains("outer.7z：", text);
            Assert.Contains(@"E:\x\outer\其余物", text);
            Assert.Contains("没有成功", text);
        }

        [Fact]
        public void 没有失败任务时给一句明确结论_而不是空文本()
        {
            var service = new TaskSummaryService();

            string text = service.BuildFailedListText(new List<ArchiveTask>());

            Assert.Contains("没有失败任务", text);
            Assert.Contains("引擎：", text);
        }

        [Fact]
        public void 部分完成也会进失败清单_不许消失在成功里()
        {
            var service = new TaskSummaryService();
            ArchiveTask task = Failed("partial.7z", StatusText.PartiallyCompleted, "部分文件可能没有解出");

            List<ArchiveTask> failed = service.GetFailedTasks(new[] { task });

            Assert.Single(failed);
            Assert.Contains("partial.7z", service.BuildFailedListText(new[] { task }));
        }

        [Fact]
        public void 引擎信息可注入_不写死7zip()
        {
            var service = new TaskSummaryService
            {
                EngineIdentity = new EngineIdentity
                {
                    EngineId = "libarchive",
                    DisplayName = "libarchive",
                    Version = "3.7.4",
                    IsAvailable = true
                }
            };

            string text = service.BuildFailedListText(new[]
            {
                Failed("x.7z", StatusText.ExtractFailed, "解压失败")
            });

            Assert.Contains("引擎：libarchive 3.7.4", text);
            Assert.DoesNotContain("7-Zip", text);
        }

        [Fact]
        public void 引擎版本未知时只写引擎名_不编版本号()
        {
            var identity = new EngineIdentity
            {
                EngineId = "sevenzip",
                DisplayName = "7-Zip 命令行",
                Version = "unknown",
                IsAvailable = true
            };

            Assert.Equal("7-Zip 命令行", identity.Describe());
        }

        [Fact]
        public void 打印一份样例失败清单_便于人工核对格式()
        {
            var service = new TaskSummaryService
            {
                EngineIdentity = new EngineIdentity
                {
                    EngineId = "sevenzip",
                    DisplayName = "7-Zip 命令行",
                    Version = "26.01",
                    IsAvailable = true
                },
                EntryDetailProvider = _ => new[] { @"docs\a.bin - CRC Failed", @"docs\b.bin - Data Error" }
            };

            ArchiveTask broken = Failed("333.7z", StatusText.PartiallyCompleted, "部分文件可能没有解出");
            broken.VerifyMessage = "校验未通过：预期 200 个文件 / 1000 字节，实际 197 个 / 900 字节";

            ArchiveTask missingVolume = Failed("444.7z.001", StatusText.VolumeMissing, "分卷压缩包缺少必要分卷");
            missingVolume.IsVolumeGroup = true;
            missingVolume.MissingVolumeNames.Add("444.7z.002");

            string text = service.BuildFailedListText(new[] { broken, missingVolume });

            _output.WriteLine(text);

            // 形态自检（真正的断言在上面各条；这条只保证样例本身是完整的）。
            Assert.Contains("[归档] 333.7z", text);
            Assert.Contains("[归档] 444.7z.001", text);
        }

        [Fact]
        public void 导出文本里不含明文密码格式()
        {
            var service = new TaskSummaryService();
            ArchiveTask task = Failed("pw.7z", StatusText.WrongPassword, "密码错误或缺少正确密码");

            string text = service.BuildFailedListText(new[] { task });

            // 清单里只允许出现"密码状态"，不允许出现 -p 明文这类形态（不变量 5）。
            Assert.DoesNotMatch(new Regex(@"-p[^\s]"), text);
        }
    }
}

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using ArchiveFixer.Models;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **列表那一行"显示谁"**（用户 2026-10-06：「列表里面显示的没有一个是对的」「后缀也不同步」）。
    ///
    /// <para>规矩只有两条：① 记下来的那份文件**必须还在盘上**才算数（改名 / 被搬走 / 被删 ⇒ 回落这一行自己的当前值，
    /// ⛔ 绝不显示一个已经不存在的名字）；② 改名 / 重扫之后**五列一起发变更通知**，否则行会停在旧名字上。</para>
    ///
    /// <para><b>红检</b>：把 <c>DisplaySourcePath</c> 的核盘那一句改成直接返回 <c>_userFacingFilePath</c> ⇒
    /// <c>记下来的那份不在了_回落当前值_不许显示旧名</c> 变红；把 <c>NotifyDisplayIdentityChanged</c> 那一调注掉 ⇒
    /// <c>改名之后_显示那几列要一起发通知</c> 变红。</para>
    /// </summary>
    public class DisplayIdentityTests : IDisposable
    {
        private readonly string _root;

        public DisplayIdentityTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "af-display-identity-" + Guid.NewGuid().ToString("N"));
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
        /// **口径①：那一行被借去当"解开这一组的单元"时，状态那一格改说"这一组"**
        /// （用户 2026-10-06 拍板；⛔ `Status` 一个字不动 ⇒ 判据 / 名单 / 配色不受影响）。
        ///
        /// <para><b>红检</b>：把 `StatusDisplayText` 里那一档撤掉 ⇒ 本条变红
        /// （会显示成"解压成功 100%"，读起来像"你这一片被单独解成功了"）。</para>
        /// </summary>
        [Fact]
        public void 被借去当单元的那一行_状态改说这一组()
        {
            string own = Path.Combine(_root, "111(4)", "111.z03");
            string entry = Path.Combine(_root, "111", "111", "111.zip");

            Directory.CreateDirectory(Path.GetDirectoryName(own)!);
            Directory.CreateDirectory(Path.GetDirectoryName(entry)!);
            File.WriteAllBytes(own, new byte[2048]);
            File.WriteAllBytes(entry, new byte[4096]);

            var unit = new ArchiveTask(own, 1);

            unit.CurrentPath = entry;
            unit.ShowUserFileIdentity(entry);
            unit.Outcome = TaskOutcome.Succeeded;
            unit.Status = StatusText.ExtractSuccess;

            Assert.StartsWith(StatusText.ExtractSuccessAsGroup, unit.StatusDisplayText, StringComparison.Ordinal);
            Assert.Equal(StatusText.ExtractSuccess, unit.Status);   // ⛔ 机器状态一个字没改

            // 对照：没被借去的那一行，状态照旧。
            var plain = new ArchiveTask(own, 2);

            plain.Outcome = TaskOutcome.Succeeded;
            plain.Status = StatusText.ExtractSuccess;

            Assert.StartsWith(StatusText.ExtractSuccess, plain.StatusDisplayText, StringComparison.Ordinal);
            Assert.DoesNotContain(StatusText.ExtractSuccessAsGroup, plain.StatusDisplayText, StringComparison.Ordinal);
        }
        /// <summary>
        /// **被借去当单元的那一行，不该把单元的内部过程详情挂在"错误信息"格里**
        /// （用户 2026-10-07：「列表里面还是显示 111.z03 解压了三次」—— 他看到的就是那格里的
        /// 「已完成 3 层递归解压…」）。⛔ `ErrorMessage` 一个字不动，只改显示。
        ///
        /// <para><b>红检</b>：把 `DisplayErrorMessage` 里那一档撤掉 ⇒ 本条变红。</para>
        /// </summary>
        [Fact]
        public void 被借去当单元的那一行_错误信息格不显示单元的过程详情()
        {
            string own = Path.Combine(_root, "111(4)", "111.z03");
            string entry = Path.Combine(_root, "111", "111", "111.zip");

            Directory.CreateDirectory(Path.GetDirectoryName(own)!);
            Directory.CreateDirectory(Path.GetDirectoryName(entry)!);
            File.WriteAllBytes(own, new byte[2048]);
            File.WriteAllBytes(entry, new byte[4096]);

            var unit = new ArchiveTask(own, 1);

            unit.CurrentPath = entry;
            unit.ShowUserFileIdentity(entry);
            unit.Outcome = TaskOutcome.Succeeded;
            unit.Status = StatusText.ExtractSuccess;
            unit.ErrorMessage = "已完成 3 层递归解压（没有更多内层归档）；产物：X；搬运 5 个文件";

            Assert.Equal(string.Empty, unit.DisplayErrorMessage);
            Assert.NotEqual(string.Empty, unit.ErrorMessage);   // ⛔ 账上的值一个字没改

            // 对照：普通那一行照旧显示。
            var plain = new ArchiveTask(own, 2);

            plain.Outcome = TaskOutcome.Succeeded;
            plain.Status = StatusText.ExtractSuccess;
            plain.ErrorMessage = "普通结论";

            Assert.Equal("普通结论", plain.DisplayErrorMessage);
        }
        /// <summary>
        /// **改底层字段也要刷新显示那几列**（用户 2026-10-07：「一直都是界面的问题，我看着日志还好好的，
        /// 你为什么没有同步」）。判据统一在 <c>OnPropertyChanged</c> 一处 ⇒ 任何动到
        /// Status / ErrorMessage / FileName / CurrentPath / SourceSizeBytes 的地方都会带上显示列。
        ///
        /// <para><b>红检</b>：把 <c>DisplayColumnTriggers</c> 那一段撤掉 ⇒ 本条变红。</para>
        /// </summary>
        [Fact]
        public void 改底层字段时_显示那几列一起发通知()
        {
            string file = Path.Combine(_root, "111.z03");

            File.WriteAllBytes(file, new byte[1024]);

            var task = new ArchiveTask(file, 1);
            var seen = new List<string>();

            ((INotifyPropertyChanged)task).PropertyChanged += (_, e) => seen.Add(e.PropertyName ?? string.Empty);

            task.ErrorMessage = "改一下";
            Assert.Contains(nameof(ArchiveTask.DisplayErrorMessage), seen);

            seen.Clear();
            task.Status = StatusText.ExtractSuccess;
            Assert.Contains(nameof(ArchiveTask.StatusDisplayText), seen);

            seen.Clear();
            task.CurrentPath = Path.Combine(_root, "111.z02");
            Assert.Contains(nameof(ArchiveTask.DisplayFileName), seen);
            Assert.Contains(nameof(ArchiveTask.DisplayExtension), seen);
        }
        /// <summary>
        /// ⛔ **变换链只写程序自己的记录**：改名（最初导入名 → 最新已知名）、续解每一层
        /// （递归报上来的那一帧原文）、这一片被谁解开的那一组接手（收场记下的事实）——
        /// 用户 2026-10-07：「我们最后想看到 `111.rar` 通过解压缩两层变换到 `111.zip`」＋
        /// 「你不要给我瞎猜，看程序怎么弄」。
        ///
        /// <para><b>红检</b>：把 `DisplayTransformationText` 里任意一段（改名 / 层记录 / 接手）撤掉 ⇒
        /// 本条对应的断言变红。</para>
        /// </summary>
        [Fact]
        public void 变换链按程序记录拼_改名加两层加接手()
        {
            string dirty = Path.Combine(_root, "111", "111.z0删除3");

            Directory.CreateDirectory(Path.GetDirectoryName(dirty)!);
            File.WriteAllBytes(dirty, new byte[1024]);

            var task = new ArchiveTask(dirty, 1);

            // ① 改名（同目录 ⇒ 账上跟着走）
            string renamed = Path.Combine(_root, "111", "111.z03");

            File.Move(dirty, renamed);
            task.CurrentPath = renamed;

            // ② 续解两层（协调器把递归报上来的帧原样落到任务上）
            task.Status = StatusText.Extracting;
            task.ApplyProgress(0, "第 0 层：111.rar", layer: 0);
            task.ApplyProgress(0, "第 1 层：111.rar", layer: 1);
            task.ApplyProgress(0, "第 2 层：111.zip", layer: 2);

            // ③ 这一片被整组接手
            task.SettledWithGroupUnitName = "111.z03";

            string chain = task.DisplayTransformationText;

            Assert.Contains("111.z0删除3 → 111.z03", chain, StringComparison.Ordinal);
            Assert.Contains("第 1 层：111.rar", chain, StringComparison.Ordinal);
            Assert.Contains("第 2 层：111.zip", chain, StringComparison.Ordinal);
            Assert.Contains("由「111.z03」那一单解开整组", chain, StringComparison.Ordinal);

            // 状态格里也看得到（用户要"看得见这件事发生过"）。
            Assert.Contains(chain, task.StatusDisplayText, StringComparison.Ordinal);
        }

        /// <summary>
        /// ⛔ **同一行跑到续解第二层时，状态格要说清"这是整组第几层"**（用户 2026-10-07：
        /// 「111.z03 显示解压了三遍 100%」—— 每层开头补一帧 0% 是既有规矩，可不说层号就像被解了三遍）。
        ///
        /// <para><b>红检</b>：把 `StatusDisplayText` 里 `_liveLayer` 那一档撤掉 ⇒ 本条变红
        /// （状态格只剩「解压中 0%」，看不出换层）。</para>
        /// </summary>
        [Fact]
        public void 续解到第二层时_状态格写清是整组第几层()
        {
            string file = Path.Combine(_root, "111(4)", "111.z03");

            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, new byte[1024]);

            var task = new ArchiveTask(file, 1);

            task.Status = StatusText.Extracting;
            task.ApplyProgress(0, "第 2 层：111.zip", layer: 2);

            Assert.Contains("整组第 2 层", task.StatusDisplayText, StringComparison.Ordinal);
            Assert.Contains("0%", task.StatusDisplayText, StringComparison.Ordinal);

            // 收尾（清进度）之后不再提层号，回到既有的"成功一律 100%"。
            task.Outcome = TaskOutcome.Succeeded;
            task.Status = StatusText.ExtractSuccess;
            task.ClearProgress();

            Assert.DoesNotContain("整组第", task.StatusDisplayText, StringComparison.Ordinal);
            Assert.Contains("100%", task.StatusDisplayText, StringComparison.Ordinal);
        }

        /// <summary>
        /// ⛔ **"这一片已经被整组接手"的行不许永远停在「已跳过 100%」**（用户 2026-10-07 原话：
        /// 「列表里面最下面的两个，从来没有变化」—— `111.rar` 与 `111(2)_.zip` 那两行）。
        ///
        /// <para><b>红检</b>：把 `StatusDisplayText` 里 `SettledWithGroupUnitName` 那一档撤掉 ⇒ 本条变红
        /// （会退回「已跳过 100%」，读起来像"程序什么都没干"）。</para>
        /// </summary>
        [Fact]
        public void 被整组接手的行_状态如实写这一片随整组解开()
        {
            string piece = Path.Combine(_root, "111", "111.rar");

            Directory.CreateDirectory(Path.GetDirectoryName(piece)!);
            File.WriteAllBytes(piece, new byte[2048]);

            var follower = new ArchiveTask(piece, 1);

            // 收场那一刻：这一片被接进整组、整组解开 ⇒ 机器终态是"完成"，状态文案是"跳过"。
            follower.Outcome = TaskOutcome.Succeeded;
            follower.MarkSkipped(StatusText.VolumePieceProducerConsumedByOtherTaskFormat);
            follower.SettledWithGroupUnitName = "111.z03";

            Assert.StartsWith("这一片随整组解开", follower.StatusDisplayText, StringComparison.Ordinal);
            Assert.Contains("111.z03", follower.StatusDisplayText, StringComparison.Ordinal);
            Assert.Equal(StatusText.Skipped, follower.Status);          // ⛔ 机器状态一个字没改

            // 对照：没被整组接手的"已跳过"行照旧（不许把所有跳过都说成随整组解开）。
            var plain = new ArchiveTask(piece, 2);

            plain.Outcome = TaskOutcome.Skipped;
            plain.MarkSkipped("同名冲突，按你的选择跳过");

            Assert.StartsWith(StatusText.Skipped, plain.StatusDisplayText, StringComparison.Ordinal);
        }

        /// <summary>
        /// ⛔ **用户那个文件被程序搬走 / 删掉之后，那一行仍旧显示"他的文件"，不许变成入口包**
        /// （用户 2026-10-07：「文件名、大小、后缀、检测格式、状态，每个都有问题，现在居然还是 `111.rar`，
        /// 你是不是一直锁定到原包，根本就没有看最新的东西」）。
        ///
        /// <para><b>口径冲突（新指令覆盖旧指令，必须报）</b>：本条原来是
        /// `记下来的那份不在了_回落当前值_不许显示旧名` —— 老口径是"那份不在了就回落到这一行自己的当前值"，
        /// 而"当前值"在真机上正是**被批末补判改写过的入口包**（48.35 MiB 的 `111\111\111.zip`），
        /// 用户看到的就是"48MB 又出现在第一行"。新口径：回落顺序是
        /// 「最新已知的**自己那个文件**的名字 / 体积 / 最后位置」，入口包一个字节都不参与显示。</para>
        /// </summary>
        [Fact]
        public void 自己那份被搬走之后_仍旧显示用户那个文件_不许变成入口包()
        {
            string original = Path.Combine(_root, "111(4)", "111.z03");
            string renamed = Path.Combine(_root, "111(4)", "111.z03.renamed");

            Directory.CreateDirectory(Path.GetDirectoryName(original)!);
            File.WriteAllBytes(original, new byte[2048]);

            var task = new ArchiveTask(original, 1);

            // 起点被改写到别处的入口包上（真机那一格）⇒ 显示身份记成"盘上那一份"。
            string entry = Path.Combine(_root, "111", "111", "111.zip");

            Directory.CreateDirectory(Path.GetDirectoryName(entry)!);
            File.WriteAllBytes(entry, new byte[48 * 1024 * 1024]);

            // 真机顺序：**先把起点改写到入口包上**，再记显示身份（那一行仍显示用户自己那个文件）。
            task.CurrentPath = entry;
            task.ShowUserFileIdentity(entry);

            Assert.Equal("111.z03", task.DisplayFileName);
            Assert.Equal(".z03", task.DisplayExtension);
            Assert.EndsWith("111.z03", task.DisplayPath, StringComparison.Ordinal);

            // 那一份被改名搬走了（= 用户那个文件已经不在原位）⇒ 仍旧显示**他的文件**的名字、后缀、体积。
            File.Move(original, renamed);

            Assert.Equal("111.z03", task.DisplayFileName);
            Assert.Equal(".z03", task.DisplayExtension);
            Assert.Equal("2 KiB", task.DisplaySizeText);            // ⛔ 不是入口包的 48 MiB
            Assert.EndsWith("111.z03", task.DisplayPath, StringComparison.Ordinal);
            Assert.DoesNotContain("111.zip", task.DisplayPath, StringComparison.Ordinal);
        }

        /// <summary>
        /// **源包按设置搬进它自己的「其余物」之后，那一行跟着那份文件走**（位置换成其余物里那一条，
        /// 名称 / 后缀一个字不变）—— 用户 2026-10-07：「根本就没有看最新的东西」。
        /// </summary>
        [Fact]
        public void 源包被搬进其余物之后_那一行显示其余物里那一份()
        {
            string original = Path.Combine(_root, "111(4)", "111.z03");
            string rest = Path.Combine(_root, "111", "其余物");
            string moved = Path.Combine(rest, "111.z03");

            Directory.CreateDirectory(Path.GetDirectoryName(original)!);
            Directory.CreateDirectory(rest);
            File.WriteAllBytes(original, new byte[2048]);

            var task = new ArchiveTask(original, 1);

            task.RestDirectoryPath = rest;

            // 搬运：原件走、其余物里多一份同名的。
            File.Move(original, moved);

            Assert.Equal("111.z03", task.DisplayFileName);
            Assert.Equal(".z03", task.DisplayExtension);
            Assert.Equal("2 KiB", task.DisplaySizeText);
            Assert.Equal(moved, task.DisplayPath);
        }

        /// <summary>
        /// ⛔ **批末补判把起点跨目录改写到入口包时，绝不许把"用户那个文件"的记录一起改掉**
        /// （真机：`111(4)\111.z0删除3` → 改名成 `111.z03` → 起点被指到 `…\111\111\111.rar`）。
        /// </summary>
        [Fact]
        public void 起点被跨目录改写到入口包_那一行不许跟着变成48MB()
        {
            string dirty = Path.Combine(_root, "111(4)", "111.z0删除3");
            string renamed = Path.Combine(_root, "111(4)", "111.z03");
            string entry = Path.Combine(_root, "111", "111", "111.rar");

            Directory.CreateDirectory(Path.GetDirectoryName(dirty)!);
            Directory.CreateDirectory(Path.GetDirectoryName(entry)!);
            File.WriteAllBytes(dirty, new byte[2048]);
            File.WriteAllBytes(entry, new byte[48 * 1024 * 1024]);

            var task = new ArchiveTask(dirty, 1);

            // ① 改回标准卷名（同目录）⇒ 显示跟着走到新名字。
            File.Move(dirty, renamed);
            task.CurrentPath = renamed;

            Assert.Equal("111.z03", task.DisplayFileName);
            Assert.Equal("2 KiB", task.DisplaySizeText);

            // ② 批末补判把起点指到入口包（跨目录）⇒ 显示**不许**跟着变。
            task.CurrentPath = entry;

            Assert.Equal("111.z03", task.DisplayFileName);
            Assert.Equal(".z03", task.DisplayExtension);
            Assert.Equal("2 KiB", task.DisplaySizeText);
            Assert.EndsWith("111.z03", task.DisplayPath, StringComparison.Ordinal);
        }

        /// <summary>⛔ 改名 / 重扫之后**五列一起发通知**（否则行停在旧名字上 —— 真机截图里第 2、3 行）。</summary>
        [Fact]
        public void 改名之后_显示那几列要一起发通知()
        {
            string file = Path.Combine(_root, "111.z0删除2");

            File.WriteAllBytes(file, new byte[1024]);

            var task = new ArchiveTask(file, 1);
            var changed = new List<string>();

            ((INotifyPropertyChanged)task).PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);

            string renamed = Path.Combine(_root, "111.z02");

            File.Move(file, renamed);
            task.CurrentPath = renamed;

            Assert.Contains(nameof(ArchiveTask.DisplayFileName), changed);
            Assert.Contains(nameof(ArchiveTask.DisplaySizeText), changed);
            Assert.Contains(nameof(ArchiveTask.DisplayExtension), changed);
            Assert.Contains(nameof(ArchiveTask.DisplayPath), changed);

            // 显示值与盘上那份一致。
            Assert.Equal("111.z02", task.DisplayFileName);
            Assert.Equal(".z02", task.DisplayExtension);
        }
    }
}

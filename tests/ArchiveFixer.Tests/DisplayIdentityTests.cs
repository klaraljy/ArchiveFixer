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
        /// <summary>⛔ 记下来的那份**不在了** ⇒ 回落当前值（不许继续显示旧名 / 旧大小 / 旧后缀）。</summary>
        [Fact]
        public void 记下来的那份不在了_回落当前值_不许显示旧名()
        {
            string original = Path.Combine(_root, "111(4)", "111.z03");
            string renamed = Path.Combine(_root, "111(4)", "111.z03.renamed");

            Directory.CreateDirectory(Path.GetDirectoryName(original)!);
            File.WriteAllBytes(original, new byte[2048]);

            var task = new ArchiveTask(original, 1);

            // 起点被改写到别处的入口包上（真机那一格）⇒ 显示身份记成"盘上那一份"。
            string entry = Path.Combine(_root, "111", "111", "111.zip");

            Directory.CreateDirectory(Path.GetDirectoryName(entry)!);
            File.WriteAllBytes(entry, new byte[4096]);

            // 真机顺序：**先把起点改写到入口包上**，再记显示身份（那一行仍显示用户自己那个文件）。
            task.CurrentPath = entry;
            task.ShowUserFileIdentity(entry);

            Assert.Equal("111.z03", task.DisplayFileName);
            Assert.Equal(".z03", task.DisplayExtension);
            Assert.EndsWith("111.z03", task.DisplayPath, StringComparison.Ordinal);

            // 那一份被改名搬走了 ⇒ 显示必须回落到这一行自己的当前值（入口包），⛔ 不许再显示旧名。
            File.Move(original, renamed);

            Assert.NotEqual("111.z03", task.DisplayFileName);
            Assert.Equal(task.FileName, task.DisplayFileName);
            Assert.Equal(task.CurrentExtension, task.DisplayExtension);
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

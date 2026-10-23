using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;


namespace ArchiveFixer.ViewModels
{
    /// <summary>任务终态收口（启动前停止 / 落点违规注记 / 没结论就按事实补一个；第 13 块，同一 partial 类）。</summary>
    internal sealed partial class ExtractionCoordinator
    {
        private void MarkStoppedBeforeExtract(ArchiveTask task, string status, string errorMessage)
        {
            /*
             * 这一轮没有任何产物、也没有任何校验：上一轮留下的**校验结论与机器终态**必须清干净
             * （任务对象是复用的：重试 / 再点一次）。
             *
             * 不清的话两头都会错：①收口那条判据（"校验没通过"才补 Failed）被上一轮的 `Passed`
             * 挡在门外；②上一轮的 `Succeeded` 会活到这一轮，汇总把一次**没做成**的处置算成成功
             * （不变量 6 的反面）。清成 `Pending` 而不是就地写 `Failed`，是为了让"终态由谁落"
             * 仍然只有收口一处（这里只说"这一单还没有结论"）。
             */
            task.IsOutputVerified = false;
            task.Outcome = TaskOutcome.Pending;

            task.MarkFailed(StatusText.OpWaiting, status, errorMessage);
        }

        /// <summary>
        /// <b>机器终态收口</b>（第 44 条追加；⚠ 唯一出口 —— 单任务收尾与批末补判都转调它）：
        /// 任务已经结束（<c>EndTime</c> 有值）而 <see cref="TaskOutcome"/> 还停在 <c>Pending</c>，
        /// 说明它走的是那些"只写了中文状态、没写终态"的失败分支（实测：识别不出格式的包
        /// 状态是「解压失败」，<c>Outcome</c> 却是 <c>Pending</c>，于是批末汇总把它算进"未处理"）。
        ///
        /// <para>汇总 / 失败清单 / 四道删除门读的都是这一位，⛔ 一律不许靠比对中文。
        /// 判据用"没通过输出校验"这条事实，只有真正的失败才会落进来。</para>
        ///
        /// <para>⚠ 2026-10-05：批末补判那一档（缺卷留到最后再判）是**在任务收尾之后**才落状态的，
        /// 它走不到原来那个 <c>finally</c> 里 ⇒ 少了这一句，真机那种"批末才报缺卷"的任务
        /// 会一直停在「未处理」（机器终态没落，汇总与诊断当场打架）。所以判据抽到这里共用。</para>
        /// </summary>
        /// <summary>
        /// 产物越界（不变量 4）：递归层已把它记成结构化事实（<see cref="RecursionLayerReport.LandingViolation"/>）
        /// ⇒ 结论必须写成「越出目标根目录」，⛔ 不许折成泛泛的"引擎解不开"（措辞与单层路逐字对齐）。
        /// </summary>
        private void ApplyLandingViolationNote(ArchiveTask task, RecursionResult result)
        {
            // 判据与措辞都搬进唯一出口 `Extraction/LandingViolationNote`（一次只搬这一个方法）。
            if (task == null || !LandingViolationNote.HasLandingViolation(result))
            {
                return;
            }

            task.ErrorMessage = LandingViolationNote.Compose(task.ErrorMessage);

            AppendLog("ERROR", $"{task.LogName}：{task.ErrorMessage}");
        }

        private static void FinalizeOutcomeIfPending(ArchiveTask task, bool producedContent = false)
        {
            // 判据与落点都搬进唯一出口 `Extraction/OutcomeFinalizer`（一次只搬这一个方法）。
            OutcomeFinalizer.FinalizeIfPending(task, producedContent);
        }

    }
}
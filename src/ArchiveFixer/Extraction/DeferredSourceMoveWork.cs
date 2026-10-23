using ArchiveFixer.Models;
using System;
using System.Collections.Generic;

namespace ArchiveFixer.Extraction
{
    /// <summary>来源包延期搬运的工作项（纯数据；从协调器嵌套类型提到 Extraction 顶层）。</summary>

    /// <summary>链结束后补搬的一次后台作业结论（日志与"要不要把任务标成部分完成"都从这里回传）。</summary>
    internal sealed class DeferredSourceMoveWork
    {
        public DeferredSourceMoveWork(List<(string Level, string Message)> logEntries, string? failure = null)
        {
            LogEntries = logEntries;
            Failure = failure;
        }

        public List<(string Level, string Message)> LogEntries { get; }

        /// <summary>非 null = 源包没能搬成（任务要标「部分完成」，内容物结论不受影响）。</summary>
        public string? Failure { get; }
    }
}

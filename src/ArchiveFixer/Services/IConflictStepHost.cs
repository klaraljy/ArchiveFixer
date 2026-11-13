namespace ArchiveFixer.Services
{
    /// <summary>同名冲突这一步要问宿主的事。</summary>
    internal interface IConflictStepHost
    {
        /// <summary>设置里的冲突档（原样，未归一）。</summary>
        string ConflictAction { get; }

        DialogService Dialogs { get; }

        PathService Paths { get; }

        void AppendLog(string level, string message);

        /// <summary>用户选了「取消本批」：停止后续 + 取消当前任务（两套取消语义一起用）。</summary>
        void StopAfterCurrent();

        void CancelCurrentTask();
    }
}

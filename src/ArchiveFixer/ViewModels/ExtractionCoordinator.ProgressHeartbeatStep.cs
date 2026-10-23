using ArchiveFixer.Services;

namespace ArchiveFixer.ViewModels
{
    /// <summary>
    /// 进度/心跳这一步的接线（第 15 轮③：实现已搬进 <c>Services/ProgressHeartbeatStep.cs</c> 独立类）。
    /// 协调器这边只保留"心跳怎么写出去"这一件事，其余调用方直接拿那个类的 Sink。
    /// </summary>
    internal sealed partial class ExtractionCoordinator : IProgressHeartbeatHost
    {
        private readonly ProgressHeartbeatStep _progressHeartbeat;

        /// <summary>心跳：绕开"成功就丢"缓冲直接写出去 —— 它要回答的正是"此刻还在动吗"。</summary>
        private void AppendHeartbeat(string message) => _vm.AppendLog("INFO", message);

        void IProgressHeartbeatHost.AppendLog(string level, string message) => AppendLog(level, message);

        void IProgressHeartbeatHost.AppendHeartbeat(string message) => AppendHeartbeat(message);
    }
}

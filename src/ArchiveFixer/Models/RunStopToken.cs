using System.Threading;

namespace ArchiveFixer.Models
{
    /// <summary>
    /// 一次运行的停止令牌：UI 按停止只把这一位从 0 翻到 1；跑批循环只读它，
    /// 不再依赖会被协调器中途复位的 <c>IsStopping</c>。
    /// </summary>
    public sealed class RunStopToken
    {
        private int _requested;

        public bool IsStopRequested => Volatile.Read(ref _requested) != 0;

        public void RequestStop() => Interlocked.Exchange(ref _requested, 1);
    }
}

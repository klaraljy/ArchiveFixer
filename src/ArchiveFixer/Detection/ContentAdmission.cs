using System.Threading;

namespace ArchiveFixer.Detection
{
    /// <summary>
    /// 内容识别的唯一准入出口。
    ///
    /// <para>为什么需要它：`FileContentProbe.Probe` 本身是纯事实读取，不知道调用上下文；
    /// 如果 UI 产物循环、递归解压循环直接调它，就会变成“对全部内容物一遍遍扫”。
    /// 所有非测试调用必须从这里进，并显式说明是用户源包还是分卷候选；
    /// <see cref="ContentAdmissionScope.None"/> 直接返回 Unknown，不打开文件。</para>
    /// </summary>
    public static class ContentAdmission
    {
        private static long _probeCalls;

        /// <summary>仅供定向测试观察“这次批量到底打开了多少次内容探测”。</summary>
        public static long ProbeCalls => Interlocked.Read(ref _probeCalls);

        /// <summary>仅供定向测试在跑批前归零。</summary>
        public static void ResetProbeCallsForTests() => Interlocked.Exchange(ref _probeCalls, 0);

        public static FileContentProbeResult Probe(
            string? path,
            ContentAdmissionScope scope,
            bool deep = false)
        {
            if (scope == ContentAdmissionScope.None
                || string.IsNullOrWhiteSpace(path))
            {
                return FileContentProbeResult.Unknown;
            }

            Interlocked.Increment(ref _probeCalls);
            return FileContentProbe.Probe(path, deep);
        }
    }
}

using System.Threading;

namespace ArchiveFixer.Detection
{
    /// <summary>
    /// 内容识别的**准入范围**。内容探测不是通用的“每个文件都问一遍”：
    /// 只有用户点名的源包，或已经进入同基名/分卷候选池的文件才允许问。
    /// </summary>
    public enum ContentAdmissionScope
    {
        /// <summary>普通产物/无候选上下文：⛔ 不探测。</summary>
        None = 0,

        /// <summary>用户明确导入的源包：只对源包本身做内容判断。</summary>
        UserSource = 1,

        /// <summary>已经进入同基名/分卷候选池的文件：允许浅/深探测。</summary>
        VolumeCandidate = 2
    }

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

// STATUS: 未完成 / 暂停（2026-11-05）。自动并发实验代码，不接入产品路径。
// 恢复前必须先完成 docs/未完成-自动并发.md 里的 T(n) 实测与验收标准。
using System;

namespace ArchiveFixer.Experimental.AutoConcurrency
{
    /// <summary>
    /// **延迟梯度 + AIMD** 控制器（Envoy / Netflix concurrency-limits 同族）。
    ///
    /// <para>不按盘符/机型写规则：设备只决定起点，实际并发由
    /// “归一化延迟有没有被并发压坏”决定。公式：
    /// <c>gradient=(minLatency×(1+buffer))/sampleLatency</c>，
    /// <c>target=round(gradient×limit+√limit)</c>。</para>
    ///
    /// <para>⛔ 纯逻辑：时钟/窗口由调用方喂；不读盘、不碰 UI。</para>
    /// </summary>
    public sealed class AdaptiveConcurrencyController
    {
        public const int DefaultMaxLimit = 8;

        /// <summary>大任务阈值（用户 2026-11-04 口径：2 GiB 以上才算大包）。</summary>
        public const long LargeTaskThresholdBytes = 2L * 1024 * 1024 * 1024;

        /// <summary>超大任务阈值：只串行，不拿单包探测并发。</summary>
        public const long HugeTaskThresholdBytes = 8L * 1024 * 1024 * 1024;

        /// <summary>系统 CPU 压力线：超过它不再加档；更高时降档。</summary>
        private const double CpuHighPercent = 80;
        private const double CpuCriticalPercent = 90;

        /// <summary>可用内存安全线。</summary>
        private const long MemoryLowBytes = 1536L * 1024 * 1024;
        private const long MemoryCriticalBytes = 1024L * 1024 * 1024;

        private const int MaxChangesPerBatch = 8;
        private const int CooldownWindows = 2;
        private const int UpVotesRequired = 2;

        private readonly double _bufferRatio;
        private readonly int _maxLimit;
        private double _minLatencySecondsPerGb;
        private double _lastAggregateMbps;
        private int _cooldown;
        private int _upVotes;
        private int _changes;
        private bool _frozen;

        public AdaptiveConcurrencyController(
            int initialLimit,
            int maxLimit = DefaultMaxLimit,
            double bufferRatio = 0.20,
            double knownMinLatencySecondsPerGb = 0,
            bool fixedLimit = false)
        {
            _maxLimit = Math.Clamp(maxLimit, 1, DefaultMaxLimit);
            _bufferRatio = Math.Clamp(bufferRatio, 0, 1);
            _minLatencySecondsPerGb = knownMinLatencySecondsPerGb > 0
                ? knownMinLatencySecondsPerGb
                : 0;
            CurrentLimit = Math.Clamp(initialLimit, 1, _maxLimit);
            InitialLimit = CurrentLimit;
            IsFixed = fixedLimit;
        }

        public bool IsFixed { get; }

        public int InitialLimit { get; }

        public int CurrentLimit { get; private set; }

        public int MaxLimit => _maxLimit;

        public double MinLatencySecondsPerGb => _minLatencySecondsPerGb;

        public int Changes => _changes;

        public bool IsFrozen => _frozen;

        /// <summary>最近一次采样为什么保持/降档；给“并发没变”的日志用。</summary>
        public string LastDecisionReason { get; private set; } = "尚未采样";

        private int Hold(string reason)
        {
            LastDecisionReason = reason;
            return CurrentLimit;
        }

        /// <summary>
        /// 喂一个窗口，返回调整后的并发上限；调用方按窗口（建议 5 秒）调用。
        /// </summary>
        public int Observe(ConcurrencyWindowSample sample)
        {
            if (IsFixed)
            {
                return Hold("固定并发覆盖");
            }

            if (_frozen)
            {
                return Hold("本批调整次数已到上限");
            }

            if (sample.DiskSpaceLow)
            {
                LastDecisionReason = "空间见底";
                return ChangeTo(1, "空间见底");
            }

            if (sample.UiStalls > 0)
            {
                LastDecisionReason = "UI 线程停顿";
                return Decrease("UI 线程停顿");
            }

            if (sample.Failures > 0)
            {
                // 失败可能来自密码/盘/权限；不据此加并发，保持当前值观察。
                _upVotes = 0;
                _cooldown = Math.Max(_cooldown, 1);
                return Hold("本窗口有失败");
            }

            if (!double.IsFinite(sample.NormalizedLatencySecondsPerGb)
                || sample.NormalizedLatencySecondsPerGb <= 0)
            {
                return Hold("延迟样本无效");
            }

            if (_minLatencySecondsPerGb <= 0)
            {
                _minLatencySecondsPerGb = sample.NormalizedLatencySecondsPerGb;
            }
            else if (sample.NormalizedLatencySecondsPerGb < _minLatencySecondsPerGb)
            {
                _minLatencySecondsPerGb = sample.NormalizedLatencySecondsPerGb;
            }

            if (sample.SystemCpuBusyPercent >= CpuCriticalPercent)
            {
                LastDecisionReason = "系统 CPU 过高";
                return Decrease("系统 CPU 过高");
            }

            if (sample.AvailablePhysicalBytes > 0
                && sample.AvailablePhysicalBytes < MemoryCriticalBytes)
            {
                LastDecisionReason = "可用内存临界";
                return ChangeTo(1, "可用内存临界");
            }

            bool cpuTight = sample.SystemCpuBusyPercent >= CpuHighPercent;
            bool memoryTight = sample.AvailablePhysicalBytes > 0
                               && sample.AvailablePhysicalBytes < MemoryLowBytes;

            if (cpuTight || memoryTight)
            {
                _upVotes = 0;
                _cooldown = Math.Max(_cooldown, 1);
                return Hold("CPU/内存余量不足");
            }

            if (_cooldown > 0)
            {
                _cooldown--;
                _lastAggregateMbps = sample.AggregateMegabytesPerSecond;
                return Hold("冷却窗口");
            }

            double baseline = _minLatencySecondsPerGb * (1 + _bufferRatio);
            double gradient = baseline / sample.NormalizedLatencySecondsPerGb;
            double headroom = Math.Sqrt(CurrentLimit);
            int target = (int)Math.Round(gradient * CurrentLimit + headroom, MidpointRounding.AwayFromZero);
            target = Math.Clamp(target, 1, _maxLimit);

            if (target == CurrentLimit)
            {
                _upVotes = 0;
                _lastAggregateMbps = sample.AggregateMegabytesPerSecond;
                return Hold("梯度目标等于当前值");
            }

            if (target < CurrentLimit)
            {
                _upVotes = 0;
                LastDecisionReason = "延迟相对基线变差";
                _lastAggregateMbps = sample.AggregateMegabytesPerSecond;
                return Decrease("延迟相对基线变差");
            }

            bool throughputDropped = _lastAggregateMbps > 0
                && sample.AggregateMegabytesPerSecond < _lastAggregateMbps * 0.9;

            _lastAggregateMbps = sample.AggregateMegabytesPerSecond;

            if (throughputDropped)
            {
                _upVotes = 0;
                _cooldown = Math.Max(_cooldown, CooldownWindows);
                return Hold("聚合吞吐下降");
            }

            // 加档要连续两个窗口同向，避免单窗口噪声。
            _upVotes++;
            LastDecisionReason = $"等待加档投票 {_upVotes}/{UpVotesRequired}";

            if (_upVotes < UpVotesRequired)
            {
                return CurrentLimit;
            }

            _upVotes = 0;
            LastDecisionReason = "延迟未恶化，试探加档";

            return ChangeTo(Math.Min(CurrentLimit + 1, target), "延迟未恶化，试探加档");
        }

        /// <summary>基线重测：压到 1 跑一个窗口，用它的延迟刷新 minLatency。</summary>
        public void BeginBaselineRecalibration()
        {
            if (_frozen)
            {
                return;
            }

            _minLatencySecondsPerGb = 0;
            CurrentLimit = 1;
            _upVotes = 0;
            _cooldown = CooldownWindows;
        }

        public void ResetBatch()
        {
            _changes = 0;
            _frozen = false;
            _cooldown = 0;
            _upVotes = 0;
        }

        private int Decrease(string reason)
        {
            _ = reason;
            _upVotes = 0;

            if (CurrentLimit <= 1)
            {
                return CurrentLimit;
            }

            return ChangeTo(CurrentLimit - 1, reason);
        }

        private int ChangeTo(int limit, string reason)
        {
            _ = reason;
            int next = Math.Clamp(limit, 1, _maxLimit);

            if (next == CurrentLimit)
            {
                return CurrentLimit;
            }

            if (_changes >= MaxChangesPerBatch)
            {
                _frozen = true;
                return CurrentLimit;
            }

            CurrentLimit = next;
            _changes++;
            _cooldown = Math.Max(_cooldown, CooldownWindows);
            _upVotes = 0;

            return CurrentLimit;
        }
    }
}

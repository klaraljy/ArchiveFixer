// STATUS: 未完成 / 暂停（2026-11-05）。自动并发实验代码，不接入产品路径。
// 恢复前必须先完成 docs/未完成-自动并发.md 里的 T(n) 实测与验收标准。
using ArchiveFixer.Extraction;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ArchiveFixer.Experimental.AutoConcurrency
{
    /// <summary>一个并发采样窗口（P0 埋点的唯一出口）。</summary>
    public readonly record struct ConcurrencyWindowSample(
        double NormalizedLatencySecondsPerGb,
        double AggregateMegabytesPerSecond,
        int ActiveTasks,
        int UiStalls,
        bool DiskSpaceLow,
        int Failures)
    {
        /// <summary>完成任务的源包字节数；0 = 旧调用方/测试，按小任务处理。</summary>
        public long TaskBytes { get; init; }

        /// <summary>系统 CPU 忙的百分比（0–100；&lt;=0 表示没有采到，按有余量处理）。</summary>
        public double SystemCpuBusyPercent { get; init; }

        /// <summary>可用物理内存字节；0 = 没有采到，按未知处理（不据此降档）。</summary>
        public long AvailablePhysicalBytes { get; init; }
    }

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

    /// <summary>本机“上次验证过好的并发/基线”，按卷+设备类别存储；不记录文件名。</summary>
    public sealed class ConcurrencyProfile
    {
        public int LastGoodLimit { get; set; } = 1;

        public double MinLatencySecondsPerGb { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }
    }

    /// <summary>
    /// 本地 profile 存取（SQL Server DOP feedback 的“持久化已验证值”类比）。
    /// 键=卷根 + 设备类别；换盘自然失效；损坏/读不出 ⇒ 返回 null。
    /// </summary>
    public static class ConcurrencyProfileStore
    {
        public static string BuildKey(string? targetRoot, StorageDeviceKind device)
        {
            string normalized = string.IsNullOrWhiteSpace(targetRoot)
                ? string.Empty
                : Path.GetFullPath(targetRoot).TrimEnd('\\').ToUpperInvariant();

            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized + "|" + device));
            return Convert.ToHexString(hash, 0, 12).ToLowerInvariant();
        }

        public static ConcurrencyProfile? Load(
            string directory,
            string? targetRoot,
            StorageDeviceKind device)
        {
            try
            {
                string path = PathFor(directory, targetRoot, device);

                if (!File.Exists(path))
                {
                    return null;
                }

                ConcurrencyProfile? profile = JsonSerializer.Deserialize<ConcurrencyProfile>(
                    File.ReadAllText(path, Encoding.UTF8));

                return profile != null && profile.LastGoodLimit >= 1 && profile.LastGoodLimit <= 8
                    ? profile
                    : null;
            }
            catch
            {
                return null;
            }
        }

        public static void Save(
            string directory,
            string? targetRoot,
            StorageDeviceKind device,
            ConcurrencyProfile profile)
        {
            try
            {
                Directory.CreateDirectory(directory);
                string path = PathFor(directory, targetRoot, device);
                string temp = path + ".tmp";
                string json = JsonSerializer.Serialize(profile);
                File.WriteAllText(temp, json, new UTF8Encoding(false));
                FileOperations.Move(temp, path, overwrite: true);
            }
            catch
            {
                // 写不进去不影响本批；下次重新学习。
            }
        }

        public static string PathFor(string directory, string? targetRoot, StorageDeviceKind device) =>
            Path.Combine(directory, "concurrency-" + BuildKey(targetRoot, device) + ".json");
    }
}

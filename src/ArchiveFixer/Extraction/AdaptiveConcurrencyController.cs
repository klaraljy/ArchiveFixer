using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ArchiveFixer.Extraction
{
    /// <summary>一个并发采样窗口（P0 埋点的唯一出口）。</summary>
    public readonly record struct ConcurrencyWindowSample(
        double NormalizedLatencySecondsPerGb,
        double AggregateMegabytesPerSecond,
        int ActiveTasks,
        int UiStalls,
        bool DiskSpaceLow,
        int Failures);

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

        /// <summary>
        /// 喂一个窗口，返回调整后的并发上限；调用方按窗口（建议 5 秒）调用。
        /// </summary>
        public int Observe(ConcurrencyWindowSample sample)
        {
            if (IsFixed)
            {
                return CurrentLimit;
            }

            if (_frozen)
            {
                return CurrentLimit;
            }

            if (sample.DiskSpaceLow)
            {
                return ChangeTo(1, "空间见底");
            }

            if (sample.UiStalls > 0)
            {
                return Decrease("UI 线程停顿");
            }

            if (sample.Failures > 0)
            {
                // 失败可能来自密码/盘/权限；不据此加并发，保持当前值观察。
                _upVotes = 0;
                _cooldown = Math.Max(_cooldown, 1);
                return CurrentLimit;
            }

            if (!double.IsFinite(sample.NormalizedLatencySecondsPerGb)
                || sample.NormalizedLatencySecondsPerGb <= 0)
            {
                return CurrentLimit;
            }

            if (_minLatencySecondsPerGb <= 0)
            {
                _minLatencySecondsPerGb = sample.NormalizedLatencySecondsPerGb;
            }
            else if (sample.NormalizedLatencySecondsPerGb < _minLatencySecondsPerGb)
            {
                _minLatencySecondsPerGb = sample.NormalizedLatencySecondsPerGb;
            }

            if (_cooldown > 0)
            {
                _cooldown--;
                _lastAggregateMbps = sample.AggregateMegabytesPerSecond;
                return CurrentLimit;
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
                return CurrentLimit;
            }

            if (target < CurrentLimit)
            {
                _upVotes = 0;
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
                return CurrentLimit;
            }

            // 加档要连续两个窗口同向，避免单窗口噪声。
            _upVotes++;

            if (_upVotes < UpVotesRequired)
            {
                return CurrentLimit;
            }

            _upVotes = 0;

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

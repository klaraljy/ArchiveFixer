using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using ArchiveFixer.Extraction;

namespace ArchiveFixer.Models
{
    /// <summary>
    /// 跑批开始时冻结的设置快照（阶段 6 影子）：UI 之后改 working copy 不影响这里。
    /// 先用 JSON 深拷贝保住全部字段，避免两个可变对象共享引用。
    /// </summary>
    public sealed class SettingsSnapshot
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly AppSettings _values;

        private SettingsSnapshot(AppSettings values)
        {
            _values = values;
        }

        public OutputPlacementMode? PlacementMode => _values.PlacementMode;

        public string CustomOutputDirectory => _values.CustomOutputDirectory ?? string.Empty;

        public string SourceHandling => _values.SourceHandling ?? string.Empty;

        public string RestHandlingAfterVerify => _values.RestHandlingAfterVerify ?? string.Empty;

        public bool UseSpecificExtraction => _values.UseSpecificExtraction;

        public bool SkipOneClickConfirm => _values.SkipOneClickConfirm;

        public bool RemindBeforeExtract => _values.RemindBeforeExtract;

        public int MaxRecursionDepth => _values.MaxRecursionDepth;

        public bool CollectResultsToDirectory => _values.CollectResultsToDirectory;

        public string CollectTargetDirectory => _values.CollectTargetDirectory ?? string.Empty;

        public int ConcurrencyOverride => _values.ConcurrencyOverride;

        public static SettingsSnapshot Capture(AppSettings? settings)
        {
            AppSettings source = settings ?? AppSettings.CreateDefault();
            string json = JsonSerializer.Serialize(source, JsonOptions);
            AppSettings clone = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)
                ?? AppSettings.CreateDefault();

            return new SettingsSnapshot(clone);
        }

        internal AppSettings ToSettings() => _values;

        /// <summary>与当前 working copy 是否存在影响跑批的差异。</summary>
        public bool DiffersFrom(AppSettings? settings)
        {
            SettingsSnapshot other = Capture(settings);
            return !string.Equals(SourceHandling, other.SourceHandling, StringComparison.Ordinal)
                || !string.Equals(RestHandlingAfterVerify, other.RestHandlingAfterVerify, StringComparison.Ordinal)
                || UseSpecificExtraction != other.UseSpecificExtraction
                || PlacementMode != other.PlacementMode
                || !string.Equals(CustomOutputDirectory, other.CustomOutputDirectory, StringComparison.Ordinal)
                || MaxRecursionDepth != other.MaxRecursionDepth
                || ConcurrencyOverride != other.ConcurrencyOverride;
        }
    }
}

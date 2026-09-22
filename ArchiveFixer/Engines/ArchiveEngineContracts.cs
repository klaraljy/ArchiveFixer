using System;
using System.Collections.Generic;

namespace ArchiveFixer.Engines
{
    /// <summary>
    /// 一次引擎调用的输入。
    /// 注意：<see cref="Password"/> 只允许在内存里传递，**绝对不要写进日志或报告**（AGENTS.md §6 第 5 条）。
    /// </summary>
    public sealed class ArchiveRequest
    {
        public string ArchivePath { get; init; } = string.Empty;

        /// <summary>本次调用使用的密码；空字符串表示"试空密码"，null 表示"不传密码参数"。</summary>
        public string? Password { get; init; }

        /// <summary>解压目标目录（仅 extract 用）。</summary>
        public string? OutputPath { get; init; }

        /// <summary>
        /// 进度接收端（可空）。**由引擎层节流之后**才调用（见 <see cref="ArchiveProgressReporter"/>），
        /// 所以接收端可以放心地往 UI 线程投递。
        ///
        /// 为什么是可写的而不是 <c>init</c>：调用方普遍用
        /// <c>ArchiveRequest.For(path, pwd)</c> 或对象初始化器建请求，
        /// 让它能在不改动既有调用点的前提下按需挂上进度。
        /// </summary>
        public IProgress<ArchiveProgress>? Progress { get; set; }

        /// <summary>
        /// "很久没有任何引擎输出"的提示（可空）。
        ///
        /// ⛔ <b>只提示，不杀进程</b>：取消语义只归用户（不变量 9）。
        /// </summary>
        public Action<ArchiveStallNotice>? Stalled { get; set; }

        /// <summary>多久没有输出算"长时间无响应"；默认 90 秒。</summary>
        public TimeSpan StallThreshold { get; set; } = EngineOutputActivityMonitor.DefaultStallThreshold;

        public static ArchiveRequest For(string archivePath, string? password = null)
        {
            return new ArchiveRequest
            {
                ArchivePath = archivePath,
                Password = password
            };
        }
    }

    /// <summary>识别结果。置信度用字符串而不是枚举，是为了和既有 <c>DetectResult</c> 的界面口径保持一致。</summary>
    public sealed class ArchiveProbeResult
    {
        public bool IsArchive { get; init; }

        public string Format { get; init; } = "Unknown";

        public string SuggestedExtension { get; init; } = string.Empty;

        /// <summary>Certain / High / Medium / Low / Unknown（设计.md §六 的五档）。</summary>
        public string Confidence { get; init; } = "Unknown";

        public bool IsEncrypted { get; init; }

        public bool IsMultiVolume { get; init; }

        public string Message { get; init; } = string.Empty;
    }

    /// <summary>归档里的一个条目。</summary>
    public sealed class ArchiveEntry
    {
        public string Path { get; init; } = string.Empty;

        /// <summary>解压后大小；目录为 0。</summary>
        public long Size { get; init; }

        public bool IsDirectory { get; init; }

        public bool IsEncrypted { get; init; }
    }

    /// <summary>列目录结果。资源预算（M5）与"解压前预检路径"都要靠它。</summary>
    public sealed class ArchiveListResult
    {
        public bool Success { get; init; }

        public IReadOnlyList<ArchiveEntry> Entries { get; init; } = new List<ArchiveEntry>();

        /// <summary>所有文件条目的解压后大小合计（目录不计）。</summary>
        public long TotalUncompressedSize { get; init; }

        public int FileCount { get; init; }

        public int DirectoryCount { get; init; }

        public bool IsEncrypted { get; init; }

        public bool IsMultiVolume { get; init; }

        /// <summary>引擎名字 + 版本，用于"结果可追溯"（AGENTS.md §6 第 14 条）。</summary>
        public string EngineId { get; init; } = string.Empty;

        public string EngineVersion { get; init; } = string.Empty;

        /// <summary>失败原因（成功时为空）。</summary>
        public string Message { get; init; } = string.Empty;

        public string ErrorType { get; init; } = "None";

        public static ArchiveListResult Failure(string errorType, string message, string engineId, string engineVersion)
        {
            return new ArchiveListResult
            {
                Success = false,
                ErrorType = errorType,
                Message = message,
                EngineId = engineId,
                EngineVersion = engineVersion
            };
        }
    }
}

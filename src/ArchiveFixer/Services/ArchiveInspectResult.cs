using System;
using System.Collections.Generic;
using ArchiveFixer.Models;

namespace ArchiveFixer.Services
{
    /// <summary>
    /// 一个任务"看一眼"的结论（纯数据、不引用 WPF；界面负责排版成一段话）。
    ///
    /// <para>⛔ 这里**没有密码字段**：只记"第几个候选能开"（<see cref="PasswordLabel"/> 用的是候选自带的
    /// 说明文字，例如"密码列表第 3 项"）—— 密码明文永远不出现在结论、日志与弹窗里（AGENTS.md §8）。</para>
    /// </summary>
    public sealed class ArchiveInspectResult
    {
        /// <summary>被看的那个任务（界面拿来显示文件名）。</summary>
        public ArchiveTask? Task { get; init; }

        /// <summary>这一次"看一眼"本身是否拿到了清单（不代表包能不能完整解压）。</summary>
        public bool Success { get; init; }

        /// <summary>识别出来的格式（识别层的结论，照抄）。</summary>
        public string Format { get; init; } = "Unknown";

        /// <summary>这个包需不需要密码。</summary>
        public bool NeedsPassword { get; init; }

        /// <summary>在候选里找到了能开的那个。</summary>
        public bool PasswordFound { get; init; }

        /// <summary>能开的那个候选的**说明文字**（脱敏，例如"密码列表第 3 项"）。</summary>
        public string PasswordLabel { get; init; } = string.Empty;

        /// <summary>试了几个候选 / 一共几个。</summary>
        public int CandidatesTried { get; init; }

        public int CandidatesTotal { get; init; }

        public int FileCount { get; init; }

        public int DirectoryCount { get; init; }

        /// <summary>清单里所有文件条目的解压后大小合计（目录不计）。</summary>
        public long TotalBytes { get; init; }

        public bool IsMultiVolume { get; init; }

        public IReadOnlyList<ArchiveInspectEntry> TopEntries { get; init; } = Array.Empty<ArchiveInspectEntry>();

        /// <summary>给用户的一句话（失败原因 / 补充说明；成功时通常是空的）。</summary>
        public string Message { get; init; } = string.Empty;

        /// <summary>用时（例如"0.4 s"）。</summary>
        public string ElapsedText { get; init; } = string.Empty;
    }
}

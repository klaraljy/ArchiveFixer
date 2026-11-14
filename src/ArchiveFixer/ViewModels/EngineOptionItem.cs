using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Input;


namespace ArchiveFixer.ViewModels;

/// <summary>
/// 设置界面上的**一行引擎**（检测状态 + 路径 + 版本 + 是不是当前在用）。
///
/// 为什么要显示这三样（AGENTS.md §3.1：界面显示当前是否在用）：
/// 用户机器上可能有**两份** UnRAR —— 自己装的 WinRAR 里那份（本机实测 6.11），
/// 以及我们内置的 <c>tools\unrar\</c>（7.23）。"到底用的哪一个、哪个版本"决定了
/// 遇到格式支持问题时报什么，也在报告里作为溯源依据（不变量 14）。
/// 只写一句"已启用"是不够的。
/// </summary>
public sealed class EngineOptionItem
{
    /// <summary>检测状态文案。**不是任务状态**（不进 StatusText / 不参与统计与配色），只是这一页的说明文字。</summary>
    public const string DetectedText = "已检测到";

    /// <summary>没检测到。</summary>
    public const string NotDetectedText = "未检测到";

    public string Id { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    public string Path { get; init; } = string.Empty;

    /// <summary>这个文件是从哪一档找到的（用户自选 / 已装 WinRAR 目录 / 内置）。</summary>
    public string SourceText { get; init; } = string.Empty;

    public bool IsAvailable { get; init; }

    public string StatusText => IsAvailable ? DetectedText : NotDetectedText;

    /// <summary>排在第几位（1 基，给人看）。</summary>
    public int Order { get; init; }

    /// <summary>是不是"当前会真正被用上"的那一个（依据能力 + 优先级算出来，见 EngineSelectionSummary）。</summary>
    public bool IsInUse { get; init; }

    /// <summary>"当前在用"的说明（为空表示这一行当前用不上）。</summary>
    public string UsageText { get; init; } = string.Empty;

    public bool CanMoveUp { get; init; }

    public bool CanMoveDown { get; init; }

    /// <summary>一句话：名字 + 版本 + 状态（列表里第一行文字）。</summary>
    public string HeaderText => IsAvailable
        ? $"{Order}. {DisplayName}　{Version}　{StatusText}"
        : $"{Order}. {DisplayName}　{StatusText}";
}


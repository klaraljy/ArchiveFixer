using ArchiveFixer.Helpers;
using ArchiveFixer.Storage;
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArchiveFixer.Extraction;

/// <summary>
/// 工作区在磁盘上的**身份与落点**：根目录、本任务目录（唯一来源）、以及任务目录上的
/// 崩溃恢复记录 <c>report.json</c>。
///
/// <para>安全边界：<see cref="RootDirectory"/> 为空是**调用方的编程错误**（用户输入的空目录
/// 应该在更上层就被拦住），当场抛；<c>taskId</c> 必须清洗成**单个目录名**，
/// 否则 <c>".."</c> / <c>"a\..\..\b"</c> 这类值能让"工作区"落到 root 之外。</para>
/// </summary>
internal sealed class WorkspaceLayout
{
    /// <summary>report.json 的文件名（崩溃后恢复时按这个约定去找）。</summary>
    private const string ReportFileName = "report.json";

    private static readonly JsonSerializerOptions ReportSerializerOptions = new()
    {
        WriteIndented = true,

        // 中文状态字符串、路径都不转成 \uXXXX：报告是给人看和给恢复逻辑读的，可读性优先。
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,

        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private WorkspaceLayout(string rootDirectory, string taskDirectory)
    {
        RootDirectory = rootDirectory;
        TaskDirectory = taskDirectory;
    }

    /// <summary>工作区根目录（由调用方指定，通常是某个临时目录）。</summary>
    internal string RootDirectory { get; }

    /// <summary>本任务的工作区目录：<c>&lt;root&gt;\&lt;清洗后的 taskId&gt;</c>。</summary>
    internal string TaskDirectory { get; }

    /// <summary>
    /// 创建并准备一个工作区（<c>&lt;rootDirectory&gt;\&lt;清洗后的 taskId&gt;</c> 会被建出来）。
    /// 建不出来说明这个位置根本不可写（磁盘满 / 权限 / 路径非法）—— 与其让后面每一层都失败一遍，
    /// 不如在这里一次性说清。
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="rootDirectory"/> 为空。</exception>
    /// <exception cref="IOException">工作区目录建不出来。</exception>
    internal static WorkspaceLayout Create(string rootDirectory, string taskId)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("工作区根目录不能为空", nameof(rootDirectory));
        }

        string root = SafePathHelper.GetFullPathSafe(rootDirectory);

        // 只取文件名部分、并清洗非法字符与保留名，保证 TaskDirectory 一定是 root 的**直接子目录**。
        string safeTaskId = FileNameHelper.SanitizeFileName(taskId);

        string taskDirectory = Path.Combine(root, safeTaskId);

        /*
         * 工作区那棵树一律"建完立刻标隐藏"（用户 2026-09-30 诉求③："要创的话就隐秘文件，
         * 就是单看着看不出来的，用户也察觉不到"）—— 父目录、任务目录、每一层目录都标。
         */
        if (!WorkspaceTree.EnsureHiddenDirectory(taskDirectory))
        {
            throw new IOException($"无法创建工作区目录：{taskDirectory}");
        }

        return new WorkspaceLayout(root, taskDirectory);
    }

    /// <summary>
    /// 写 report.json（给"崩溃后可恢复"用，字段自己定，但必须是可读的 JSON）。
    ///
    /// 两条硬约束：① **绝不写密码** —— 报告只记结构和数量；② 写不进去（磁盘满 / 权限不足 /
    /// 序列化失败）**吞掉异常**：报告是辅助品，写不出来不该让整个已经跑完的任务失败。
    /// </summary>
    internal void WriteReport(object report)
    {
        if (report == null)
        {
            return;
        }

        try
        {
            if (!SafePathHelper.EnsureDirectoryExists(TaskDirectory))
            {
                return;
            }

            string json = JsonSerializer.Serialize(report, ReportSerializerOptions);

            File.WriteAllText(
                Path.Combine(TaskDirectory, ReportFileName),
                json,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch
        {
            // 见方法注释：报告写不出来是"少一条恢复线索"，不是"任务失败"。
        }
    }
}

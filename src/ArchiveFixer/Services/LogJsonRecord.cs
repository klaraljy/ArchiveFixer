using ArchiveFixer.Models;
using ArchiveFixer.ViewModels;
using System.Globalization;
using System.Text;

namespace ArchiveFixer.Services;

/// <summary>
/// **机器读**那两份（`.jsonl` 与人读行一比一、`.trace.jsonl` 一行一条诊断事件）的唯一拼装口。
///
/// <para>为什么单独一个类：字段名、字段顺序、转义规则是**外部契约**（用户只发日志回来时，
/// 解析方按这些字段筛/汇总/比对）。它以前混在日志服务里，和屏幕日志、文件生命周期、导出
/// 混在同一类型上；拆开后改字段只需要看这一处，也不会顺手改到别的东西。</para>
///
/// <para>⛔ 零依赖：自己转义，不引第三方；⛔ 换行绝不许漏进记录里。
/// ⛔ 本类**只拼字符串**，不写盘、不判任何业务。</para>
/// </summary>
internal static class LogJsonRecord
{
    /// <summary>
    /// 把一行日志拼成**机器读**的 JSON 对象（⛔ 零依赖：自己转义，不引第三方）。
    /// 字段固定：<c>ts/level/batch/round/sample/stage/task/layer/file/identity/criterion/inputs/verdict/action/msg</c>。
    /// </summary>
    internal static string Build(
        OperationLogItem item,
        LogRunContext context,
        CriterionFields? criterion,
        LogEventFields? evidence = null,
        long sequence = 0,
        string runId = "",
        int processId = 0,
        int threadId = 0,
        string operationId = "")
    {
        var builder = new StringBuilder(256);

        builder.Append('{');
        AppendField(builder, "schema", "archivefixer.log.v3", first: true);
        AppendField(builder, "ts", LogService.FormatTimestamp(item.Time));
        AppendField(builder, "level", item.Level);
        AppendNumber(builder, "seq", sequence);
        AppendField(builder, "run", runId);
        AppendField(builder, "operation_id", operationId);
        AppendField(builder, "operation_name", string.IsNullOrWhiteSpace(evidence?.OperationName) ? DiagnosticSpan.CurrentOperationName : evidence!.OperationName);
        AppendField(builder, "event_id", evidence?.EventId ?? string.Empty);
        AppendField(builder, "error_code", evidence?.ErrorCode ?? string.Empty);
        AppendField(builder, "component", evidence?.Component ?? string.Empty);
        AppendField(builder, "source_file", evidence?.SourceFile ?? string.Empty);
        AppendNumber(builder, "source_line", evidence?.SourceLine ?? 0);
        AppendField(builder, "caller", evidence?.Caller ?? string.Empty);
        AppendField(builder, "code_source", evidence?.CodeSource ?? string.Empty);
        string effectiveSpanId = string.IsNullOrWhiteSpace(evidence?.SpanId)
            ? DiagnosticSpan.CurrentSpanId
            : evidence!.SpanId;

        if (string.IsNullOrWhiteSpace(effectiveSpanId))
        {
            effectiveSpanId = operationId;
        }

        AppendField(builder, "span_id", effectiveSpanId);
        AppendField(builder, "parent_span_id", string.IsNullOrWhiteSpace(evidence?.ParentSpanId) ? DiagnosticSpan.CurrentParentSpanId : evidence!.ParentSpanId);
        AppendField(builder, "task_id", string.IsNullOrWhiteSpace(evidence?.TaskId) ? DiagnosticSpan.CurrentTaskId : evidence!.TaskId);
        AppendNumber(builder, "pid", processId);
        AppendNumber(builder, "thread", threadId);
        AppendField(builder, "batch", context.Batch);
        AppendNumber(builder, "round", context.Round);
        AppendField(builder, "sample", context.Sample);
        AppendField(builder, "stage", string.IsNullOrWhiteSpace(evidence?.Stage) ? context.Stage : evidence!.Stage);
        AppendField(builder, "task", context.Task);
        AppendNumber(builder, "layer", context.Layer);
        AppendField(builder, "file", criterion?.File ?? item.TaskPath);
        AppendField(builder, "identity", criterion?.Identity ?? string.Empty);
        AppendField(builder, "criterion", criterion?.Criterion ?? string.Empty);
        AppendField(builder, "inputs", criterion?.Inputs ?? string.Empty);
        AppendField(builder, "verdict", criterion?.Verdict ?? string.Empty);
        AppendField(builder, "action", criterion?.Action ?? string.Empty);
        AppendField(builder, "msg", item.Message);
        AppendField(builder, "event", evidence?.Event ?? string.Empty);
        AppendField(builder, "source", evidence?.Source ?? string.Empty);
        AppendField(builder, "target", evidence?.Target ?? string.Empty);
        AppendField(builder, "op_action", evidence?.Action ?? string.Empty);
        AppendField(builder, "result", evidence?.Result ?? string.Empty);
        AppendField(builder, "detail", evidence?.Detail ?? string.Empty);
        AppendField(builder, "stack", evidence?.Stack ?? string.Empty);
        AppendField(builder, "engine", evidence?.Engine ?? string.Empty);
        AppendNumber(builder, "exitCode", evidence?.ExitCode);
        AppendNumber(builder, "bytes", evidence?.Bytes);
        AppendNumber(builder, "durationMs", evidence?.DurationMs);
        AppendField(builder, "group", evidence?.Group ?? string.Empty);
        AppendField(builder, "op_identity", evidence?.Identity ?? string.Empty);
        builder.Append('}');

        return builder.ToString();
    }

    private static void AppendField(StringBuilder builder, string name, string? value, bool first = false)
    {
        if (!first)
        {
            builder.Append(',');
        }

        builder.Append('"').Append(name).Append("\":\"").Append(EscapeJson(value)).Append('"');
    }

    private static void AppendNumber(StringBuilder builder, string name, int value)
    {
        builder.Append(",\"").Append(name).Append("\":").Append(value.ToString(CultureInfo.InvariantCulture));
    }

    private static void AppendNumber(StringBuilder builder, string name, long value)
    {
        builder.Append(",\"").Append(name).Append("\":").Append(value.ToString(CultureInfo.InvariantCulture));
    }

    private static void AppendNumber(StringBuilder builder, string name, int? value)
    {
        if (value.HasValue)
        {
            AppendNumber(builder, name, (long)value.Value);
        }
    }

    private static void AppendNumber(StringBuilder builder, string name, long? value)
    {
        if (value.HasValue)
        {
            AppendNumber(builder, name, value.Value);
        }
    }

    /// <summary>JSON 字符串转义（零依赖；控制字符走 <c>\uXXXX</c>，⛔ 换行绝不许漏进记录里）。</summary>
    internal static string EscapeJson(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length + 16);

        foreach (char ch in value)
        {
            switch (ch)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (ch < 0x20)
                    {
                        builder.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(ch);
                    }

                    break;
            }
        }

        return builder.ToString();
    }
}

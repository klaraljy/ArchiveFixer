using System;
using System.Collections.Generic;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;

namespace ArchiveFixer.Services
{
    /// <summary>
    /// 稳定诊断事件码。码是“问题分类”，⛔ 不按样本名/文件名生成；
    /// 已发布的码只追加，不改变含义。文档：docs\开发\诊断事件码表.md。
    /// </summary>
    public static class DiagnosticCodes
    {
        public const string AppUnknown = "AF-APP-000";
        public const string TaskFailed = "AF-TASK-001";
        public const string VerifyFailed = "AF-VERIFY-002";
        public const string VolumeIncomplete = "AF-VOL-003";
        public const string FileIo = "AF-IO-004";
        public const string EngineFailed = "AF-ENGINE-003";
        public const string UiStall = "AF-UI-001";
        public const string Unhandled = "AF-UNHANDLED-007";
        public const string LogWrite = "AF-LOG-008";
        public const string Configuration = "AF-CONFIG-009";
        public const string Password = "AF-PASSWORD-010";
        public const string Decision = "AF-DECISION-011";
        public const string ArchiveDamaged = "AF-ARCHIVE-012";
        public const string DiskSpace = "AF-SPACE-013";
        public const string SourceChanged = "AF-SOURCE-014";
        public const string LimitReached = "AF-LIMIT-015";
        public const string UnsafePath = "AF-SAFETY-016";

        public sealed record DiagnosticCodeInfo(
            string Code,
            string Component,
            string Meaning,
            string Source,
            string FirstCheck);

        private static readonly IReadOnlyDictionary<string, DiagnosticCodeInfo> Catalog =
            new Dictionary<string, DiagnosticCodeInfo>(StringComparer.Ordinal)
            {
                [AppUnknown] = new(AppUnknown, "Application", "未归类的应用错误",
                    "src/ArchiveFixer/Services/LogService.cs", "先看调用方 source_file/source_line 和同任务的 task.end"),
                [TaskFailed] = new(TaskFailed, "ExtractionCoordinator", "任务最终未成功",
                    "src/ArchiveFixer/ViewModels/ExtractionCoordinator.ExtractTaskMainStep.cs", "先看 task.end 的 status/reason/verification 和最近一条 ERROR"),
                [VerifyFailed] = new(VerifyFailed, "OutputVerifier", "输出校验未通过",
                    "src/ArchiveFixer/Extraction/OutputVerifier.cs", "先看 criterion 的 expected/actual 清单差异"),
                [VolumeIncomplete] = new(VolumeIncomplete, "VolumeBoundary", "分卷组缺片/不完整",
                    "src/ArchiveFixer/ViewModels/ExtractionCoordinator.ExtractTaskMainStep.cs", "先看组事实 pieces/missing 与入口卷判定"),
                [FileIo] = new(FileIo, "FileOperations", "文件移动、删除、改名或建链接失败",
                    "src/ArchiveFixer/Storage/FileOperations.cs", "先看 source/target、Win32 错误与目标目录状态"),
                [EngineFailed] = new(EngineFailed, "ArchiveEngine", "外部引擎调用失败或工具缺失",
                    "src/ArchiveFixer/Engines/", "先看 engine/exit_code/原话，再看工具版本"),
                [UiStall] = new(UiStall, "UiResponsivenessMonitor", "UI 线程停顿",
                    "src/ArchiveFixer/Services/UiResponsivenessMonitor.cs", "先看 stall 事件里的阶段/标记线程，判断阻塞发生在标记前还是后"),
                [Unhandled] = new(Unhandled, "App", "未处理异常",
                    "src/ArchiveFixer/App.xaml.cs", "先看 stack 第一帧和 fatal 日志"),
                [LogWrite] = new(LogWrite, "LogService", "日志写入失败",
                    "src/ArchiveFixer/Services/LogService.cs", "先看磁盘权限、目录占用和 fatal 旁路"),
                [Configuration] = new(Configuration, "SettingsService", "设置/配置读取或保存失败",
                    "src/ArchiveFixer/Services/SettingsService.cs", "先看 settings.redacted.json 和配置文件时间/权限"),
                [Password] = new(Password, "PasswordService", "密码本读取、写入或匹配失败",
                    "src/ArchiveFixer/Password/", "先看候选数与密码来源，不要在日志里找明文"),
                [Decision] = new(Decision, "Decision", "关键判据给出否定结论",
                    "src/ArchiveFixer/Services/LogService.cs", "先看 criterion/inputs/verdict/action"),
                [ArchiveDamaged] = new(ArchiveDamaged, "ArchiveEngine", "归档数据损坏或密码/损坏两义",
                    "src/ArchiveFixer/Extraction/RecursiveExtractor.cs", "先看引擎原话与是否试完候选；两义不要折成单因"),
                [DiskSpace] = new(DiskSpace, "Storage", "磁盘空间不足",
                    "src/ArchiveFixer/Storage/", "先看 requirement/available/shortfall 与目标盘"),
                [SourceChanged] = new(SourceChanged, "SourceGuard", "源文件在处理期间发生变化",
                    "src/ArchiveFixer/ViewModels/ExtractionCoordinator.ExtractTaskMainStep.cs", "先看 source 快照与当前文件 size/mtime/identity"),
                [LimitReached] = new(LimitReached, "RecursionLimits", "撞上递归安全上限",
                    "src/ArchiveFixer/Extraction/RecursiveExtractor.cs", "先看 depth/files/size/ratio 哪一项超限"),
                [UnsafePath] = new(UnsafePath, "ArchivePathGuard", "危险条目或路径穿越被拦截",
                    "src/ArchiveFixer/Security/ArchivePathGuard.cs", "先看 entry path 与落点根目录")
            };

        public static IReadOnlyDictionary<string, DiagnosticCodeInfo> All => Catalog;

        public static string Normalize(string? code) =>
            string.IsNullOrWhiteSpace(code) ? AppUnknown : code.Trim();

        public static string ComponentFor(string? code)
        {
            string normalized = Normalize(code);
            return Catalog.TryGetValue(normalized, out DiagnosticCodeInfo? value)
                ? value.Component
                : "Application";
        }

        /// <summary>递归停因 → 诊断码的唯一映射。⛔ 不按中文文案判断。</summary>
        public static string ForRecursionStop(RecursionStopReason stopReason) => stopReason switch
        {
            RecursionStopReason.PasswordAttemptsExceeded => Password,
            RecursionStopReason.WrongPassword => Password,
            RecursionStopReason.EncryptedHeaders => Password,
            RecursionStopReason.MissingVolume => VolumeIncomplete,
            RecursionStopReason.EngineFailed => EngineFailed,
            RecursionStopReason.Corrupted => ArchiveDamaged,
            RecursionStopReason.PasswordOrCorrupted => ArchiveDamaged,
            RecursionStopReason.DiskSpaceInsufficient => DiskSpace,
            RecursionStopReason.SourceChanged => SourceChanged,
            RecursionStopReason.UnsafeEntry => UnsafePath,
            RecursionStopReason.MaxDepthReached => LimitReached,
            RecursionStopReason.MaxTotalFilesReached => LimitReached,
            RecursionStopReason.MaxTotalSizeReached => LimitReached,
            RecursionStopReason.MaxSingleFileSizeReached => LimitReached,
            RecursionStopReason.ExpansionRatioExceeded => LimitReached,
            RecursionStopReason.TooManyInnerArchives => LimitReached,
            _ => string.Empty
        };

        /// <summary>
        /// 任务终态 → 兜底码。只在任务没有捕获到更具体的事件码时使用；
        /// 调用方必须把 code_source 标成 fallback，不能把推断码当精确码。
        /// </summary>
        public static string ForTask(ArchiveTask? task)
        {
            if (task == null)
            {
                return string.Empty;
            }

            if (task.OutputVerification == OutputVerificationOutcome.Failed)
            {
                return VerifyFailed;
            }

            switch (task.Reason)
            {
                case TaskReason.VolumeMissing:
                case TaskReason.FinalGroupIncomplete:
                    return VolumeIncomplete;
                case TaskReason.VerificationFailed:
                    return VerifyFailed;
                case TaskReason.EngineFailed:
                case TaskReason.FinalGroupCorrupted:
                    return EngineFailed;
                case TaskReason.PasswordExhausted:
                    return Password;
                case TaskReason.UnsafePath:
                    return UnsafePath;
                case TaskReason.SourceChanged:
                    return SourceChanged;
            }

            if (task.Outcome == TaskOutcome.Failed)
            {
                return TaskFailed;
            }

            if (task.Outcome == TaskOutcome.PartiallyCompleted)
            {
                return TaskFailed;
            }

            return string.Empty;
        }
    }
}

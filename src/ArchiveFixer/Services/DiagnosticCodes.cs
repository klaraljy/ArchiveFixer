using System;
using System.Collections.Generic;

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
        public const string VolumeIncomplete = "AF-VOLUME-003";
        public const string FileIo = "AF-IO-004";
        public const string EngineFailed = "AF-ENGINE-005";
        public const string UiStall = "AF-UI-006";
        public const string Unhandled = "AF-UNHANDLED-007";
        public const string LogWrite = "AF-LOG-008";
        public const string Configuration = "AF-CONFIG-009";
        public const string Password = "AF-PASSWORD-010";
        public const string Decision = "AF-DECISION-011";

        private static readonly IReadOnlyDictionary<string, (string Component, string Meaning)> Catalog =
            new Dictionary<string, (string Component, string Meaning)>(StringComparer.Ordinal)
            {
                [AppUnknown] = ("Application", "未归类的应用错误"),
                [TaskFailed] = ("ExtractionCoordinator", "任务最终失败"),
                [VerifyFailed] = ("OutputVerifier", "输出校验未通过"),
                [VolumeIncomplete] = ("VolumeBoundary", "分卷组缺片/不完整"),
                [FileIo] = ("FileOperations", "文件移动、删除、改名或建链接失败"),
                [EngineFailed] = ("ArchiveEngine", "外部引擎调用失败"),
                [UiStall] = ("UiResponsivenessMonitor", "UI 线程停顿"),
                [Unhandled] = ("App", "未处理异常"),
                [LogWrite] = ("LogService", "日志写入失败"),
                [Configuration] = ("SettingsService", "设置/配置读取或保存失败"),
                [Password] = ("PasswordService", "密码本读取、写入或匹配失败"),
                [Decision] = ("Decision", "关键判据给出否定结论")
            };

        public static IReadOnlyDictionary<string, (string Component, string Meaning)> All => Catalog;

        public static string Normalize(string? code) =>
            string.IsNullOrWhiteSpace(code) ? AppUnknown : code.Trim();

        public static string ComponentFor(string? code)
        {
            string normalized = Normalize(code);
            return Catalog.TryGetValue(normalized, out (string Component, string Meaning) value)
                ? value.Component
                : "Application";
        }
    }
}

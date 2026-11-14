using ArchiveFixer.Engines;
using ArchiveFixer.Models;
using System.Collections.Generic;

namespace ArchiveFixer.Services;

/// <summary>密码候选这一步要问宿主的事（密码本、全局密码、设置、引擎、日志、手工密码表、取消状态）。</summary>
internal interface IPasswordCandidatesHost
{
    AppSettings Settings { get; }

    string GlobalPassword { get; }

    PasswordService Passwords { get; }

    IArchiveEngine Engine { get; }

    /// <summary>用户在手动「只解压」里输的密码（本批有效；清空由 ResetManualBatchPassword 负责）。</summary>
    List<string> ManualBatchPasswords { get; }

    void AppendLog(string level, string message);

    /// <summary>用户点了「取消当前」（密码探针/排序时据此提前收手）。</summary>
    bool IsStopping { get; }

    ProgressHeartbeatStep.Sink CreateProgressSink(ArchiveTask task);

    ArchiveRequest BuildTrackedRequest(
        string archivePath,
        string password,
        string? outputPath,
        ProgressHeartbeatStep.Sink progressSink);
}

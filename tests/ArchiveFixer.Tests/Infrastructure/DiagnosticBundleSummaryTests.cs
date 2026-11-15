using ArchiveFixer.BindingModels;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using System;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Xunit;

namespace ArchiveFixer.Tests;

/// <summary>
/// 诊断包汇总必须和界面/导出头部读同一个 <see cref="BatchOutcomeTally"/>。
/// 用户真机（2026-11-15）：一份已跳过的跟班卷被诊断包算成“成功 60”，
/// 同一操作的 txt 头部却写“成功 59”——诊断包不能自己再数一遍。
/// </summary>
[Collection("InfrastructureState")]
public sealed class DiagnosticBundleSummaryTests : IDisposable
{
    private readonly string _root;

    public DiagnosticBundleSummaryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "DiagnosticSummary_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch
        {
        }
    }

    [Fact]
    public void 诊断包成功数不含跟班卷_失败清单只列真正的问题任务()
    {
        var log = new LogService(new PathService { DataRootDirectory = _root });
        log.Initialize(enableFileLog: true);
        log.MarkOperationStart("诊断汇总口径");

        var normal = new ArchiveTask(Path.Combine(_root, "normal.7z"), 1)
        {
            Outcome = TaskOutcome.Succeeded,
            Status = StatusText.ExtractSuccess,
            OutputVerification = OutputVerificationOutcome.Passed
        };

        var follower = new ArchiveTask(Path.Combine(_root, "normal.7z.002"), 2)
        {
            Outcome = TaskOutcome.Succeeded,
            Status = StatusText.Skipped,
            IsVolumeGroupFollower = true
        };

        var failed = new ArchiveTask(Path.Combine(_root, "bad.7z"), 3)
        {
            Outcome = TaskOutcome.Failed,
            Status = StatusText.ExtractFailed,
            OutputVerification = OutputVerificationOutcome.Failed
        };

        var failedFollower = new ArchiveTask(Path.Combine(_root, "normal.7z.003"), 4)
        {
            Outcome = TaskOutcome.Failed,
            Status = StatusText.ExtractFailed,
            IsVolumeGroupFollower = true
        };

        string zipPath = Path.Combine(_root, "diag.zip");
        DiagnosticBundleService.Export(
            log,
            zipPath,
            new[] { normal, follower, failed, failedFollower },
            AppSettings.CreateDefault());

        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        JsonElement summary = ReadJson(archive, "extra/summary.json");
        JsonElement failedTasks = ReadJson(archive, "extra/failed-tasks.json");

        Assert.Equal(4, summary.GetProperty("task_count").GetInt32());
        Assert.Equal(1, summary.GetProperty("succeeded").GetInt32());
        Assert.Equal(1, summary.GetProperty("failed").GetInt32());
        Assert.Equal(0, summary.GetProperty("skipped").GetInt32());
        Assert.Equal(2, summary.GetProperty("skipped_followers").GetInt32());

        Assert.Equal(1, failedTasks.GetArrayLength());
        Assert.Equal("bad.7z", failedTasks[0].GetProperty("file").GetString());
    }

    private static JsonElement ReadJson(ZipArchive archive, string name)
    {
        ZipArchiveEntry entry = archive.GetEntry(name)
            ?? throw new InvalidOperationException("诊断包缺文件：" + name);
        using Stream stream = entry.Open();
        using var reader = new StreamReader(stream);
        using JsonDocument document = JsonDocument.Parse(reader.ReadToEnd());
        return document.RootElement.Clone();
    }
}

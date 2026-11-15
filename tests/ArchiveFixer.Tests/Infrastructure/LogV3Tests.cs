using ArchiveFixer.BindingModels;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace ArchiveFixer.Tests;

[Collection("ArchiveFixerGlobalState")]
public sealed class LogV3Tests : IDisposable
{
    private readonly string _root;

    public LogV3Tests()
    {
        _root = Path.Combine(Path.GetTempPath(), "af-log-v3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void 每次操作一个文件夹_六件套齐全_根目录不再平铺()
    {
        var pathService = new PathService { DataRootDirectory = _root };
        var log = new LogService(pathService);
        log.Initialize(enableFileLog: true);

        string firstDir = log.CurrentOperationDirectory;
        Assert.StartsWith(log.CurrentRunDirectory, firstDir, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(firstDir));
        Assert.True(File.Exists(Path.Combine(firstDir, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(firstDir, "summary.json")));
        Assert.True(File.Exists(Path.Combine(firstDir, "operation.log")));
        Assert.True(File.Exists(Path.Combine(firstDir, "operation.user.log")));
        Assert.True(File.Exists(Path.Combine(firstDir, "operation.jsonl")));
        Assert.True(File.Exists(Path.Combine(firstDir, "operation.trace.jsonl")));

        log.MarkOperationStart("测试操作");

        string secondDir = log.CurrentOperationDirectory;
        Assert.NotEqual(firstDir, secondDir, StringComparer.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(secondDir));
        Assert.True(File.Exists(Path.Combine(secondDir, "operation.log")));
        Assert.Empty(Directory.GetFiles(log.LogDirectory, "ArchiveFixer_*.log", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void 旧平铺日志迁移到legacy_不删除()
    {
        string logs = new PathService { DataRootDirectory = _root }.LogsDirectory;
        Directory.CreateDirectory(logs);
        string old = Path.Combine(logs, "ArchiveFixer_20250101_000000.log");
        File.WriteAllText(old, "old");

        var log = new LogService(new PathService { DataRootDirectory = _root });
        log.Initialize(enableFileLog: true);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(Path.Combine(logs, "legacy", Path.GetFileName(old))));
    }

    [Fact]
    public void 结构化事件带操作ID_span_错误码_异常栈()
    {
        var log = new LogService(new PathService { DataRootDirectory = _root });
        log.Initialize(enableFileLog: true);
        log.MarkOperationStart("错误测试");

        using (DiagnosticSpan.BeginTask("task-1", "错误测试"))
        {
            log.WriteWithCode(
                "ERROR",
                "校验失败",
                errorCode: DiagnosticCodes.VerifyFailed,
                component: "OutputVerifier.CompareExpectedEntries");

            try
            {
                throw new InvalidOperationException("synthetic");
            }
            catch (Exception ex)
            {
                log.Exception("测试异常", ex, errorCode: DiagnosticCodes.VerifyFailed);
            }
        }

        string jsonl = File.ReadAllText(log.CurrentJsonlFilePath);
        JsonElement root = default;
        bool found = false;

        foreach (string line in jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using JsonDocument document = JsonDocument.Parse(line);

            if (document.RootElement.TryGetProperty("error_code", out JsonElement code)
                && code.GetString() == DiagnosticCodes.VerifyFailed)
            {
                root = document.RootElement.Clone();
                found = true;
                break;
            }
        }

        Assert.True(found, "没有找到带 AF-VERIFY-002 的 ERROR 事件");
        Assert.Equal("archivefixer.log.v3", root.GetProperty("schema").GetString());
        Assert.Equal(log.CurrentOperationId, root.GetProperty("operation_id").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("span_id").GetString()));
        Assert.Equal("task-1", root.GetProperty("task_id").GetString());
        Assert.Equal(DiagnosticCodes.VerifyFailed, root.GetProperty("error_code").GetString());
        Assert.Contains("LogV3Tests.cs", root.GetProperty("source_file").GetString(), StringComparison.Ordinal);
        Assert.True(root.GetProperty("source_line").GetInt32() > 0);
        Assert.Equal("结构化事件带操作ID_span_错误码_异常栈", root.GetProperty("caller").GetString());

        string trace = File.ReadAllText(log.CurrentTraceFilePath);
        Assert.Contains("AF-VERIFY-002", trace, StringComparison.Ordinal);
        Assert.Contains("synthetic", trace, StringComparison.Ordinal);
        Assert.Contains("stack", trace, StringComparison.Ordinal);
    }

    [Fact]
    public void 一键诊断包_包含六件套与失败任务_不含密码属性()
    {
        var log = new LogService(new PathService { DataRootDirectory = _root });
        log.Initialize(enableFileLog: true);
        log.MarkOperationStart("诊断包测试");
        log.WriteWithCode("ERROR", "模拟失败", errorCode: DiagnosticCodes.TaskFailed);

        var task = new ArchiveTask(Path.Combine(_root, "sample.7z"))
        {
            Status = "失败",
            ErrorMessage = "模拟"
        };
        task.Outcome = TaskOutcome.Failed;
        task.OutputVerification = OutputVerificationOutcome.Failed;

        var settings = AppSettings.CreateDefault();
        string zipPath = Path.Combine(_root, "diag.zip");
        DiagnosticBundleService.Export(log, zipPath, new[] { task }, settings);

        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        string[] names = archive.Entries.Select(e => e.FullName).ToArray();
        Assert.Contains("README.txt", names);
        Assert.Contains("operation/manifest.json", names);
        Assert.Contains("operation/operation.jsonl", names);
        Assert.Contains("extra/summary.json", names);
        Assert.Contains("extra/tasks.json", names);
        Assert.Contains("extra/failed-tasks.json", names);

        ZipArchiveEntry summaryEntry = archive.GetEntry("extra/summary.json")!;
        using Stream summaryStream = summaryEntry.Open();
        using var summaryReader = new StreamReader(summaryStream);
        using JsonDocument summary = JsonDocument.Parse(summaryReader.ReadToEnd());
        JsonElement summaryRoot = summary.RootElement;
        Assert.Equal(log.CurrentRunId, summaryRoot.GetProperty("run_id").GetString());
        Assert.Equal(log.CurrentOperationId, summaryRoot.GetProperty("operation_id").GetString());
        Assert.NotEqual(summaryRoot.GetProperty("run_id").GetString(), summaryRoot.GetProperty("operation_id").GetString());
        Assert.Contains("extra/settings.redacted.json", names);
        Assert.Contains("extra/error-codes.json", names);
        Assert.DoesNotContain(names, n => n.EndsWith("passwords.txt", StringComparison.OrdinalIgnoreCase));
    }
}

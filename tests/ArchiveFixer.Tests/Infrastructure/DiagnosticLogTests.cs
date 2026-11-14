using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 日志升级 v2 的守门：批次清单、任务文件事实、设置/工具、文件操作、异常栈。
    /// 用户口径：以后只发日志、没有原包和工具，所以这些字段必须能在日志里自证。
    /// </summary>
    public sealed class DiagnosticLogTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "ArchiveFixerDiagnosticLog",
            Guid.NewGuid().ToString("N"));

        public DiagnosticLogTests()
        {
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch
            {
                // 清理失败不影响判据。
            }
        }

        [Fact]
        public void 批次清单带文件大小身份与内容卡_环境头与设置也在()
        {
            LogService log = CreateLog(out string human, out string jsonl);

            string sample = Path.Combine(_root, "1111.7z");
            File.WriteAllBytes(sample, new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0, 4 });

            var task = new ArchiveTask(sample)
            {
                Status = "已识别",
                DetectedFormat = "7Z",
                ExtensionStatus = "后缀正常",
                IsArchive = true,
                IsSelected = true
            };

            DiagnosticLog.WriteBatchStart(
                log,
                batch: "大测试",
                source: "单测",
                tasks: new[] { task },
                settings: AppSettings.CreateDefault(),
                tools: "7z=测试；unrar=测试");

            DiagnosticLog.WriteTaskSnapshot(log, "task.begin", task);

            JsonElement batch = FindEvent(jsonl, "batch.start");
            using JsonDocument detail = JsonDocument.Parse(batch.GetProperty("detail").GetString()!);
            JsonElement first = detail.RootElement.GetProperty("tasks")[0];

            Assert.Equal(sample, first.GetProperty("current_path").GetString());
            Assert.Equal("已识别", first.GetProperty("status").GetString());
            Assert.Equal("7Z", first.GetProperty("detected_format").GetString());
            Assert.False(first.TryGetProperty("file", out _));

            JsonElement taskSnapshot = FindEvent(jsonl, "task.begin");
            using JsonDocument taskDetail =
                JsonDocument.Parse(taskSnapshot.GetProperty("detail").GetString()!);
            Assert.Equal(
                "SevenZip",
                taskDetail.RootElement.GetProperty("content_card").GetProperty("format").GetString());

            JsonElement environment = FindEvent(jsonl, "session.environment");
            using JsonDocument environmentDetail =
                JsonDocument.Parse(environment.GetProperty("detail").GetString()!);
            Assert.False(string.IsNullOrWhiteSpace(
                environmentDetail.RootElement.GetProperty("os").GetString()));

            JsonElement settings = FindEvent(jsonl, "session.settings");
            Assert.Contains("tools", settings.GetProperty("detail").GetString()!, StringComparison.Ordinal);
            Assert.DoesNotContain("Password", settings.GetProperty("detail").GetString()!, StringComparison.OrdinalIgnoreCase);

            Assert.Equal(File.ReadAllLines(human).Length, File.ReadAllLines(log.CurrentJsonlFilePath).Length);
        }

        [Fact]
        public void 文件操作事件带来源目标结果()
        {
            LogService log = CreateLog(out _, out string jsonl);

            DiagnosticLog.WriteFileOperation(
                log,
                "delete",
                source: @"H:\源\111.7z",
                target: null,
                bytes: 1024,
                result: "deleted");

            JsonElement record = FindEvent(jsonl, "file.delete");
            Assert.Equal("delete", record.GetProperty("op_action").GetString());
            Assert.Equal(@"H:\源\111.7z", record.GetProperty("source").GetString());
            Assert.Equal("deleted", record.GetProperty("result").GetString());
            Assert.Equal(1024, record.GetProperty("bytes").GetInt64());
        }

        [Fact]
        public void 异常事件带完整栈()
        {
            LogService log = CreateLog(out _, out string jsonl);

            try
            {
                throw new InvalidOperationException("测试异常");
            }
            catch (Exception exception)
            {
                log.Exception("单测上下文", exception);
            }

            JsonElement record = FindEvent(jsonl, "exception");
            Assert.Contains("InvalidOperationException", record.GetProperty("stack").GetString()!, StringComparison.Ordinal);
            Assert.Contains("单测上下文", record.GetProperty("msg").GetString()!, StringComparison.Ordinal);
        }

        private LogService CreateLog(out string human, out string jsonl)
        {
            string dataRoot = Path.Combine(_root, "data");
            Directory.CreateDirectory(dataRoot);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var log = new LogService(pathService);
            log.Initialize(enableFileLog: true);

            human = log.CurrentLogFilePath;
            jsonl = log.CurrentTraceFilePath;
            return log;
        }

        private static JsonElement FindEvent(string jsonlPath, string eventName)
        {
            foreach (string line in File.ReadAllLines(jsonlPath))
            {
                using JsonDocument document = JsonDocument.Parse(line);

                if (document.RootElement.TryGetProperty("event", out JsonElement eventElement)
                    && eventElement.GetString() == eventName)
                {
                    return document.RootElement.Clone();
                }
            }

            throw new InvalidOperationException($"日志里没有事件：{eventName}");
        }
    }
}

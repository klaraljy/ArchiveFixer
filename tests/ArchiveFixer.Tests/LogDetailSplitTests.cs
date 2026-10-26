using System;
using System.IO;
using ArchiveFixer.Services;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 用户 2026-10-26 口径：`data/logs` 内部日志永远详细；用户选项只控制屏幕显示和导出。
    /// 普通档导出不能出现只写文件的详细行。
    /// </summary>
    public sealed class LogDetailSplitTests : IDisposable
    {
        private readonly string _root;

        public LogDetailSplitTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerLogSplit", Guid.NewGuid().ToString("N"));
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
            }
        }

        [Fact]
        public void 普通档_内部日志保留详细行_普通导出与详细导出分开()
        {
            var pathService = new PathService { DataRootDirectory = Path.Combine(_root, "data") };
            var log = new LogService(pathService);

            log.Initialize(enableFileLog: true);
            log.MarkOperationStart("拆分测试");

            log.WriteInfo("普通行");
            log.WriteFileOnly("INFO", "详细探针行");

            string detailed = log.CurrentLogFilePath;
            string user = Path.ChangeExtension(detailed, ".user.log");

            Assert.Contains("普通行", File.ReadAllText(detailed), StringComparison.Ordinal);
            Assert.Contains("详细探针行", File.ReadAllText(detailed), StringComparison.Ordinal);

            Assert.True(File.Exists(user), "普通档日志应该单独有一份 .user.log");
            Assert.Contains("普通行", File.ReadAllText(user), StringComparison.Ordinal);
            Assert.DoesNotContain("详细探针行", File.ReadAllText(user), StringComparison.Ordinal);

            string normalExport = Path.Combine(_root, "normal.txt");
            log.ExportOperationLog(normalExport, includeDetails: false);

            Assert.Contains("普通行", File.ReadAllText(normalExport), StringComparison.Ordinal);
            Assert.DoesNotContain("详细探针行", File.ReadAllText(normalExport), StringComparison.Ordinal);

            string detailedExport = Path.Combine(_root, "detailed.txt");
            log.ExportOperationLog(detailedExport, includeDetails: true);

            Assert.Contains("详细探针行", File.ReadAllText(detailedExport), StringComparison.Ordinal);
        }
    }
}

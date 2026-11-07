using System;
using System.IO;
using ArchiveFixer.Services;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 用户数据/日志/临时目录只能有一个来源：
    /// App 的启动目录初始化与 PathService 必须指向同一处，不能再在 LocalAppData 下
    /// 另建一套废弃的 logs/temp。
    /// </summary>
    public sealed class UserDataPathTests
    {
        [Fact]
        public void App的日志和临时目录与PathService同一来源()
        {
            var pathService = new PathService();

            Assert.Equal(pathService.LogsDirectory, ArchiveFixer.App.LogsDirectory);
            Assert.Equal(pathService.TempDirectory, ArchiveFixer.App.TempDirectory);
        }

        [Fact]
        public void 旧Local日志只清我们自己的文件_不碰用户文件()
        {
            string root = Path.Combine(Path.GetTempPath(), "af-userdata-" + Guid.NewGuid().ToString("N"));
            string legacy = Path.Combine(root, "logs");
            string current = Path.Combine(root, "program-logs");
            Directory.CreateDirectory(legacy);
            Directory.CreateDirectory(current);
            File.WriteAllText(Path.Combine(legacy, "ArchiveFixer_old.log"), "old");
            File.WriteAllText(Path.Combine(legacy, "fatal_old.log"), "fatal");
            File.WriteAllText(Path.Combine(legacy, "用户自己的说明.txt"), "keep");

            try
            {
                UserDataMaintenance.CleanupLegacyLocalLogs(root, current);

                Assert.False(File.Exists(Path.Combine(legacy, "ArchiveFixer_old.log")));
                Assert.False(File.Exists(Path.Combine(legacy, "fatal_old.log")));
                Assert.True(File.Exists(Path.Combine(legacy, "用户自己的说明.txt")), "不得删用户文件");

                File.Delete(Path.Combine(legacy, "用户自己的说明.txt"));
                UserDataMaintenance.CleanupLegacyLocalLogs(root, current);

                Assert.False(Directory.Exists(legacy));
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }

        [Fact]
        public void 旧Local临时目录只在空的时候清理()
        {
            string root = Path.Combine(Path.GetTempPath(), "af-usertemp-" + Guid.NewGuid().ToString("N"));
            string legacy = Path.Combine(root, "temp");
            string current = Path.Combine(root, "program-temp");
            Directory.CreateDirectory(legacy);
            Directory.CreateDirectory(current);

            try
            {
                File.WriteAllText(Path.Combine(legacy, "keep.txt"), "keep");
                UserDataMaintenance.CleanupEmptyLegacyLocalTemp(root, current);
                Assert.True(Directory.Exists(legacy));

                File.Delete(Path.Combine(legacy, "keep.txt"));
                UserDataMaintenance.CleanupEmptyLegacyLocalTemp(root, current);
                Assert.False(Directory.Exists(legacy));
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }

        [Fact]
        public void 正式日志和临时目录跟程序目录走()
        {
            var pathService = new PathService();

            if (!string.Equals(
                    pathService.DataRootDirectory,
                    PathService.DefaultDataRootDirectoryCore,
                    StringComparison.OrdinalIgnoreCase))
            {
                // 全量测试进程会用 ARCHIVEFIXER_DATA_ROOT 隔离数据根；这时日志/临时目录
                // 按 PathService 的测试口径留在临时根，不适用生产目录断言。
                return;
            }

            Assert.Equal(Path.Combine(AppContext.BaseDirectory, "logs"), pathService.LogsDirectory);
            Assert.Equal(Path.Combine(AppContext.BaseDirectory, "temp"), pathService.TempDirectory);
        }
    }
}

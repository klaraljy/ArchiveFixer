using System;
using System.IO;
using System.Text.Json;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 「缓存根目录」设置项**彻底删除**之后的守门用例（用户 2026-09-30："这个彻底取消，
    /// 用户没有定工作区的权力，就是在解压的地方设立隐形的工作区，这就完全不存在跨盘的操作"）。
    ///
    /// <para>钉三件事：</para>
    /// <list type="number">
    /// <item><description>旧 <c>appsettings.json</c> 里残留的 <c>CacheRootDirectory</c> 键**安静忽略**：
    /// 不抛、不备份成 broken、不迁移到任何别的字段；</description></item>
    /// <item><description>模型里**再没有**这个属性（谁把它加回来，这里立刻变红）；</description></item>
    /// <item><description>那个键的取值**不产生任何目录**：它既不再决定数据根，也不再决定工作区。</description></item>
    /// </list>
    /// </summary>
    public class SettingsCacheRootRemovalTests : IDisposable
    {
        private readonly string _root;

        public SettingsCacheRootRemovalTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerCacheRootRemoval", Guid.NewGuid().ToString("N"));
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
                // 临时目录清不掉不影响结论（句柄可能还在释放中）。
            }
        }

        /// <summary>模型里不许再有这个属性 —— 它是"彻底取消"的唯一可机器判定的证据。</summary>
        [Fact]
        public void 设置模型里再没有缓存根目录这个属性()
        {
            Assert.Null(typeof(AppSettings).GetProperty("CacheRootDirectory"));

            // 序列化出来也不许出现这个键（默认设置走唯一序列化出口 SettingsService.Serialize）。
            string json = new SettingsService(new PathService { DataRootDirectory = _root })
                .Serialize(AppSettings.CreateDefault());

            Assert.DoesNotContain("CacheRootDirectory", json, StringComparison.Ordinal);
        }

        /// <summary>
        /// 旧配置带 <c>CacheRootDirectory</c> 时**照样能加载**：不抛、不备份成 broken、
        /// 同一份里的其它设置项原样读回来。
        /// </summary>
        [Fact]
        public void 旧配置带缓存根目录键_照样正常加载且其它设置项不受影响()
        {
            string legacyCacheRoot = Path.Combine(_root, "老配置里的缓存根");
            string settingsPath = WriteLegacySettings(legacyCacheRoot);

            var pathService = new PathService { DataRootDirectory = _root };
            var service = new SettingsService(pathService);

            AppSettings loaded = service.Load();

            // 同一份配置里的其它设置项一个都不许丢。
            Assert.Equal(3, loaded.MaxParallelExtractCount);
            Assert.True(loaded.VerboseLog);
            Assert.Equal(7, loaded.MaxRecursionDepth);

            // 也没有被当成"损坏配置"备份掉（那会写一个 appsettings.broken.json 出来）。
            Assert.False(File.Exists(Path.Combine(_root, "appsettings.broken.json")));

            // 再存一次：那个键自然消失（安静忽略 = 不迁移、不报错、不保留）。
            Assert.True(service.Save(loaded));

            string rewritten = File.ReadAllText(settingsPath);

            Assert.DoesNotContain("CacheRootDirectory", rewritten, StringComparison.Ordinal);
            Assert.Equal(3, JsonDocument.Parse(rewritten).RootElement
                .GetProperty(nameof(AppSettings.MaxParallelExtractCount)).GetInt32());
        }

        /// <summary>
        /// 那个键的取值**不产生任何东西**：数据根仍然是程序目录下的 data（这里 = 测试自己的临时根），
        /// 老配置里指的那个目录不会被建出来、也不会被当成工作区。
        /// </summary>
        [Fact]
        public void 旧配置里的缓存根目录_不建目录也不当工作区()
        {
            string legacyCacheRoot = Path.Combine(_root, "老配置里的缓存根");
            WriteLegacySettings(legacyCacheRoot);

            var pathService = new PathService { DataRootDirectory = _root };
            AppSettings loaded = new SettingsService(pathService).Load();

            // 数据根还是这一个（唯一出口 = PathService.DataRootDirectory 自己的值）。
            Assert.Equal(_root, pathService.DataRootDirectory);

            // 老配置里那个目录：**没有**被建出来，也没有人往那儿放东西。
            Assert.False(Directory.Exists(legacyCacheRoot), "旧键的值不许被迁移成任何真实位置");
            Assert.False(Directory.Exists(Path.Combine(legacyCacheRoot, "work")));
            Assert.DoesNotContain(
                Directory.GetFileSystemEntries(_root, "*", SearchOption.AllDirectories),
                entry => entry.StartsWith(legacyCacheRoot, StringComparison.OrdinalIgnoreCase));

            // 未解析时的工作区根是老位置（<数据根>\work），⛔ 不是旧键指的那个目录。
            Assert.Equal(
                Path.Combine(_root, WorkspaceRootResolver.LegacyWorkspaceSubDirectoryName),
                pathService.WorkDirectory);

            Assert.DoesNotContain(
                pathService.WorkDirectory,
                legacyCacheRoot,
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>写一份"旧版本留下的" appsettings.json（含已删除的 <c>CacheRootDirectory</c> 键）。</summary>
        private string WriteLegacySettings(string cacheRootDirectory)
        {
            string path = new PathService { DataRootDirectory = _root }.SettingsFilePath;

            Directory.CreateDirectory(_root);

            File.WriteAllText(
                path,
                $$"""
                {
                  "MaxParallelExtractCount": 3,
                  "VerboseLog": true,
                  "MaxRecursionDepth": 7,
                  "CacheRootDirectory": {{JsonSerializer.Serialize(cacheRootDirectory)}}
                }
                """);

            return path;
        }
    }
}

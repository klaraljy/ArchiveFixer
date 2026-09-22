using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 本批新增的四个设置项（依据 <c>docs/WinRAR功能参考.md</c> §2 对照表里标"采纳"的行）：
    ///
    /// <list type="number">
    /// <item><description><c>LowProcessPriority</c>：低运行优先级升为设置项（**默认开**）——
    /// 它以前是 <c>App.xaml.cs</c> 里的硬编码，用户觉得慢时**无从下手**。</description></item>
    /// <item><description><c>OpenOutputFolderWhenDone</c>：定稿完成后打开输出目录（**默认关**）——
    /// 这是"会动用户桌面"的行为，必须用户显式开启。</description></item>
    /// <item><description><c>RestRemovalDefaultMode</c>：「其余物」清理默认档（**默认回收站**）——
    /// 只决定确认框里那个勾选框的默认状态，不是"跳过确认"。</description></item>
    /// <item><description><c>ReportDangerousEntries</c>：危险条目统计（**默认开**，**只提示不阻断**）。</description></item>
    /// </list>
    ///
    /// <para>
    /// 每个设置项都要钉住三件事（与 <see cref="PasswordAttemptLimitSettingTests"/> 同一口径）：
    /// ① 默认值就是文档承诺的那一个；② **能落盘、重启后保持**；
    /// ③ 旧配置缺这个字段时取默认值、**不报错**（System.Text.Json 不会碰属性初始化器已赋好的值）。
    /// </para>
    /// </summary>
    public class SettingsKnobsTests : IDisposable
    {
        private readonly string _dataRoot;

        public SettingsKnobsTests()
        {
            _dataRoot = Path.Combine(Path.GetTempPath(), "ArchiveFixerSettingsKnobs", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dataRoot);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_dataRoot))
                {
                    Directory.Delete(_dataRoot, recursive: true);
                }
            }
            catch
            {
                // 临时目录清不掉不影响结论。
            }
        }

        private PathService CreatePathService() => new() { DataRootDirectory = _dataRoot };

        // ================================================================ ① 默认值

        [Fact]
        public void 默认值_低优先级开_完成后不打开输出目录_其余物默认回收站_危险条目统计开()
        {
            AppSettings fresh = new AppSettings();
            AppSettings defaults = AppSettings.CreateDefault();

            Assert.True(fresh.LowProcessPriority, "低运行优先级以前是硬编码的 BelowNormal，做成设置项后默认必须仍是开");
            Assert.True(defaults.LowProcessPriority);

            Assert.False(fresh.OpenOutputFolderWhenDone, "「完成后打开输出目录」会动用户桌面，必须默认关");
            Assert.False(defaults.OpenOutputFolderWhenDone);

            Assert.Equal(RestRemovalModes.RecycleBin, fresh.RestRemovalDefaultMode);
            Assert.Equal(RestRemovalModes.RecycleBin, defaults.RestRemovalDefaultMode);

            Assert.True(fresh.ReportDangerousEntries, "危险条目统计默认开，而且只提示不阻断");
            Assert.True(defaults.ReportDangerousEntries);
        }

        // ================================================================ ② 其余物清理默认档

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("Whatever")]
        [InlineData("99")]            // 数字骗不过去，我们只认枚举名
        [InlineData("Delete")]        // 不是我们词表里的名字
        [InlineData("recycle")]       // 只写了半截
        public void 其余物清理默认档_空或非法一律回落回收站(string? stored)
        {
            Assert.Equal(RestRemovalModes.RecycleBin, RestRemovalModes.Normalize(stored));
            Assert.False(RestRemovalModes.IsPermanent(stored));
        }

        [Theory]
        [InlineData("RecycleBin", RestRemovalModes.RecycleBin)]
        [InlineData(" recyclebin ", RestRemovalModes.RecycleBin)]
        [InlineData("Permanent", RestRemovalModes.Permanent)]
        [InlineData("PERMANENT", RestRemovalModes.Permanent)]
        public void 其余物清理默认档_合法值原样解析(string stored, string expected)
        {
            Assert.Equal(expected, RestRemovalModes.Normalize(stored));
            Assert.Equal(expected == RestRemovalModes.Permanent, RestRemovalModes.IsPermanent(stored));
        }

        [Fact]
        public void 其余物清理默认档_归一化写回合法值_读不懂时绝不变成彻底删除()
        {
            var settings = new AppSettings { RestRemovalDefaultMode = "谁把这里改坏了" };

            settings.Normalize();

            Assert.Equal(RestRemovalModes.RecycleBin, settings.RestRemovalDefaultMode);
            Assert.False(RestRemovalModes.IsPermanent(settings.RestRemovalDefaultMode));

            settings.RestRemovalDefaultMode = "  Permanent  ";
            settings.Normalize();

            Assert.Equal(RestRemovalModes.Permanent, settings.RestRemovalDefaultMode);
        }

        // ================================================================ ③ 落盘 + 重启保持

        [Fact]
        public void 四个设置项_存盘后重新读取仍然是用户设的值()
        {
            var settingsService = new SettingsService(CreatePathService());

            AppSettings settings = AppSettings.CreateDefault();
            settings.LowProcessPriority = false;
            settings.OpenOutputFolderWhenDone = true;
            settings.RestRemovalDefaultMode = RestRemovalModes.Permanent;
            settings.ReportDangerousEntries = false;
            settingsService.Save(settings);

            // 换一个全新的 SettingsService/PathService 读回来 = 模拟"重启程序"
            AppSettings reloaded = new SettingsService(CreatePathService()).Load();

            Assert.False(reloaded.LowProcessPriority);
            Assert.True(reloaded.OpenOutputFolderWhenDone);
            Assert.Equal(RestRemovalModes.Permanent, reloaded.RestRemovalDefaultMode);
            Assert.False(reloaded.ReportDangerousEntries);
        }

        [Fact]
        public void 升级前的配置文件没有这四项时_用默认值且不报错()
        {
            /*
             * 老用户的 appsettings.json 里当然没有这四个字段。
             * System.Text.Json 反序列化时不会碰属性初始化器已经赋好的值 ——
             * 这条就是钉住"旧配置缺字段不报错、也不悄悄变成激进档"这个前提的。
             */
            string json = """
            {
              "RecursiveScan": true,
              "ScanMode": "ScanAllFiles",
              "MaxParallelExtractCount": 1,
              "SourceHandling": "MoveToRest"
            }
            """;

            var pathService = CreatePathService();
            File.WriteAllText(pathService.SettingsFilePath, json, new UTF8Encoding(false));

            AppSettings loaded = new SettingsService(pathService).Load();

            Assert.True(loaded.LowProcessPriority);
            Assert.False(loaded.OpenOutputFolderWhenDone);
            Assert.Equal(RestRemovalModes.RecycleBin, loaded.RestRemovalDefaultMode);
            Assert.True(loaded.ReportDangerousEntries);
        }

        // ================================================================ ④ 两个旋钮互相独立

        [Fact]
        public void 低优先级与并发数是两个独立的旋钮_改一个不会动另一个()
        {
            /*
             * 抄的是 WinRAR 6.02 修过的坑（docs/WinRAR功能参考.md §2 D 组）：
             * 它的 `-ri`（优先级）曾被 `-ibck`（后台）覆盖，用户调了 A 却被 B 决定。
             * 我们这里钉住的是"两个设置项各管各的"——归一化、序列化都不会顺手改另一个。
             */
            var settings = new AppSettings
            {
                MaxParallelExtractCount = 4,
                LowProcessPriority = false
            };

            settings.Normalize();

            Assert.Equal(4, settings.MaxParallelExtractCount);
            Assert.False(settings.LowProcessPriority);

            settings.MaxParallelExtractCount = 2;
            settings.Normalize();

            Assert.Equal(2, settings.MaxParallelExtractCount);
            Assert.False(settings.LowProcessPriority);
        }

        // ================================================================ ⑤ 设置项真的接到进程优先级上

        [Theory]
        [InlineData(true, ProcessPriorityClass.BelowNormal)]
        [InlineData(false, ProcessPriorityClass.Normal)]
        public void 低运行优先级设置_映射到进程优先级(bool lowProcessPriority, ProcessPriorityClass expected)
        {
            /*
             * 只测"设置值 → 优先级类"这一个纯映射（唯一实现处 App.ResolveProcessPriorityClass）。
             * 刻意**不**在测试里真的改测试宿主进程的优先级：那会拖慢同进程里并行跑的其它测试，
             * 而且宿主进程的优先级本来就该由启动方决定。
             */
            Assert.Equal(expected, App.ResolveProcessPriorityClass(lowProcessPriority));
        }
    }
}

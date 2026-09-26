using System;
using System.IO;
using System.Text;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 「每层密码尝试上限」这个设置项（AGENTS.md §9.2：每层、每任务、每批次都要有尝试上限）。
    ///
    /// 以前它是 ExtractionCoordinator 里的一个常量，用户改不了。做成设置项之后必须钉住三件事：
    /// 默认值是 10、超范围会被夹回 1~1000、**能落盘且重启后保持**（用户改完下次启动还在，
    /// 不然"设置项"就只是个摆设）。
    /// </summary>
    public class PasswordAttemptLimitSettingTests : IDisposable
    {
        private readonly string _dataRoot;

        public PasswordAttemptLimitSettingTests()
        {
            _dataRoot = Path.Combine(Path.GetTempPath(), "ArchiveFixerSettings", Guid.NewGuid().ToString("N"));
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

        [Fact]
        public void 默认值是10()
        {
            Assert.Equal(10, new AppSettings().MaxPasswordAttemptsPerLayer);
            Assert.Equal(10, AppSettings.CreateDefault().MaxPasswordAttemptsPerLayer);
        }

        [Theory]
        [InlineData(0, 1)]
        [InlineData(-5, 1)]
        [InlineData(1, 1)]
        [InlineData(10, 10)]
        [InlineData(500, 500)]
        [InlineData(1000, 1000)]
        [InlineData(1001, 1000)]
        [InlineData(int.MaxValue, 1000)]
        public void 超出1到1000会被夹回(int input, int expected)
        {
            var settings = new AppSettings { MaxPasswordAttemptsPerLayer = input };

            settings.Normalize();

            Assert.Equal(expected, settings.MaxPasswordAttemptsPerLayer);
        }

        [Fact]
        public void 存盘后重新读取仍然是用户设的值()
        {
            var settingsService = new SettingsService(CreatePathService());

            AppSettings settings = AppSettings.CreateDefault();
            settings.MaxPasswordAttemptsPerLayer = 77;
            settingsService.Save(settings);

            // 换一个全新的 SettingsService/PathService 读回来 = 模拟"重启程序"
            AppSettings reloaded = new SettingsService(CreatePathService()).Load();

            Assert.Equal(77, reloaded.MaxPasswordAttemptsPerLayer);
        }

        [Fact]
        public void 升级前的配置文件没有这一项时_用默认10()
        {
            /*
             * 老用户的 appsettings.json 里没有这个字段。
             * System.Text.Json 反序列化时不会碰属性初始化器已经赋好的值，
             * 所以缺字段 = 拿到默认的 10，不需要额外兼容分支 —— 这条就是钉住这个前提的。
             */
            string json = """
            {
              "RecursiveScan": true,
              "ScanMode": "ScanAllFiles",
              "MaxRecursionDepth": 3,
              "MaxParallelExtractCount": 1
            }
            """;

            var pathService = CreatePathService();
            File.WriteAllText(pathService.SettingsFilePath, json, new UTF8Encoding(false));

            AppSettings loaded = new SettingsService(pathService).Load();

            Assert.Equal(10, loaded.MaxPasswordAttemptsPerLayer);
        }

        [Fact]
        public void 保存设置时超范围会被夹回并如实说明()
        {
            // 用户在设置窗口里把上限填成 5000：保存时必须夹回 1000，而且要告诉用户，
            // 不能静默改掉他填的数字（"我以为我设成了 5000"）。
            var viewModel = new SettingsViewModel(AppSettings.CreateDefault(), new SettingsService(CreatePathService()));

            viewModel.Settings.MaxPasswordAttemptsPerLayer = 5000;
            viewModel.SaveCommand.Execute(null);

            Assert.Equal(1000, viewModel.Settings.MaxPasswordAttemptsPerLayer);
            Assert.True(viewModel.DialogResult == true, "保存成功要能关窗（DialogResult = true）");
            Assert.Contains("1~1000", viewModel.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void 保存设置时没超范围就不多嘴()
        {
            var viewModel = new SettingsViewModel(AppSettings.CreateDefault(), new SettingsService(CreatePathService()));

            viewModel.Settings.MaxPasswordAttemptsPerLayer = 40;
            viewModel.SaveCommand.Execute(null);

            Assert.Equal(40, viewModel.Settings.MaxPasswordAttemptsPerLayer);
            Assert.Equal("设置已保存。", viewModel.Message);
        }
    }
}

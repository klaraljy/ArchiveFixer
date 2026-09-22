using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using System;
using System.Threading.Tasks;
using System.Windows;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// UI v2 的回归测试：
    /// ① 无 UI 宿主下 <see cref="DialogService"/> 必须"写日志 + 返回默认值"，绝不抛异常、绝不死等；
    /// ② 设置窗口的"输出位置四选一"必须严格映射到原来那两个布尔（不新增设置项）。
    /// </summary>
    public class UiV2Tests
    {
        // ------------------------------------------------------------------ ① 对话框服务降级

        [Fact]
        public void 无UI宿主_前提确认_测试进程里没有WPF应用()
        {
            Assert.Null(Application.Current);
        }

        [Fact]
        public void 无UI宿主_ShowInfo不抛异常且写降级日志()
        {
            var service = new DialogService();

            service.ShowInfo("这是一条信息提示。");

            Assert.Contains(
                DialogService.FallbackLog,
                entry => entry.Contains("ShowInfo", StringComparison.Ordinal));
        }

        [Fact]
        public void 无UI宿主_ShowConfirm返回默认值false()
        {
            var service = new DialogService();

            bool confirmed = service.ShowConfirm("确定要清空吗？");

            Assert.False(confirmed, "没有界面时不能替用户点「确定」");
            Assert.Contains(
                DialogService.FallbackLog,
                entry => entry.Contains("ShowConfirm", StringComparison.Ordinal));
        }

        [Fact]
        public void 无UI宿主_带可选项位的ShowConfirm也返回false且选项为false()
        {
            var service = new DialogService();

            bool confirmed = service.ShowConfirm(
                "你输入的是空密码，确定要加入密码列表吗？",
                "本次运行内不再询问",
                optionCheckedByDefault: true,
                out bool optionChecked);

            Assert.False(confirmed);
            Assert.False(optionChecked);
        }

        [Fact]
        public void 无UI宿主_危险操作确认返回false()
        {
            var service = new DialogService();

            Assert.False(service.ShowDestructiveConfirm("确定要清空密码列表吗？", "清空"));
        }

        [Fact]
        public void 无UI宿主_ShowYesNoCancel返回Cancel()
        {
            var service = new DialogService();

            Assert.Equal(MessageBoxResult.Cancel, service.ShowYesNoCancel("继续吗？"));
        }

        [Fact]
        public void 无UI宿主_警告与错误提示不抛异常()
        {
            var service = new DialogService();

            service.ShowWarning("警告内容");
            service.ShowError("错误内容");
            service.ShowException(new InvalidOperationException("出事"), "导出失败清单失败");
            service.ShowException(null!);

            Assert.Contains(
                DialogService.FallbackLog,
                entry => entry.Contains("ShowException", StringComparison.Ordinal));
        }

        [Fact]
        public void 无UI宿主_降级日志不写明文密码()
        {
            var service = new DialogService();

            service.ShowInfo("尝试密码：SuperSecret123");

            Assert.DoesNotContain(
                DialogService.FallbackLog,
                entry => entry.Contains("SuperSecret123", StringComparison.Ordinal));
        }

        [Fact]
        public void 无UI宿主_文件对话框返回空结果而不是抛异常()
        {
            var service = new DialogService();

            Assert.Empty(service.ShowOpenFileDialog());
            Assert.Empty(service.ShowOpenSingleFileDialog());
            Assert.Equal(string.Empty, service.ShowFolderBrowserDialog());
            Assert.Empty(service.ShowMultiFolderBrowserDialog());
            Assert.Equal(string.Empty, service.ShowSaveFileDialog());
        }

        [Fact]
        public async Task 无UI宿主_后台线程调用也不抛异常()
        {
            var service = new DialogService();

            Exception? captured = await Task.Run(() =>
            {
                try
                {
                    service.ShowInfo("后台线程的信息");
                    service.ShowWarning("后台线程的警告");
                    _ = service.ShowConfirm("后台线程的确认");
                    return null;
                }
                catch (Exception ex)
                {
                    return ex;
                }
            });

            Assert.Null(captured);
        }

        // ------------------------------------------------------------------ ② 输出位置四选一

        [Theory]
        [InlineData(true, true, OutputPlacementOption.ArchiveNamedSubfolder)]
        [InlineData(true, false, OutputPlacementOption.SourceDirectory)]
        [InlineData(false, true, OutputPlacementOption.CustomNamedSubfolder)]
        [InlineData(false, false, OutputPlacementOption.CustomFlat)]
        public void 两个布尔到四选一的映射(
            bool extractToOriginalDirectory,
            bool keepArchiveNameFolder,
            OutputPlacementOption expected)
        {
            Assert.Equal(
                expected,
                SettingsViewModel.ResolveOutputPlacement(extractToOriginalDirectory, keepArchiveNameFolder));
        }

        [Fact]
        public void 选中指定位置档位_只改原来那两个布尔并启用路径输入()
        {
            SettingsViewModel viewModel = CreateSettingsViewModel();

            viewModel.OutputPlacement = OutputPlacementOption.CustomFlat;

            Assert.False(viewModel.Settings.ExtractToOriginalDirectory);
            Assert.False(viewModel.Settings.KeepArchiveNameFolder);
            Assert.True(viewModel.IsCustomOutputEnabled);

            viewModel.OutputPlacement = OutputPlacementOption.CustomNamedSubfolder;

            Assert.False(viewModel.Settings.ExtractToOriginalDirectory);
            Assert.True(viewModel.Settings.KeepArchiveNameFolder);
            Assert.True(viewModel.IsCustomOutputEnabled);
        }

        [Fact]
        public void 选中压缩包所在位置档位_路径输入置灰不适用()
        {
            SettingsViewModel viewModel = CreateSettingsViewModel();

            viewModel.OutputPlacement = OutputPlacementOption.ArchiveNamedSubfolder;

            Assert.True(viewModel.Settings.ExtractToOriginalDirectory);
            Assert.True(viewModel.Settings.KeepArchiveNameFolder);
            Assert.False(viewModel.IsCustomOutputEnabled);

            viewModel.OutputPlacement = OutputPlacementOption.SourceDirectory;

            Assert.True(viewModel.Settings.ExtractToOriginalDirectory);
            Assert.False(viewModel.Settings.KeepArchiveNameFolder);
            Assert.False(viewModel.IsCustomOutputEnabled);
        }

        [Fact]
        public void 落点示例随档位实时变化()
        {
            SettingsViewModel viewModel = CreateSettingsViewModel();

            viewModel.OutputPlacement = OutputPlacementOption.ArchiveNamedSubfolder;
            Assert.Contains("111\\222\\内容物", viewModel.OutputPlacementSummary, StringComparison.Ordinal);

            viewModel.OutputPlacement = OutputPlacementOption.SourceDirectory;
            Assert.Contains("111\\内容物", viewModel.OutputPlacementSummary, StringComparison.Ordinal);

            viewModel.OutputPlacement = OutputPlacementOption.CustomNamedSubfolder;
            viewModel.CustomOutputDirectory = @"D:\输出";
            Assert.Contains(@"D:\输出\222\内容物", viewModel.OutputPlacementSummary, StringComparison.Ordinal);

            viewModel.OutputPlacement = OutputPlacementOption.CustomFlat;
            Assert.Contains(@"D:\输出\内容物", viewModel.OutputPlacementSummary, StringComparison.Ordinal);
        }

        [Fact]
        public void 指定位置为空时摘要说明暂按压缩包所在目录处理()
        {
            SettingsViewModel viewModel = CreateSettingsViewModel();

            viewModel.OutputPlacement = OutputPlacementOption.CustomFlat;
            viewModel.CustomOutputDirectory = string.Empty;

            Assert.Contains("尚未选择", viewModel.OutputPlacementSummary, StringComparison.Ordinal);
        }

        [Fact]
        public void 恢复默认后档位回到默认的同名子文件夹()
        {
            SettingsViewModel viewModel = CreateSettingsViewModel();

            viewModel.OutputPlacement = OutputPlacementOption.CustomFlat;

            AppSettings defaults = new SettingsService().CreateDefault();
            viewModel.Settings = defaults;

            Assert.Equal(
                SettingsViewModel.ResolveOutputPlacement(
                    defaults.ExtractToOriginalDirectory,
                    defaults.KeepArchiveNameFolder),
                viewModel.OutputPlacement);
        }

        private static SettingsViewModel CreateSettingsViewModel()
        {
            var settings = new AppSettings
            {
                ExtractToOriginalDirectory = true,
                KeepArchiveNameFolder = true
            };

            return new SettingsViewModel(settings, new SettingsService());
        }
    }
}

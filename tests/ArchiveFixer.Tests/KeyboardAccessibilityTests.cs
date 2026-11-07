using System;
using System.IO;
using System.Linq;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 缩小后的键盘/可访问性目标：主要列表、密码控件有可读名称，
    /// 弹窗保留 Enter/Esc 路径；不把完整 Narrator 认证当成本轮验收。
    /// </summary>
    public sealed class KeyboardAccessibilityTests
    {
        [Fact]
        public void 主要列表与密码控件有可读名称()
        {
            Assert.Contains(
                "AutomationProperties.Name=\"任务列表\"",
                Read("src", "ArchiveFixer", "Views", "Tabs", "TaskTab.xaml"),
                StringComparison.Ordinal);

            Assert.Contains(
                "AutomationProperties.Name=\"密码列表\"",
                Read("src", "ArchiveFixer", "Views", "PasswordListWindow.xaml"),
                StringComparison.Ordinal);

            string passwordTab = Read("src", "ArchiveFixer", "Views", "Tabs", "PasswordTab.xaml");
            Assert.Contains("AutomationProperties.Name=\"统一密码\"", passwordTab, StringComparison.Ordinal);
            Assert.Contains("AutomationProperties.Name=\"显示或隐藏统一密码\"", passwordTab, StringComparison.Ordinal);

            string main = Read("src", "ArchiveFixer", "MainWindow.xaml");
            Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", main, StringComparison.Ordinal);
        }

        [Fact]
        public void 模态子窗保留取消路径()
        {
            string views = Path.Combine(RepositoryRoot(), "src", "ArchiveFixer", "Views");

            foreach (string name in new[]
                     {
                         "HelpWindow.xaml",
                         "ManualOperationWindow.xaml",
                         "OneClickOptionsWindow.xaml",
                         "PackingConfirmWindow.xaml",
                         "PasswordListWindow.xaml",
                         "PasswordPickerWindow.xaml",
                         "RenamePreviewWindow.xaml",
                         "TaskDetailWindow.xaml"
                     })
            {
                Assert.Contains("IsCancel=\"True\"", File.ReadAllText(Path.Combine(views, name)), StringComparison.Ordinal);
            }
        }

        [Fact]
        public void 自定义按钮保留键盘焦点视觉()
        {
            string app = Read("src", "ArchiveFixer", "App.xaml");

            Assert.Contains("IsKeyboardFocusWithin", app, StringComparison.Ordinal);
            Assert.Contains("IsKeyboardFocused", app, StringComparison.Ordinal);
        }

        private static string Read(params string[] parts)
            => File.ReadAllText(Path.Combine(new[] { RepositoryRoot() }.Concat(parts).ToArray()));

        private static string RepositoryRoot()
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);

            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ArchiveFixer.slnx")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            return AppContext.BaseDirectory;
        }
    }
}

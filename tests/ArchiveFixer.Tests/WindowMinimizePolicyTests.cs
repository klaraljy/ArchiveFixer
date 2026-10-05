using ArchiveFixer.Views;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 模态子窗"不许最小化"那条统一策略的守门用例（用户 2026-10-05 拍板 A2）。
    ///
    /// <para><b>为什么钉结构而不只钉行为</b>：这一轮修的就是"每个窗口在 XAML / 构造函数里
    /// 各写各的窗口属性"——密码本能从任务栏找回来、说明窗最小化后连任务栏按钮都没有，
    /// 而主窗口此刻被模态禁用，用户只能去任务管理器。判据本身（
    /// <see cref="WindowMinimizePolicy.ShouldRestoreOnMinimize"/>）是纯函数，这里逐格钉死；
    /// "哪个窗口挂了这条策略"是**文件里写了什么**的事实，用源码扫描钉住，零误报、不需要 WPF 宿主。</para>
    ///
    /// <para>⛔ 刻意**不**起真窗口：这条策略一挂上就会去动 Win32 系统菜单、还会在窗口被最小化时抢回来，
    /// 跑用例时弹窗 / 抢焦点是明令禁止的（AGENTS.md §9.1）。</para>
    /// </summary>
    public class WindowMinimizePolicyTests
    {
        /// <summary>模态子窗（<c>Owner = 主窗口</c> + <c>ShowDialog()</c>）—— 一律不许最小化。</summary>
        private static readonly string[] ModalWindowNames =
        {
            "HelpWindow",
            "PasswordListWindow",
            "PasswordPickerWindow",
            "TaskDetailWindow",
            "RenamePreviewWindow",
            "AppDialogWindow",
            "OneClickOptionsWindow",
            "PackingConfirmWindow"
        };

        public static IEnumerable<object[]> ModalWindows =>
            ModalWindowNames.Select(name => new object[] { name });

        [Theory]
        [MemberData(nameof(ModalWindows))]
        public void 模态子窗_构造函数里都挂了不许最小化的策略(string windowName)
        {
            string source = File.ReadAllText(RepoPath("src", "ArchiveFixer", "Views", windowName + ".xaml.cs"));

            Assert.Contains("WindowMinimizePolicy.Apply(this)", source);
        }

        [Theory]
        [MemberData(nameof(ModalWindows))]
        public void 模态子窗_都不开任务栏按钮(string windowName)
        {
            // 模态子窗开着任务栏按钮 = 主窗口被禁用时任务栏上多出一个点了也没用的入口；
            // 关掉它，同时"不许最小化"保证用户不会因此把它弄丢。
            string source = File.ReadAllText(RepoPath("src", "ArchiveFixer", "Views", windowName + ".xaml"));

            Assert.Contains("ShowInTaskbar=\"False\"", source);
        }

        /// <summary>
        /// ⛔ **代码里内联建的**模态子窗也要挂同一条策略（它们不在 XAML 名单里，最容易漏）。
        ///
        /// <para>用户 2026-10-05 拍板 A2 时点的就是"一律"：模式窗不许最小化。真机上就有一个漏网的 ——
        /// `RenameCoordinator` 的「改名」框是 `CanResize` + 不显示任务栏按钮那种形状
        /// （= 最小化之后任务栏没按钮、主窗口又被模态禁用 ⇒ 窗口找不回来）。</para>
        /// </summary>
        [Theory]
        [InlineData(@"src\ArchiveFixer\ViewModels\RenameCoordinator.cs")]
        [InlineData(@"src\ArchiveFixer\ViewModels\ExtractionCoordinator.cs")]
        public void 内联建的模态子窗_同样挂了这条策略(string relativePath)
        {
            string source = File.ReadAllText(RepoPath(relativePath.Split('\\')));

            Assert.Contains("WindowMinimizePolicy.Apply(window)", source);
        }

        /// <summary>
        /// ⛔ 日志窗是用户自己点开的**非模态**窗：最小化 + 任务栏按钮正是它该有的行为。
        /// </summary>
        [Fact]
        public void 日志窗不许挂这条策略()
        {
            string source = File.ReadAllText(RepoPath("src", "ArchiveFixer", "Views", "LogWindow.xaml.cs"));

            Assert.DoesNotContain("WindowMinimizePolicy.Apply", source);
        }

        /// <summary>⛔ 主窗口更不该被这条策略管住（它就是那个"Owner"）。</summary>
        [Fact]
        public void 主窗口不许挂这条策略()
        {
            string source = File.ReadAllText(RepoPath("src", "ArchiveFixer", "MainWindow.xaml.cs"));

            Assert.DoesNotContain("WindowMinimizePolicy.Apply", source);
        }

        /// <summary>
        /// 判据四格（自己最小化 / 主窗口最小化）。后两格防的是"最小化整个程序被搅成闪烁"：
        /// 用户点主窗口的最小化 / 按 Win+D 时主窗口也是 <see cref="WindowState.Minimized"/>，那时按兵不动。
        /// </summary>
        [Theory]
        [InlineData(WindowState.Minimized, WindowState.Normal, true)]
        [InlineData(WindowState.Minimized, WindowState.Minimized, false)]
        [InlineData(WindowState.Normal, WindowState.Normal, false)]
        [InlineData(WindowState.Normal, WindowState.Minimized, false)]
        public void 判据_自己被单独最小化时才还原(WindowState self, WindowState owner, bool expected)
        {
            Assert.Equal(expected, WindowMinimizePolicy.ShouldRestoreOnMinimize(self, owner));
        }

        [Fact]
        public void 判据_没有主窗口时也还原()
        {
            // 没 Owner（null）= 不存在"主窗口也被最小化"这件事 ⇒ 按还原办。
            Assert.True(WindowMinimizePolicy.ShouldRestoreOnMinimize(WindowState.Minimized, null));
        }

        [Fact]
        public void 判据_最大化与普通都不还原()
        {
            Assert.False(WindowMinimizePolicy.ShouldRestoreOnMinimize(WindowState.Maximized, WindowState.Normal));
            Assert.False(WindowMinimizePolicy.ShouldRestoreOnMinimize(WindowState.Normal, null));
        }

        [Fact]
        public void 策略是公开的_且改大小不受它管()
        {
            // 策略必须能被 View 层直接用（它在 Views/ 下，⛔ 不进不得引用 WPF 的那几层）。
            Assert.True(typeof(WindowMinimizePolicy).IsPublic);
            Assert.Equal("ArchiveFixer.Views", typeof(WindowMinimizePolicy).Namespace);

            Assert.True(File.Exists(RepoPath("src", "ArchiveFixer", "Views", "WindowMinimizePolicy.cs")));

            // A2 = 保留拖边框改大小：策略自己**不许**碰 ResizeMode（碰了就成了"换个地方各写各的"）。
            // 只看代码行：注释里提到 ResizeMode 是说明为什么，不算改行为。
            string source = string.Join("\n", NonCommentLines(File.ReadAllLines(
                RepoPath("src", "ArchiveFixer", "Views", "WindowMinimizePolicy.cs"))));

            Assert.DoesNotContain("ResizeMode", source);
        }

        /// <summary>去掉注释行（<c>///</c> / <c>//</c> / <c>*</c> / <c>/*</c> 开头那些）。</summary>
        private static IEnumerable<string> NonCommentLines(IEnumerable<string> lines)
        {
            foreach (string line in lines)
            {
                string trimmed = line.TrimStart();

                if (trimmed.StartsWith("///", StringComparison.Ordinal) ||
                    trimmed.StartsWith("//", StringComparison.Ordinal) ||
                    trimmed.StartsWith("*", StringComparison.Ordinal) ||
                    trimmed.StartsWith("/*", StringComparison.Ordinal))
                {
                    continue;
                }

                yield return line;
            }
        }

        /// <summary>
        /// 全项目不许再用 <c>ResizeMode="CanMinimize"</c>：它的语义是"只能最小化、不能改大小"，
        /// 会把"能不能最小化"这件事又变成每个窗口各写各的（正是这一轮拆掉的东西）。
        /// </summary>
        [Fact]
        public void 没有任何窗口_XAML_用_CanMinimize()
        {
            string appDirectory = Path.Combine(RepositoryRoot, "src", "ArchiveFixer");
            var findings = new List<string>();

            foreach (string file in Directory
                         .EnumerateFiles(appDirectory, "*.xaml", SearchOption.AllDirectories)
                         .Where(path => !IsBuildOutput(path)))
            {
                string[] lines = File.ReadAllLines(file);

                for (int index = 0; index < lines.Length; index++)
                {
                    if (lines[index].Contains("CanMinimize", StringComparison.Ordinal))
                    {
                        findings.Add($"{Path.GetFileName(file)}:{index + 1}（{lines[index].Trim()}）");
                    }
                }
            }

            Assert.True(
                findings.Count == 0,
                "这些地方在用 CanMinimize 把「能不能最小化」又变成各处自定：" +
                Environment.NewLine + string.Join(Environment.NewLine, findings));
        }

        private static bool IsBuildOutput(string path)
        {
            string separator = Path.DirectorySeparatorChar.ToString();

            return path.Contains(separator + "obj" + separator, StringComparison.OrdinalIgnoreCase)
                || path.Contains(separator + "bin" + separator, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>仓库根下的文件路径（从测试输出目录往上找 ArchiveFixer.slnx）。</summary>
        private static string RepoPath(params string[] parts)
        {
            return Path.Combine(new[] { RepositoryRoot }.Concat(parts).ToArray());
        }

        private static string RepositoryRoot
        {
            get
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

                throw new InvalidOperationException("找不到仓库根（ArchiveFixer.slnx）。");
            }
        }
    }
}

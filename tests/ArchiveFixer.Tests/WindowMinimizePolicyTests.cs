using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Views;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// <b>子窗一律"能缩到任务栏"</b>（用户 2026-10-05 拍板的口径）。
    ///
    /// <para>用户原话：「我想要的是最小化全部缩到状态栏里面，你现在不要搞那些多余的东西」
    /// —— 也就是**普通窗口的行为**：点最小化 ⇒ 缩到任务栏、任务栏上有按钮、点一下就回来。</para>
    ///
    /// <para><b>所以这里钉四件事</b>：① 每个子窗（含代码里内联建的那两个）都挂了
    /// <see cref="WindowMinimizePolicy.Apply"/>；② <b>任何窗口都不许把自己排除在任务栏之外</b>
    /// （<c>ShowInTaskbar="False"</c> 的子窗一旦被最小化，任务栏上没有它的按钮、主窗口此刻又被模态禁用
    /// ⇒ 只能去任务管理器 —— 这正是用户报的那个毛病）；③ 策略只做"放进任务栏"这一件事；
    /// ④ ⛔ 策略里不许再出现那些"多余的东西"（置灰最小化按钮 / <c>StateChanged</c> 强行还原 / 碰
    /// <c>ResizeMode</c>）—— 前者是反着来，后者在真机上把整程序的最小化搅成**一闪一闪**。</para>
    /// </summary>
    public class WindowMinimizePolicyTests
    {
        /// <summary>主窗口拥有的模态子窗（<c>Owner = 主窗口</c> + <c>ShowDialog()</c>）。</summary>
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
        public void 模态子窗_构造里都挂了缩到任务栏的策略(string windowName)
        {
            string source = File.ReadAllText(RepoPath("src", "ArchiveFixer", "Views", windowName + ".xaml.cs"));

            Assert.Contains("WindowMinimizePolicy.Apply(this)", source);
        }

        /// <summary>
        /// ⛔ **代码里内联建的**子窗也要挂（它们不在 XAML 名单里，最容易漏）。
        /// </summary>
        [Theory]
        [InlineData(@"src\ArchiveFixer\ViewModels\RenameCoordinator.cs")]
        [InlineData(@"src\ArchiveFixer\ViewModels\ExtractionCoordinator.cs")]
        public void 内联建的子窗_同样挂了这条策略(string relativePath)
        {
            string source = File.ReadAllText(RepoPath(relativePath.Split('\\')));

            Assert.Contains("WindowMinimizePolicy.Apply(window)", source);
        }

        /// <summary>⛔ 主窗口与日志窗本来就是普通窗口（本来就在任务栏上），不需要也不该接这条策略。</summary>
        [Fact]
        public void 主窗口与日志窗不接这条策略()
        {
            Assert.DoesNotContain(
                "WindowMinimizePolicy.Apply",
                File.ReadAllText(RepoPath("src", "ArchiveFixer", "MainWindow.xaml.cs")));

            Assert.DoesNotContain(
                "WindowMinimizePolicy.Apply",
                File.ReadAllText(RepoPath("src", "ArchiveFixer", "Views", "LogWindow.xaml.cs")));
        }

        /// <summary>
        /// ⛔ <b>任何窗口都不许把自己排除在任务栏之外</b>（用户 2026-10-05 的诉求就是"最小化全部缩到状态栏里"）：
        /// <c>ShowInTaskbar="False"</c> 的子窗被最小化之后，任务栏上没有它的按钮，而主窗口此刻被模态禁用
        /// ⇒ 用户找不回来（真机原话：「之前还能在状态栏里面」）。
        /// </summary>
        [Fact]
        public void 任何窗口都不许把自己排除在任务栏之外()
        {
            string viewsDirectory = Path.Combine(XamlBindingScan.RepositoryRoot, "src", "ArchiveFixer", "Views");

            var offenders = Directory
                .EnumerateFiles(viewsDirectory, "*.xaml", SearchOption.AllDirectories)
                .Where(path => File.ReadAllText(path).Contains("ShowInTaskbar=\"False\"", StringComparison.Ordinal))
                .Select(Path.GetFileName)
                .ToList();

            Assert.True(
                offenders.Count == 0,
                "这些窗口把自己排除了任务栏 —— 它们被最小化之后就找不回来了：" + string.Join("、", offenders));

            // 内联建的那个「改名」框同理（它当初也写着 ShowInTaskbar = false）。
            string rename = File.ReadAllText(RepoPath("src", "ArchiveFixer", "ViewModels", "RenameCoordinator.cs"));

            Assert.DoesNotContain("ShowInTaskbar = false", rename);
        }

        /// <summary>
        /// 策略只做一件事：把窗口放进任务栏。⛔ 那些"多余的东西"一个都不许回来
        /// （置灰按钮 = 反着来；<c>StateChanged</c> 强行还原 = 真机上的一闪一闪；碰 <c>ResizeMode</c> = 把
        /// "能不能改大小"变成各写各的）。只看代码行 —— 注释里提到这些词是说明为什么，不算改行为。
        /// </summary>
        [Fact]
        public void 策略只做把窗口放进任务栏这一件事()
        {
            string source = string.Join("\n", NonCommentLines(File.ReadAllLines(
                RepoPath("src", "ArchiveFixer", "Views", "WindowMinimizePolicy.cs"))));

            Assert.Contains("ShowInTaskbar = true", source);

            Assert.DoesNotContain("StateChanged", source);
            Assert.DoesNotContain("WindowState", source);
            Assert.DoesNotContain("EnableMenuItem", source);
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

        /// <summary>从仓库根读一个文本文件（测试的工作目录是 bin\…，往上找到仓库那一层为止）。</summary>
        private static string RepoPath(params string[] parts)
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);

            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ArchiveFixer.slnx")))
                {
                    return Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException("找不到仓库根（ArchiveFixer.slnx）。");
        }
    }
}

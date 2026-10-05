using System;
using System.IO;
using System.Text;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 设置里那一格「推荐另外装一个 WinRAR（可选）」+ 官方下载页（用户 2026-10-05 拍板）。
    ///
    /// <para><b>为什么要有这一组</b>：真机上一个 WinZip AES 的 ZIP，内置 7-Zip 26.03 对**正确**的密码报
    /// 「Wrong password」，用户自己用 WinRAR 十秒就打开了（AGENTS.md §11.5 最后一条）⇒ 程序补了"候选试完
    /// 仍失败就换本机装的 WinRAR 再试"这一档兜底，而那一档**得有 WinRAR 才跑得起来**。于是设置里要有一处
    /// 指路：说明为什么值得装、说清**可选**、说清我们**绝不捆绑、绝不分发、不改他的 WinRAR 设置**。</para>
    ///
    /// <para>⛔ 这一组只钉**指路**这件事，不下载、不代装、不检查本机装没装（那是
    /// <c>ToolLocator</c> / <c>WinRarProcessRunner</c> 的事）。</para>
    /// </summary>
    public class WinRarRecommendationTests
    {
        /// <summary>
        /// 下载页地址必须**同时**满足三件事：https、主机正好是发布方官网、**过得了白名单**。
        /// 最后一条是关键 —— 地址改了而白名单没跟着改，按钮就会变成"点了没反应"。
        /// </summary>
        [Fact]
        public void 下载页地址是发布方官网的_https_且过得了白名单()
        {
            string url = SettingsViewModel.WinRarDownloadUrl;

            Assert.StartsWith("https://", url, StringComparison.Ordinal);
            Assert.Contains("rarlab.com", url, StringComparison.OrdinalIgnoreCase);
            Assert.True(
                SafePathHelper.IsAllowedExternalLink(url),
                $"下载页地址 {url} 过不了白名单 ⇒ 那个按钮会点了没反应。");
        }

        /// <summary>
        /// 白名单只放行发布方官网：⛔ 别的协议（<c>http</c> / <c>file</c>）与**看着像**的域名一律不放行。
        ///
        /// <para>为什么这条必须守：<c>UseShellExecute = true</c> 是把字符串直接交给 shell 的出口，
        /// 一旦"什么 URL 都能开"，这个按钮就成了任意程序执行的后门；而 <c>EndsWith("rarlab.com")</c>
        /// 这类写法会让 <c>rarlab.com.evil.tld</c> 跟着过 —— 所以判据是**主机名逐字相等**。</para>
        /// </summary>
        [Fact]
        public void 白名单挡得住伪装域名_别的协议_与本地可执行文件()
        {
            Assert.True(SafePathHelper.IsAllowedExternalLink("https://www.rarlab.com/download.htm"));
            Assert.True(SafePathHelper.IsAllowedExternalLink("https://rarlab.com/download.htm"));

            Assert.False(SafePathHelper.IsAllowedExternalLink("http://www.rarlab.com/download.htm"));
            Assert.False(SafePathHelper.IsAllowedExternalLink("https://www.rarlab.com.evil.tld/download.htm"));
            Assert.False(SafePathHelper.IsAllowedExternalLink("https://evil.tld/?x=rarlab.com"));
            Assert.False(SafePathHelper.IsAllowedExternalLink(@"file:///C:/Windows/System32/calc.exe"));
            Assert.False(SafePathHelper.IsAllowedExternalLink(@"C:\Windows\System32\calc.exe"));
            Assert.False(SafePathHelper.IsAllowedExternalLink("javascript:alert(1)"));
            Assert.False(SafePathHelper.IsAllowedExternalLink(null));
            Assert.False(SafePathHelper.IsAllowedExternalLink("   "));

            // 真起进程那条出口只认白名单（⛔ 用例不去点它，免得在别人机器上弹浏览器）。
            Assert.False(SafePathHelper.OpenExternalLink("https://evil.tld/x"));
        }

        /// <summary>
        /// 文案三件事一个都不许省：**可选**、**绝不捆绑 / 不随包分发**、**不改你的 WinRAR 设置**。
        /// 用户原话是「在设置里面写一个推荐一同安装WinRAR」「我 C 盘里面的 winrar 设置你不要带入联系起来」。
        /// </summary>
        [Fact]
        public void 那一格的说法必须写明_可选_不捆绑_不动他的设置()
        {
            Assert.Contains("可选", StatusText.SettingsWinRarRecommendLabel, StringComparison.Ordinal);
            Assert.Contains("WinRAR", StatusText.SettingsWinRarRecommendLabel, StringComparison.Ordinal);

            string hint = StatusText.SettingsWinRarRecommendHint;

            Assert.Contains("绝不捆绑", hint, StringComparison.Ordinal);
            Assert.Contains("绝不随包分发", hint, StringComparison.Ordinal);
            Assert.Contains("不改你的 WinRAR 设置", hint, StringComparison.Ordinal);
            Assert.Contains("不装也能用", hint, StringComparison.Ordinal);

            // ⛔ 界面字符串不许出现 Markdown 星号（§9.3：WPF 会原样显示成星号）。
            Assert.DoesNotContain("**", hint, StringComparison.Ordinal);
        }

        /// <summary>
        /// 那一格真的在②页（引擎与工具那一组）挂上了：标题 / 说明 / 按钮三处绑定都在，
        /// 且按钮绑的是 <c>OpenWinRarDownloadPageCommand</c>（⛔ 不是"只写了文案忘了接命令"）。
        /// </summary>
        [Fact]
        public void 设置页真的把这一格和那个按钮绑上了()
        {
            string xamlPath = Path.Combine(
                XamlBindingScan.RepositoryRoot,
                "src",
                "ArchiveFixer",
                "Views",
                "Tabs",
                "ExtractionTab.xaml");

            Assert.True(File.Exists(xamlPath), $"找不到②页 XAML：{xamlPath}");

            string xaml = File.ReadAllText(xamlPath, Encoding.UTF8);

            // ⚠ 断言必须是**整条绑定表达式**（带结尾那个 `}`）：只比裸成员名的话，
            //    `OpenWinRarDownloadPageCommandRedCheck` 这种改名照样"包含"它 ⇒ 用例不会红。
            Assert.Contains("{Binding SettingsEditor.WinRarRecommendLabel}", xaml, StringComparison.Ordinal);
            Assert.Contains("{Binding SettingsEditor.WinRarRecommendHint}", xaml, StringComparison.Ordinal);
            Assert.Contains("{Binding SettingsEditor.WinRarDownloadButtonText}", xaml, StringComparison.Ordinal);
            Assert.Contains("{Binding SettingsEditor.OpenWinRarDownloadPageCommand}", xaml, StringComparison.Ordinal);
        }
    }
}

using System;
using System.IO;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 安装包脚本的静态守门：Unicode/BOM、桌面快捷方式可选、卸载说明用新密码文件。
    /// </summary>
    public sealed class InstallerScriptTests
    {
        [Fact]
        public void 安装包脚本_保持UnicodeBOM并提供桌面快捷方式组件页()
        {
            string path = FindInstallerScript();
            byte[] bytes = File.ReadAllBytes(path);

            Assert.True(
                bytes.Length >= 3
                && bytes[0] == 0xEF
                && bytes[1] == 0xBB
                && bytes[2] == 0xBF,
                "ArchiveFixer.nsi 必须保留 UTF-8 BOM；否则 makensis 会报 Bad text encoding");

            string text = File.ReadAllText(path);

            Assert.Contains("Unicode true", text, StringComparison.Ordinal);
            Assert.Contains("MUI_PAGE_COMPONENTS", text, StringComparison.Ordinal);
            Assert.Contains("桌面快捷方式（可选）", text, StringComparison.Ordinal);
            Assert.Contains("passwords.txt", text, StringComparison.Ordinal);
            Assert.DoesNotContain("password-list.dat —— 密码本", text, StringComparison.Ordinal);

            // 固定用户数据目录必须与 PathService.DefaultDataRootDirectory 一致；
            // 升级页要同时写旧版本和新安装包版本，卸载页要指向固定目录。
            Assert.Contains("!define USER_DATA_DIR \"$LOCALAPPDATA\\ArchiveFixer\"", text, StringComparison.Ordinal);
            Assert.Contains("当前安装包版本", text, StringComparison.Ordinal);
            Assert.Contains("旧版本", text, StringComparison.Ordinal);
            Assert.Contains("WriteRegStr HKCU \"${APP_KEY}\" \"DataDir\"", text, StringComparison.Ordinal);
            Assert.Contains("RMDir /r \"${USER_DATA_DIR}\"", text, StringComparison.Ordinal);
            Assert.Contains("RMDir /r \"$INSTDIR\\tools\"", text, StringComparison.Ordinal);
            Assert.Contains("PurgeInstalledAppDirs", text, StringComparison.Ordinal);
            Assert.Contains("RMDir /r \"$INSTDIR\\zh-Hans\"", text, StringComparison.Ordinal);
            Assert.Contains("RMDir /r \"$INSTDIR\\zh-Hant\"", text, StringComparison.Ordinal);
            Assert.Contains("StrCpy $R8 $INSTDIR", text, StringComparison.Ordinal);
            Assert.DoesNotContain("FindFirst $R0 $R1 \"$INSTDIR\\*.*\"", text, StringComparison.Ordinal);
        }

        /// <summary>
        /// 说明页那条红字（用户 2026-11-10 原文要求）：首句固定，且必须说清"空间不足模式"的边界。
        /// </summary>
        [Fact]
        public void 说明页空间不足红字_首句固定且说清边界()
        {
            string hint = ArchiveFixer.Models.HelpContent.SpaceTightModeHint;

            Assert.StartsWith(
                "普通档位解压要 2–3 倍空间，若只有一半但能容纳最大资源，可开空间不足模式。",
                hint,
                StringComparison.Ordinal);
            Assert.Contains("不删原包", hint, StringComparison.Ordinal);
            Assert.Contains("源包一个字节都不动", hint, StringComparison.Ordinal);

            string xaml = File.ReadAllText(FindHelpBodyPanel());

            Assert.Contains("HelpContent.SpaceTightModeHint", xaml, StringComparison.Ordinal);
            Assert.Contains("DangerTextBrush", xaml, StringComparison.Ordinal);
        }

        private static string FindHelpBodyPanel()
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);

            while (directory != null)
            {
                string candidate = Path.Combine(directory.FullName, "src", "ArchiveFixer", "Views", "HelpBodyPanel.xaml");

                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("往上找不到 src\\ArchiveFixer\\Views\\HelpBodyPanel.xaml");
        }
        private static string FindInstallerScript()
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);

            while (directory != null)
            {
                string candidate = Path.Combine(
                    directory.FullName,
                    "installer",
                    "ArchiveFixer.nsi");

                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("往上找不到 installer\\ArchiveFixer.nsi");
        }
    }
}

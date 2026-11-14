using System;
using System.IO;
using Xunit;

namespace ArchiveFixer.Tests;

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
        // 页面顺序固定为：欢迎 → 许可 → 安装位置 → 组件 → 安装进度 → 完成。
        Assert.Contains("!define USER_DATA_DIR \"$LOCALAPPDATA\\ArchiveFixer\"", text, StringComparison.Ordinal);
        Assert.Contains("MUI_PAGE_WELCOME", text, StringComparison.Ordinal);
        Assert.Contains("MUI_PAGE_LICENSE", text, StringComparison.Ordinal);
        Assert.Contains("MUI_PAGE_DIRECTORY", text, StringComparison.Ordinal);
        Assert.Contains("MUI_PAGE_COMPONENTS", text, StringComparison.Ordinal);
        Assert.Contains("MUI_PAGE_INSTFILES", text, StringComparison.Ordinal);
        Assert.Contains("MUI_PAGE_FINISH", text, StringComparison.Ordinal);
        Assert.Contains("ArchiveFixer 用于批量识别", text, StringComparison.Ordinal);
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
        Assert.Contains("会删源包", hint, StringComparison.Ordinal);
        Assert.DoesNotContain("不删原包", hint, StringComparison.Ordinal);

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
    /// <summary>
    /// 许可页正文必须是 ASCII：NSIS 许可页按 ANSI 读取，UTF-8 中文会整页乱码
    /// （0.2.2 实测：LICENSE 的「第三方组件」中文表格整段变乱码）。
    /// ⇒ 构建脚本只把 LICENSE 里第一个 `---` 之前的 MIT 正文当许可页，中文部分留在 LICENSE 原文。
    /// </summary>
    [Fact]
    public void 许可页正文必须是ASCII_MIT部分可独立成页()
    {
        string repoRoot = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(FindInstallerScript())!, ".."));

        string license = File.ReadAllText(Path.Combine(repoRoot, "LICENSE"));
        int separator = license.IndexOf("\n---", StringComparison.Ordinal);
        string mitPart = separator > 0 ? license[..separator] : license;

        Assert.DoesNotContain(mitPart, c => c > 0x7F);

        string installer = File.ReadAllText(Path.Combine(repoRoot, "scripts", "installer.ps1"));
        Assert.Contains("af-license-page", installer, StringComparison.Ordinal);
        Assert.Contains("/DLICENSEFILE=$licensePagePath", installer, StringComparison.Ordinal);
    }
    /// <summary>
    /// 单文件发布：运行时被打进 exe，但 **tools\ 必须留在外面**（7z/UnRAR 要被当进程调起来），
    /// 升级覆盖还要清掉旧多文件版遗留的根目录 DLL。缺任何一条都会退回「安装目录一堆 DLL」或引擎找不到。
    /// </summary>
    [Fact]
    public void 独立版单文件_工具留在外面_升级会清旧运行时文件()
    {
        string repoRoot = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(FindInstallerScript())!, ".."));

        string package = File.ReadAllText(Path.Combine(repoRoot, "scripts", "package.ps1"));
        Assert.Contains("PublishSingleFile=true", package, StringComparison.Ordinal);
        Assert.Contains("IncludeNativeLibrariesForSelfExtract=true", package, StringComparison.Ordinal);

        string csproj = File.ReadAllText(Path.Combine(
            repoRoot, "src", "ArchiveFixer", "ArchiveFixer.csproj"));
        Assert.True(
            csproj.Split("<ExcludeFromSingleFile>true</ExcludeFromSingleFile>").Length - 1 >= 6,
            "tools 目录下的 7z.exe / 7z.dll / UnRAR.exe 与许可文本必须标 ExcludeFromSingleFile");

        string nsi = File.ReadAllText(Path.Combine(repoRoot, "installer", "ArchiveFixer.nsi"));
        Assert.Contains("Delete \"$INSTDIR\\*.dll\"", nsi, StringComparison.Ordinal);
        Assert.Contains("Delete \"$INSTDIR\\createdump.exe\"", nsi, StringComparison.Ordinal);
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

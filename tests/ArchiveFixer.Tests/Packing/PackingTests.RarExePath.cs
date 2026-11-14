using ArchiveFixer.Engines;
using ArchiveFixer.Models;
using ArchiveFixer.Packing;
using ArchiveFixer.Password;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace ArchiveFixer.Tests;

/// <summary>PackingTests 拆分：⑪ 自选 Rar.exe 路径。</summary>
public partial class PackingTests
{

    // ══════════════════════════ ⑪ 自选 Rar.exe 路径（用户 2026-09-23 决定） ══════════════════════════

    [Fact]
    public void 自选RarExe路径_合法就用它_留空回落已装目录()
    {
        string fakeRar = Path.Combine(_root, "我自己的-Rar.exe");
        File.WriteAllText(fakeRar, "占位：只验证'自选路径被解析链采纳'，本用例不会执行它");

        // ① 填了一个真实存在的文件 → 就用它（即使"已装 WinRAR 目录"这一档被关掉）。
        var custom = new ToolLocator { UseWinRarInstallation = false, CustomRarExePath = fakeRar };

        Assert.True(custom.RarExists);
        Assert.True(custom.IsUsingCustomRarPath);
        Assert.Equal(fakeRar, custom.RarExePath);
        Assert.Contains("自选", custom.DescribeRarResolution(), StringComparison.Ordinal);
        Assert.Contains(fakeRar, custom.DescribeRarResolution(), StringComparison.Ordinal);

        // ② 填了一个不存在的 → **不吃掉后面的档位**（与 UnRAR 同一口径：回落到下一档并如实说明）。
        var missing = new ToolLocator { CustomRarExePath = Path.Combine(_root, "没有这个-Rar.exe") };

        Assert.False(missing.IsUsingCustomRarPath);
        Assert.True(missing.RarExists, "回落到'已装 WinRAR 目录'那一档（本机确实装了 WinRAR）");
        Assert.Contains("已装 WinRAR 目录", missing.DescribeRarResolution(), StringComparison.Ordinal);

        // ③ 留空 = 自动：用本机已装 WinRAR 目录里的那一份（本机实测有 C:\Program Files\WinRAR\Rar.exe）。
        var auto = new ToolLocator();

        if (!auto.RarExists)
        {
            Skip("测试机上没有 Rar.exe（没装 WinRAR）—— 第 ③ 段跳过，前两段照样跑到了");
            return;
        }

        Assert.False(auto.IsUsingCustomRarPath);
        Assert.Contains("已装 WinRAR 目录", auto.DescribeRarResolution(), StringComparison.Ordinal);
        Assert.EndsWith("Rar.exe", auto.RarExePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 设置里的RarExe路径_不存在时保存被拦下并说明改法()
    {
        string previous = ToolLocator.Default.CustomRarExePath;

        try
        {
            SettingsViewModel viewModel = CreateSettingsViewModel("data-rar");
            string typed = Path.Combine(_root, "并不存在的-Rar.exe");

            viewModel.CustomRarExePath = typed;
            viewModel.SaveCommand.Execute(null);

            // 没有关窗（DialogResult 仍为 null）= 保存没有发生。
            Assert.Null(viewModel.DialogResult);
            Assert.Contains("Rar.exe 路径", viewModel.Message, StringComparison.Ordinal);
            Assert.Contains("不存在", viewModel.Message, StringComparison.Ordinal);
            Assert.Contains("清空这一格", viewModel.Message, StringComparison.Ordinal);

            // 说清"留空是什么行为"（= 自动用本机已装 WinRAR 目录里的那一份）。
            Assert.Contains(StatusText.SettingsRarExePathEmptyHint, viewModel.Message, StringComparison.Ordinal);

            // 最要紧的一条：用户填的值**还在**（不是"被清空 + 告诉你保存成功"）。
            Assert.Equal(typed, viewModel.CustomRarExePath);

            // 走正常的 Normalize 一定会被清空 —— 那正是必须拦在前面的原因。
            var probe = new AppSettings { CustomRarExePath = typed };
            probe.Normalize();

            Assert.Equal(string.Empty, probe.CustomRarExePath);

            // 合法文件 → 保存通过，而且值真的推给了外部工具路径的唯一来源。
            string real = Path.Combine(_root, "Rar-占位.exe");
            File.WriteAllText(real, "占位：只验证路径被接受");

            SettingsViewModel ok = CreateSettingsViewModel("data-rar-ok");

            ok.CustomRarExePath = real;

            // 工具状态那一行要能看出"用的是自选的那份"。
            Assert.Contains("自选", ok.RarToolStatusText, StringComparison.Ordinal);

            ok.SaveCommand.Execute(null);

            Assert.True(ok.DialogResult);
            Assert.Equal(real, ok.CustomRarExePath);
            Assert.Equal(real, ToolLocator.Default.CustomRarExePath);
        }
        finally
        {
            ToolLocator.Default.CustomRarExePath = previous;
        }
    }

    [Fact]
    public void 引擎页里真的有RarExe那一格_并且写明许可边界()
    {
        /*
         * 2026-09-24 第 11 条之后外部工具路径归到「引擎」分组（原来是设置窗口的「高级设置」里）；
         * ⚠ 2026-10-14（用户最终设计：「引擎：从解压方式那边搬过来的」）整组搬到⑦引擎页。
         */
        string xaml = File.ReadAllText(
            Path.Combine(XamlBindingScan.RepositoryRoot, "src", "ArchiveFixer", "Views", "Tabs", "EngineTab.xaml"));

        Assert.Contains("CustomRarExePath", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectRarExeCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("RarExePathHint", xaml, StringComparison.Ordinal);
        Assert.Contains("RarToolStatusText", xaml, StringComparison.Ordinal);

        // 文案必须写明许可边界，而不是只写一个"路径"。
        Assert.Contains("绝不随包分发", StatusText.SettingsRarExePathHint, StringComparison.Ordinal);
        Assert.Contains("共享软件", StatusText.SettingsRarExePathHint, StringComparison.Ordinal);
        Assert.Contains("你自己安装", StatusText.SettingsRarExePathHint, StringComparison.Ordinal);
        Assert.Contains("留空", StatusText.SettingsRarExePathHint, StringComparison.Ordinal);
    }
}

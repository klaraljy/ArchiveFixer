using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Models;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **系统固定默认值表**（用户 2026-10-14 最终设计 + 当场逐项拍板）。
    ///
    /// <para>用户原话：「我觉得有些删除的选项里面都应该时系统底层会做的而不是让用户来选择的」，
    /// 并对"删除 = 从界面移出、值固定成系统默认（功能一个都不删）"当场确认：「对，就是这个意思」。</para>
    ///
    /// <para>这一批设置项**界面入口已经删掉**，所以必须钉住三件事，缺一件都会变成暗档：</para>
    /// <list type="number">
    /// <item><description>归一之后**一律是系统值** —— 用户机器上 `appsettings.json` 里存过的旧值不许留
    /// （留着 = 行为跟他以为的不一样，还没地方改回来）；</description></item>
    /// <item><description>界面上**一个入口都没有**（含窗口：`Views/**/*.xaml` 全扫）；</description></item>
    /// <item><description>**还有入口的项照旧不许被迁移**（这一条防的是"钉过头"：把用户的正常选择也顶掉）。</description></item>
    /// </list>
    ///
    /// <para><b>红检</b>：把 <see cref="AppSettings.Normalize"/> 里那一段固定值删掉任意一行 ⇒
    /// 对应的那一格当场红（`Expected: True / Actual: False` 之类）。</para>
    /// </summary>
    public class FixedDefaultsTests
    {
        /// <summary>钉死的项：设置名 → 归一之后必须是这个值。</summary>
        private static readonly (string Name, object Expected)[] Pinned =
        {
            (nameof(AppSettings.RememberLastOutputDirectory), true),
            (nameof(AppSettings.LowProcessPriority), false),
            (nameof(AppSettings.RecursiveScan), true),
            (nameof(AppSettings.AutoScanAfterDrop), true),
            (nameof(AppSettings.ScanMode), "ScanAllFiles"),
            (nameof(AppSettings.UnknownFormatAction), "MarkUnknown"),
            (nameof(AppSettings.DefaultExtension), ".7z"),
            (nameof(AppSettings.ConflictAction), ConflictActions.AutoRename),
            (nameof(AppSettings.OmitMiddleContinuationLayers), false),
            (nameof(AppSettings.OverwriteMode), "AutoRenameExisting"),
            (nameof(AppSettings.KeepFailedWorkspace), false),
            (nameof(AppSettings.RememberPasswordList), true),

            /*
             * ⛔ 这里**故意没有** `PartialPublishEnabled`：设计要的是"损坏压缩包那一档自动发布"，
             * 而那个开关现在同时管着取消途中不发布 / 工作区探针等无关行为 —— 直接钉 true 会打红
             * 5 条既有守门（实测），属于"改错了层"。留给「损坏压缩包 ⇒ 自动留内容物」那一刀：
             * 先把语义收窄到那一档，再钉值、再删界面。
             */
        };

        /// <summary>用户可能存过的"旧值"（每项给一个跟系统值不同的），用来证明归一真的把它顶掉了。</summary>
        private static object OldValueFor(string name) => name switch
        {
            nameof(AppSettings.RememberLastOutputDirectory) => false,
            nameof(AppSettings.LowProcessPriority) => true,
            nameof(AppSettings.RecursiveScan) => false,
            nameof(AppSettings.AutoScanAfterDrop) => false,
            nameof(AppSettings.ScanMode) => "ScanKnownArchiveExtensions",
            nameof(AppSettings.UnknownFormatAction) => "AskUser",
            nameof(AppSettings.DefaultExtension) => ".zip",
            nameof(AppSettings.ConflictAction) => "Ask",
            nameof(AppSettings.OmitMiddleContinuationLayers) => true,
            nameof(AppSettings.OverwriteMode) => "OverwriteAll",
            nameof(AppSettings.KeepFailedWorkspace) => true,
            nameof(AppSettings.RememberPasswordList) => false,
            nameof(AppSettings.RecursionMode) => "SingleLayer",
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "旧值表缺这一项")
        };

        [Fact]
        public void 钉死的值_用户存过什么都不算_归一之后一律是系统值()
        {
            foreach ((string name, object expected) in Pinned)
            {
                var settings = new AppSettings();
                typeof(AppSettings).GetProperty(name)!.SetValue(settings, OldValueFor(name));

                settings.Normalize();

                Assert.Equal(expected, typeof(AppSettings).GetProperty(name)!.GetValue(settings));
            }
        }

        [Fact]
        public void 出厂默认本来就是系统值()
        {
            var defaults = AppSettings.CreateDefault();

            foreach ((string name, object expected) in Pinned)
            {
                Assert.Equal(expected, typeof(AppSettings).GetProperty(name)!.GetValue(defaults));
            }
        }

        [Fact]
        public void 钉死的项_界面上一个入口都没有()
        {
            var offenders = new List<string>();

            foreach (string file in ViewXamlFiles())
            {
                string text = File.ReadAllText(file);

                foreach ((string name, _) in Pinned)
                {
                    if (text.Contains("Settings." + name, StringComparison.Ordinal)
                        || text.Contains("SettingsEditor." + name, StringComparison.Ordinal))
                    {
                        offenders.Add($"{Path.GetFileName(file)}：{name}");
                    }
                }
            }

            Assert.True(
                offenders.Count == 0,
                "这些项已经是系统固定值、界面上不该再有入口（有入口 = 用户改得动但下次归一又被顶掉）："
                + Environment.NewLine
                + string.Join(Environment.NewLine, offenders));
        }

        [Fact]
        public void 还有入口的项照旧不许被迁移()
        {
            /*
             * ⛔ 防"钉过头"：这一批**没有**被钉死的项，用户在界面上选过的值必须原样留着 ——
             * 这正是从 2026-09-24 起一直守着的那条规矩（只改出厂默认、不迁移用户存过的值）。
             */
            var settings = new AppSettings
            {
                MaxRecursionDepth = 9,
                MaxParallelExtractCount = 3,
                MaxPasswordAttemptsPerLayer = 25,
                KeepBrokenFiles = true,
                EnableLog = false,
                VerboseLog = true
            };

            settings.Normalize();

            Assert.Equal(9, settings.MaxRecursionDepth);
            Assert.Equal(3, settings.MaxParallelExtractCount);
            Assert.Equal(25, settings.MaxPasswordAttemptsPerLayer);
            Assert.True(settings.KeepBrokenFiles);
            Assert.False(settings.EnableLog);
            Assert.True(settings.VerboseLog);
        }

        private static IEnumerable<string> ViewXamlFiles()
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);

            while (directory != null)
            {
                string candidate = Path.Combine(directory.FullName, "src", "ArchiveFixer", "Views");

                if (Directory.Exists(candidate))
                {
                    return Directory.GetFiles(candidate, "*.xaml", SearchOption.AllDirectories)
                        .OrderBy(path => path, StringComparer.Ordinal);
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("往上找不到 src\\ArchiveFixer\\Views");
        }
    }
}

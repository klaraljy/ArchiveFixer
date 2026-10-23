using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 2026-09-27 真机第二轮六条问题的回归钉子（用户原话见 <c>修改日志.md</c> 那一行）。
    ///
    /// <list type="number">
    /// <item><description>列表要显示选中文件的大小（排在文件名后面）+ 勾选合计；</description></item>
    /// <item><description>「移除勾选的」的用法与可用性（跑批中途照样可点、没勾选时不亮、灰着也读得出为什么）；</description></item>
    /// <item><description>日志里"其余物会删什么"必须跟着**源包那一档**说，且术语统一（其余物 = 过程物 + 原包）；</description></item>
    /// <item><description>「停止后续」之后不许再试新的密码候选（在候选之间停）；</description></item>
    /// <item><description>不加密的包 / 连续候选结果相同 → 不许把结论写成「密码错误」，也不许继续烧候选；</description></item>
    /// <item><description>帮助 → 使用说明找不到文档时要列出找过的位置；docs 随程序一起发。</description></item>
    /// </list>
    ///
    /// <para>本文件里**不需要真 7z**：校验口径、术语表、列表列、命令可用性都是纯逻辑；
    /// 候选循环那三条在 <c>ExtractionPipelineFixTests</c> 里（那里已有假引擎与管线夹具）。</para>
    /// </summary>
    // 碰进程级静态（构造 MainViewModel 会写工作区根）：与同类用例串行跑，
    // 不与别的集合并行 —— 见 InnerLayerContinuationTests 顶部的 CollectionDefinition。
    [Collection("ArchiveFixerGlobalState")]
    public sealed class ItemSept27FixTests : IDisposable
    {
        private readonly string _root;

        public ItemSept27FixTests()
        {
            _root = CreateIsolatedRoot();
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch
            {
                // 临时目录删不掉不影响断言结果。
            }
        }

        // ================================================================ 第 5 条：校验口径




        private static ArchiveListResult CaseCollisionList() => new()
        {
            Success = true,
            FileCount = 4,
            TotalUncompressedSize = 100,   // 故意写一个"清单口径"的数：折算后必须换成 90
            Entries = new List<ArchiveEntry>
            {
                new() { Path = "res/9N.9.png", Size = 10 },
                new() { Path = "res/9n.9.png", Size = 20 },   // 与上一条仅大小写不同 → 后写的赢
                new() { Path = "res/a.png", Size = 30 },
                new() { Path = "readme.txt", Size = 40 }
            },
            EngineId = "fake",
            EngineVersion = "1.0"
        };

        // ================================================================ 第 3 条：其余物范围 + 说明






        // ================================================================ 第 1 条：大小列 + 勾选合计




        // ================================================================ 落点模型 v2：手动档「解压到当前文件夹」


        // ================================================================ 第 2 条：移除勾选的


        // ================================================================ 第 6 条：docs 随程序发



        // ================================================================ 装配

        private MainViewModel CreateViewModel() => CreateViewModel(out _);

        private MainViewModel CreateViewModel(out LogService logService)
        {
            string cacheRoot = Path.Combine(_root, "data");
            Directory.CreateDirectory(cacheRoot);

            var pathService = new PathService { DataRootDirectory = cacheRoot };
            var settingsService = new SettingsService(pathService);

            AppSettings settings = AppSettings.CreateDefault();
            settings.AutoScanAfterDrop = false;
            settingsService.Save(settings);

            string? previousWorkspaceRoot = RecursiveExtractor.ConfiguredWorkspaceRoot;
            string previousSevenZipPath = ToolLocator.Default.CustomSevenZipExePath;

            logService = new LogService(pathService);

            try
            {
                return new MainViewModel(
                    new FileScanService(),
                    new ArchiveDetectService(),
                    new RenameService(),
                    new ArchiveFixer.Engines.SevenZip.SevenZipEngine(),
                    new PasswordService(),
                    logService,
                    settingsService,
                    pathService,
                    new TaskSummaryService(),
                    new ClipboardService(),
                    new DialogService());
            }
            finally
            {
                RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;
                ToolLocator.Default.CustomSevenZipExePath = previousSevenZipPath;
            }
        }

        /// <summary>读仓库里的文件（与 <c>InterfaceRefactorTests</c> 同一手法；找不到就让测试红掉）。</summary>
        private static string Read(params string[] parts)
        {
            string path = Path.Combine(new[] { XamlBindingScan.RepositoryRoot, "src", "ArchiveFixer" }.Concat(parts).ToArray());

            Assert.True(File.Exists(path), $"读不到 {path}");

            return File.ReadAllText(path);
        }

        /// <summary>
        /// 隔离根目录：优先放在**测试程序集所在那块盘**（避免落到 C 盘 —— 缓存根目录那一档会拒 C 盘），
        /// 都不行才退回 %TEMP%。
        /// </summary>
        private static string CreateIsolatedRoot()
        {
            var candidates = new List<string>();

            try
            {
                string? assemblyDirectory = Path.GetDirectoryName(typeof(ItemSept27FixTests).Assembly.Location);

                if (!string.IsNullOrWhiteSpace(assemblyDirectory))
                {
                    candidates.Add(Path.GetPathRoot(assemblyDirectory) ?? string.Empty);
                }
            }
            catch
            {
                // 取不到就换下面的候选。
            }

            candidates.Add("E:\\");
            candidates.Add(Path.GetTempPath());

            foreach (string baseDirectory in candidates)
            {
                if (string.IsNullOrWhiteSpace(baseDirectory))
                {
                    continue;
                }

                try
                {
                    string parent = Path.Combine(baseDirectory, "ArchiveFixer-tests", "item-sept-27");
                    Directory.CreateDirectory(parent);
                    return Path.Combine(parent, "run-" + Guid.NewGuid().ToString("N"));
                }
                catch
                {
                    // 这块盘不能写就换下一块。
                }
            }

            return Path.Combine(Path.GetTempPath(), "af-item-sept-27-" + Guid.NewGuid().ToString("N"));
        }
    }
}

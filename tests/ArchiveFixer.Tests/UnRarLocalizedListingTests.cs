using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Engines.WinRar;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **本地化界面的 UnRAR 列出的清单不许被当成"这个包是空的"**（用户 2026-10-02 真机日志实锤）。
    ///
    /// <para><b>现场</b>：用户机器上装的 WinRAR 自带 <c>UnRAR.exe</c> 是**中文界面**那一份（6.11）。
    /// 它的 <c>lt</c> 把键名也本地化了 —— 表头「压缩文件:」、条目行「名称: / 类型: / 大小: / 旗标: 已加密」。
    /// 解析器按英文键（<c>Name:</c> / <c>Size:</c>）读 ⇒ **0 个条目**，而退出码是 0 ⇒ 老口径落成
    /// "列目录成功、清单 0 个文件 / 0 字节"。真机日志里那一行是：
    /// <c>完整性：可证完整（拿归档清单逐条核对过，文件数与总字节都对得上）（…清单 0 个文件 / 0 字节 …
    /// Expected=0;Actual=5）</c> —— 0 比 5，那句"对得上"是假的，而这一档正是**唯一**允许把源包
    /// 搬进其余物并永久删除的判据；解压前那道"条目名安全预检"也因为这 0 个条目静默失效。</para>
    ///
    /// <para><b>修法</b>：认不出清单就如实报「列不出来」（<c>ParserRejected</c> = 既有口径里"可换引擎"那一档），
    /// `EngineRouter` 照既有规则改问 7-Zip（它的 <c>-slt</c> 键名不随界面语言变）。解压不动 —— UnRAR 的
    /// 退出码与错误分类不受本地化影响。</para>
    ///
    /// <para>⚠ 样本从哪来：中文那一份清单没法凭空造（键名来自 RARLAB 那份本地化二进制），
    /// 所以用例里放的是**真机抓到的那段文本**（路径与条目名已脱敏，AGENTS.md §8）；
    /// 端到端那一条则在本机真装了 WinRAR 时用**已装的那份 UnRAR** 现场跑一遍。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class UnRarLocalizedListingTests : IDisposable
    {
        private readonly string _root;
        private readonly ITestOutputHelper _output;

        public UnRarLocalizedListingTests(ITestOutputHelper output)
        {
            _output = output;
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerUnRarLocalized", Guid.NewGuid().ToString("N"));
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
                // 临时目录清不掉不影响结论。
            }
        }

        /// <summary>
        /// 真机抓到的那段中文清单（路径、条目名已脱敏，逐字保留行首空格与那行英文的 <c>Pack-CRC32</c>）。
        ///
        /// <para>⚠ 最后一行 <c>Pack-CRC32: 81FC1455</c> 是刻意的：它是**唯一**能匹配英文字典的键 ——
        /// "输出里有没有认得出来的键"这种判据会被它骗过去（认得一个就以为整份都认得），
        /// 所以判据必须是"见没见过归档级表头 <c>Archive:</c>"。</para>
        /// </summary>
        private const string ChineseListing = """
UNRAR 6.11 x64 免费软件      版权所有 (c) 1993-2022 Alexander Roshal

压缩文件: C:\samples\pack.part1.rar
: RAR 4, 卷

          名称: pack\payload.bin
          类型: 文件
          大小: 419430400
        打包大小: 419430291
         压缩率: -->
         已修改: 2026-09-29 13:30:11,854112300
          属性: ..A....
  Pack-CRC32: 81FC1455
        压缩平台: Windows
          压缩: RAR 1.5(v29) -m3 -md=4M
          旗标: 已加密 

""";

        /// <summary>同一份包的英文清单（内置 7.23 实测形状，就是解析器认得的那一种）。</summary>
        private const string EnglishListing = """
UNRAR 7.23 x64 freeware      Copyright (c) 1993-2026 Alexander Roshal

Archive: C:\samples\pack.part1.rar
Details: RAR 4, volume

        Name: pack\payload.bin
        Type: File
        Size: 419430400
 Packed size: 419430291
       Ratio: -->
    Modified: 2026-09-29 13:30:11,854112300
  Attributes: ..A....
  Pack-CRC32: 81FC1455
     Host OS: Windows
 Compression: RAR 1.5(v29) -m3 -md=4M
       Flags: encrypted 

""";

        // ────────────────────────── ① 判据本身（真机文本 + 可测出口） ──────────────────────────

        /// <summary>
        /// 红检就红在这一条：把 <c>UnRarEngine.InterpretListing</c> 里那道 <c>HeaderRecognized</c> 闸门撤掉，
        /// 它立刻变回"Success + 0 个条目"⇒ 变红。
        /// </summary>
        [Fact]
        public void 中文界面的清单_必须如实报列不出来_不许报成空包()
        {
            UnRarEngine engine = CreateEngine();

            ArchiveListResult listing = engine.InterpretListing(
                Succeeded(ChineseListing),
                @"C:\samples\pack.part1.rar");

            _output.WriteLine($"Success={listing.Success} ErrorType={listing.ErrorType} FileCount={listing.FileCount} Message={listing.Message}");

            Assert.False(listing.Success, "认不出来的清单绝不许当成「列目录成功」");
            Assert.Equal(EngineErrorTypes.ParserRejected, listing.ErrorType);

            // 可换引擎那一档才有人接手（EngineSelector 的既有口径，一个字没动）。
            Assert.True(EngineSelector.ShouldTryFallback(listing.ErrorType), "ParserRejected 必须落在「可换引擎」那一档");
            Assert.Contains("7-Zip", listing.Message, StringComparison.Ordinal);
        }

        /// <summary>对照：英文清单照旧一条不改地解析出来（别为了修中文把正常的读坏）。</summary>
        [Fact]
        public void 英文界面的清单_照旧解析出条目与加密位()
        {
            UnRarEngine engine = CreateEngine();

            ArchiveListResult listing = engine.InterpretListing(
                Succeeded(EnglishListing),
                @"C:\samples\pack.part1.rar");

            _output.WriteLine($"Success={listing.Success} FileCount={listing.FileCount} Bytes={listing.TotalUncompressedSize} IsEncrypted={listing.IsEncrypted}");

            Assert.True(listing.Success, listing.Message);
            Assert.Equal(1, listing.FileCount);
            Assert.Equal(419430400, listing.TotalUncompressedSize);
            Assert.True(listing.IsEncrypted, "条目行写着 encrypted ⇒ 必须如实报加密");
        }

        /// <summary>
        /// 中文清单被当成空包会连带把"没加密"也报出去 —— 那比"不知道"更糟（下游按不用密码排任务）。
        /// 这条钉的是同一件事的第二个面：认不出来时**一个字都不许猜**。
        /// </summary>
        [Fact]
        public void 中文界面的清单_不许顺手报成没加密()
        {
            UnRarEngine engine = CreateEngine();

            ArchiveListResult listing = engine.InterpretListing(
                Succeeded(ChineseListing),
                @"C:\samples\pack.part1.rar");

            Assert.False(listing.Success);
            Assert.False(listing.IsEncrypted, "列不出来时这一位只能是默认的 false（下游读的是 Success，不是这一位）");
            Assert.Empty(listing.Entries);
        }

        // ────────────────────────── ② 端到端：本机真装了中文版 UnRAR 时，路由器必须换引擎 ──────────────────────────

        /// <summary>
        /// 用**本机已装的那份 UnRAR**现场跑一遍（真机复现）。已装那份是英文界面时这条会走另一支 ——
        /// 那时它自己就该列出条目来；是本地化界面时必须报 <c>ParserRejected</c>，
        /// 而**走路由器**（UnRAR + 7-Zip）无论如何都要拿到真清单：这正是真机上要的结果。
        /// </summary>
        [Fact]
        public async Task 真机_已装的本地化UnRAR列不出清单时_路由器必须回落7Zip拿到真清单()
        {
            string? installed = FindInstalledUnRar();

            if (installed == null)
            {
                _output.WriteLine("本机没装 WinRAR（找不到 UnRAR.exe）⇒ 这一条不适用，跳过");
                return;
            }

            RarSampleSet? samples = RarSampleSet.TryCreate(_root, _output.WriteLine);

            if (samples == null)
            {
                _output.WriteLine("造不出 RAR 样本（既没设 ARCHIVEFIXER_RAR_SAMPLES，本机也没有 Rar.exe）⇒ 跳过");
                return;
            }

            string sample = samples.PlainArchive;

            var installedEngine = new UnRarEngine(new UnRarProcessRunner(new ToolLocator
            {
                CustomUnRarExePath = installed
            }));

            ArchiveListResult byInstalled = await installedEngine.ListAsync(
                ArchiveRequest.For(sample),
                CancellationToken.None);

            _output.WriteLine(
                $"已装 UnRAR（{installed}）：Success={byInstalled.Success} ErrorType={byInstalled.ErrorType} "
                + $"FileCount={byInstalled.FileCount} Message={byInstalled.Message}");

            if (byInstalled.Success)
            {
                Assert.True(
                    byInstalled.FileCount > 0,
                    "列目录成功时清单不许是空的 —— 本地化输出必须报 ParserRejected，让路由器换引擎");
            }
            else
            {
                Assert.Equal(EngineErrorTypes.ParserRejected, byInstalled.ErrorType);
                Assert.True(EngineSelector.ShouldTryFallback(byInstalled.ErrorType));
            }

            // 真实用途是这一条：走路由器（RAR → UnRAR → 7-Zip）必须拿到非空清单。
            var registry = new EngineRegistry();
            registry.Register(new UnRarEngine(new UnRarProcessRunner(new ToolLocator
            {
                CustomUnRarExePath = installed
            })));
            registry.Register(new SevenZipEngine());

            var router = new EngineRouter(registry);

            ArchiveListResult routed = await router.ListAsync(ArchiveRequest.For(sample), CancellationToken.None);

            _output.WriteLine($"走路由器：Success={routed.Success} 引擎={routed.EngineId} FileCount={routed.FileCount} Message={routed.Message}");

            Assert.True(routed.Success, routed.Message);
            Assert.True(routed.FileCount > 0, "路由器必须回落到 7-Zip 拿到真清单");
        }

        // ────────────────────────── 装配 ──────────────────────────

        private static UnRarEngine CreateEngine()
        {
            return new UnRarEngine(new UnRarProcessRunner(new ToolLocator
            {
                CustomUnRarExePath = Path.Combine(AppContext.BaseDirectory, "tools", "unrar", "UnRAR.exe")
            }));
        }

        private static ArchiveOperationResult Succeeded(string stdout)
        {
            return new ArchiveOperationResult
            {
                Success = true,
                ExitCode = 0,
                StandardOutput = stdout,
                Status = ArchiveFixer.Models.StatusText.Success,
                Message = "操作成功",
                DetectedErrorType = "None"
            };
        }

        /// <summary>本机已装的 UnRAR（只读；找不到返回 null —— 那种情况下这一条不适用）。</summary>
        private static string? FindInstalledUnRar()
        {
            var candidates = new List<string>
            {
                @"C:\Program Files\WinRAR\UnRAR.exe",
                @"C:\Program Files (x86)\WinRAR\UnRAR.exe"
            };

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }
    }
}

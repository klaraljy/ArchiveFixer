using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Engines.WinRar;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **真样本对照**：第二引擎（RARLAB UnRAR）与 7-Zip 在同一批真 RAR 上的行为差异。
    ///
    /// <para>
    /// 这一组用例就是验收清单的五条（见交付报告 `fix-unrar.md`）：
    /// ① 加密文件名（<c>-hp</c>）的 RAR：UnRAR 能列出/解出，且无密码时**如实**报"文件名已加密"；
    /// ② 普通 RAR：两个引擎都能解时，按优先级**走 UnRAR**；
    /// ③ zip / 7z：仍然走 7-Zip（UnRAR 不参与）；
    /// ④ 模拟"这台机器上没有 UnRAR"：**自动回落 7-Zip，包照样打开**；
    /// ⑤ 多卷 RAR：整组处理正确（内容哈希与源一致），缺卷时报出缺的是哪一卷。
    /// </para>
    ///
    /// <para>
    /// <b>样本从哪来</b>：RAR 是**专利格式**，7-Zip 只能解不能建，而本仓库**绝不内置**建包工具
    /// （`Rar.exe` 是共享软件，许可禁止分发；许可第 4 条也禁止拿 UnRAR 去重建 RAR 压缩算法）。
    /// 所以样本有两条来源，按顺序尝试：
    /// <list type="number">
    /// <item><description>环境变量 <c>ARCHIVEFIXER_RAR_SAMPLES</c> 指向的目录（离线准备好的样本，验收时用这一条）；</description></item>
    /// <item><description>本机**已装**的 WinRAR 目录里的 <c>Rar.exe</c>（只读取、只在本机临时目录里造样本，绝不复制进仓库）。</description></item>
    /// </list>
    /// 两条都没有时用例**跳过**（打印一行原因后正常返回）—— 这是刻意的：把 RAR 样本提交进仓库
    /// 既违反隐私/体积口径（AGENTS.md §8），也会把"我们偷偷带了建包工具"变成既成事实。
    /// CI 上没有 WinRAR 时，这一组会全绿跳过，其余单元用例照旧覆盖参数、解析与优先级。
    /// </para>
    /// </summary>
    public class UnRarRealSampleTests : IDisposable
    {
        /// <summary>测试用密码。**占位符**，不是任何真实密码（AGENTS.md §8）。</summary>
        private const string SamplePassword = "<sample-password>";

        private readonly string _root;
        private readonly ITestOutputHelper _output;
        private readonly RarSampleSet? _samples;

        public UnRarRealSampleTests(ITestOutputHelper output)
        {
            _output = output;
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerUnRar", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);

            _samples = RarSampleSet.TryCreate(_root, _output.WriteLine);
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
                // 删不掉不影响结论，下次跑换一个 GUID 目录。
            }
        }

        // ────────────────────────── ① 加密文件名（-hp） ──────────────────────────

        [Fact]
        public async Task 加密文件名的RAR_无密码时如实报文件名已加密_给对密码能列出并解出()
        {
            if (_samples == null)
            {
                Skip("没有 RAR 样本（既没设 ARCHIVEFIXER_RAR_SAMPLES，本机也没装 WinRAR）");
                return;
            }

            Assert.True(_samples.HasEncryptedHeadersArchive, "样本里没有 -hp 的 RAR");

            var engine = new UnRarEngine(new UnRarProcessRunner(new ToolLocator
            {
                CustomUnRarExePath = _samples.BundledUnRarPath
            }));

            Assert.True(engine.IsAvailable, "内置 UnRAR 应当可用");

            // ① 无密码：必须落成"文件名已加密"，不是"密码错误"、更不是"文件损坏"。
            ArchiveListResult withoutPassword = await engine.ListAsync(
                ArchiveRequest.For(_samples.EncryptedHeadersArchive!),
                CancellationToken.None);

            _output.WriteLine($"无密码 list：Success={withoutPassword.Success} ErrorType={withoutPassword.ErrorType} Message={withoutPassword.Message}");

            Assert.False(withoutPassword.Success);
            Assert.Equal(UnRarOutputParser.EncryptedHeadersErrorType, withoutPassword.ErrorType);
            Assert.Equal(StatusText.EncryptedHeaders, UnRarOutputParser.ErrorTypeToTaskStatus(withoutPassword.ErrorType));

            // ② 给对密码：能列出条目（这是"包本身没坏，只是要密码"的证据）。
            ArchiveRequest withPassword = new()
            {
                ArchivePath = _samples.EncryptedHeadersArchive!,
                Password = SamplePassword
            };

            ArchiveListResult listing = await engine.ListAsync(withPassword, CancellationToken.None);

            _output.WriteLine($"带密码 list：Success={listing.Success} 条目={listing.Entries.Count} IsEncrypted={listing.IsEncrypted} Message={listing.Message}");

            Assert.True(listing.Success, listing.Message);
            Assert.True(listing.Entries.Count > 0, "给对密码应当能列出条目");
            Assert.True(listing.IsEncrypted, "加密头的包 IsEncrypted 必须为真");

            // ③ 真解出来，并逐字节核对内容（不是"退出码 0"就算数）。
            string output = Path.Combine(_root, "out_hp");
            ArchiveOperationResult extract = await engine.ExtractAsync(
                new ArchiveRequest
                {
                    ArchivePath = _samples.EncryptedHeadersArchive!,
                    OutputPath = output,
                    Password = SamplePassword
                },
                new ExtractOptions(),
                CancellationToken.None);

            _output.WriteLine($"带密码 extract：Success={extract.Success} Status={extract.Status} Engine={extract.EngineId} {extract.EngineVersion}");

            Assert.True(extract.Success, extract.Message);
            Assert.Equal(EngineIds.WinRar, extract.EngineId);
            _samples.AssertContentMatches(output);

            /*
             * ⚠ 诚实记录一条**与预期相反**的实测结论（原始输出见报告与 _tmp 探针日志）：
             * 7-Zip 26.01 **能**用正确密码解开 RAR5 的 -hp（`Everything is Ok`，退出码 0），
             * 也能解开 RAR4 的 -hp 与 -hp 分卷。所以"7-Zip 解不开 -hp"这个前提在 26.01 上不成立，
             * 第二引擎的价值落在"分类与报错更准（缺卷点名、加密头显式标注）+ 将来 RAR 7.x 新格式"上。
             * 这条不写成断言 —— 断言"7z 失败"会在 7-Zip 升级后变成假失败，
             * 而断言"7z 成功"又不是我们能保证的事（换台机器可能装的是旧版 7-Zip）。
             */
        }

        // ────────────────────────── ② 普通 RAR：按优先级走 UnRAR ──────────────────────────

        [Fact]
        public async Task 普通RAR_两个引擎都能解时按优先级走UnRAR()
        {
            if (_samples == null)
            {
                Skip("没有 RAR 样本");
                return;
            }

            var tools = new ToolLocator { CustomUnRarExePath = _samples.BundledUnRarPath };
            EngineRegistry registry = EngineRegistry.CreateDefault(tools);

            // 默认优先级：WinRar → SevenZip（用户 2026-09-22 指示）。
            var selector = new EngineSelector(registry, EngineIds.DefaultPriority);

            IArchiveEngine? selected = selector.SelectFor("RAR5", EngineOperation.Extract);

            Assert.NotNull(selected);
            Assert.Equal(EngineIds.WinRar, selected!.Id);

            // 两个引擎**都能干**这个格式（能力筛不排除任何一个），差别只在优先级。
            EngineSelection explained = selector.Explain("RAR5", EngineOperation.Extract);

            _output.WriteLine($"RAR5 候选：{string.Join("、", explained.Candidates.Select(e => e.Id))}；选中 {explained.Describe()}");

            Assert.Contains(explained.Candidates, e => e.Id == EngineIds.SevenZip);
            Assert.Contains(explained.Candidates, e => e.Id == EngineIds.WinRar);

            // 备用引擎里必须是 7-Zip（回退链是双向可用的，不是"只认第一个"）。
            IReadOnlyList<IArchiveEngine> fallbacks = selector.SelectFallbacks("RAR5", EngineOperation.Extract, EngineIds.WinRar);

            Assert.Contains(fallbacks, e => e.Id == EngineIds.SevenZip);

            // 真解一遍：两个引擎的产物内容必须一致（优先级只是"先用谁"，不是"只有谁能"）。
            string viaUnRar = Path.Combine(_root, "out_plain_unrar");
            string viaSevenZip = Path.Combine(_root, "out_plain_7z");

            ArchiveRequest request = ArchiveRequest.For(_samples.PlainArchive);

            ArchiveOperationResult unrarResult = await registry.FindById(EngineIds.WinRar)!.ExtractAsync(
                new ArchiveRequest { ArchivePath = request.ArchivePath, OutputPath = viaUnRar, Password = null },
                new ExtractOptions(),
                CancellationToken.None);

            ArchiveOperationResult sevenZipResult = await registry.FindById(EngineIds.SevenZip)!.ExtractAsync(
                new ArchiveRequest { ArchivePath = request.ArchivePath, OutputPath = viaSevenZip, Password = null },
                new ExtractOptions(),
                CancellationToken.None);

            _output.WriteLine($"UnRAR：Success={unrarResult.Success} Status={unrarResult.Status}");
            _output.WriteLine($"7-Zip：Success={sevenZipResult.Success} Status={sevenZipResult.Status}");

            Assert.True(unrarResult.Success, unrarResult.Message);
            Assert.True(sevenZipResult.Success, sevenZipResult.Message);

            _samples.AssertContentMatches(viaUnRar);
            Assert.Equal(ContentFingerprint(viaUnRar), ContentFingerprint(viaSevenZip));

            // 结果可追溯（不变量 14）：正常路径的结果里必须带引擎名 + 版本。
            Assert.Equal(EngineIds.WinRar, unrarResult.EngineId);
            Assert.False(string.IsNullOrWhiteSpace(unrarResult.EngineVersion));
            Assert.Equal(EngineIds.SevenZip, sevenZipResult.EngineId);
            Assert.Contains("UnRAR", unrarResult.ToEngineIdentity().Describe(), StringComparison.OrdinalIgnoreCase);
        }

        // ────────────────────────── ③ zip / 7z 仍走 7-Zip ──────────────────────────

        [Fact]
        public async Task zip与7z_仍然走7zip_UnRAR不参与()
        {
            if (_samples == null)
            {
                Skip("没有 RAR 样本");
                return;
            }

            var tools = new ToolLocator { CustomUnRarExePath = _samples.BundledUnRarPath };
            var registry = EngineRegistry.CreateDefault(tools);
            var selector = new EngineSelector(registry, EngineIds.DefaultPriority);

            foreach (string format in new[] { "ZIP", "7Z", "TAR", "GZIP" })
            {
                IArchiveEngine? selected = selector.SelectFor(format, EngineOperation.Extract);

                _output.WriteLine($"{format} → {selected?.Id ?? "（无）"}");

                Assert.NotNull(selected);
                Assert.Equal(EngineIds.SevenZip, selected!.Id);
            }

            // 能力位如实：UnRAR 侧**没有**登记任何非 RAR 格式（不许声称能处理 zip）。
            IArchiveEngine unrar = registry.FindById(EngineIds.WinRar)!;

            Assert.Null(unrar.Capabilities.ForFormat("ZIP"));
            Assert.Null(unrar.Capabilities.ForFormat("7Z"));
            Assert.NotNull(unrar.Capabilities.ForFormat("RAR5"));
            Assert.NotNull(unrar.Capabilities.ForFormat("RAR4"));
            Assert.NotNull(unrar.Capabilities.ForFormat("RAR"));

            // 反向确认：真的让 UnRAR 去开一个 zip，它必须**失败**（不是"其实也能干"）。
            string zip = Path.Combine(_root, "sample.zip");
            RarSampleSet.RunSevenZip(
                Path.Combine(AppContext.BaseDirectory, "tools", "7zip", "7z.exe"),
                _root,
                "a",
                "-tzip",
                zip,
                _samples.ReadmePath);

            ArchiveOperationResult notRar = await unrar.ExtractAsync(
                new ArchiveRequest { ArchivePath = zip, OutputPath = Path.Combine(_root, "out_zip_by_unrar") },
                new ExtractOptions(),
                CancellationToken.None);

            _output.WriteLine($"UnRAR 开 zip：Success={notRar.Success} ErrorType={notRar.DetectedErrorType} Message={notRar.Message}");

            Assert.False(notRar.Success);
        }

        // ────────────────────────── ④ 没装 UnRAR → 自动回落 7-Zip ──────────────────────────

        [Fact]
        public async Task 没有UnRAR时自动回落7zip_包照样打开()
        {
            if (_samples == null)
            {
                Skip("没有 RAR 样本");
                return;
            }

            /*
             * 模拟"这台机器上没有 UnRAR"：
             * ① 自选路径指向一个**不存在**的文件；
             * ② 关掉"已装 WinRAR 目录"这一档（本机真的有 WinRAR，不关就测不出回落）；
             * ③ 关掉内置那一档。
             * 三档都不成立之后，UnRarEngine.IsAvailable 必须是 false —— 这才是真"没装"。
             */
            var tools = new ToolLocator
            {
                CustomUnRarExePath = Path.Combine(_root, "not-exists", "UnRAR.exe"),
                UseWinRarInstallation = false,
                UseBundledUnRar = false
            };

            EngineRegistry registry = EngineRegistry.CreateDefault(tools);
            var selector = new EngineSelector(registry, EngineIds.DefaultPriority);

            IArchiveEngine unrar = registry.FindById(EngineIds.WinRar)!;

            _output.WriteLine($"UnRAR 可用={unrar.IsAvailable} 解析结果：{tools.DescribeUnRarResolution()}");

            Assert.False(unrar.IsAvailable);

            // 关键：**不许因为"排第一但没装"就打不开包** —— 必须落到 7-Zip 并且真能解。
            IArchiveEngine? selected = selector.SelectFor("RAR5", EngineOperation.Extract);

            Assert.NotNull(selected);
            Assert.Equal(EngineIds.SevenZip, selected!.Id);

            EngineSelection explained = selector.Explain("RAR5", EngineOperation.Extract);

            Assert.Contains(explained.SkippedUnavailable, e => e.Id == EngineIds.WinRar);

            string output = Path.Combine(_root, "out_fallback");

            ArchiveOperationResult result = await selected.ExtractAsync(
                new ArchiveRequest { ArchivePath = _samples.PlainArchive, OutputPath = output, Password = null },
                new ExtractOptions(),
                CancellationToken.None);

            _output.WriteLine($"回落解压：Success={result.Success} Engine={result.EngineId} Status={result.Status}");

            Assert.True(result.Success, result.Message);
            Assert.Equal(EngineIds.SevenZip, result.EngineId);
            _samples.AssertContentMatches(output);
        }

        // ────────────────────────── ⑤ 多卷 RAR ──────────────────────────

        [Fact]
        public async Task 多卷RAR_整组处理正确_缺卷时报出缺哪一卷()
        {
            if (_samples == null)
            {
                Skip("没有 RAR 样本");
                return;
            }

            Assert.True(_samples.HasVolumeSet, "样本里没有分卷 RAR");

            var engine = new UnRarEngine(new UnRarProcessRunner(new ToolLocator
            {
                CustomUnRarExePath = _samples.BundledUnRarPath
            }));

            // 从第一卷启动：列表要认出这是分卷，条目要齐。
            ArchiveListResult listing = await engine.ListAsync(
                ArchiveRequest.For(_samples.FirstVolumePath!),
                CancellationToken.None);

            _output.WriteLine($"分卷 list：Success={listing.Success} IsMultiVolume={listing.IsMultiVolume} 条目={listing.Entries.Count} 总大小={listing.TotalUncompressedSize}");

            Assert.True(listing.Success, listing.Message);
            Assert.True(listing.IsMultiVolume, "应当认出这是分卷压缩包");

            string output = Path.Combine(_root, "out_vol");

            ArchiveOperationResult extract = await engine.ExtractAsync(
                new ArchiveRequest
                {
                    ArchivePath = _samples.FirstVolumePath!,
                    OutputPath = output,
                    Password = null
                },
                new ExtractOptions(),
                CancellationToken.None);

            _output.WriteLine($"分卷 extract：Success={extract.Success} Status={extract.Status} Message={extract.Message}");

            Assert.True(extract.Success, extract.Message);

            // 整组处理正确 = 解出来的大文件与源文件逐字节一致（分卷最容易"少一截还报成功"）。
            _samples.AssertPayloadMatches(output);

            /*
             * 缺卷：把中间那一卷挪走，再从第一卷解。
             * 必须报"分卷缺失"并**点名缺的是哪一个**（不变量 7）——
             * 这正是 UnRAR 比 7-Zip 强的地方：它自己会写 "Cannot find volume …"。
             */
            string hidden = _samples.HideMiddleVolume();

            try
            {
                ArchiveOperationResult missing = await engine.ExtractAsync(
                    new ArchiveRequest
                    {
                        ArchivePath = _samples.FirstVolumePath!,
                        OutputPath = Path.Combine(_root, "out_vol_missing"),
                        Password = null
                    },
                    new ExtractOptions(),
                    CancellationToken.None);

                _output.WriteLine($"缺卷 extract：Success={missing.Success} ErrorType={missing.DetectedErrorType} Status={missing.Status} Message={missing.Message}");

                Assert.False(missing.Success);
                Assert.Equal(EngineErrorTypes.VolumeMissing, missing.DetectedErrorType);
                Assert.Equal(StatusText.VolumeMissing, missing.Status);
                Assert.Contains(Path.GetFileName(hidden), missing.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                _samples.RestoreHiddenVolume();
            }
        }

        // ────────────────────────── ⑤b 损坏包：-kb 只决定半成品留不留 ──────────────────────────

        [Fact]
        public async Task 损坏的RAR_保留受损文件绝不把失败变成成功()
        {
            if (_samples == null)
            {
                Skip("没有 RAR 样本");
                return;
            }

            Assert.True(_samples.HasBrokenArchive, "样本里没有损坏的 RAR");

            var engine = new UnRarEngine(new UnRarProcessRunner(new ToolLocator
            {
                CustomUnRarExePath = _samples.BundledUnRarPath
            }));

            ArchiveOperationResult withoutKeep = await engine.ExtractAsync(
                new ArchiveRequest
                {
                    ArchivePath = _samples.BrokenArchive!,
                    OutputPath = Path.Combine(_root, "out_broken_no_kb"),
                    Password = null
                },
                new ExtractOptions(),
                CancellationToken.None);

            EngineRuntimeSettings.SetKeepBrokenFiles(true);

            try
            {
                ArchiveOperationResult withKeep = await engine.ExtractAsync(
                    new ArchiveRequest
                    {
                        ArchivePath = _samples.BrokenArchive!,
                        OutputPath = Path.Combine(_root, "out_broken_kb"),
                        Password = null
                    },
                    new ExtractOptions(),
                    CancellationToken.None);

                _output.WriteLine($"不保留：Success={withoutKeep.Success} Status={withoutKeep.Status} ErrorType={withoutKeep.DetectedErrorType}");
                _output.WriteLine($"保留：Success={withKeep.Success} Status={withKeep.Status} ErrorType={withKeep.DetectedErrorType}");

                // 不变量 6：两种情况下都**不是成功**，状态必须是失败 / 部分完成。
                Assert.False(withoutKeep.Success);
                Assert.False(withKeep.Success);
                Assert.NotEqual(StatusText.ExtractSuccess, withKeep.Status);
                Assert.NotEqual(StatusText.Success, withKeep.Status);
                Assert.Contains(
                    withKeep.Status,
                    new[] { StatusText.Corrupted, StatusText.PartiallyCompleted, StatusText.ExtractFailed, StatusText.TestFailed });
            }
            finally
            {
                EngineRuntimeSettings.SetKeepBrokenFiles(false);
            }
        }

        // ────────────────────────── 辅助 ──────────────────────────

        private void Skip(string reason)
        {
            _output.WriteLine("跳过：" + reason);
        }

        /// <summary>产物的"内容指纹"：相对路径 + 长度 + 内容的 SHA256（用来比对两个引擎解出来是否一致）。</summary>
        private static string ContentFingerprint(string directory)
        {
            var lines = new List<string>();

            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                         .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                string relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
                byte[] hash = SHA256.HashData(File.ReadAllBytes(file));
                lines.Add($"{relative}|{new FileInfo(file).Length}|{Convert.ToHexString(hash)}");
            }

            return string.Join('\n', lines);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 加密的**内嵌 ZIP 直读**（2026-09-25 第 29 条）：用户真机报"密码本里第一个密码就是这个包的密码，
    /// 却一直报密码错误，我那个密码是中文的就没有办法了吗"。
    ///
    /// <para><b>根因（本机取证）</b>：ZIP 的 AES 加密只认<b>字节</b>，不认字符串。同一个中文密码，
    /// 打包方按 UTF-8 字节写进去、7-Zip 按 ANSI(936) 字节派生密钥 → 永远对不上。实测三条证据：</para>
    /// <list type="number">
    /// <item><description>WinRAR 造的 AES-256 包（管理员权限无关）：<c>中文密码ABC</c> 按 GBK 字节派生的校验值
    /// <c>0x0B81</c> 与包里存的一致，按 UTF-8 派生的是 <c>0x391E</c> —— WinRAR 用 ANSI 字节；</description></item>
    /// <item><description>把 WinRAR 的包手工置上/清掉通用位标志 bit11（"Encrypt UTF8"），7-Zip 的结论**一个字不变** ——
    /// 它解码时根本不看这一位；</description></item>
    /// <item><description>用户的包（百度网盘分享打包）恰好相反：归档里声明着 <c>Encrypt UTF8</c>，
    /// 密码按 UTF-8 字节派生 —— 于是 7-Zip 拿着正确的中文密码也只能报密码错误。</description></item>
    /// </list>
    ///
    /// <para><b>修法</b>：内嵌归档直读自己做 AES 解密（<see cref="ZipAesCrypto"/>），
    /// 候选密码逐个 × **UTF-8 / ANSI 两种字节**各试一遍；校验值（2 字节）筛，认证码（HMAC，10 字节）确认。
    /// 一个都不通过就是**硬失败**（不再回落抠取 —— 那一份等大副本对 AES 包毫无意义，用户明说过
    /// "我不希望有这么多的失败残留"）。</para>
    ///
    /// <para>样本全部由 <see cref="ZipAesTestWriter"/> 现造（可以指定密码字节编码，这是现成工具做不到的），
    /// 并用**真 7z 反向校验**器材本身：按 ANSI 字节造的包 7z 必须能解开，按 UTF-8 造的必须解不开
    /// （后者正是本功能存在的理由；哪天 7-Zip 支持了，那一条会红，届时可以安全删掉。）。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class ZipAesDirectReadTests : IDisposable
    {
        /// <summary>中文密码：本组所有用例的主角（用户现场就是中文密码）。</summary>
        private const string ChinesePassword = "中文密码ABC";

        private const string WrongPassword = "错的密码XYZ";

        /// <summary>假视频头长度：与真实现场同量级。</summary>
        private const int FakeVideoPrefixLength = 32768;

        /// <summary>EOCD 之后那段正常数据的长度。</summary>
        private const int TailAfterEocdLength = 15000;

        /// <summary>包里那条内容物（中文名，顺便钉住"加密条目名照常按 UTF-8 解"）。</summary>
        private const string EntryName = "内容物/说明.txt";

        private const string PayloadText = "加密内嵌包里的最终数据 2026-09-25\n第二行\n";

        private readonly string _root;

        public ZipAesDirectReadTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerZipAes", Guid.NewGuid().ToString("N"));
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

        // ================================================================ ① 用户的现场：UTF-8 打包的中文密码

        /// <summary>
        /// **用户现场那一档**：密码按 UTF-8 字节打包（百度网盘分享包就是这么干的，归档里还写着
        /// <c>Encrypt UTF8</c>）＋ 中文密码 → 内置读取器必须解开，而且**不产生抠取副本**。
        /// </summary>
        [Fact]
        public void UTF8字节打包的中文密码_直读解得开()
        {
            byte[] payload = Encoding.UTF8.GetBytes(PayloadText);
            byte[] zip = ZipAesTestWriter.Build(
                EntryName,
                payload,
                ChinesePassword,
                ZipAesPasswordEncoding.Utf8,
                utf8NameFlag: true);

            string polyglot = Path.Combine(_root, "utf8.mp4");
            long archiveEnd = ZipAesTestWriter.BuildPolyglot(polyglot, zip, FakeVideoPrefixLength, TailAfterEocdLength);
            string stage = Path.Combine(_root, "work-utf8", "stage");

            EmbeddedZipProbeResult probe = EmbeddedZipStreamExtractor.Probe(
                polyglot,
                FakeVideoPrefixLength,
                archiveEnd,
                stage,
                new[] { ChinesePassword });

            Assert.True(probe.Supported, probe.Reason);
            Assert.Equal(1, probe.ResolvedPasswordIndex);
            Assert.Equal(ZipAesPasswordEncoding.Utf8, probe.ResolvedPasswordEncoding);
            Assert.True(probe.List!.IsEncrypted, "加密包必须如实标成加密（报告里要写密码状态）");

            EmbeddedZipExtractResult result = EmbeddedZipStreamExtractor.Extract(
                polyglot,
                FakeVideoPrefixLength,
                archiveEnd,
                stage,
                progress: null,
                cancellationToken: CancellationToken.None,
                budgetOptions: null,
                passwords: new[] { ChinesePassword });

            Assert.True(result.Success, result.Message);
            Assert.True(result.WasEncrypted, "这一单是加密包，结论里必须写明");
            Assert.Equal(payload, File.ReadAllBytes(Path.Combine(stage, "内容物", "说明.txt")));

            // 直读那条路：工作区里不许出现抠取副本（这是"省掉 980 MB 级副本"的直接证据）。
            Assert.Empty(Directory.GetFiles(Path.Combine(_root, "work-utf8"), "*.zip", SearchOption.AllDirectories));
        }

        /// <summary>
        /// **与真 7z 的对照**：同一个按 UTF-8 字节打包的中文密码包，7-Zip 拿着**正确**的密码也解不开
        /// （它只按 ANSI 字节派生）—— 这就是用户"密码明明是对的却一直报密码错误"的机制，
        /// 也是本功能不能靠"回落给 7z"来解决的原因。
        /// </summary>
        [Fact]
        public void UTF8字节打包的中文密码_真七z拿着正确密码也解不开()
        {
            byte[] zip = ZipAesTestWriter.Build(
                "payload.txt",
                Encoding.UTF8.GetBytes(PayloadText),
                ChinesePassword,
                ZipAesPasswordEncoding.Utf8,
                utf8NameFlag: true);

            Assert.False(
                RunSevenZipExtract(zip, ChinesePassword, "sevenzip-utf8"),
                "7-Zip 竟然解开了按 UTF-8 字节派生密码的 AES 包 —— 它要是真支持了，这条断言可以删掉" +
                "（我们的读取器照样能用），但结论必须重新取证，不能默认它还是「只按 ANSI」");
        }

        /// <summary>
        /// **反向校验器材**：按 ANSI(936) 字节打包的中文密码包（WinRAR 的约定），真 7z 必须能解开 ——
        /// 说明本组用的写入器写出来的确实是**规范**的 AES ZIP，而不是自成一派。
        /// 同一个包，内置读取器也要能解开（双编码那一条）。
        /// </summary>
        [Fact]
        public void ANSI字节打包的中文密码_真七z与内置读取器都能解开()
        {
            byte[] payload = Encoding.UTF8.GetBytes(PayloadText);
            byte[] zip = ZipAesTestWriter.Build(
                EntryName,
                payload,
                ChinesePassword,
                ZipAesPasswordEncoding.AnsiCodePage,
                utf8NameFlag: false);

            Assert.True(
                RunSevenZipExtract(zip, ChinesePassword, "sevenzip-ansi"),
                "真 7z 解不开按 ANSI 字节打包的 AES 包 —— 要么写入器不合规范，要么 7z 变了；两种都必须查");

            string polyglot = Path.Combine(_root, "ansi.mp4");
            long archiveEnd = ZipAesTestWriter.BuildPolyglot(polyglot, zip, FakeVideoPrefixLength, TailAfterEocdLength);
            string stage = Path.Combine(_root, "work-ansi", "stage");

            EmbeddedZipProbeResult probe = EmbeddedZipStreamExtractor.Probe(
                polyglot,
                FakeVideoPrefixLength,
                archiveEnd,
                stage,
                new[] { WrongPassword, ChinesePassword });

            Assert.True(probe.Supported, probe.Reason);
            Assert.Equal(2, probe.ResolvedPasswordIndex);
            Assert.Equal(ZipAesPasswordEncoding.AnsiCodePage, probe.ResolvedPasswordEncoding);

            EmbeddedZipExtractResult result = EmbeddedZipStreamExtractor.Extract(
                polyglot,
                FakeVideoPrefixLength,
                archiveEnd,
                stage,
                progress: null,
                cancellationToken: CancellationToken.None,
                budgetOptions: null,
                passwords: new[] { WrongPassword, ChinesePassword });

            Assert.True(result.Success, result.Message);
            Assert.Equal(payload, File.ReadAllBytes(Path.Combine(stage, "内容物", "说明.txt")));
        }

        // ================================================================ ② 失败世界

        /// <summary>
        /// 密码一个都不对：必须是 <see cref="EmbeddedZipProbeResult.PasswordRejected"/>（**硬失败、不回落**），
        /// 而且**一个字节都不许写** —— 回落抠取只会白拷一份等大的副本再报同样的错。
        /// </summary>
        [Fact]
        public void 密码都不对_判硬失败且不产生任何产物()
        {
            byte[] zip = ZipAesTestWriter.Build(
                EntryName,
                Encoding.UTF8.GetBytes(PayloadText),
                ChinesePassword,
                ZipAesPasswordEncoding.Utf8,
                utf8NameFlag: true);

            string polyglot = Path.Combine(_root, "wrong.mp4");
            long archiveEnd = ZipAesTestWriter.BuildPolyglot(polyglot, zip, FakeVideoPrefixLength, TailAfterEocdLength);
            string stage = Path.Combine(_root, "work-wrong", "stage");

            EmbeddedZipProbeResult probe = EmbeddedZipStreamExtractor.Probe(
                polyglot,
                FakeVideoPrefixLength,
                archiveEnd,
                stage,
                new[] { WrongPassword });

            Assert.False(probe.Supported);
            Assert.True(probe.PasswordRejected, "密码不对必须是硬失败，不能变成'回落抠取'这个信号");
            Assert.False(probe.PathRejected);
            Assert.Contains("UTF-8", probe.Message, StringComparison.Ordinal);
            Assert.Contains("ANSI", probe.Message, StringComparison.Ordinal);

            EmbeddedZipExtractResult result = EmbeddedZipStreamExtractor.Extract(
                polyglot,
                FakeVideoPrefixLength,
                archiveEnd,
                stage,
                progress: null,
                cancellationToken: CancellationToken.None,
                budgetOptions: null,
                passwords: new[] { WrongPassword });

            Assert.False(result.Success);
            Assert.False(result.Unsupported);
            Assert.Empty(Directory.Exists(stage) ? Directory.GetFiles(stage, "*", SearchOption.AllDirectories) : Array.Empty<string>());
            Assert.Empty(Directory.GetFiles(Path.Combine(_root, "work-wrong"), "*", SearchOption.AllDirectories));
        }

        /// <summary>
        /// **密文被改过**：认证码必须发现（AES 的全部意义就在这儿）。只看校验值是不够的 ——
        /// 校验值只与"盐 + 密码"有关，改密文它一点反应都没有。
        /// </summary>
        [Fact]
        public void 密文被改一个字节_认证码必须发现且不留产物()
        {
            byte[] zip = ZipAesTestWriter.Build(
                EntryName,
                Encoding.UTF8.GetBytes(PayloadText),
                ChinesePassword,
                ZipAesPasswordEncoding.Utf8,
                utf8NameFlag: true);

            // 本地头 30 + 名字长 + 扩展长（4+7）→ 再跳过盐 16 与校验值 2 → 密文中间。
            int nameLength = BitConverter.ToUInt16(zip, 26);
            int extraLength = BitConverter.ToUInt16(zip, 28);
            int cipherStart = 30 + nameLength + extraLength + 16 + 2;
            int cipherLength = (int)BitConverter.ToUInt32(zip, 18) - 16 - 2 - 10;

            Assert.True(cipherLength > 2, "样本的密文太短，这条测试没意义");
            zip[cipherStart + (cipherLength / 2)] ^= 0xFF;

            string polyglot = Path.Combine(_root, "tampered.mp4");
            long archiveEnd = ZipAesTestWriter.BuildPolyglot(polyglot, zip, FakeVideoPrefixLength, TailAfterEocdLength);
            string stage = Path.Combine(_root, "work-tampered", "stage");

            EmbeddedZipProbeResult probe = EmbeddedZipStreamExtractor.Probe(
                polyglot,
                FakeVideoPrefixLength,
                archiveEnd,
                stage,
                new[] { ChinesePassword });

            Assert.False(probe.Supported);
            Assert.True(probe.PasswordRejected);
            Assert.Contains("认证码", probe.Message, StringComparison.Ordinal);

            EmbeddedZipExtractResult result = EmbeddedZipStreamExtractor.Extract(
                polyglot,
                FakeVideoPrefixLength,
                archiveEnd,
                stage,
                progress: null,
                cancellationToken: CancellationToken.None,
                budgetOptions: null,
                passwords: new[] { ChinesePassword });

            Assert.False(result.Success);
            Assert.Empty(Directory.Exists(stage) ? Directory.GetFiles(stage, "*", SearchOption.AllDirectories) : Array.Empty<string>());
        }

        /// <summary>
        /// 加密包但**一个候选密码都没有**（识别阶段的探查就是这种形态）→ 仍然是"不支持"这个**回落信号**，
        /// 不能升级成硬失败：识别阶段不该替用户猜密码，也不该在识别时就把任务判死。
        /// </summary>
        [Fact]
        public void 没给候选密码_加密包仍然是回落信号()
        {
            byte[] zip = ZipAesTestWriter.Build(
                EntryName,
                Encoding.UTF8.GetBytes(PayloadText),
                ChinesePassword,
                ZipAesPasswordEncoding.Utf8,
                utf8NameFlag: true);

            string polyglot = Path.Combine(_root, "nopw.mp4");
            long archiveEnd = ZipAesTestWriter.BuildPolyglot(polyglot, zip, FakeVideoPrefixLength, TailAfterEocdLength);

            EmbeddedZipProbeResult probe = EmbeddedZipStreamExtractor.Probe(
                polyglot,
                FakeVideoPrefixLength,
                archiveEnd,
                Path.Combine(_root, "work-nopw", "stage"));

            Assert.False(probe.Supported);
            Assert.False(probe.PasswordRejected);
            Assert.False(probe.PathRejected);
            Assert.Contains("需要密码", probe.Reason, StringComparison.Ordinal);

            /*
             * 但必须带上"只差一个密码"这个标记：识别阶段靠它把空间账面记成"不需要那份等大的临时副本"
             * （否则一个 40 GB 的加密双面文件会被一笔根本不会发生的抠取副本卡在空间门上）。
             */
            Assert.True(probe.RequiresPassword, "加密 AES 内嵌包必须标成'只差一个密码'");
        }

        // ================================================================ ③ 不回归

        /// <summary>
        /// 不加密的内嵌包：即使把候选密码表传进去，行为也必须与以前逐字相同（能直读、内容正确）。
        /// 这一条挡的是"加了加密支持之后，普通包被误判成加密包"。
        /// </summary>
        [Fact]
        public void 不加密的内嵌包_带密码表也照常直读()
        {
            byte[] payload = Encoding.UTF8.GetBytes(PayloadText);
            byte[] zip = ZipAesTestWriter.Build(EntryName, payload);

            string polyglot = Path.Combine(_root, "plain.mp4");
            long archiveEnd = ZipAesTestWriter.BuildPolyglot(polyglot, zip, FakeVideoPrefixLength, TailAfterEocdLength);
            string stage = Path.Combine(_root, "work-plain", "stage");

            EmbeddedZipProbeResult probe = EmbeddedZipStreamExtractor.Probe(
                polyglot,
                FakeVideoPrefixLength,
                archiveEnd,
                stage,
                new[] { ChinesePassword });

            Assert.True(probe.Supported, probe.Reason);
            Assert.Equal(0, probe.ResolvedPasswordIndex);
            Assert.False(probe.List!.IsEncrypted);

            EmbeddedZipExtractResult result = EmbeddedZipStreamExtractor.Extract(
                polyglot,
                FakeVideoPrefixLength,
                archiveEnd,
                stage,
                progress: null,
                cancellationToken: CancellationToken.None,
                budgetOptions: null,
                passwords: new[] { ChinesePassword });

            Assert.True(result.Success, result.Message);
            Assert.False(result.WasEncrypted);
            Assert.Equal(payload, File.ReadAllBytes(Path.Combine(stage, "内容物", "说明.txt")));
        }

        /// <summary>
        /// 加密 + **deflate**：先解密再解压那条路（真实打包方大多压一下）。
        /// 顺便钉住"deflate 读完不等于密文读完，剩下那段也要过认证"。
        /// </summary>
        [Fact]
        public void AES加密的deflate条目_内容正确()
        {
            byte[] payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(PayloadText, 40)));
            byte[] zip = ZipAesTestWriter.Build(
                EntryName,
                payload,
                ChinesePassword,
                ZipAesPasswordEncoding.Utf8,
                deflate: true,
                utf8NameFlag: true);

            string polyglot = Path.Combine(_root, "deflate.mp4");
            long archiveEnd = ZipAesTestWriter.BuildPolyglot(polyglot, zip, FakeVideoPrefixLength, TailAfterEocdLength);
            string stage = Path.Combine(_root, "work-deflate", "stage");

            EmbeddedZipExtractResult result = EmbeddedZipStreamExtractor.Extract(
                polyglot,
                FakeVideoPrefixLength,
                archiveEnd,
                stage,
                progress: null,
                cancellationToken: CancellationToken.None,
                budgetOptions: null,
                passwords: new[] { ChinesePassword });

            Assert.True(result.Success, result.Message);
            Assert.Equal(payload, File.ReadAllBytes(Path.Combine(stage, "内容物", "说明.txt")));
        }

        /// <summary>
        /// **真管线端到端**：9 MiB 假 MP4 头 + UTF-8 打包的中文密码 AES ZIP + 15 KB 尾巴，
        /// 密码放在密码本里（文件按 UTF-8 无 BOM 写，与用户现场同形）→ 一键处理必须成功，
        /// 内容物解出来，**工作区里没有抠出来的 .zip**、日志里也没有"取出内嵌归档"。
        ///
        /// <para>这一条钉的是"接线"：候选密码必须在**直读探查之前**算出来并传进去，
        /// 否则密码再对也到不了读取器手里（这正是修法里最容易漏的一步）。</para>
        /// </summary>
        [Fact]
        public async Task 端到端_加密双面文件走真管线_中文密码直接解开且不抠副本()
        {
            byte[] payload = Encoding.UTF8.GetBytes(PayloadText);
            byte[] zip = ZipAesTestWriter.Build(
                EntryName,
                payload,
                ChinesePassword,
                ZipAesPasswordEncoding.Utf8,
                utf8NameFlag: true);

            string source = Path.Combine(_root, "e2e-encrypted.mp4");
            ZipAesTestWriter.BuildPolyglot(source, zip, 9 * 1024 * 1024, TailAfterEocdLength);

            Harness harness = CreateHarness();
            await harness.AddPathsAsync(source);

            ArchiveTask task = Assert.Single(harness.Vm.Tasks);
            Assert.True(task.EmbeddedArchiveOffset > 0, "应该识别出尾部有内嵌归档");

            await harness.RunOneClickAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);

            string[] produced = Directory.GetFiles(harness.OutputRoot, "说明.txt", SearchOption.AllDirectories);
            Assert.True(produced.Length == 1, $"应该只解出一份说明.txt，实际 {produced.Length} 份");
            Assert.Equal(PayloadText, File.ReadAllText(produced[0]));

            Assert.Empty(Directory.GetFiles(harness.WorkRoot, "*.zip", SearchOption.AllDirectories));
            Assert.DoesNotContain(harness.Log.Logs, x => x.Message.Contains("取出内嵌归档", StringComparison.Ordinal));
            Assert.Contains(harness.Log.Logs, x => x.Message.Contains("AES", StringComparison.Ordinal));
        }

        // ================================================================ 器材：真 7z

        /// <summary>把内存里的 ZIP 写成文件、再用内置 7z 解一次，返回是否成功（含密码）。</summary>
        private bool RunSevenZipExtract(byte[] zip, string password, string tag)
        {
            string packageDirectory = Path.Combine(_root, tag);
            Directory.CreateDirectory(packageDirectory);

            string package = Path.Combine(packageDirectory, "pack.zip");
            File.WriteAllBytes(package, zip);

            string output = Path.Combine(packageDirectory, "out");
            Directory.CreateDirectory(output);

            var startInfo = new ProcessStartInfo
            {
                FileName = ToolLocator.Default.SevenZipExePath,
                WorkingDirectory = packageDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            startInfo.ArgumentList.Add("x");
            startInfo.ArgumentList.Add("-y");
            startInfo.ArgumentList.Add("-p" + password);
            startInfo.ArgumentList.Add("-o" + output);
            startInfo.ArgumentList.Add(package);

            using Process process = Process.Start(startInfo)!;

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();

            return process.ExitCode == 0;
        }

        // ================================================================ 端到端装配

        /// <summary>
        /// 一套真实装配：真 MainViewModel + 真各 Coordinator + 真 7z 引擎；
        /// 密码本按 UTF-8（无 BOM）写一行中文密码 —— 与用户现场的密码本同形。
        /// </summary>
        private Harness CreateHarness()
        {
            string dataRoot = Path.Combine(_root, "data");
            string outputRoot = Path.Combine(_root, "out");

            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(outputRoot);

            string bookPath = Path.Combine(_root, "password-book.txt");
            File.WriteAllText(
                bookPath,
                "# 合成密码本（本组测试自己造的中文密码）" + Environment.NewLine + ChinesePassword + Environment.NewLine,
                new UTF8Encoding(false));

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var settingsService = new SettingsService(pathService);

            AppSettings settings = AppSettings.CreateDefault();
            settings.CacheRootDirectory = dataRoot;
            settings.CustomOutputDirectory = outputRoot;
            settings.ExtractToOriginalDirectory = false;
            settings.KeepArchiveNameFolder = true;
            settings.PasswordBookPath = bookPath;
            settings.CustomSevenZipExePath = string.Empty;
            settingsService.Save(settings);

            var engine = new SevenZipEngine();
            var passwordService = new PasswordService();
            var logService = new LogService(pathService);

            string? previousWorkspaceRoot = RecursiveExtractor.ConfiguredWorkspaceRoot;
            string previousSevenZipPath = ToolLocator.Default.CustomSevenZipExePath;

            var vm = new MainViewModel(
                new FileScanService(),
                new ArchiveDetectService(),
                new RenameService(),
                engine,
                passwordService,
                logService,
                settingsService,
                pathService,
                new TaskSummaryService(),
                new ClipboardService(),
                new DialogService());

            RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;
            ToolLocator.Default.CustomSevenZipExePath = previousSevenZipPath;

            var scan = new ScanCoordinator(vm, new FileScanService(), new ArchiveDetectService(), new DialogService());
            var rename = new RenameCoordinator(vm, scan, new RenameService(), new DialogService());
            var extraction = new ExtractionCoordinator(vm, engine, passwordService, pathService, new DialogService());

            // 这个用例拿「细节日志」当行为证据（第 44 条之后，成功时默认只留两行）。
            extraction.KeepTaskDetailInLog = true;
            var oneClick = new OneClickCoordinator(vm, scan, rename, extraction, new DialogService());

            return new Harness(vm, oneClick, outputRoot, pathService.WorkDirectory, logService);
        }

        private sealed class Harness
        {
            private readonly OneClickCoordinator _oneClick;

            public Harness(MainViewModel vm, OneClickCoordinator oneClick, string outputRoot, string workRoot, LogService log)
            {
                Vm = vm;
                _oneClick = oneClick;
                OutputRoot = outputRoot;
                WorkRoot = workRoot;
                Log = log;
            }

            public MainViewModel Vm { get; }

            public string OutputRoot { get; }

            /// <summary>工作区根：抠出来的中间件会落在这里（本组要求它里面**没有** .zip）。</summary>
            public string WorkRoot { get; }

            public LogService Log { get; }

            public Task AddPathsAsync(params string[] paths) => Vm.AddPathsAsync(paths);

            public Task<OneClickOutcome> RunOneClickAsync() => _oneClick.RunPipelineAsync(Vm.Tasks.ToList());
        }
    }
}

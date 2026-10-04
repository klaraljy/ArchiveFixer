using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using Xunit;
using Xunit.Abstractions;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// <b>内层包本身是「双面文件」时，递归层必须先按偏移抠出来再解</b>
    /// （用户 2026-10-04 真机原话：「这么简单的操作，密码也是对的，怎么解压不了」）。
    ///
    /// <para><b>现场</b>（<c>ArchiveFixer-本次操作_20261004_234400.txt</c>）：<c>HK.7z.001</c> 第 0 层正常解出
    /// 3 个 4K 视频（7.11 GiB，14 分钟），第 1 层探到 <c>4K (11)_2.mp4</c> 是内层包，把它**原样**交给 7-Zip ⇒
    /// <c>Cannot open the file as archive</c> ⇒ 整条链判「部分完成」⇒ 已经解出来的 7.11 GiB
    /// 一个字节都不发布、工作区整份删掉。那个 mp4 是**双面文件**（真视频 + 尾部一整个 ZIP），
    /// 而 7-Zip 只在前面垫的数据 ≤ 8 MiB 时才容忍这种整体偏移 —— 单层路径早就接了这一档
    /// （先按偏移抠出来再解），**递归这条路一直没接**；出厂默认档 2026-10-04 起正是「展开所有分支」，
    /// 于是它成了默认路径。</para>
    ///
    /// <para><b>这里钉四件事</b>：① 双面文件（前缀 9 MiB &gt; 8 MiB 的容忍上限）确实被识别成归档、
    /// 且**直接**交给真 7-Zip 会失败（<c>UnsupportedFormat</c>，就是真机那句）—— 这是前提，前提不成立这条用例证明不了任何事；
    /// ② 递归层带着这个内层包跑完必须 <c>Completed</c>（不再判部分完成），而且**里面的内容物真的解出来了**；
    /// ③ 日志点名说了「双面文件…按偏移取出副本…再解一次」；
    /// ④ ⛔ 原文件（外层源包）一个字节没动（SHA256 前后相同），⛔ 抠出来的副本不留在产物目录里。</para>
    ///
    /// <para><b>红检</b>：把 <c>RecursiveExtractor</c> 里那次"引擎说不是归档 ⇒ 先看它是不是双面文件"的补救
    /// 撤掉（连同 <c>archivePath</c> 那个参数一起退回按 <c>item.ArchivePath</c> 解）⇒
    /// <see cref="内层是双面文件_递归层要先抠出尾部归档再解_整链不再判部分完成"/> 当场红
    /// （链停在 EngineFailed、内容物一个都没有）。</para>
    /// </summary>
    public class InnerDoubleFacedCarveTests : IDisposable
    {
        /// <summary>7-Zip 容忍的"前面垫的数据"上限（实测边界见 <c>EmbeddedArchiveCarver</c> 类注释）：9 MiB 必然落在拒绝区。</summary>
        private const int PrefixBytes = 9 * 1024 * 1024;

        private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

        private readonly string _root;
        private readonly string? _sevenZip = SevenZipFactAttribute.LocateSevenZipPath();
        private readonly ITestOutputHelper _output;

        public InnerDoubleFacedCarveTests(ITestOutputHelper output)
        {
            _output = output;
            _root = Path.Combine(Path.GetTempPath(), "af-inner-doubleface-" + Guid.NewGuid().ToString("N"));
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
                // 临时目录删不掉不影响结论。
            }
        }

        [SevenZipFact]
        public async Task 内层是双面文件_递归层要先抠出尾部归档再解_整链不再判部分完成()
        {
            RequireSevenZip();

            // ===== 夹具：外层 7z 里装着一个「双面文件」（真视频前缀 + 尾部一个完整 ZIP） =====
            string doubleFaced = BuildDoubleFacedInnerPackage(out string innerZip);

            // 前提 ①：它必须真的被识别成"归档"（否则递归层根本不会去解它，这条用例也就证明不了什么）。
            Assert.True(
                await new MagicArchiveProber().IsArchiveAsync(doubleFaced),
                "双面文件必须被内容探测认成归档（真机就是这一档把 mp4 当成了内层包）");

            // 前提 ②：**直接**交给真 7-Zip 必然打不开 —— 真机那句 `Cannot open the file as archive`
            //         （这正是"必须先抠出来"的理由；它不成立，后面那条断言就没有意义）。
            string directOut = Path.Combine(_root, "direct-out");
            ArchiveOperationResult direct = await new SevenZipEngine().ExtractAsync(
                new ArchiveRequest { ArchivePath = doubleFaced, OutputPath = directOut },
                new ExtractOptions(),
                CancellationToken.None);

            Assert.False(direct.Success, "前缀 9 MiB 的双面文件直接交给 7-Zip 应当打不开");
            Assert.Equal(EngineErrorTypes.UnsupportedFormat, direct.DetectedErrorType);

            string outerSource = Path.Combine(_root, "外层内容");
            Directory.CreateDirectory(outerSource);
            File.Copy(doubleFaced, Path.Combine(outerSource, Path.GetFileName(doubleFaced)));

            string outer = Path.Combine(_root, "HK.7z");
            Run7z("a", "-t7z", outer, Path.Combine(outerSource, "*"));

            string outerHashBefore = Sha256Of(outer);

            // ===== 跑真实生产路径：递归核心 + 真 7-Zip + 真探测器 =====
            string output = Path.Combine(_root, "out");
            var log = new List<(string Level, string Message)>();
            var task = new ArchiveTask(outer);
            var extractor = new RecursiveExtractor(
                new SevenZipEngine(),
                new MagicArchiveProber(),
                _ => new[] { string.Empty },
                limits: null,
                log: (level, message) => log.Add((level, message)));

            RecursionResult result = await extractor.ExtractAsync(
                task,
                output,
                RecursionMode.AllBranches,
                null,
                CancellationToken.None);

            foreach ((string level, string message) in log)
            {
                _output.WriteLine($"[{level}] {message}");
            }

            // ① 整链跑完、**不再**停在"引擎操作失败"那一档。
            Assert.True(result.Completed, result.Summary);
            Assert.Equal(RecursionStopReason.Completed, result.StopReason);

            // ② 双面文件尾部那个归档里的内容物真的解出来了（这才是修好与"少报一个失败"的分水岭）。
            string? payload = FindFileUnder(output, "内层内容.txt");
            Assert.NotNull(payload);
            Assert.Contains("双面文件里的真内容", File.ReadAllText(payload!), StringComparison.Ordinal);

            // ③ 日志必须点名说清"这是双面文件、按偏移取出副本再解"（⛔ 不许悄悄改行为）。
            Assert.Contains(
                log,
                entry => entry.Message.Contains("双面文件", StringComparison.Ordinal)
                         && entry.Message.Contains("按偏移", StringComparison.Ordinal));

            // ④ ⛔ 原文件一个字节没动：外层源包 SHA256 前后相同。
            Assert.Equal(outerHashBefore, Sha256Of(outer));

            // ⑤ 抠出来的副本用完就删：产物目录、以及产物目录的兄弟位置都不该留一个 ZIP 副本
            //    （真机那 7.11 GiB 就是这么被撑大的）。
            Assert.DoesNotContain(
                Directory.EnumerateFiles(output, "*.zip", SearchOption.AllDirectories),
                path => path.Contains("carved", StringComparison.OrdinalIgnoreCase));
        }

        [SevenZipFact]
        public async Task 对照_前缀很小的时候七Zip自己就能打开_不该多抠一份副本()
        {
            RequireSevenZip();

            /*
             * 对照（防"顺手对所有双面文件都抠一份"）：
             * 同样的形状、只把前缀缩到 1 MiB —— 7-Zip 自己就能打开这种整体偏移（实测边界 8 MiB），
             * 于是递归层**一次都不该**走"抠出来再解"那条路：
             * 日志里不许出现"双面文件…按偏移"，产物照旧正确。
             *
             * 为什么这条对照重要：抠取是把整个归档复制一份（真机上是 2.4 GB 级别），
             * 无差别地抠会让每一层都多占一份磁盘、还会拉长每次递归。
             */
            string doubleFaced = BuildDoubleFacedInnerPackage(out _, prefixBytes: 1 * 1024 * 1024);

            string outerSource = Path.Combine(_root, "外层内容-小前缀");
            Directory.CreateDirectory(outerSource);
            File.Copy(doubleFaced, Path.Combine(outerSource, Path.GetFileName(doubleFaced)));

            string outer = Path.Combine(_root, "小前缀.7z");
            Run7z("a", "-t7z", outer, Path.Combine(outerSource, "*"));

            string output = Path.Combine(_root, "out-small");
            var log = new List<(string Level, string Message)>();
            var extractor = new RecursiveExtractor(
                new SevenZipEngine(),
                new MagicArchiveProber(),
                _ => new[] { string.Empty },
                limits: null,
                log: (level, message) => log.Add((level, message)));

            RecursionResult result = await extractor.ExtractAsync(
                new ArchiveTask(outer),
                output,
                RecursionMode.AllBranches,
                null,
                CancellationToken.None);

            foreach ((string level, string message) in log)
            {
                _output.WriteLine($"[{level}] {message}");
            }

            Assert.True(result.Completed, result.Summary);
            Assert.NotNull(FindFileUnder(output, "内层内容.txt"));

            Assert.DoesNotContain(
                log,
                entry => entry.Message.Contains("双面文件", StringComparison.Ordinal));
        }

        // ================================================================ 夹具与工具

        /// <summary>
        /// 造一个"双面文件"：<c>[前缀字节][一个完整 ZIP]</c>，名字用真机那个（<c>4K (11)_2.mp4</c>）。
        /// <paramref name="prefixBytes"/> 必须跨越 7-Zip 的 8 MiB 容忍边界（> 8 MiB 才打不开）。
        /// </summary>
        /// <param name="innerZipPath">造出来的那个 ZIP 本体（内容物在它里面）。</param>
        private string BuildDoubleFacedInnerPackage(out string innerZipPath, int prefixBytes = PrefixBytes)
        {
            string payloadDir = Path.Combine(_root, "payload-" + prefixBytes);
            Directory.CreateDirectory(payloadDir);
            File.WriteAllText(Path.Combine(payloadDir, "内层内容.txt"), "双面文件里的真内容\n", Utf8NoBom);

            innerZipPath = Path.Combine(_root, "尾部-" + prefixBytes + ".zip");
            Run7z("a", "-tzip", innerZipPath, Path.Combine(payloadDir, "*"));

            byte[] zip = File.ReadAllBytes(innerZipPath);
            byte[] prefix = new byte[prefixBytes];

            // 前缀用可复现的伪随机字节（假视频数据）：真视频也是这种"看着像随机"的字节。
            new Random(20261004).NextBytes(prefix);

            byte[] combined = new byte[prefix.Length + zip.Length];
            Array.Copy(prefix, 0, combined, 0, prefix.Length);
            Array.Copy(zip, 0, combined, prefix.Length, zip.Length);

            string path = Path.Combine(_root, "4K (11)_2.mp4");
            File.WriteAllBytes(path, combined);

            return path;
        }

        private static string Sha256Of(string path)
        {
            using FileStream stream = File.OpenRead(path);

            return Convert.ToHexString(SHA256.HashData(stream));
        }

        private static string? FindFileUnder(string root, string fileName)
        {
            if (!Directory.Exists(root))
            {
                return null;
            }

            return Directory
                .GetFiles(root, fileName, SearchOption.AllDirectories)
                .FirstOrDefault();
        }

        private void RequireSevenZip()
        {
            if (string.IsNullOrEmpty(_sevenZip))
            {
                throw new InvalidOperationException(
                    "找不到内置 7z.exe，且 [SevenZipFact] 没有把它跳过：环境与特性探测结果不一致。");
            }
        }

        /// <summary>跑一次内置 7-Zip（造夹具用；与生产引擎无关）。</summary>
        private void Run7z(params object[] args)
        {
            var psi = new ProcessStartInfo(_sevenZip!)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = _root
            };

            foreach (object arg in args)
            {
                if (arg is IEnumerable<string> many)
                {
                    foreach (string one in many)
                    {
                        psi.ArgumentList.Add(one);
                    }

                    continue;
                }

                psi.ArgumentList.Add(Convert.ToString(arg) ?? string.Empty);
            }

            using Process process = Process.Start(psi)
                ?? throw new InvalidOperationException("起不来 7z.exe");

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();

            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"7z {string.Join(' ', psi.ArgumentList)} 退出码 {process.ExitCode}：{stdout}{stderr}");
            }
        }
    }
}

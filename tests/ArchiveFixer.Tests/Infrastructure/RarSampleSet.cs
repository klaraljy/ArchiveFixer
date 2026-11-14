using ArchiveFixer.Engines;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// RAR 真样本的**准备与自检**（只给测试用）。
    ///
    /// <para>
    /// 为什么样本不随仓库走：RAR 是 RARLAB 的专利格式，7-Zip 只能解不能建；
    /// 建包工具 <c>Rar.exe</c> 是**共享软件**，其许可禁止再分发/捆绑
    /// （`docs/引擎与外部工具.md` §4）。把 RAR 样本提交进仓库等于把"我们用了它的建包器"
    /// 变成既成事实；而样本本身体积也不适合入库（`AGENTS.md` §12 第 2 条）。
    /// </para>
    ///
    /// <para>两条来源，按顺序尝试：
    /// ① 环境变量 <c>ARCHIVEFIXER_RAR_SAMPLES</c> 指向的目录（离线准备好的一组样本，验收走这条）；
    /// ② 本机**已装**的 WinRAR 目录里的 <c>Rar.exe</c>（只读取、只在本机临时目录里造，绝不入库）。
    /// 都没有 → <see cref="TryCreate"/> 返回 null，用例打印原因后跳过。</para>
    ///
    /// <para>约定的样本名（两条来源必须一致）：
    /// <c>plain.rar</c>（RAR5、无密码）、<c>hp.rar</c>（RAR5、<c>-hp&lt;sample-password&gt;</c>）、
    /// <c>broken.rar</c>（<c>plain.rar</c> 截断）、<c>vol.part1.rar</c> 起的整组分卷。</para>
    /// </summary>
    internal sealed class RarSampleSet
    {
        /// <summary>环境变量名：指向离线样本目录。</summary>
        internal const string SamplesEnvironmentVariable = "ARCHIVEFIXER_RAR_SAMPLES";

        /// <summary>测试用密码占位符（**不是**真实密码，AGENTS.md §8）。</summary>
        internal const string Password = "<sample-password>";

        private string? _hiddenVolumePath;

        private RarSampleSet(string directory, string source, Action<string> log)
        {
            Directory = directory;
            Source = source;
        }

        internal string Directory { get; }

        /// <summary>这批样本是怎么来的（"离线样本目录" / "本机 WinRAR 现造"）。</summary>
        internal string Source { get; }

        internal string PlainArchive => Path.Combine(Directory, "plain.rar");

        internal string? EncryptedHeadersArchive =>
            File.Exists(Path.Combine(Directory, "hp.rar")) ? Path.Combine(Directory, "hp.rar") : null;

        internal string? BrokenArchive =>
            File.Exists(Path.Combine(Directory, "broken.rar")) ? Path.Combine(Directory, "broken.rar") : null;

        internal bool HasEncryptedHeadersArchive => EncryptedHeadersArchive != null;

        internal bool HasBrokenArchive => BrokenArchive != null;

        internal bool HasVolumeSet => FirstVolumePath != null;

        internal string? FirstVolumePath
        {
            get
            {
                string[] candidates =
                {
                    Path.Combine(Directory, "vol.part1.rar"),
                    Path.Combine(Directory, "vol.rar")
                };

                return candidates.FirstOrDefault(File.Exists);
            }
        }

        /// <summary>内置 UnRAR 的路径（测试里显式指定它，免得被"本机已装 WinRAR 目录"那一档抢走）。</summary>
        internal string BundledUnRarPath
        {
            get
            {
                string local = Path.Combine(AppContext.BaseDirectory, "tools", "unrar", "UnRAR.exe");

                if (File.Exists(local))
                {
                    return local;
                }

                // 从测试输出目录往上找仓库，再用源码树里的那一份（两种布局都能跑）。
                DirectoryInfo? current = new(AppContext.BaseDirectory);

                while (current != null)
                {
                    if (File.Exists(Path.Combine(current.FullName, "ArchiveFixer.slnx")))
                    {
                        string candidate = Path.Combine(current.FullName, "src", "ArchiveFixer", "tools", "unrar", "UnRAR.exe");

                        if (File.Exists(candidate))
                        {
                            return candidate;
                        }
                    }

                    current = current.Parent;
                }

                return local;
            }
        }

        /// <summary>解出来的 readme 是否与源一致（内容比对比"退出码 0"可靠）。</summary>
        internal void AssertContentMatches(string outputDirectory)
        {
            string? readme = FindByName(outputDirectory, "readme.txt");

            if (readme == null)
            {
                throw new Xunit.Sdk.XunitException($"产物里没有 readme.txt：{outputDirectory}");
            }

            string actual = File.ReadAllText(readme).Trim();
            string expected = File.ReadAllText(ReadmePath).Trim();

            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                throw new Xunit.Sdk.XunitException($"readme.txt 内容不一致：期望「{expected}」实际「{actual}」");
            }

            // 中文条目名（Unicode）也必须真的解出来 —— 这是 -scf 那个开关的验收点。
            if (FindByName(outputDirectory, "中文名.txt") == null)
            {
                throw new Xunit.Sdk.XunitException("产物里没有中文名条目：Unicode 名称没有正确还原");
            }
        }

        /// <summary>分卷里那个大文件必须与源逐字节一致（分卷最容易"少一截还报成功"）。</summary>
        internal void AssertPayloadMatches(string outputDirectory)
        {
            string source = PayloadPath;
            string? extracted = FindByName(outputDirectory, "payload.bin");

            if (extracted == null)
            {
                throw new Xunit.Sdk.XunitException($"产物里没有 payload.bin：{outputDirectory}");
            }

            string sourceHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
            string extractedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(extracted)));

            if (!string.Equals(sourceHash, extractedHash, StringComparison.Ordinal))
            {
                throw new Xunit.Sdk.XunitException(
                    $"payload.bin 哈希不一致：源 {sourceHash}，产物 {extractedHash}（大小 {new FileInfo(source).Length} vs {new FileInfo(extracted).Length}）");
            }
        }

        /// <summary>把中间那一卷挪开（离卷场景），返回它的新路径。</summary>
        internal string HideMiddleVolume()
        {
            string first = FirstVolumePath ?? throw new InvalidOperationException("没有分卷样本");

            string directory = Path.GetDirectoryName(first)!;
            string middle = System.IO.Directory.EnumerateFiles(directory, "vol.part*.rar")
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Skip(1)
                .First();

            _hiddenVolumePath = middle + ".hidden";
            File.Move(middle, _hiddenVolumePath);

            return middle;
        }

        internal string ReadmePath => FindByName(Directory, "readme.txt")
            ?? Path.Combine(Directory, "readme.txt");

        /// <summary>分卷里那个大载荷的源文件（离线样本目录可能把它放在 src\ 下，所以两级都找）。</summary>
        internal string PayloadPath => FindByName(Directory, "payload.bin")
            ?? Path.Combine(Directory, "payload.bin");

        /// <summary>
        /// 用随包分发的 7z.exe 造一个对照用的 zip（验收第 3 条要证明"zip 仍然走 7-Zip"）。
        /// 刻意不引第三方库：仓库里已经有 7z.exe，它就是"造样本"的现成工具。
        /// </summary>
        internal static void RunSevenZip(string sevenZipExe, string workingDirectory, params string[] args)
        {
            if (!File.Exists(sevenZipExe))
            {
                throw new Xunit.Sdk.XunitException("找不到内置 7z.exe：" + sevenZipExe);
            }

            var psi = new ProcessStartInfo(sevenZipExe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDirectory
            };

            foreach (string arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using Process process = Process.Start(psi)
                ?? throw new InvalidOperationException("无法启动 7z.exe");

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();

            if (!process.WaitForExit(120_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                throw new TimeoutException("7z.exe 超时");
            }

            if (process.ExitCode != 0)
            {
                throw new Xunit.Sdk.XunitException($"7z.exe 退出码 {process.ExitCode}（{string.Join(' ', args)}）");
            }
        }

        /// <summary>把挪走的那一卷放回去（用例的 finally 里调用，保证下一个用例拿到完整的一组）。</summary>
        internal void RestoreHiddenVolume()
        {
            if (_hiddenVolumePath == null)
            {
                return;
            }

            string original = _hiddenVolumePath[..^".hidden".Length];

            if (File.Exists(_hiddenVolumePath) && !File.Exists(original))
            {
                File.Move(_hiddenVolumePath, original);
            }

            _hiddenVolumePath = null;
        }

        internal static RarSampleSet? TryCreate(string workRoot, Action<string> log)
        {
            string? configured = Environment.GetEnvironmentVariable(SamplesEnvironmentVariable);

            if (!string.IsNullOrWhiteSpace(configured) && System.IO.Directory.Exists(configured))
            {
                var fromDirectory = new RarSampleSet(configured, "离线样本目录（" + SamplesEnvironmentVariable + "）", log);

                if (File.Exists(fromDirectory.PlainArchive))
                {
                    log($"使用离线 RAR 样本：{configured}");
                    return fromDirectory;
                }

                log($"环境变量指向的目录里没有 plain.rar：{configured}");
            }

            string? rarExe = LocateRarExe();

            if (rarExe == null)
            {
                return null;
            }

            string directory = Path.Combine(workRoot, "rar-samples");
            System.IO.Directory.CreateDirectory(directory);

            try
            {
                BuildSamples(rarExe, directory, log);
            }
            catch (Exception ex)
            {
                log("用本机 Rar.exe 造样本失败：" + ex.Message);
                return null;
            }

            var created = new RarSampleSet(directory, "本机已装 WinRAR 现造（" + rarExe + "）", log);

            return File.Exists(created.PlainArchive) ? created : null;
        }

        /// <summary>
        /// 找本机已装的 <c>Rar.exe</c>。用 <see cref="ToolLocator.WinRarInstallationCandidates"/>
        /// 的同一套探测目录（那里探的是 UnRAR.exe，这里换成同目录的 Rar.exe）——
        /// 只读取、只调用，绝不复制进仓库。
        ///
        /// <para><c>internal</c>：加密样本那一组（<c>RarEncryptionSampleSet</c>）用的是同一个探测器，
        /// ⛔ 不另写一份"Rar.exe 在哪"。</para>
        /// </summary>
        internal static string? LocateRarExe()
        {
            foreach (string candidate in new ToolLocator().WinRarInstallationCandidates())
            {
                string? directory = Path.GetDirectoryName(candidate);

                if (string.IsNullOrWhiteSpace(directory))
                {
                    continue;
                }

                string rar = Path.Combine(directory, "Rar.exe");

                if (File.Exists(rar))
                {
                    return rar;
                }
            }

            return null;
        }

        /// <summary>
        /// 造一组样本。内容刻意包含：普通文本、**中文名条目**、子目录、一段**不可压缩**的随机载荷。
        /// 最后一项是为了让分卷真的分成多卷（可压缩的载荷会被压成一卷，"分卷测试"就名不副实了）。
        /// </summary>
        private static void BuildSamples(string rarExe, string directory, Action<string> log)
        {
            string source = Path.Combine(directory, "src");
            string sub = Path.Combine(source, "sub");

            System.IO.Directory.CreateDirectory(sub);

            File.WriteAllText(Path.Combine(source, "readme.txt"), "ArchiveFixer RAR sample" + Environment.NewLine);
            File.WriteAllText(Path.Combine(source, "中文名.txt"), "unicode name entry" + Environment.NewLine);
            File.WriteAllText(Path.Combine(sub, "inner.txt"), new string('x', 4000));

            var bytes = new byte[60_000];
            new Random(20260922).NextBytes(bytes);
            File.WriteAllBytes(Path.Combine(directory, "payload.bin"), bytes);

            string passwordArg = "-hp" + Password;

            // 普通 RAR5（-ep1：不带路径，产物扁平，便于逐条比对）。
            RunRar(rarExe, directory, log, "a", "-ma5", "-ep1", "-idq", "-r",
                Path.Combine(directory, "plain.rar"), Path.Combine(source, "*"));

            // 加密文件名（-hp）的 RAR5：UnRAR 无密码时列不出内容，给对密码全能列出并解出。
            RunRar(rarExe, directory, log, "a", "-ma5", "-ep1", "-idq", "-r", passwordArg,
                Path.Combine(directory, "hp.rar"), Path.Combine(source, "*"));

            // 分卷：-m0（不压缩）+ 60KB 随机载荷，确保真的分成多卷。
            RunRar(rarExe, directory, log, "a", "-ma5", "-m0", "-ep1", "-idq", "-v10k",
                Path.Combine(directory, "vol.rar"),
                Path.Combine(directory, "payload.bin"),
                Path.Combine(source, "readme.txt"));

            // 损坏样本：plain.rar 截掉尾部 1/4（校验和 / 归档结尾都会坏）。
            byte[] plain = File.ReadAllBytes(Path.Combine(directory, "plain.rar"));
            File.WriteAllBytes(
                Path.Combine(directory, "broken.rar"),
                plain[..(int)(plain.Length * 0.75)]);
        }

        /// <summary>
        /// 跑一次 <c>Rar.exe</c>（非 0 退出码只写日志、不抛 —— 造样本失败由调用方按"样本不全"处理）。
        /// <c>internal</c>：加密样本那一组也走它（⛔ 不另写一份"怎么调 Rar.exe"）。
        /// </summary>
        internal static void RunRar(string rarExe, string workingDirectory, Action<string> log, params string[] args)
        {
            var psi = new ProcessStartInfo(rarExe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDirectory
            };

            foreach (string arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using Process process = Process.Start(psi)
                ?? throw new InvalidOperationException("无法启动 Rar.exe");

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(120_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                throw new TimeoutException("Rar.exe 造样本超时");
            }

            if (process.ExitCode != 0)
            {
                log($"Rar.exe {(args.Length > 0 ? args[0] : string.Empty)} 退出码 {process.ExitCode}：{stdout}{stderr}");
            }
        }

        private static string? FindByName(string directory, string fileName)
        {
            return System.IO.Directory
                .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .FirstOrDefault(x => string.Equals(Path.GetFileName(x), fileName, StringComparison.OrdinalIgnoreCase));
        }
    }
}

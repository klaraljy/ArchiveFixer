using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// ZIP **加密**样本的准备（只给测试用，用户 2026-09-30）。
    ///
    /// <para><b>为什么必须用真样本</b>：这一组的判据是"中央目录记录的 flags 在 +8、bit0 = 加密"
    /// 这类**字段偏移**，而"我对偏移的理解对不对"只有真产品写出来的字节能回答 ——
    /// 合成字节只能证明"我按自己的理解造的字节能被自己的解析器读出来"。</para>
    ///
    /// <para>样本全部用**项目内置的 7z.exe** 现造（随包分发、不引外部依赖），
    /// 造不出时 <see cref="TryCreate"/> 返回 null，用例打印原因后**跳过**（⛔ 不许假装跑过）。
    /// 密码一律占位符（AGENTS.md §8）。</para>
    ///
    /// <para>⚠ 样本本体**不入库** —— 每次都在本机临时目录里现造。</para>
    /// </summary>
    internal sealed class ZipEncryptionSampleSet
    {
        /// <summary>测试用密码占位符（**不是**真实密码，AGENTS.md §8）。</summary>
        internal const string Password = "<sample-password>";

        private ZipEncryptionSampleSet(string directory, string sevenZipPath)
        {
            Directory = directory;
            SevenZipPath = sevenZipPath;
        }

        internal string Directory { get; }

        /// <summary>这批样本用的内置 7z.exe 路径。</summary>
        internal string SevenZipPath { get; }

        /// <summary>这批样本是怎么来的（"项目内置 7z.exe 现造"）。</summary>
        internal string Source => "项目内置 7z.exe 现造（" + SevenZipPath + "）";

        /// <summary>不加密对照组（单文件）。</summary>
        internal string Plain => Path.Combine(Directory, "zip-plain.zip");

        /// <summary>ZipCrypto（<c>-tzip -p</c>：flags = 0x0001、method = 0）。</summary>
        internal string ZipCrypto => Path.Combine(Directory, "zip-zc.zip");

        /// <summary>AES-256（<c>-tzip -mem=AES256 -p</c>：flags = 0x0001、method = 99）。</summary>
        internal string Aes => Path.Combine(Directory, "zip-aes.zip");

        /// <summary>
        /// **本次缺陷的红检样本**：第一个本地头是**目录条目** <c>sub/</c>（flags = <c>0x0000</c>），
        /// 第二个条目 <c>sub/inner.txt</c> 才是加密的（flags = <c>0x0001</c>）。
        /// 旧实现只看第一个本地头 ⇒ 判成"没加密"（漏报）。
        /// </summary>
        internal string DirectoryFirstEncrypted => Path.Combine(Directory, "zip-dirfirst.zip");

        /// <summary>混合：<c>a.txt</c> 不加密、<c>sub/</c> 目录不加密、<c>sub/inner.txt</c> 加密。</summary>
        internal string Mixed => Path.Combine(Directory, "zip-mixed.zip");

        /// <summary>现造一组加密样本。拿不到内置 7z / 造样本失败 → 返回 null（用例跳过并说明原因）。</summary>
        internal static ZipEncryptionSampleSet? TryCreate(string workRoot, Action<string> log)
        {
            string? sevenZip = SevenZipFactAttribute.LocateSevenZipPath();

            if (string.IsNullOrEmpty(sevenZip))
            {
                log("测试机上没有内置 7z.exe，ZIP 加密样本造不出来 —— 本用例跳过。");

                return null;
            }

            string directory = Path.Combine(workRoot, "zip-encryption-samples");
            System.IO.Directory.CreateDirectory(directory);

            try
            {
                Build(sevenZip!, directory);
            }
            catch (Exception ex)
            {
                log("用内置 7z.exe 造 ZIP 加密样本失败：" + ex.Message);

                return null;
            }

            var created = new ZipEncryptionSampleSet(directory, sevenZip!);

            return File.Exists(created.Plain) && File.Exists(created.DirectoryFirstEncrypted) ? created : null;
        }

        private static void Build(string sevenZip, string directory)
        {
            string source = Path.Combine(directory, "src");

            System.IO.Directory.CreateDirectory(Path.Combine(source, "sub"));

            // 载荷用随机字节（不可压缩）：加密与不加密两种样本的体积差才有意义。
            var payload = new byte[64_000];
            new Random(20260930).NextBytes(payload);
            File.WriteAllBytes(Path.Combine(source, "data.bin"), payload);
            File.WriteAllText(Path.Combine(source, "a.txt"), "ArchiveFixer ZIP encryption sample" + Environment.NewLine);
            File.WriteAllText(Path.Combine(source, "sub", "inner.txt"), "unicode name entry" + Environment.NewLine);

            // ① 不加密
            Run(sevenZip, directory, "-tzip", Path.Combine(directory, "zip-plain.zip"), Path.Combine(source, "data.bin"));

            // ② ZipCrypto（7-Zip 的默认 -p 就是 ZipCrypto）
            Run(sevenZip, directory, "-tzip", "-p" + Password, Path.Combine(directory, "zip-zc.zip"), Path.Combine(source, "data.bin"));

            // ③ AES-256
            Run(sevenZip, directory, "-tzip", "-mem=AES256", "-p" + Password, Path.Combine(directory, "zip-aes.zip"), Path.Combine(source, "data.bin"));

            /*
             * ④ 第一个条目是目录：`-r` 连子目录一起收，7-Zip 会把目录条目排在前面，
             * 于是"第一个本地头"恰好是那个 flags = 0x0000 的目录 —— 这正是缺陷现场。
             */
            Run(sevenZip, directory, "-tzip", "-p" + Password, "-r", Path.Combine(directory, "zip-dirfirst.zip"), Path.Combine(source, "sub"));

            // ⑤ 混合：先加不加密的 a.txt，再用 -p 往同一个包里追加加密的 sub/
            Run(sevenZip, directory, "-tzip", Path.Combine(directory, "zip-mixed.zip"), Path.Combine(source, "a.txt"));
            Run(sevenZip, directory, "-tzip", "-p" + Password, "-r", Path.Combine(directory, "zip-mixed.zip"), Path.Combine(source, "sub"));
        }

        /// <summary>调真 7z.exe 的 <c>a</c>（添加）子命令。用 ArgumentList 安全传参，**不拼命令行字符串**（不变量 10）。</summary>
        private static void Run(string sevenZip, string workingDirectory, params string[] args)
        {
            var info = new ProcessStartInfo(sevenZip)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDirectory
            };

            info.ArgumentList.Add("a");
            info.ArgumentList.Add("-bso0");
            info.ArgumentList.Add("-bsp0");

            foreach (string arg in args)
            {
                info.ArgumentList.Add(arg);
            }

            using Process process = Process.Start(info) ?? throw new InvalidOperationException("无法启动 7z.exe");

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(120_000))
            {
                process.Kill(entireProcessTree: true);

                throw new InvalidOperationException("7z.exe 超时未退出");
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"7z.exe 失败（{process.ExitCode}）：{stdout}{stderr}");
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 7z **加密**样本的准备（只给测试用，用户 2026-09-30）。
    ///
    /// <para><b>为什么必须用真样本</b>：这一组的判据是 7z 头的**结构化解析**（变长数字、coder 链、
    /// AES 的 method ID <c>06 F1 07 01</c>），而"我对格式说明的理解对不对"只有真 7-Zip 写出来的字节能回答。
    /// 现场实测（内置 7z.exe）：
    /// <c>-p</c> 的单文件包明文头里直接有 <c>06 f1 07 01</c>；
    /// <c>-mhe=on</c> 的头是 <c>kEncodedHeader(0x17)</c> 且解码链里有 AES；
    /// 而 <c>-p</c> + 80 个文件时头被**压缩**（<c>0x17</c>）—— 编码头里只有 LZMA、看不见 AES，
    /// 但它是**真加密**的（<c>7z t -pWrongPassword</c> 报 "Wrong password?"）⇒ 那一档只能"不知道"。</para>
    ///
    /// <para>样本全部用**项目内置的 7z.exe** 现造；造不出时 <see cref="TryCreate"/> 返回 null，
    /// 用例打印原因后**跳过**（⛔ 不许假装跑过）。密码一律占位符（AGENTS.md §8）。
    /// 样本本体**不入库**。</para>
    /// </summary>
    internal sealed class SevenZipEncryptionSampleSet
    {
        /// <summary>测试用密码占位符（**不是**真实密码，AGENTS.md §8）。</summary>
        internal const string Password = "<sample-password>";

        /// <summary>让头"值得压缩"的文件数（80 个长名字，实测 7-Zip 就会把 next header 编码掉）。</summary>
        internal const int ManyFileCount = 80;

        private SevenZipEncryptionSampleSet(string directory, string sevenZipPath)
        {
            Directory = directory;
            SevenZipPath = sevenZipPath;
        }

        internal string Directory { get; }

        /// <summary>这批样本用的内置 7z.exe 路径。</summary>
        internal string SevenZipPath { get; }

        /// <summary>这批样本是怎么来的（"项目内置 7z.exe 现造"）。</summary>
        internal string Source => "项目内置 7z.exe 现造（" + SevenZipPath + "）";

        /// <summary>普通包（单文件、明文头、没有 AES）⇒ 应当报"不加密"。</summary>
        internal string Plain => Path.Combine(Directory, "plain.7z");

        /// <summary><c>-p</c> 单文件（明文头里就有 AES coder）⇒ 数据加密。</summary>
        internal string DataEncrypted => Path.Combine(Directory, "datapw.7z");

        /// <summary><c>-mhe=on</c> 单文件（头被编码 + 解码链里有 AES）⇒ 头加密。</summary>
        internal string HeadersEncrypted => Path.Combine(Directory, "mhe.7z");

        /// <summary>80 个文件、不加密（头被压缩，编码链里只有 LZMA）⇒ 读不出来（"不知道"）。</summary>
        internal string ManyPlain => Path.Combine(Directory, "many-plain.7z");

        /// <summary>
        /// **本组最重要的一条**：80 个文件 + <c>-p</c>（头被压缩，编码链里只有 LZMA、看不见 AES）
        /// ⇒ 读不出来（"不知道"）。⛔ 这一档**绝不许**报"不加密"。
        /// </summary>
        internal string ManyDataEncrypted => Path.Combine(Directory, "many-p.7z");

        /// <summary>80 个文件 + <c>-mhe</c>（头被编码 + 解码链里有 AES）⇒ 头加密。</summary>
        internal string ManyHeadersEncrypted => Path.Combine(Directory, "many-mhe.7z");

        /// <summary>多卷（<c>-v100k</c>）普通包的第 1 卷。</summary>
        internal string? VolumePlainFirst => Existing("vol-plain.7z.001");

        /// <summary>多卷（<c>-v100k</c>）<c>-p</c> 包的第 1 卷。</summary>
        internal string? VolumeDataEncryptedFirst => Existing("vol-p.7z.001");

        /// <summary>多卷（<c>-v100k</c>）<c>-mhe</c> 包的第 1 卷。</summary>
        internal string? VolumeHeadersEncryptedFirst => Existing("vol-mhe.7z.001");

        /// <summary>某个多卷包的第 1 卷对应的**整组卷**（名字升序 = 拼接顺序）。</summary>
        internal IReadOnlyList<string> VolumesOf(string firstVolumePath)
        {
            // 名字前缀 = 去掉末尾的 ".001"（自己按前缀筛，不靠目录通配的 8.3 短名规则）。
            string prefix = Path.GetFileName(firstVolumePath)[..^3];

            return System.IO.Directory
                .GetFiles(Directory)
                .Where(path => Path.GetFileName(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();
        }

        private string? Existing(string fileName)
        {
            string path = Path.Combine(Directory, fileName);

            return File.Exists(path) ? path : null;
        }

        /// <summary>现造一组加密样本。拿不到内置 7z / 造样本失败 → 返回 null（用例跳过并说明原因）。</summary>
        internal static SevenZipEncryptionSampleSet? TryCreate(string workRoot, Action<string> log)
        {
            string? sevenZip = SevenZipFactAttribute.LocateSevenZipPath();

            if (string.IsNullOrEmpty(sevenZip))
            {
                log("测试机上没有内置 7z.exe，7z 加密样本造不出来 —— 本用例跳过。");

                return null;
            }

            string directory = Path.Combine(workRoot, "sevenzip-encryption-samples");
            System.IO.Directory.CreateDirectory(directory);

            try
            {
                Build(sevenZip!, directory);
            }
            catch (Exception ex)
            {
                log("用内置 7z.exe 造 7z 加密样本失败：" + ex.Message);

                return null;
            }

            var created = new SevenZipEncryptionSampleSet(directory, sevenZip!);

            return File.Exists(created.Plain)
                   && File.Exists(created.DataEncrypted)
                   && File.Exists(created.HeadersEncrypted)
                   && File.Exists(created.ManyDataEncrypted)
                ? created
                : null;
        }

        private static void Build(string sevenZip, string directory)
        {
            string source = Path.Combine(directory, "src");
            string many = Path.Combine(directory, "many");

            System.IO.Directory.CreateDirectory(source);
            System.IO.Directory.CreateDirectory(many);

            // 单文件那几档的载荷：256 KiB 随机字节（与用户实测用的样本同规模）。
            var payload = new byte[256 * 1024];
            new Random(20260930).NextBytes(payload);
            File.WriteAllBytes(Path.Combine(directory, "data.bin"), payload);

            /*
             * 80 个长文件名的 .txt：**头比数据更值得压**，7-Zip 于是把 next header 编码掉
             * （实测 many-plain.7z 的 next header 第一字节 = 0x17 = kEncodedHeader）。
             */
            for (int index = 0; index < ManyFileCount; index++)
            {
                string name = $"long-file-name-{index:D3}-{'x' * 40}.txt";
                File.WriteAllText(Path.Combine(many, name), "entry " + index + Environment.NewLine);
            }

            // 多卷的载荷：300 KB 随机字节 ⇒ -v100k 切出 3 卷。
            var volumePayload = new byte[300_000];
            new Random(7).NextBytes(volumePayload);
            File.WriteAllBytes(Path.Combine(directory, "big.bin"), volumePayload);

            string sourceData = Path.Combine(directory, "data.bin");
            string password = "-p" + Password;

            Run(sevenZip, directory, "plain.7z", sourceData);
            Run(sevenZip, directory, password, "datapw.7z", sourceData);
            Run(sevenZip, directory, password, "-mhe=on", "mhe.7z", sourceData);

            Run(sevenZip, directory, "many-plain.7z", Path.Combine(many, "*"));
            Run(sevenZip, directory, password, "many-p.7z", Path.Combine(many, "*"));
            Run(sevenZip, directory, password, "-mhe=on", "many-mhe.7z", Path.Combine(many, "*"));

            // 多卷：元数据在**最后一卷**，所以"只看第 1 卷"读不出 7z 的加密（这正是多卷判据的意义）。
            string big = Path.Combine(directory, "big.bin");

            Run(sevenZip, directory, "-v100k", "vol-plain.7z", big);
            Run(sevenZip, directory, "-v100k", password, "vol-p.7z", big);
            Run(sevenZip, directory, "-v100k", password, "-mhe=on", "vol-mhe.7z", big);
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

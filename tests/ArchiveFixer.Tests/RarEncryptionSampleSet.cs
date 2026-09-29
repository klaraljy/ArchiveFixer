using System;
using System.IO;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// RAR **加密标志**样本的准备（只给测试用，用户 2026-09-29 任务 A）。
    ///
    /// <para><b>为什么必须用真样本</b>：<c>MHD_PASSWORD</c> / <c>LHD_PASSWORD</c> / RAR5 的
    /// "归档加密头"这些判据全部来自格式说明，而"我对说明的理解对不对"只有 WinRAR 自己写出来的字节
    /// 能回答 —— 合成字节只能证明"我按自己的理解造的字节能被自己的解析器读出来"。
    /// 现场实测（2026-09-29，WinRAR 的 <c>Rar.exe</c>）也确实是这么核的：
    /// <c>-ma4 -p…</c> 的主头 flags = <c>0x0000</c>、第一个文件头 flags = <c>0x0024</c>（含 <c>0x0004</c>）；
    /// <c>-ma4 -hp…</c> 的主头 flags = <c>0x0080</c>，其后全是密文；
    /// <c>-ma5 -hp…</c> 签名后第一个头类型 = <c>4</c>（归档加密头）。</para>
    ///
    /// <para>⛔ 样本本体**不入库**（Rar.exe 是共享软件，其许可禁止再分发；AGENTS.md §3）——
    /// 每次都在本机临时目录里现造。本机没有 <c>Rar.exe</c> 时 <see cref="TryCreate"/> 返回 null，
    /// 用例打印原因后**跳过**（⛔ 不许改成"假装跑过"）。</para>
    /// </summary>
    internal sealed class RarEncryptionSampleSet
    {
        /// <summary>测试用密码占位符（**不是**真实密码，AGENTS.md §8）。</summary>
        internal const string Password = "<sample-password>";

        private RarEncryptionSampleSet(string directory, string source)
        {
            Directory = directory;
            Source = source;
        }

        internal string Directory { get; }

        /// <summary>这批样本是怎么来的（"本机已装 WinRAR 现造"）。</summary>
        internal string Source { get; }

        /// <summary>RAR 1.5–4.x（RAR4）不加密对照组。</summary>
        internal string Rar4Plain => Path.Combine(Directory, "r4-plain.rar");

        /// <summary>RAR4、<c>-p</c>：只有文件数据加密（文件名可见）。</summary>
        internal string Rar4DataEncrypted => Path.Combine(Directory, "r4-p.rar");

        /// <summary>RAR4、<c>-hp</c>：连头一起加密。</summary>
        internal string Rar4HeadersEncrypted => Path.Combine(Directory, "r4-hp.rar");

        /// <summary>RAR5 不加密对照组。</summary>
        internal string Rar5Plain => Path.Combine(Directory, "r5-plain.rar");

        /// <summary>RAR5、<c>-p</c>：只有文件数据加密。</summary>
        internal string Rar5DataEncrypted => Path.Combine(Directory, "r5-p.rar");

        /// <summary>RAR5、<c>-hp</c>：连头一起加密。</summary>
        internal string Rar5HeadersEncrypted => Path.Combine(Directory, "r5-hp.rar");

        /// <summary>RAR4、<c>-p</c> + 分卷：第 1 卷（判据就看它）。</summary>
        internal string? Rar4DataEncryptedFirstVolume =>
            Existing("r4-p-vol.part1.rar");

        /// <summary>RAR4、<c>-hp</c> + 分卷：第 1 卷。</summary>
        internal string? Rar4HeadersEncryptedFirstVolume =>
            Existing("r4-hp-vol.part1.rar");

        private string? Existing(string fileName)
        {
            string path = Path.Combine(Directory, fileName);

            return File.Exists(path) ? path : null;
        }

        /// <summary>
        /// 现造一组加密样本。拿不到 <c>Rar.exe</c> / 造样本失败 → 返回 null（用例跳过并说明原因）。
        /// </summary>
        internal static RarEncryptionSampleSet? TryCreate(string workRoot, Action<string> log)
        {
            string? rarExe = RarSampleSet.LocateRarExe();

            if (rarExe == null)
            {
                log("本机没有 Rar.exe（WinRAR），RAR 加密样本造不出来 —— 本用例跳过。");
                return null;
            }

            string directory = Path.Combine(workRoot, "rar-encryption-samples");
            System.IO.Directory.CreateDirectory(directory);

            try
            {
                Build(rarExe, directory, log);
            }
            catch (Exception ex)
            {
                log("用本机 Rar.exe 造 RAR 加密样本失败：" + ex.Message);
                return null;
            }

            var created = new RarEncryptionSampleSet(directory, "本机已装 WinRAR 现造（" + rarExe + "）");

            return File.Exists(created.Rar4Plain) && File.Exists(created.Rar5Plain) ? created : null;
        }

        private static void Build(string rarExe, string directory, Action<string> log)
        {
            string source = Path.Combine(directory, "src");

            System.IO.Directory.CreateDirectory(source);

            File.WriteAllText(Path.Combine(source, "readme.txt"), "ArchiveFixer RAR encryption sample" + Environment.NewLine);
            File.WriteAllText(Path.Combine(source, "中文名.txt"), "unicode name entry" + Environment.NewLine);

            // 分卷用的载荷：随机字节（不可压缩），-m0 保证真的被切成多卷。
            var payload = new byte[60_000];
            new Random(20260929).NextBytes(payload);
            File.WriteAllBytes(Path.Combine(directory, "payload.bin"), payload);

            string password = "-p" + Password;
            string passwordHeaders = "-hp" + Password;

            BuildOne(rarExe, directory, log, "r4-plain.rar", "-ma4", null, Path.Combine(source, "*"));
            BuildOne(rarExe, directory, log, "r4-p.rar", "-ma4", password, Path.Combine(source, "*"));
            BuildOne(rarExe, directory, log, "r4-hp.rar", "-ma4", passwordHeaders, Path.Combine(source, "*"));
            BuildOne(rarExe, directory, log, "r5-plain.rar", "-ma5", null, Path.Combine(source, "*"));
            BuildOne(rarExe, directory, log, "r5-p.rar", "-ma5", password, Path.Combine(source, "*"));
            BuildOne(rarExe, directory, log, "r5-hp.rar", "-ma5", passwordHeaders, Path.Combine(source, "*"));

            // 分卷：第 1 卷才可能带主头 / 第一个文件头，判据也只看它。
            string[] volumeInputs =
            {
                Path.Combine(directory, "payload.bin"),
                Path.Combine(source, "readme.txt")
            };

            string[] volumeArgs = { "-m0", "-v10k" };

            BuildOne(rarExe, directory, log, "r4-p-vol.rar", "-ma4", password, volumeInputs, volumeArgs);

            BuildOne(rarExe, directory, log, "r4-hp-vol.rar", "-ma4", passwordHeaders, volumeInputs, volumeArgs);
        }

        private static void BuildOne(
            string rarExe,
            string directory,
            Action<string> log,
            string archiveName,
            string formatSwitch,
            string? passwordSwitch,
            params string[] inputs)
        {
            BuildOne(rarExe, directory, log, archiveName, formatSwitch, passwordSwitch, inputs, Array.Empty<string>());
        }

        private static void BuildOne(
            string rarExe,
            string directory,
            Action<string> log,
            string archiveName,
            string formatSwitch,
            string? passwordSwitch,
            string[] inputs,
            string[] extraArgs)
        {
            var args = new System.Collections.Generic.List<string>
            {
                "a",
                formatSwitch,
                "-ep1",
                "-idq",
                "-r"
            };

            args.AddRange(extraArgs);

            if (!string.IsNullOrEmpty(passwordSwitch))
            {
                args.Add(passwordSwitch!);
            }

            args.Add(Path.Combine(directory, archiveName));
            args.AddRange(inputs);

            RarSampleSet.RunRar(rarExe, directory, log, args.ToArray());
        }
    }
}

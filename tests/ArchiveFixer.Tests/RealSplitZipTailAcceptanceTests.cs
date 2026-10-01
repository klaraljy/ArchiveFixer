using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ArchiveFixer.Detection;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **真样本**：用户真机上那对"内层跨盘 zip"（`222.zi删除p` + `222.z0sc1`，2026-10-01）。
    ///
    /// <para>为什么必须有真样本用例：这一对的形状（末片**没有本地文件头**、只有中央目录 + EOCD）是
    /// 合成样本最容易"自己验自己"的地方 —— 我按理解的字节写一个，再用自己的解析器读，两边一起错也发现不了。
    /// 真样本才有资格回答"真实产品写出来的字节，我读得对不对"。</para>
    ///
    /// <para><b>怎么给样本</b>（⛔ 样本本体绝不进仓库、原目录只读）：把那一对拷到一个目录里，然后设环境变量
    /// <c>ARCHIVEFIXER_REAL_SPLITTAIL_DIR</c> 指向它；或者放在仓库同级的
    /// <c>_tmp\ArchiveFixer\split-tail-real\</c>。两种都没有就**跳过并说明**（⛔ 不伪装成验过）。</para>
    ///
    /// <para><b>钉两条</b>：① 末片必须被认成 <c>ZIP_SPANNED</c>（不是 `Unknown`）；
    /// ② 定稿前那一组必须被改成标准卷名 <c>X.zip</c> + <c>X.z01</c>，改完 7-Zip 真的解得开 ——
    /// 用例只动自己复制出来的副本，样本原件一个字节都不碰。</para>
    /// </summary>
    public sealed class RealSplitZipTailAcceptanceTests : IDisposable
    {
        /// <summary>样本目录的环境变量名（与 <c>ARCHIVEFIXER_REAL_SAMPLE_DIR</c> 同一套约定）。</summary>
        internal const string SampleDirectoryEnvironmentVariable = "ARCHIVEFIXER_REAL_SPLITTAIL_DIR";

        private readonly string _root;

        public RealSplitZipTailAcceptanceTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerSplitTailReal-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch
            {
                // 临时目录清不掉不影响结论。
            }
        }

        [RealSplitZipTailSampleFact]
        public void 真机那一对_末片认得出且整组能改成标准卷名并被七Zip解开()
        {
            string sampleDirectory = ResolveSampleDirectory()!;
            string tail = Path.Combine(sampleDirectory, "222.zi删除p");
            string firstPart = Path.Combine(sampleDirectory, "222.z0sc1");

            // 只动副本：把原件拷进临时目录。
            string stage = Path.Combine(_root, "stage");
            Directory.CreateDirectory(stage);

            string copiedTail = Path.Combine(stage, "222.zi删除p");
            string copiedFirst = Path.Combine(stage, "222.z0sc1");

            File.Copy(tail, copiedTail, overwrite: true);
            File.Copy(firstPart, copiedFirst, overwrite: true);

            // ① 识别：末片必须被认成跨盘 zip（而不是"按内容认不出是归档"）。
            DetectResult detected = new ArchiveDetectService().DetectAsync(copiedTail).GetAwaiter().GetResult();

            Assert.True(detected.IsArchive, $"末片必须被认成归档，实际 {detected.Format}：{detected.Message}");
            Assert.Equal("ZIP_SPANNED", detected.Format);

            long tailSizeBefore = new FileInfo(copiedTail).Length;

            // ② 定稿前整组改名：必须变成 222.zip + 222.z01。
            ExtractionCoordinator.FinalLayoutPlan plan = ExtractionCoordinator.PlanFinalLayout(
                stage,
                Path.Combine(_root, "out"),
                sharedOutputRoot: false,
                "222.zscip");

            Assert.False(plan.Failed, plan.FailureReason);
            Assert.True(File.Exists(Path.Combine(stage, "222.zip")), "末片必须被改成 222.zip");
            Assert.True(File.Exists(Path.Combine(stage, "222.z01")), "第 1 片必须被改成 222.z01");
            Assert.False(File.Exists(copiedFirst), "旧名不该还在");

            // ⛔ 只改名字：字节数一个都不许变。
            Assert.Equal(tailSizeBefore, new FileInfo(Path.Combine(stage, "222.zip")).Length);
            Assert.Equal(
                new FileInfo(copiedFirst).Length,
                new FileInfo(Path.Combine(stage, "222.z01")).Length);

            // ⛔ 刚改过名的那一组是内容物，不许进"可删的其余物"。
            Assert.DoesNotContain(
                plan.ProcessArtifactSources,
                path => path.EndsWith("222.zip", StringComparison.OrdinalIgnoreCase));

            // ③ 端到端：改完名字之后 7-Zip 真的解得开这一组（这是用户关心的那一句）。
            string sevenZip = LocateSevenZip();
            string extractTo = Path.Combine(_root, "extracted");
            Directory.CreateDirectory(extractTo);

            (int exitCode, string output) = Run(sevenZip, new[]
            {
                "x",
                "-y",
                "-o" + extractTo,
                Path.Combine(stage, "222.zip")
            });

            Assert.True(
                exitCode == 0,
                $"改完标准卷名之后 7-Zip 必须解得开（退出码 {exitCode}）：{output}");

            List<string> extracted = Directory
                .GetFiles(extractTo, "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName)
                .Where(name => name != null && name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                .Select(name => name!)
                .ToList();

            Assert.NotEmpty(extracted);
        }

        // ================================================================ 辅助

        /// <summary>样本目录：环境变量优先，其次 <c>_tmp\ArchiveFixer\split-tail-real</c>。</summary>
        internal static string? ResolveSampleDirectory()
        {
            string configured = Environment.GetEnvironmentVariable(SampleDirectoryEnvironmentVariable) ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(configured) && HasPair(configured))
            {
                return configured;
            }

            string fallback = ResolveFallbackSampleDirectory();

            return HasPair(fallback) ? fallback : null;
        }

        private static bool HasPair(string directory)
        {
            try
            {
                return Directory.Exists(directory)
                    && File.Exists(Path.Combine(directory, "222.zi删除p"))
                    && File.Exists(Path.Combine(directory, "222.z0sc1"));
            }
            catch
            {
                return false;
            }
        }

        private static string ResolveFallbackSampleDirectory()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ArchiveFixer.slnx")))
                {
                    string parent = directory.Parent?.FullName ?? directory.FullName;

                    return Path.Combine(parent, "_tmp", "ArchiveFixer", "split-tail-real");
                }

                directory = directory.Parent;
            }

            return string.Empty;
        }

        private static string LocateSevenZip() =>
            SevenZipFactAttribute.LocateSevenZipPath()
            ?? throw new InvalidOperationException("测试机上没有可用的 7z.exe");

        private static (int ExitCode, string Output) Run(string exe, IEnumerable<string> arguments)
        {
            var info = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (string argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            using Process process = Process.Start(info)!;

            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();

            process.WaitForExit();

            return (process.ExitCode, output);
        }
    }

    /// <summary>
    /// 真样本开关：样本不在（没设环境变量、也没有 <c>_tmp\ArchiveFixer\split-tail-real\</c>）就**发现阶段跳过**，
    /// ⛔ 不伪装成"验过"。没有可用的 7z 时也跳过（这一条要真解压）。
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class RealSplitZipTailSampleFactAttribute : FactAttribute
    {
        public RealSplitZipTailSampleFactAttribute()
        {
            if (string.IsNullOrEmpty(SevenZipFactAttribute.LocateSevenZipPath()))
            {
                Skip = "测试机上没有可用的 7z.exe，跳过真样本用例。";

                return;
            }

            if (RealSplitZipTailAcceptanceTests.ResolveSampleDirectory() == null)
            {
                Skip = $"真样本不在（没有 {RealSplitZipTailAcceptanceTests.SampleDirectoryEnvironmentVariable}，"
                    + @"也没有 _tmp\ArchiveFixer\split-tail-real\ 里的 222.zi删除p + 222.z0sc1），跳过。";
            }
        }
    }
}

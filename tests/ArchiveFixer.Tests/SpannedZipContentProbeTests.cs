using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ArchiveFixer.Extraction;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **真样本端到端**：跨盘 zip 的盘号识别 → 改名计划 → 真的改完 → 内置 7z 认这一组。
    ///
    /// <para><b>样本怎么造的</b>（⛔ 只用项目内置的 7z.exe，不下载任何东西）：</para>
    /// <list type="number">
    /// <item><description><c>7z a -tzip -mx0 -v1m sp2.zip data.bin</c> 切出两片（7z 的 <c>-v</c> 是**裸切**，
    /// 末片的 EOCD 里盘号还是 0）；</description></item>
    /// <item><description>按真 PKZIP 跨盘的样子补两处：末片 EOCD 的"本盘号"改成 1（= 第 2 片、总共 2 片）、
    /// 首片结尾加上跨盘标记 <c>PK\x07\x08</c> —— 这台机器上没有任何工具能写真正的 PKZIP 跨盘
    /// （WinRAR / Info-ZIP 都没有，也不许装），所以样本是"真 7z 切出来的字节 + 两处按格式补的盘号信息"。</description></item>
    /// <item><description>把整组名字改烂（首片缀上「删除」），再走产品代码：内容定序 → 改名 → 引擎开这一组。</description></item>
    /// </list>
    ///
    /// <para>⚠ 断言到"引擎能打开这一组并列出里面的条目"为止：这个**合成**跨盘样本的数据偏移对不上
    /// （7z 会报 <c>Headers Error</c> 警告），所以**不**拿"解出来的字节一致"当断言 —— 那是样本合成的锅，
    /// 不是判据的锅。真机上的 PKZIP 跨盘包没有这个问题。</para>
    /// </summary>
    public class SpannedZipContentProbeTests : IDisposable
    {
        private const int PayloadBytes = 1572864; // 1.5 MiB：-v1m 正好切两片

        private readonly string _root;

        public SpannedZipContentProbeTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerZipSpan", Guid.NewGuid().ToString("N"));
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
                // 清不掉只是脏一点。
            }
        }

        [SevenZipFact]
        public async Task 真七z切出来的两片_补上盘号后内容认得出_改名之后引擎能打开这一组()
        {
            string directory = Path.Combine(_root, "span");
            Directory.CreateDirectory(directory);

            string payload = Path.Combine(directory, "data.bin");
            var bytes = new byte[PayloadBytes];
            new Random(20260929).NextBytes(bytes);
            File.WriteAllBytes(payload, bytes);

            Run7z(directory, "a", "-tzip", "-mx0", "-v1m", "sp2.zip", "data.bin");

            Assert.True(File.Exists(Path.Combine(directory, "sp2.zip.001")), "样本不是两片");
            Assert.True(File.Exists(Path.Combine(directory, "sp2.zip.002")), "样本不是两片");

            // ① 名字改烂：末片还叫 sp2.zip（跨盘 zip 的入口本来就是它），首片缀上「删除」
            string first = Path.Combine(directory, "sp2.zip.001删除");
            string tail = Path.Combine(directory, "sp2.zip");

            File.Move(Path.Combine(directory, "sp2.zip.001"), first);
            File.Move(Path.Combine(directory, "sp2.zip.002"), tail);

            // ② 按真跨盘的样子补盘号信息（见类注释）
            PatchTailDiskNumber(tail, diskNumber: 1);
            AppendSpanningMarker(first);

            // ③ 名字改烂之后，引擎连这一组都打不开（改名之前的样子）
            Assert.False(CanListEntry(directory, tail), "名字坏着的时候引擎不该能打开它");

            // ④ 走产品代码：内容定序 → 改名计划 → 真的改
            VolumeNameRepairPlan plan = await VolumeNameRepair.PlanByContentAsync(
                tail,
                VolumeNameRepair.EnumerateVolumeCandidatesInDirectory(tail),
                engine: new Engines.SevenZip.SevenZipEngine());

            Assert.True(plan.CanRepair, plan.Reason);
            Assert.Equal(new[] { "sp2.z01", "sp2.zip" }, plan.Items.Select(i => i.SuggestedFileName).ToArray());

            VolumeNameRepairResult result = VolumeNameRepair.TryApply(plan);

            Assert.True(result.Success, result.Message);
            Assert.True(File.Exists(Path.Combine(directory, "sp2.z01")));
            Assert.True(File.Exists(tail));

            // ⑤ 改完名字，引擎真的认得这一组了（列得出里面的条目）
            Assert.True(CanListEntry(directory, tail), "改完名字之后引擎还是打不开这一组");
        }

        // ── 样本加工 ──

        /// <summary>把末片 EOCD 里的"本盘号"改成指定值（0 起；注释长度为 0 时 EOCD 就在文件最后 22 字节）。</summary>
        private static void PatchTailDiskNumber(string tailPath, ushort diskNumber)
        {
            byte[] data = File.ReadAllBytes(tailPath);
            int eocd = data.Length - 22;

            Assert.True(
                data[eocd] == 0x50 && data[eocd + 1] == 0x4B && data[eocd + 2] == 0x05 && data[eocd + 3] == 0x06,
                "末片结尾不是 EOCD（样本形状变了）");

            data[eocd + 4] = (byte)(diskNumber & 0xFF);
            data[eocd + 5] = (byte)(diskNumber >> 8);

            File.WriteAllBytes(tailPath, data);
        }

        /// <summary>非末片结尾的跨盘标记（真 PKZIP 跨盘就这么标"后面还有片"）。</summary>
        private static void AppendSpanningMarker(string segmentPath)
        {
            byte[] data = File.ReadAllBytes(segmentPath);
            File.WriteAllBytes(segmentPath, data.Concat(new byte[] { 0x50, 0x4B, 0x07, 0x08 }).ToArray());
        }

        /// <summary>内置 7z 能不能从这一片打开整组、并列出里面的 <c>data.bin</c>。</summary>
        private static bool CanListEntry(string workingDirectory, string path)
        {
            string sevenZip = SevenZipFactAttribute.LocateSevenZipPath();

            Assert.False(string.IsNullOrEmpty(sevenZip), "测试机上没有 7z.exe");

            var psi = new ProcessStartInfo(sevenZip)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDirectory
            };

            psi.ArgumentList.Add("l");
            psi.ArgumentList.Add(path);

            using Process process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 7z.exe");

            string stdout = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();

            process.WaitForExit(120_000);

            // ⚠ 只看"列没列出条目"：这个合成样本会带 Headers Error 警告（退出码 2），那是样本的锅。
            return stdout.Contains("data.bin", StringComparison.Ordinal)
                && stdout.Contains("Multivolume = +", StringComparison.Ordinal);
        }

        private static void Run7z(string workingDirectory, params string[] args)
        {
            string sevenZip = SevenZipFactAttribute.LocateSevenZipPath();

            var psi = new ProcessStartInfo(sevenZip)
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

            using Process process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 7z.exe");

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();

            Assert.True(process.WaitForExit(120_000), "7z 超时");
            Assert.True(process.ExitCode == 0, $"7z 失败：{stdout}{stderr}");
        }
    }
}

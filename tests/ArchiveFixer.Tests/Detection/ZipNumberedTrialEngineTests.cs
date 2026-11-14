using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Detection;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// ZIP 通用分片 4 片、中间片名字被换掉时，必须靠硬链接试开确认顺序；这条用例真的调 7-Zip，
    /// 不是只测排列生成。⛔ 不改用户文件：整个夹具都在临时目录。
    /// </summary>
    public sealed class ZipNumberedTrialEngineTests
    {
        [SevenZipFact]
        public async Task 四到五片zip_名字不标准_试开能确认顺序()
        {
            string sevenZip = SevenZipFactAttribute.LocateSevenZipPath()!;
            string root = Path.Combine(Path.GetTempPath(), "af-zip-trial-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            try
            {
                string payload = Path.Combine(root, "payload.bin");
                byte[] bytes = new byte[3 * 1024 * 1024];
                new Random(17).NextBytes(bytes);
                File.WriteAllBytes(payload, bytes);

                string archive = Path.Combine(root, "archive.zip");
                var info = new ProcessStartInfo(sevenZip)
                {
                    WorkingDirectory = root,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                info.ArgumentList.Add("a");
                info.ArgumentList.Add("-tzip");
                info.ArgumentList.Add("-mx=0");
                info.ArgumentList.Add("-v1m");
                info.ArgumentList.Add(archive);
                info.ArgumentList.Add(payload);

                using (Process? process = Process.Start(info))
                {
                    Assert.NotNull(process);
                    process!.WaitForExit(60_000);
                    Assert.Equal(0, process.ExitCode);
                }

                List<string> pieces = Directory.GetFiles(root, "archive.zip.*")
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                Assert.InRange(pieces.Count, 4, 5);

                // 打乱中间片的名字，只保留首片和末片可辨认；内容不动。
                for (int index = 1; index < pieces.Count - 1; index++)
                {
                    string renamed = Path.Combine(root, $"middle-{index}.bin");
                    File.Move(pieces[index], renamed);
                    pieces[index] = renamed;
                }

                string tail = pieces[^1];
                IReadOnlyList<IReadOnlyList<string>> orders = VolumeOrderTrial.BuildCandidateOrders(
                    pieces,
                    tail,
                    VolumeNamingFamily.ZipNumbered,
                    "archive");
                Assert.NotEmpty(orders);

                var verifier = new VolumeProbeVerifier(new SevenZipEngine());
                VolumeProbeOutcome outcome = await verifier.VerifyNumberedZipOrderAsync(
                    orders,
                    Path.Combine(root, ".ArchiveFixer.work"),
                    CancellationToken.None);

                Assert.True(outcome.Confirmed, outcome.Reason);
                Assert.Equal(pieces.Count, outcome.OrderedVolumes.Count);
            }
            finally
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch
                {
                }
            }
        }
    }
}

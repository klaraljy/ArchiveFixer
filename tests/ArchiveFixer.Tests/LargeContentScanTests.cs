using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ArchiveFixer.Detection;
using ArchiveFixer.Tests.Batch120;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 4,000 个普通图片作为产物时，扫描只做轻量归档判定：
    /// 不允许对每个图片再走内容识别/FileNature 全量事实。
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public sealed class LargeContentScanTests
    {
        private const int ContentFileCount = 4000;

        [Fact]
        public async Task 大批内容物_不逐张做内容识别()
        {
            const string sampleId = "999000098";
            string sampleDir = Path.Combine(Batch120Operation.BackupBatchRoot, sampleId);
            string sourceDir = Path.Combine(sampleDir, "原包");
            string sourceZip = Path.Combine(sourceDir, "内容包.zip");

            if (Directory.Exists(sampleDir))
            {
                Directory.Delete(sampleDir, recursive: true);
            }

            Directory.CreateDirectory(sourceDir);
            WriteContentZip(sourceZip, ContentFileCount);

            var recipe = new JsonObject
            {
                ["sampleId"] = sampleId,
                ["category"] = "Normal",
                ["kind"] = "normal",
                ["scenario"] = new JsonObject
                {
                    ["placement"] = "Original",
                    ["sourceHandling"] = "KeepInPlace",
                    ["contentHandling"] = "Keep"
                }
            };

            File.WriteAllText(
                Path.Combine(sampleDir, "recipe.json"),
                recipe.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));

            ContentAdmission.ResetProbeCallsForTests();

            Batch120RunRecord record = await Batch120Operation.RunOneAsync(sampleId);

            try
            {
                Assert.True(string.IsNullOrEmpty(record.Error), "一键处理报错：" + record.Error);
                Assert.Single(record.Tasks);
                Assert.NotEqual("Failed", record.Tasks[0].Outcome);
                Assert.NotEqual("Pending", record.Tasks[0].Outcome);
                Assert.True(
                    record.After.Count(item => item.Path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    >= ContentFileCount,
                    ContentFileCount + " 个 PNG 没有全部落地");

                /*
                 * 旧的全量内容闸门会在 OneClick 产物扫描里对 1000 个 PNG 逐个 Probe；
                 * 现在只允许源包/同基名候选级别的少量探测。
                 */
                Assert.True(
                    ContentAdmission.ProbeCalls < 100,
                    "普通内容物被内容识别了：" + ContentAdmission.ProbeCalls + " 次");
            }
            finally
            {
                try
                {
                    if (Directory.Exists(sampleDir))
                    {
                        Directory.Delete(sampleDir, recursive: true);
                    }
                }
                catch
                {
                }

                ContentAdmission.ResetProbeCallsForTests();
            }
        }

        private static void WriteContentZip(string path, int count)
        {
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Create);

            byte[] png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

            for (int index = 0; index < count; index++)
            {
                ZipArchiveEntry entry = zip.CreateEntry("内容-" + index + ".png", CompressionLevel.NoCompression);
                using Stream output = entry.Open();
                output.Write(png, 0, png.Length);
            }
        }
    }
}

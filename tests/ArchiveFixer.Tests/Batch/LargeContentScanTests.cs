using ArchiveFixer.Detection;
using ArchiveFixer.Services;
using ArchiveFixer.Tests.Batch120;
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace ArchiveFixer.Tests;

/// <summary>
/// 4,000 个普通图片作为产物时，扫描只做轻量归档判定：
/// 不允许对每个图片再走内容识别/FileNature 全量事实。
/// </summary>
[Collection("ArchiveFixerGlobalState")]
[Trait("Category", "Stress")]
public sealed class LargeContentScanTests
{
    private const int ContentFileCount = 4000;

    [Fact]
    public async Task 大批内容物_不逐张做内容识别()
    {
        const string sampleId = "999000098";
        string sampleRoot = Path.Combine(TestPaths.TempRoot, "large-content-" + Guid.NewGuid().ToString("N"));
        string sampleDir = Path.Combine(sampleRoot, "backup", sampleId);
        string sourceDir = Path.Combine(sampleDir, "原包");
        string sourceZip = Path.Combine(sourceDir, "内容包.zip");
        string runRoot = Path.Combine(sampleRoot, "run");

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
        ArchiveDetectService.ResetProbeCallsForTests();

        Batch120RunRecord record = await Batch120Operation.RunOneAsync(
            sampleId,
            Path.Combine(sampleRoot, "backup"),
            Path.Combine(runRoot, "测试位置"),
            Path.Combine(runRoot, "指定位置"),
            Path.Combine(runRoot, "日志"));
        long archiveProbeCalls = ArchiveDetectService.ProbeCalls;

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

            Assert.True(
                archiveProbeCalls < 100,
                "普通内容物被逐文件归档探测了：" + archiveProbeCalls + " 次");
        }
        finally
        {
            try
            {
                if (Directory.Exists(sampleRoot))
                {
                    Directory.Delete(sampleRoot, recursive: true);
                }
            }
            catch
            {
            }

            ContentAdmission.ResetProbeCallsForTests();
            ArchiveDetectService.ResetProbeCallsForTests();
        }
    }

    private static void WriteContentZip(string path, int count)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);

        // 合法 1x1 PNG；8 字节魔数桩会被 FileNature 判成 Unknown，不能代表普通图片。
        byte[] png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

        for (int index = 0; index < count; index++)
        {
            ZipArchiveEntry entry = zip.CreateEntry("内容-" + index + ".png", CompressionLevel.NoCompression);
            using Stream output = entry.Open();
            output.Write(png, 0, png.Length);
        }
    }
}

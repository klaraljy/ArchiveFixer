using System;
using System.IO;
using System.Linq;
using ArchiveFixer.Extraction;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 「整组名字改回标准名」（用户 2026-09-28：网盘给每卷缀了「删除」，只改第一卷没用）。
    ///
    /// 真机现场：`giu910.7z.001删除 / .002删除 / .003删除` —— 三卷都在、内容完好，
    /// 只是 7-Zip 按 `.002` 找卷时对不上名字。这一组用例钉住：**整组一起改**、
    /// **只删尾巴不动卷号**、**目标被占就整组不改**、**改完字节数一个不差**。
    /// </summary>
    public class VolumeNameRepairGroupTests : IDisposable
    {
        private readonly string _dir;

        public VolumeNameRepairGroupTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "af-volrepair-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_dir))
                {
                    Directory.Delete(_dir, recursive: true);
                }
            }
            catch
            {
                // 清理失败不影响判据
            }
        }

        private string Make(string name, int size = 16)
        {
            string path = Path.Combine(_dir, name);
            File.WriteAllBytes(path, new byte[size]);
            return path;
        }

        private string[] AllNames() =>
            Directory.GetFiles(_dir).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray()!;

        [Fact]
        public void 整组带垃圾尾巴_计划里三卷一起改_只删尾巴不动卷号()
        {
            Make("giu910.7z.001删除");
            Make("giu910.7z.002删除");
            Make("giu910.7z.003删除");

            VolumeNameRepairPlan plan = VolumeNameRepair.Plan(
                Path.Combine(_dir, "giu910.7z.001删除"),
                VolumeNameRepair.EnumerateFileNamesInDirectory(Path.Combine(_dir, "giu910.7z.001删除")));

            Assert.True(plan.CanRepair);
            Assert.Equal(3, plan.Items.Count);
            Assert.Equal(
                new[] { "giu910.7z.001", "giu910.7z.002", "giu910.7z.003" },
                plan.Items.Select(i => i.SuggestedFileName).ToArray());
            Assert.Contains("3 卷", plan.Describe());
        }

        [Fact]
        public void 整组带垃圾尾巴_一次点下去整组都改好_字节数一个不差()
        {
            Make("giu910.7z.001删除", 128);
            Make("giu910.7z.002删除", 256);
            Make("giu910.7z.003删除", 64);

            string first = Path.Combine(_dir, "giu910.7z.001删除");
            VolumeNameRepairPlan plan = VolumeNameRepair.Plan(first, VolumeNameRepair.EnumerateFileNamesInDirectory(first));

            VolumeNameRepairResult result = VolumeNameRepair.TryApply(plan);

            Assert.True(result.Success);
            Assert.Contains("整组 3 卷", result.Message);
            Assert.Equal(
                new[] { "giu910.7z.001", "giu910.7z.002", "giu910.7z.003" },
                AllNames());
            Assert.Equal(128, new FileInfo(Path.Combine(_dir, "giu910.7z.001")).Length);
            Assert.Equal(256, new FileInfo(Path.Combine(_dir, "giu910.7z.002")).Length);
            Assert.Equal(64, new FileInfo(Path.Combine(_dir, "giu910.7z.003")).Length);
        }

        [Fact]
        public void 只改自己那一组_别的文件一个字节都不动()
        {
            Make("giu910.7z.001删除");
            Make("giu910.7z.002删除");
            Make("other.7z.001删除");   // 另一组，同样带垃圾 —— 不该被这次改到
            Make("readme.txt");

            string first = Path.Combine(_dir, "giu910.7z.001删除");
            VolumeNameRepairPlan plan = VolumeNameRepair.Plan(first, VolumeNameRepair.EnumerateFileNamesInDirectory(first));

            Assert.True(plan.CanRepair);
            Assert.Equal(2, plan.Items.Count);

            Assert.True(VolumeNameRepair.TryApply(plan).Success);
            Assert.Contains("other.7z.001删除", AllNames());
            Assert.Contains("readme.txt", AllNames());
        }

        [Fact]
        public void 目标名被占用_那一卷不动_别的卷照旧各改各的()
        {
            Make("giu910.7z.001删除");
            Make("giu910.7z.002删除");
            Make("giu910.7z.002");      // ⛔ `.002` 的目标名被占

            string first = Path.Combine(_dir, "giu910.7z.001删除");
            VolumeNameRepairPlan plan = VolumeNameRepair.Plan(first, VolumeNameRepair.EnumerateFileNamesInDirectory(first));

            /*
             * ⚠ 按用户 2026-10-06 的新指令改口径（旧断言是「整组一个都不改」）：
             * 改名是**一件一件各自可证**的 —— `.002` 的目标名被占 ⇒ **那一卷不动**（⛔ 绝不覆盖），
             * 而 `.001删除` 的目标名空着 ⇒ 它照样改回 `giu910.7z.001`。
             * ⛔ 红线不变：**任何被占的目标名都不许被覆盖**。
             */
            Assert.True(plan.CanRepair, plan.Describe());
            Assert.Equal("giu910.7z.001", plan.SuggestedFileName);
            Assert.Single(plan.Items);
            Assert.DoesNotContain(plan.Items, item => item.CurrentFileName.Contains("002", StringComparison.Ordinal));

            Assert.True(VolumeNameRepair.TryApply(plan).Success);

            Assert.Contains("giu910.7z.001", AllNames());
            Assert.Contains("giu910.7z.002", AllNames());
            Assert.Contains("giu910.7z.002删除", AllNames());   // 目标被占 ⇒ 这一卷原样不动
        }

        [Fact]
        public void 名字本来就标准_不给计划()
        {
            Make("giu910.7z.001");
            Make("giu910.7z.002");

            string first = Path.Combine(_dir, "giu910.7z.001");
            VolumeNameRepairPlan plan = VolumeNameRepair.Plan(first, VolumeNameRepair.EnumerateFileNamesInDirectory(first));

            Assert.False(plan.CanRepair);
        }

        [Fact]
        public void 第一卷名字被改坏那一档_照旧只改一卷_行为不变()
        {
            // 老现场：第一卷叫 x.7z(删掉.001 —— 没有"卷号 + 垃圾"的形状，走老路（单卷）
            Make("x.7z");
            Make("x.7z.002");
            Make("x.7z.003");

            string first = Path.Combine(_dir, "x.7z");
            VolumeNameRepairPlan plan = VolumeNameRepair.Plan(first, VolumeNameRepair.EnumerateFileNamesInDirectory(first));

            if (plan.CanRepair)
            {
                Assert.Single(plan.Items);
                Assert.True(VolumeNameRepair.TryApply(plan).Success);
            }
            else
            {
                // 推不出标准名时也必须是"如实拒绝"，不许瞎改
                Assert.False(string.IsNullOrWhiteSpace(plan.Reason));
            }
        }
    }
}

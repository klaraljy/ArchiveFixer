using System;
using System.IO;
using ArchiveFixer.Extraction;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 整组改名必须"全成或全不成"。
    /// <para>真机现场（2026-09-30，用户 876 行日志）：日志写「改名没成功：The process cannot access the file…」，
    /// 盘上却已经出现 `111.part1.002` —— 半改状态把用户的名字改成七零八落，7-Zip 反而整组都打不开。
    /// 旧行为是"停下并报已改了几卷（已改的那几卷不会再动）"；现在必须**倒序改回原名**。</para>
    /// </summary>
    public sealed class VolumeNameRepairRollbackTests : IDisposable
    {
        private readonly string _dir;

        public VolumeNameRepairRollbackTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "af-volrepair-rollback-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch
            {
                // 清理失败不影响用例结论。
            }
        }

        [Fact]
        public void 整组改名中途失败_已经改过的那几卷必须改回原名()
        {
            string first = Path.Combine(_dir, "set.part删1.ra除r");
            string second = Path.Combine(_dir, "set.part删2.ra除r");
            string firstTarget = Path.Combine(_dir, "set.part1.rar");
            string secondTarget = Path.Combine(_dir, "set.part2.rar");

            File.WriteAllBytes(first, new byte[16]);
            File.WriteAllBytes(second, new byte[16]);

            // 第二卷的目标名已经被占 ⇒ 改名必然中途失败（真机上对应"文件被别的进程占用 / 目标抢先被建"）。
            File.WriteAllBytes(secondTarget, new byte[4]);

            var plan = new VolumeNameRepairPlan
            {
                CanRepair = true,
                CurrentPath = first,
                CurrentFileName = Path.GetFileName(first),
                SuggestedFileName = Path.GetFileName(firstTarget),
                TargetPath = firstTarget,
                Items = new[]
                {
                    new VolumeRepairItem
                    {
                        CurrentPath = first,
                        CurrentFileName = Path.GetFileName(first),
                        SuggestedFileName = Path.GetFileName(firstTarget),
                        TargetPath = firstTarget
                    },
                    new VolumeRepairItem
                    {
                        CurrentPath = second,
                        CurrentFileName = Path.GetFileName(second),
                        SuggestedFileName = Path.GetFileName(secondTarget),
                        TargetPath = secondTarget
                    }
                }
            };

            VolumeNameRepairResult result = VolumeNameRepair.TryApply(plan);

            Assert.False(result.Success);
            Assert.True(File.Exists(first), "第一卷必须已经改回原名");
            Assert.False(File.Exists(firstTarget), "⛔ 不许留下半改状态（改过的名字还在）");
            Assert.True(File.Exists(second), "第二卷本来就没改成，必须原地不动");
            Assert.Contains("改回原样", result.Message);
        }
    }
}

using ArchiveFixer.Extraction;
using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 内层续解链的整组改名必须**全成或全不成**（与 `VolumeNameRepair` 同一口径）。
    ///
    /// <para>真机现场（2026-09-30）：日志写「改名没成功」，盘上却已经出现 `111.part1.002` ——
    /// 半改状态比不改更糟：7-Zip 按新基名去找后续卷，名字七零八落时整组都打不开。
    /// `VolumeNameRepair.TryApply` 的同一处缺口 2026-10-01 已修（见它的 `Undo`）；
    /// <b>这一处是当时记下的残留</b> —— 它的 `Rollback` 原来把临时名改回**目标名**
    /// （= 接着把改名做完），而不是改回**原名**，方法注释却写着"原样改回去"。</para>
    /// </summary>
    public class BrokenVolumeChainRepairRollbackTests : IDisposable
    {
        private readonly string _dir;

        public BrokenVolumeChainRepairRollbackTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ArchiveFixerVolumeChainRollback", Guid.NewGuid().ToString("N"));
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
                // 清理失败不影响用例结论。
            }
        }

        [Fact]
        public void 整组改名中途失败_已经动过的名字必须全部改回原名()
        {
            // 真机那个形状：第一卷的名字被改坏（打包者塞了字、吃掉右括号），后续卷名字正常。
            string first = Path.Combine(_dir, "set.7z(删掉.001");
            string second = Path.Combine(_dir, "set.7z.002");
            string third = Path.Combine(_dir, "set.7z.004");

            const string FirstTarget = "set.7z.001";
            const string SecondTarget = "set.7z.003";

            File.WriteAllBytes(first, new byte[16]);
            File.WriteAllBytes(second, new byte[16]);
            File.WriteAllBytes(third, new byte[16]);

            // 第二个目标名已经被占 ⇒ 第二阶段必然失败（真机对应"文件被别的进程占用 / 目标抢先被建"）。
            string occupied = Path.Combine(_dir, SecondTarget);
            File.WriteAllBytes(occupied, new byte[4]);

            var plan = new BrokenVolumeChainRepair.RenamePlan
            {
                StandardBaseName = "set.7z",
                Renames = new List<(string SourcePath, string TargetName)>
                {
                    (first, FirstTarget),
                    (second, SecondTarget)
                },
                FirstVolumePathAfterRename = Path.Combine(_dir, FirstTarget)
            };

            bool ok = BrokenVolumeChainRepair.TryApply(plan, out string failure);

            Assert.False(ok, "第二个目标名被占 ⇒ 必须如实报失败");
            Assert.NotEmpty(failure);

            // ⛔ 核心判据：盘上必须与"一组没改"完全一致（旧写法会留下半改状态）。
            Assert.True(File.Exists(first), "第一卷必须已经改回原名");
            Assert.False(
                File.Exists(Path.Combine(_dir, FirstTarget)),
                "⛔ 不许留下半改状态：改过的那个新名字不该存在");
            Assert.True(File.Exists(second), "第二卷本来就没改成，必须原地不动");
            Assert.True(File.Exists(third), "没进改名计划的那一卷一个字节都不该动");

            // 被占的那个目标名还是别人那一份（4 字节）—— ⛔ 绝不覆盖。
            Assert.Equal(4, new FileInfo(occupied).Length);
        }

        /// <summary>
        /// **对照**：名字本来就标准的那一项（目标名 == 原名）要能被回滚跳过 ——
        /// 它没有"改回去"这回事，⛔ 不许因为它在计划里就被算成"回滚失败"。
        /// </summary>
        [Fact]
        public void 回滚时_目标名与原名相同的项跳过_不算回滚失败()
        {
            string first = Path.Combine(_dir, "plain.7z.001");
            string second = Path.Combine(_dir, "plain.7z(坏.002");

            File.WriteAllBytes(first, new byte[16]);
            File.WriteAllBytes(second, new byte[16]);

            string occupied = Path.Combine(_dir, "plain.7z.003");
            File.WriteAllBytes(occupied, new byte[4]);

            var plan = new BrokenVolumeChainRepair.RenamePlan
            {
                StandardBaseName = "plain.7z",
                Renames = new List<(string SourcePath, string TargetName)>
                {
                    (first, "plain.7z.001"),   // 目标名 == 原名：本来就标准的那一卷
                    (second, "plain.7z.003")   // 目标名被占 ⇒ 失败
                },
                FirstVolumePathAfterRename = first
            };

            bool ok = BrokenVolumeChainRepair.TryApply(plan, out string failure);

            Assert.False(ok);
            Assert.True(File.Exists(first), "本来就标准的那一卷仍在原名上");
            Assert.True(File.Exists(second), "被改过的那一卷必须改回原名");

            // ⛔ 没回滚失败就不许说"半改状态" —— 那句话是给用户的手工核对提示，乱报只会吓人。
            Assert.DoesNotContain("半改状态", failure, StringComparison.Ordinal);
        }
    }
}

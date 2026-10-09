using System;
using System.IO;
using System.Linq;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **源包搬运的决策层：什么样的"源"才配按源包处理**（用户 2026-10-09 口令：
    /// 「**原包就是原包**」——只有**用户导入的那份**才算原包）。
    ///
    /// <para><b>真机现场</b>（`EEEE` 2026-10-09 19:14 那一趟）：`111.rar` 解出来的组入口包 `111.zip`
    /// 落在 `…\111\111\111.zip`；下一轮它自己成了一单（真机行 218/220「内层包，产物归入父任务输出目录」），
    /// 于是这里把它当成"源包"搬进其余物（行 287/289 两条 `源包移入其余物`，还顺带搬走 `111.z01`），
    /// 批末「其余物 = 彻底删除」全删（行 292 `彻底删除：111\其余物（5 项 / 545.1 MB）`）
    /// ⇒ **组入口包没了、第一大步永远闭不了环**，用户看到的就是"两个 `111.zip`"。</para>
    ///
    /// <para><b>判据落在哪</b>：<see cref="SourcePackageMover.Plan"/> —— 它是
    /// "哪些算源包"的**唯一决策处**（搬运的规划与执行都在这个模块里）。⚠ 2026-10-09 我先加在
    /// `ExtractionCoordinator` 的链尾循环里，真机复跑**完全没拦住**：调用者有多个
    /// （每单收尾 / 链尾补搬 / 手动档），加在调用者上必然漏一个。</para>
    ///
    /// <para><b>红检</b>：撤掉 `Plan` 里那道 `if (task.IsContinuationTask)` ⇒ 第一条用例当场变红
    /// （`Moves` 不再为空、跳过原因也不再是 <c>SourceIsOurOwnContent</c>）。</para>
    /// </summary>
    public class SourcePackageOwnershipTests : IDisposable
    {
        private readonly string _root;

        public SourcePackageOwnershipTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "af-src-owner-" + Guid.NewGuid().ToString("N"));
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
                // 临时目录清不掉不影响结论。
            }
        }

        /// <summary>
        /// **续解出来的内层包 ⇒ 它的"源"是我们自己产出的内容物，⛔ 一个字节都不搬**
        /// （用户口令：「原包就是原包」）。
        /// </summary>
        [Fact]
        public void 内层包的源_不是用户的原包_一个字节都不搬()
        {
            string landing = Path.Combine(_root, "111", "111");
            Directory.CreateDirectory(landing);

            // 我们自己刚产出的组入口包（它就在我们自己的落点树里）。
            string ownProduct = Path.Combine(landing, "111.zip");
            File.WriteAllBytes(ownProduct, new byte[4096]);

            string rest = Path.Combine(landing, "其余物");
            Directory.CreateDirectory(rest);

            var task = new ArchiveTask(ownProduct, 1)
            {
                // 事实位唯一出口：ParentOutputDirectory 非空 = 续解出来的内层包。
                ParentOutputDirectory = landing
            };

            Assert.True(task.IsContinuationTask, "这一单是内层包（被测前提）");

            SourcePackageMovePlan plan = new SourcePackageMover().Plan(task, rest);

            Assert.Empty(plan.Moves);
            Assert.Contains(plan.Skipped, skip => skip.Reason == ArtifactSkipReason.SourceIsOurOwnContent);

            // ⛔ 一个字节都不许动。
            Assert.True(File.Exists(ownProduct), "内层包的源必须原地不动");
        }

        /// <summary>
        /// **对照组：用户自己导入的那份原包照旧按档处理**（⛔ 这次改动不许把正当场景一起关掉）。
        /// </summary>
        [Fact]
        public void 对照组_用户导入的原包_照旧规划搬运()
        {
            string source = Path.Combine(_root, "111");
            Directory.CreateDirectory(source);

            string userPackage = Path.Combine(source, "111.rar");
            File.WriteAllBytes(userPackage, new byte[4096]);

            string landing = Path.Combine(source, "111");
            Directory.CreateDirectory(landing);

            string rest = Path.Combine(landing, "其余物");
            Directory.CreateDirectory(rest);

            // 没有 ParentOutputDirectory = 用户导入的那一份。
            var task = new ArchiveTask(userPackage, 1);

            Assert.False(task.IsContinuationTask, "这一单不是内层包（被测前提）");

            SourcePackageMovePlan plan = new SourcePackageMover().Plan(task, rest);

            Assert.Single(plan.Moves);
            Assert.Equal(userPackage, plan.Moves[0].SourcePath, ignoreCase: true);
        }
    }
}

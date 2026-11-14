using System;
using System.Collections.Generic;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **链停下来那一组"是不是这一单自己这一组"**（`ExtractionCoordinator.IsStalledGroupOwnGroup`）的守门用例。
    ///
    /// <para><b>真机现场（2026-10-10 EEEE）</b>：`111.rar`（**RarOld 族**）解出来的 `111.zip`
    /// 是**跨盘 ZIP 那一组 `111`（ZipSpanned 族）**的入口，可整组当时还缺 `.z01/.z02/.z03` ⇒
    /// `111.rar` 的链停在这一档。老判据**只比基名**（都是 `111`）⇒ 它被登记进"缺卷留到批末再判"名单
    /// ⇒ 批末拿它的名字去问一个**永不可能凑齐**的老式 RAR 组 ⇒ **同一单**既写着
    /// 「这一片随整组解开（由「111.z03」那单解的）」，又被扣一句
    /// `[ERROR] 分卷缺失，未开始解压` + 「按部分完成记」。</para>
    ///
    /// <para><b>断点证据</b>（debug-mcp 停在 `ExtractionCoordinator.cs:9912`）：
    /// `RememberDeferredVolumeDeficit:9912` ← `RunRecursiveAsync():6846`，`task.FileName = "111.rar"`。</para>
    ///
    /// <para>修法 = 判据转调项目**唯一那把"同一组"的尺子**
    /// <see cref="ArchiveFixer.Detection.VolumeGroupDetector.BelongsToSameGroup"/>（**族 + 基名**）。</para>
    /// </summary>
    public class StalledGroupRegistrationTests
    {
        /// <summary>
        /// 真机那一格：**不同族、同基名** ⇒ 不算"这一单自己这一组" ⇒ 不登记 ⇒ 不会在批末被扣一句缺卷。
        /// </summary>
        [Fact]
        public void 老式RAR包解出了跨盘ZIP那一组的入口_不算它自己那一组()
        {
            var task = new ArchiveTask(@"C:\t\in\111\111.rar", 1);
            var result = new RecursionResult
            {
                UnresolvedVolumePieces = new List<string> { @"C:\t\in\111\111\111.zip" }
            };

            Assert.False(
                ExtractionCoordinator.IsStalledGroupOwnGroup(task, result),
                "111.rar 是 RarOld 族、111.zip 是 ZipSpanned 族 —— 只比基名会把它误登记，批末就会扣它一句「分卷缺失」");
        }

        /// <summary>
        /// **哨兵（正向）**：这一单自己**就是那一组的成员**（同族同基名）⇒ 照样登记（批末那一站才有机会
        /// 把散着的片收拢起来把整组解开）。
        /// </summary>
        [Fact]
        public void 这一单自己就是那一组的成员_照样登记()
        {
            var task = new ArchiveTask(@"C:\t\in\111(4)\111.z02", 1);
            var result = new RecursionResult
            {
                UnresolvedVolumePieces = new List<string> { @"C:\t\in\111\111\111.zip" }
            };

            Assert.True(
                ExtractionCoordinator.IsStalledGroupOwnGroup(task, result),
                "111.z02 与 111.zip 同族同基名 ⇒ 它必须进名单（否则批末那一站根本不会去收片）");
        }

        /// <summary>基名都对不上（不同基名）⇒ 不登记。</summary>
        [Fact]
        public void 基名对不上_不登记()
        {
            var task = new ArchiveTask(@"C:\t\in\111(2)\111(2)_.zip", 1);
            var result = new RecursionResult
            {
                UnresolvedVolumePieces = new List<string> { @"C:\t\in\111\111\111.zip" }
            };

            Assert.False(ExtractionCoordinator.IsStalledGroupOwnGroup(task, result));
        }
    }
}

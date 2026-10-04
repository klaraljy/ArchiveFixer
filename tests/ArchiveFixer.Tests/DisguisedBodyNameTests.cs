using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Storage;
using ArchiveFixer.Models;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **"归档本体后缀被塞了垃圾"这一档的守门用例**（用户 2026-09-27 真机：222 那一组）。
    ///
    /// <para><b>现场</b>：外层是一对跨盘 zip（`222.zscip` + `222.z删除01`，网盘把中文塞进后缀），
    /// 改名成 `.zip` / `.z01` 之后解压成功、结果校验通过、L3 可证完整 —— 然后定稿那一步报
    /// 「这一层里有分卷组不完整：222：不完整·疑缺卷（弱证据）：名字上看缺 222.zip；
    /// 同目录的 222.zi删除p（246.76MB）按体积 + 位置推定是第 1 卷」，整层作废：
    /// 内容物一个字节没搬、源包没进其余物、链尾其余物一个都删不掉。</para>
    ///
    /// <para><b>同一个名字的两种残留</b>（这是本文件的判据核心）：</para>
    /// <list type="bullet">
    /// <item><description><b>分卷标记</b>被塞中文：<c>222.z删除01</c> → 去杂质后是 <c>z01</c>，
    /// 骨架档本来就认（`.001`/`.z01`/`.r00`/`.partN`）⇒ 一直是对的；</description></item>
    /// <item><description><b>归档本体后缀</b>被塞中文：<c>222.zi删除p</c> → 去杂质后是 <c>zip</c>，
    /// <b>没有任何一支认它</b> ⇒ 它退化成"一个没有卷标记的普通文件"，基名被算成 <c>222.zscip</c>，
    /// 整组改名产出 <c>222.zscip.zip</c>，与归档内部记的 <c>222.zip</c> 逐字对不上。</description></item>
    /// </list>
    ///
    /// <para>本文件钉四件事：① 唯一出口能还原；② 探测器把它认成"第 1 卷本体"；
    /// ③ 基名推到 <c>222</c>（不再产出脏基名）；④ 同号冲突时规范名优先，且那一组照旧**不许删**。</para>
    /// </summary>
    public sealed class DisguisedBodyNameTests : IDisposable
    {
        private readonly string _root;

        public DisguisedBodyNameTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixer-disguised-body-" + Guid.NewGuid().ToString("N"));
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

        // ================================================================ ① 唯一出口：还原归档本体后缀

        /// <summary>
        /// 唯一出口能把"被塞了中文 / 被缩写的归档后缀"还原出来（真机那两个名字逐字进用例）。
        /// </summary>
        [Theory]
        [InlineData("zi删除p", "zip")]
        [InlineData("zscip", "zip")]
        [InlineData("z删除ip", "zip")]
        [InlineData("zi\u3000\u3000p", "zip")]
        [InlineData("ra删除r", "rar")]
        [InlineData("7删除z", "7z")]
        public void 本体后缀被塞垃圾_唯一出口能还原(string segment, string expected)
        {
            Assert.True(
                ExtensionHelper.TryRecoverDisguisedArchiveBody(segment, out string canonical, out _),
                $"「{segment}」应当能还原成 {expected}");

            Assert.Equal(expected, canonical, ignoreCase: true);
        }

        /// <summary>
        /// ⛔ 本来就干净的后缀、以及"另一个归档的后缀"都不许被当成"被塞了垃圾"：
        /// 前者由调用方按原样处理，后者是**另一个名字**，归一了就认错组（宁可不动）。
        /// </summary>
        [Theory]
        [InlineData("zip")]
        [InlineData("rar")]
        [InlineData("7z")]
        [InlineData("001")]
        [InlineData("7")]
        [InlineData("")]
        public void 干净后缀与单字符段_一律不还原(string segment)
        {
            Assert.False(
                ExtensionHelper.TryRecoverDisguisedArchiveBody(segment, out _, out _),
                $"「{segment}」不该被还原 —— 它要么本来就干净，要么根本不是一个后缀");
        }

        // ================================================================ ② 探测器：认成第 1 卷本体

        /// <summary>
        /// **真机那两个名字**：本体后缀被塞中文的那一卷必须被认成"第 1 卷"，
        /// 而分卷标记被塞中文的那一卷照旧是第 2 卷（老口径一个字不变）。
        /// </summary>
        [Fact]
        public void 真机两个名字_认成第1卷与第2卷()
        {
            Assert.Equal(1, VolumeGroupDetector.TryGetVolumeIndex("222.zi删除p"));
            Assert.Equal(2, VolumeGroupDetector.TryGetVolumeIndex("222.z0sc1"));

            // 对照组：干净的名字不许被这一支抢走（老口径）。
            Assert.Equal(1, VolumeGroupDetector.TryGetVolumeIndex("222.zip"));
            Assert.Equal(2, VolumeGroupDetector.TryGetVolumeIndex("222.z01"));
        }

        // ================================================================ ③ 基名：不再产出脏基名

        /// <summary>
        /// **本次事故的根因那一步**：整组改名要推基名，而基名是从"这一组里最像标准名的那一卷"
        /// （跨盘 zip 取末片）推的。老判据剥不掉 <c>zscip</c>（差两个字符）⇒ 基名成了 `222.zscip`
        /// ⇒ 产出 `222.zscip.zip`。现在必须推到 `222`，整组名字才是 `222.z01` + `222.zip`
        /// —— 与归档内部记的名字**逐字对上**。
        /// </summary>
        [Fact]
        public void 脏末片名_基名推到222而不是222点zscip()
        {
            Assert.True(
                VolumeNumberFromContent.TryDeriveStem(
                    @"C:\x\222.zscip",
                    VolumeContentFormat.Zip,
                    out string stem),
                "应当能推出基名");

            Assert.Equal("222", stem);

            // 整组名字就是归档内部记的那一对（7-Zip 实测认这一族）。
            List<string> names = VolumeNumberFromContent
                .BuildStandardNames(stem, VolumeNamingFamily.ZipSpanned, 2)
                .ToList();

            Assert.Equal(new[] { "222.z01", "222.zip" }, names);

            // 对照组：干净的名字照旧（老口径不许被误伤）。
            Assert.True(VolumeNumberFromContent.TryDeriveStem(@"C:\x\222.zip", VolumeContentFormat.Zip, out string clean));
            Assert.Equal("222", clean);
        }

        // ================================================================ ④ 同号冲突：规范名优先 + 组照旧不许删

        /// <summary>
        /// **两个文件都声称自己是第 1 卷**（真机暂存目录的形状：`222.zip` + `222.zi删除p`）。
        /// <summary>
        /// **真机那一组完整形状**（外层跨盘 zip 解出来的两个条目 + 分卷标记那一卷）：
        /// `222.zip`（规范名本体）+ `222.zi删除p`（同号的两个名字）+ `222.z01`（干净的后续卷）。
        ///
        /// <para>三条一起钉：① 结论是**完整**（卷号 1..2 连续无洞，不再报"缺 222.zip"）；
        /// ② 两个同号的名字都算组成员；③ 因为有一个成员名字不标准，⛔ 整组仍然不许进可删的其余物。</para>
        ///
        /// <para>⚠ 只有这一对**没有**任何带卷标记的兄弟时（`222.zip` + `222.zi删除p` 两条光杆本体），
        /// 归组那一条老闸门照旧判"不是分卷组"（`VolumeGroupDetector.Group` 的"只有本体不算组"）——
        /// 那是**刻意**的：一份单独的 `.zip` 后面到底还有没有卷，名字给不出答案，宁可判不出。</para>
        /// </summary>
        [Fact]
        public void 真机完整形状_判完整但整组仍然不许删()
        {
            string directory = NewDirectory("real-shape");

            string canonical = WriteFile(directory, "222.zip", 4096);
            string disguised = WriteFile(directory, "222.zi删除p", 4096);
            string part = WriteFile(directory, "222.z01", 4096);

            VolumeGroupResolution resolution = Resolve(canonical);

            Assert.Equal(VolumeGroupVerdict.Complete, resolution.Verdict);
            Assert.Empty(resolution.MissingVolumeNames);
            Assert.DoesNotContain("缺 222.zip", resolution.Reason, StringComparison.Ordinal);

            Assert.True(resolution.IsGroupMember(canonical));
            Assert.True(resolution.IsGroupMember(disguised));
            Assert.True(resolution.IsGroupMember(part));

            Assert.True(resolution.HasRenamedVolume);
            Assert.False(resolution.CanEnterDeletableRestItems, "⛔ 名字不标准的那一组永远不许进可删的其余物");
        }

        /// <summary>
        /// 对照组：末片名字干净的一对（`222.z01` + `222.zip`）照旧判完整且**允许**进其余物
        /// —— 这一档是拿扩展名拼出来的老行为，一个字都不许被这轮改动误伤。
        /// </summary>
        [Fact]
        public void 对照组_干净的一对_照旧完整且允许进其余物()
        {
            string directory = NewDirectory("clean-pair");

            WriteFile(directory, "222.z01", 4096);
            string body = WriteFile(directory, "222.zip", 4096);

            VolumeGroupResolution resolution = Resolve(body);

            Assert.Equal(VolumeGroupVerdict.Complete, resolution.Verdict);
            Assert.False(resolution.HasRenamedVolume);
            Assert.True(resolution.CanEnterDeletableRestItems);
        }

        // ================================================================ ⑤ 链尾闸门：跟班卷不算链上成员

        /// <summary>
        /// **111 / 333 的其余物为什么一删不掉**（用户在真机上先看到的就是这个）：
        /// 一组分卷的第二卷那一单**按设计**落「已跳过」（它由首卷那一单启动，不重复解），
        /// 可链尾那道**整链闸门**把"已跳过"当成"链没跑完" ⇒ 每一批都被自己组里的跟班卷拦下。
        ///
        /// <para>⚠ 那道整链闸门本身 2026-10-04 被用户推翻（「你不会读设置吗，我勾选了保留吗，
        /// 没勾选你留着干什么」）⇒ 链尾「删除操作」只问**根源包自己那一层**，
        /// 跟班卷的终态是"已跳过"这件事**再也不参与**那个裁决。</para>
        ///
        /// <para>但"跟班卷**不在链上**"这条口径**一个字没改**，而且仍然在别处生效：
        /// 输出校验缺口（<c>DescribeChainVerificationGap</c>）、逐层回收、批末计数都排除它。
        /// 这一条用例钉的就是这个事实 —— 它判的已经不是"其余物删不删"。</para>
        /// </summary>
        [Fact]
        public void 跟班卷不算链上成员_真续解成员照旧算()
        {
            var root = new ArchiveTask { FileName = "111.part1.rar", CurrentPath = @"C:\x\111.part1.rar" };

            var follower = new ArchiveTask
            {
                FileName = "111.part2.rar",
                CurrentPath = @"C:\x\111.part2.rar",
                ParentOutputDirectory = @"C:\out\111\111",
                ContentDirectoryPath = @"C:\out\111\111",
                RootSourcePath = @"C:\x\111.part1.rar",
                IsVolumeGroupFollower = true
            };

            // 跟班卷的终态就是「已跳过」（设计如此），而且它**不在链上** ⇒ 不参与链尾的输出校验缺口。
            string destination = @"C:\out\111\111";

            root.OutputPath = destination;
            follower.OutputVerification = OutputVerificationOutcome.NotAttempted;
            root.IsOutputVerified = true;
            root.OutputManifestCrossChecked = true;

            Assert.Null(ExtractionCoordinator.DescribeChainVerificationGap(destination, root, new[] { root, follower }));

            // 反过来：真·续解成员判不出完整性 ⇒ 照样拦（老红线一个字不放宽）。
            var realContinuation = new ArchiveTask
            {
                FileName = "inner.7z",
                CurrentPath = @"C:\out\111\111\inner.7z",
                ParentOutputDirectory = @"C:\out\111\111",
                ContentDirectoryPath = @"C:\out\111\111",
                RootSourcePath = @"C:\x\111.part1.rar",
                OutputVerification = OutputVerificationOutcome.Passed,
                OutputManifestCrossChecked = false,
                IsOutputVerified = true
            };

            Assert.NotNull(ExtractionCoordinator.DescribeChainVerificationGap(destination, root, new[] { root, realContinuation }));
        }

        // ================================================================ ⑥ 定稿：引擎写出来的东西不是过程物

        /// <summary>
        /// **本次事故那一层为什么作废**（真机 222 倒数第二步）。
        ///
        /// <para>暂存目录里躺着引擎这一趟刚解出来的 <c>222\222.zip</c> + <c>222\222.zi删除p</c>
        /// —— 那两个**本身就是一份完整的两卷 split zip**。老判据只看后缀 ⇒ <c>222.zip</c> 被算成
        /// "待续解的过程物"，另一卷名字又不标准 ⇒ 整份计划作废，678 MB 产物一个字节都没落地。</para>
        ///
        /// <para>现在必须：**计划不失败**、两个文件都当内容物搬进成品目录
        /// （过程物名单里没有它们 ⇒ 也就不会进"可删的其余物"）。</para>
        /// </summary>
        [Fact]
        public void 定稿_引擎解出来的那两个split卷_不许被当成过程物()
        {
            string stage = NewDirectory("stage-engine-output");
            string inner = Path.Combine(stage, "222");

            Directory.CreateDirectory(inner);
            WriteFile(inner, "222.zip", 4096);
            WriteFile(inner, "222.zi删除p", 4096);

            var recursion = new RecursionResult
            {
                StopReason = RecursionStopReason.Completed,
                Completed = true,
                FinalOutputPath = stage
            };

            ExtractionCoordinator.FinalLayoutPlan plan = ExtractionCoordinator.PlanFinalLayout(
                stage,
                Path.Combine(_root, "out"),
                sharedOutputRoot: false,
                "222.zscip",
                engineOutput: recursion);

            Assert.False(plan.Failed, plan.FailureReason);

            /*
             * 产物按目录成层搬（暂存目录里那一层 `222\` 就是"内容物那一层"），
             * 所以判据是"这两个文件**都在**产物清单覆盖的目录里"，而不是逐个文件各一条 move。
             */
            Assert.Contains(
                plan.Moves,
                move => move.To.EndsWith("222", StringComparison.OrdinalIgnoreCase));

            // ⛔ 一个都不许进"过程物（可删的其余物）"名单。
            Assert.DoesNotContain(
                plan.ProcessArtifactSources,
                path => path.EndsWith("222.zip", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(
                plan.ProcessArtifactSources,
                path => path.EndsWith("222.zi删除p", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// **这一档的红检**：同一个暂存目录，只是**没有**递归结果（拿不到"这是引擎写出来的"这条事实）
        /// ⇒ 后缀像归档的那一份照旧算过程物，于是那道分卷组闸门照旧能把整份计划作废
        /// （形状借自 `VolumeGroupAssemblyGuardTests`：硬链接成的两卷"判不出"）。
        ///
        /// <para>它同时说明：让这一档放行的**唯一**依据就是"引擎这一趟写出来的产物清单"，
        /// ⛔ 不是把"后缀像归档就当过程物"这条老判据放松掉（老红线一个字节都没改）。</para>
        /// </summary>
        [Fact]
        public void 对照组_没有引擎产物清单时_那一堆照旧当待续解过程物_计划作废()
        {
            string stage = NewDirectory("stage-no-engine-output");

            string first = WriteFile(stage, "x.7z.001", 4096);
            string second = Path.Combine(stage, "x.7z.002");

            Assert.True(TryCreateHardLink(second, first), "本机文件系统不支持硬链接，这条用例失去意义");

            ExtractionCoordinator.FinalLayoutPlan plan = ExtractionCoordinator.PlanFinalLayout(
                stage,
                Path.Combine(_root, "out2"),
                sharedOutputRoot: false,
                "x");

            Assert.True(plan.Failed, "判不出的分卷组必须让整份计划作废");
            Assert.Empty(plan.ProcessArtifactSources);
            Assert.Empty(plan.Moves);
        }

        /// <summary>
        /// ...而同一个形状，只要递归结果说"这是引擎这一趟写出来的" ⇒ 它就不是过程物，
        /// 两道分卷组闸门都不进，照旧当内容物搬走（用户真机 222 那一单就是这个形状：
        /// 引擎解出来的两个条目本身就是一份完整的两卷 split zip）。
        /// </summary>
        [Fact]
        public void 对照组_有引擎产物清单时_同一堆照旧当内容物搬走()
        {
            string stage = NewDirectory("stage-with-engine-output");

            string first = WriteFile(stage, "x.7z.001", 4096);
            string second = Path.Combine(stage, "x.7z.002");

            Assert.True(TryCreateHardLink(second, first), "本机文件系统不支持硬链接，这条用例失去意义");

            var recursion = new RecursionResult
            {
                StopReason = RecursionStopReason.Completed,
                Completed = true,
                FinalOutputPath = stage
            };

            ExtractionCoordinator.FinalLayoutPlan plan = ExtractionCoordinator.PlanFinalLayout(
                stage,
                Path.Combine(_root, "out3"),
                sharedOutputRoot: false,
                "x",
                engineOutput: recursion);

            Assert.False(plan.Failed, plan.FailureReason);
            Assert.NotEmpty(plan.Moves);
            Assert.Empty(plan.ProcessArtifactSources);
        }

        // ================================================================ 辅助

        private static VolumeGroupResolution Resolve(string anchorPath) =>
            new VolumeGroupResolver().Resolve(new VolumeGroupQuery
            {
                AnchorPath = anchorPath,
                AllowTrialOpen = false
            });

        private string NewDirectory(string name)
        {
            string directory = Path.Combine(_root, name);
            Directory.CreateDirectory(directory);
            return directory;
        }

        private static string WriteFile(string directory, string name, long size)
        {
            string path = Path.Combine(directory, name);
            File.WriteAllBytes(path, new byte[size]);
            return path;
        }

        /// <summary>造一个硬链接（判定器靠"同 FileId"认"同一份数据被数成两卷"）。</summary>
        private static bool TryCreateHardLink(string linkPath, string existingPath)
        {
            try
            {
                return NativeHardLink.CreateHardLink(linkPath, existingPath);
            }
            catch
            {
                return false;
            }
        }
        /// <summary>硬链接（用例自己造的，用来钉"同一 FileId ⇒ 同一份"）。</summary>
        private static class NativeHardLink
        {
            [System.Runtime.InteropServices.DllImport(
                "kernel32.dll",
                CharSet = System.Runtime.InteropServices.CharSet.Unicode,
                SetLastError = true)]
            [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
            private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

            public static bool CreateHardLink(string linkPath, string existingPath) =>
                CreateHardLinkW(linkPath, existingPath, IntPtr.Zero);
        }
    }
}

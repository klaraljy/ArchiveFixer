using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using System;
using System.IO;

namespace ArchiveFixer.ViewModels
{
    /// <summary>
    /// 续解落点规划：内层包的下一层目录怎么推导、要不要套包名层、链根包名层怎么补。
    ///
    /// <para>为什么单独成类：这些判据只读 <see cref="ArchiveTask"/> 的落点字段与盘上事实，
    /// 与"一轮跑几层、任务怎么建"无关；放在一键处理主体里会让"落点怎么算"淹没在编排代码里。
    /// ⛔ 判据字面未改，只是换了宿主类型。</para>
    /// </summary>
    internal static class OneClickContinuationPlanner
    {
        /// <summary>
        /// 内层包的落点 = **那个内层包自己所在的那一层目录**（第 35 条改正；归集开启时仍以归集目录为准）。
        ///
        /// <para><b>真机现场</b>（"添加文件夹 + 指定位置"，多个包共用同一个输出根）：外层包第一层的产物是
        /// 一个文件夹 <c>BBB\111\2222\</c>，里面躺着下一层的包 <c>222.ra删除r</c>。
        /// 老实现按**父任务的 OutputPath**（＝共用根 <c>BBB\111</c>）落下一层的产物，于是内容物与包名目录
        /// **平级**（<c>BBB\111\222</c> 与 <c>BBB\111\2222</c> 并排）。用户原话：
        /// "我觉得你应该是要将 `BBB\111\222` 文件夹放在 `BBB\111\2222` 这个里面"。</para>
        ///
        /// <para>判据用"内层包自己所在的目录"这条**事实**，不猜任何名字：下一层的内容物落在那个包**旁边**，
        /// 天然就是"一个源包 = 一个目录"。唯一要处理的是内层包**本身就在「其余物」里**的情况
        /// （过程物集中处）—— 那时要爬到「其余物」外面那一层，否则下一层的内容物会被放进其余物里、
        /// 跟着「彻底删除」一起没了。</para>
        ///
        /// <para>归集（<c>CollectResultsToDirectory</c>）是"把产物目录整个搬走"，那时旧路径已经不存在，
        /// 权威落点是 <see cref="ArchiveTask.CollectedPath"/>（既有行为，不变）。</para>
        /// </summary>
        /// <param name="parentTask">内层包所属的父任务。</param>
        /// <param name="innerPackagePath">内层包自己的路径（给 null 时退回"父任务的内容物层 / 输出目录"）。</param>
        /// <param name="ownLayerName">
        /// 给**这一层自己**再套的那一层文件夹名（2026-09-27 用户拍板的"首尾必留、中间看开关"）：
        /// 给了就在算出来的目录后面追加一层（<c>…\222</c> → <c>…\222\333</c>），
        /// 给 null / 空 = 不追加（这一层的产物直接落进父任务那一层）。
        ///
        /// <para>⛔ 名字**必须由调用方按 <see cref="ShouldAddContinuationLevelLayer"/> 算出来再传**：
        /// 这里只管"怎么拼、要不要防重复"，不管"该不该加"—— 判据只有一处（扫描那一侧），
        /// 免得同一件事在两个地方各判一遍、判出两个结果。</para>
        /// </param>
        internal static string ResolveContinuationOutputDirectory(
            ArchiveTask parentTask,
            string? innerPackagePath = null,
            string? ownLayerName = null)
        {
            string directory = ResolveContinuationBaseDirectory(parentTask, innerPackagePath);

            /*
             * ⚠ 这一层的位置**被内层包自己那个文件占着**时不能建（2026-09-27 真机测试逮到）：
             * Windows 同一条路径上不许既有文件又有目录。典型现场 = **名字被改坏、剥不出后缀**的包
             * （`user.jp删除g`、`222.ra删除r`）：它的包基名就等于文件名本身，于是
             * `<父层>\user.jp删除g` 既是那个文件、又要当这一层的目录 → 建目录必然失败，
             * 任务会落成"最终目录不存在且创建失败"。
             *
             * 处置：这一层**不另建**，内容落进父任务那一层里（内层包自己就躺在那里）——
             * 这是物理上唯一可行的选择，调用方会写一条日志说清为什么（见扫描那一侧）。
             */
            if (IsOwnLayerOccupiedByPackageFile(directory, ownLayerName, innerPackagePath))
            {
                return directory;
            }

            return AppendOwnLayer(directory, ownLayerName);
        }

        /// <summary>
        /// 这一层要建的位置是不是**被内层包自己那个文件占着**（同路径不许既有文件又有目录）。
        ///
        /// <para>只有"包基名 == 文件名"（名字被改坏、剥不出已知后缀那种）才会命中；
        /// 正常包（`333.7z` → 层名 `333`）永远不会命中。</para>
        /// </summary>
        internal static bool IsOwnLayerOccupiedByPackageFile(
            string directory,
            string? ownLayerName,
            string? innerPackagePath)
        {
            if (string.IsNullOrWhiteSpace(innerPackagePath) || string.IsNullOrWhiteSpace(ownLayerName))
            {
                return false;
            }

            string layered = AppendOwnLayer(directory, ownLayerName);

            if (string.Equals(layered, directory, StringComparison.OrdinalIgnoreCase))
            {
                // 没打算加层（名字为空 / 最后一段已经同名）——不关这一条的事。
                return false;
            }

            try
            {
                return File.Exists(layered) && !Directory.Exists(layered);
            }
            catch
            {
                // 路径形状怪异时按"没被占"处理：真建不出来会在任务级如实报出来。
                return false;
            }
        }

        /// <summary>
        /// 续解层"要不要给这一层套一个以包名命名的文件夹"（用户 2026-09-27：**首尾必留，中间看开关**；
        /// 2026-10-16：中间那个开关删掉了，现在中间层一律照建）。
        ///
        /// <para>
        /// ⚠ 判据**只有一处**：<see cref="PackageLayerRules.ShouldAddInnerPackageLayer"/>。
        /// 这里只做转发（保留这个方法名是因为既有用例与文档都按它指认这一档规则），
        /// ⛔ 不许在这里再写第二份规则 —— 递归发布侧读的是同一个出口。
        /// </para>
        /// </summary>
        internal static bool ShouldAddContinuationLevelLayer(
            bool parentProducedContent,
            int siblingCount)
        {
            return PackageLayerRules.ShouldAddInnerPackageLayer(
                parentProducedContent,
                siblingCount);
        }

        /// <summary>
        /// 把"这一层自己的包名"追到目录后面（<c>…\222</c> → <c>…\222\333</c>）。
        ///
        /// <para>三条防重复/防空的判断，每条都对应一种真实形状：</para>
        /// <list type="bullet">
        /// <item><description>名字为空（取不出包名）→ 一个字都不加；</description></item>
        /// <item><description>目录最后一段**已经**叫这个名字（<c>…\333\333.rar</c> 那种"包在自名文件夹里"）
        /// → 不再加，否则会得到 <c>333\333</c> 这种重复层（用户明确抱怨过的形状）；</description></item>
        /// <item><description><b>过程物名不成层</b>（用户 2026-09-27 红线）：分卷组的基名（真机现场
        /// <c>59768866.001/.002</c> → <c>59768866</c>）**不是**用户认得出来的包名，
        /// 拿它当一层只会在成品里造出 <c>…\包名\59768866\真内容</c> 这种怪目录 →
        /// 这一档**不追加**，那一层的内容物归父任务自己那一层。</description></item>
        /// </list>
        /// </summary>
        internal static string AppendOwnLayer(string directory, string? ownLayerName)
        {
            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(ownLayerName))
            {
                return directory;
            }

            string layer = FileNameHelper.SanitizeFileName(ownLayerName);

            if (layer.Length == 0)
            {
                return directory;
            }

            string trimmed = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (trimmed.Length == 0)
            {
                return directory;
            }

            if (string.Equals(Path.GetFileName(trimmed), layer, StringComparison.OrdinalIgnoreCase))
            {
                return directory;
            }

            return Path.Combine(trimmed, layer);
        }

        /// <summary>
        /// 这一层该叫什么（= 内层包**去掉假后缀的基名**：就地替换留下的那个文件夹名）；
        /// **分卷组返回空** —— 过程物名不成层。
        ///
        /// <para>
        /// ⚠ 判据**只有一处**：<see cref="PackageLayerRules.ResolveInPlaceLayerName"/>
        /// （递归发布侧留目录用的是同一个方法），这里只做转发。
        /// </para>
        /// </summary>
        internal static string ResolveContinuationLayerName(string? innerPackagePath)
        {
            return PackageLayerRules.ResolveInPlaceLayerName(innerPackagePath);
        }

        /// <summary>
        /// 内层包落点的**原有推导**（2026-09-25 第 35 条 + 第 43 条）：只算"内层包自己所在的那一层"，
        /// 不追加任何层 —— <see cref="ResolveContinuationOutputDirectory"/> 在外面套那一层。
        ///
        /// <para>⚠ <c>internal</c>（不是 private）只为一处：扫描那一侧要拿它判
        /// "这一层的位置是不是被内层包自己占着"，好在日志里说清为什么没建那一层。</para>
        /// </summary>
        internal static string ResolveContinuationBaseDirectory(ArchiveTask parentTask, string? innerPackagePath = null)
        {
            if (parentTask == null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(parentTask.CollectedPath))
            {
                return parentTask.CollectedPath;
            }

            string fallback = string.IsNullOrWhiteSpace(parentTask.ContentDirectoryPath)
                ? parentTask.OutputPath
                : parentTask.ContentDirectoryPath;

            if (string.IsNullOrWhiteSpace(innerPackagePath))
            {
                return EnsureChainRootPackageLayer(parentTask, fallback);
            }

            string? directory = Path.GetDirectoryName(innerPackagePath);

            if (string.IsNullOrWhiteSpace(directory))
            {
                return EnsureChainRootPackageLayer(parentTask, fallback);
            }

            // 内层包在「其余物」（含旧名「过程物」）里 → 爬到那一层之外。
            string? outermostArtifact = null;
            string? cursor = directory;

            // 「其余物\<这一层>\…」里的 <这一层>：共用根那一档下它就是**外层包的包基名**（D-10 的布局）。
            string? artifactChild = null;
            string? childCursor = directory;

            while (!string.IsNullOrWhiteSpace(childCursor))
            {
                if (ProcessArtifactLayout.IsArtifactDirectoryName(Path.GetDirectoryName(childCursor)))
                {
                    artifactChild = Path.GetFileName(childCursor);
                }

                childCursor = Path.GetDirectoryName(childCursor);
            }

            while (!string.IsNullOrWhiteSpace(cursor))
            {
                if (ProcessArtifactLayout.IsArtifactDirectoryName(cursor))
                {
                    outermostArtifact = cursor;
                }

                cursor = Path.GetDirectoryName(cursor);
            }

            if (outermostArtifact != null)
            {
                directory = Path.GetDirectoryName(outermostArtifact);
            }

            if (string.IsNullOrWhiteSpace(directory))
            {
                return EnsureChainRootPackageLayer(parentTask, fallback, artifactChild);
            }

            /*
             * 不许越出根任务的输出范围（共用输出根模式下 root 就是那个根）：
             * 内层包如果被放在了范围之外（理论上不该发生），落点退回 fallback，绝不往外写。
             */
            string outputRoot = string.IsNullOrWhiteSpace(parentTask.OutputPath)
                ? parentTask.ContentDirectoryPath
                : parentTask.OutputPath;

            if (!string.IsNullOrWhiteSpace(outputRoot) &&
                !ArchivePathGuard.IsInsideRoot(outputRoot, directory, out _))
            {
                return EnsureChainRootPackageLayer(parentTask, fallback, artifactChild);
            }

            return EnsureChainRootPackageLayer(parentTask, directory, artifactChild);
        }

        /// <summary>
        /// **链根那一层包名目录必须存在**（用户 2026-09-25 第 43 条，真机：68 套图全平铺进共用根）。
        ///
        /// <para><b>现场</b>：用户"添加文件夹 + 指定位置"导入 68 个包，落点是**共用输出根**
        /// （`CCC\SJA佳爷…\`，最后一段是**导入文件夹名**）。每个外层包的清单里**只有内层包**
        /// （`00NN_auto.7z.001/.002/.003`）、**没有内容文件夹** —— 于是外层包这一轮什么都没定稿，
        /// 链根那一层"包名目录"从来没被建出来。续解任务按"内层包所在的那一层"算落点，
        /// 得到的就是那个**共用根**；再加上「每个包只留一层内容」把最后一层塌掉，
        /// 68 套图全倒进同一个目录（真机实测：4339 个文件平铺、几千个同名被自动改名成 `(1)…`）。</para>
        ///
        /// <para><b>该是什么样</b>：用户 2026-09-24 拍板的语义就是 `222\1111\内容物`
        /// （成品目录 \ **包名** \ 内容物）。所以这里补一层**链根任务的包基名**：
        /// `CCC\SJA佳爷…\055\…`——每个包一个目录，兄弟包互不干扰。</para>
        ///
        /// <para><b>判据只有两条事实</b>（⛔ 不猜、不看文案）：①父任务是**链根**（不是续解任务）；
        /// ②算出来的目录**最后一段不是它自己的包基名** —— 那说明这是"多个包共用的根"
        /// （包名那一层不在路径里），而不是"它自己的包名目录"。两条都成立才补。</para>
        ///
        /// <para>⚠ 第二条正是它**不会**动到第 35 条那两种情形的原因：内层包在内容文件夹里时
        /// （`…\111\2222\`），最后一段就是包名 `2222`；单个包 + 指定位置时目录本来就是
        /// `…\指定位置\包名\` —— 两种都不补，行为与从前逐字相同。</para>
        /// </summary>
        private static string EnsureChainRootPackageLayer(
            ArchiveTask parentTask,
            string directory,
            string? artifactChildLayer = null)
        {
            if (parentTask == null
                || parentTask.IsContinuationTask
                || string.IsNullOrWhiteSpace(directory))
            {
                return directory;
            }

            /*
             * ⚠ 只有"算出来的目录就是这个链根自己的输出根"时才补那一层（⛔ 不许用"最后一段名字不等于包名"
             * 之类的近似判据 —— 实测踩到：扫描把**兄弟包**内容目录里的内层包也算到这个父任务头上时，
             * 那个近似判据会把兄弟的那一层当成共用根，补出一层 `…\3333\2222` 的错目录，把第 35 条那条
             * "跟着内层包自己所在的目录走"给毁了）。
             */
            string ownRoot = string.IsNullOrWhiteSpace(parentTask.ContentDirectoryPath)
                ? parentTask.OutputPath
                : parentTask.ContentDirectoryPath;

            if (string.IsNullOrWhiteSpace(ownRoot) || !SafePathHelper.PathEquals(ownRoot, directory))
            {
                return directory;
            }

            /*
             * 层名优先取**内层包真正所在的那一层**（「其余物\055\…」里的 `055`）：
             * 共用根那一档下"其余物"按包基名分了子目录（ResultFinalizer 的 D-10），
             * 那个子目录名就是外层包的包基名，**比父任务的名字更可靠** ——
             * 实测：两个兄弟包都装着同名内层卷时，扫描会把另一个包的内层包也算到这个父任务头上
             * （`2222.7z：内层包移入其余物 —— …\3333\333.7z`），用父任务名就会把两个包的内容
             * 倒进同一个目录（真机上就是"混乱"）。
             */
            string packageLayer = string.IsNullOrWhiteSpace(artifactChildLayer)
                ? FileNameHelper.SanitizeFileName(FileNameHelper.GetArchiveBaseName(parentTask.CurrentPath))
                : FileNameHelper.SanitizeFileName(artifactChildLayer!);

            if (string.IsNullOrWhiteSpace(packageLayer))
            {
                return directory;
            }

            string trimmed = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.Equals(Path.GetFileName(trimmed), packageLayer, StringComparison.OrdinalIgnoreCase))
            {
                // 已经在自己那一层里（单个包 + 指定位置 / 内层包在内容文件夹里）→ 一个字都不动。
                return directory;
            }

            string layered = Path.Combine(trimmed, packageLayer);

            /*
             * ⚠ 2026-10-18 困难批 `11111111` 组合 2 运行期定案（调试器读数逐字）：
             * `rootTask.ContentFileCount = 1`、`ContentDirectoryPath = …\aaa-W0-0`、
             * 实际内容在 `…\aaa-W0-0\aaa-L2\content-60mb.bin`（递归最后一层的包名是 `aaa-L2`，
             * **不是**链根包名 `aaa-W0-1删除`）⇒ 老写法无条件补出
             * `…\aaa-W0-0\aaa-W0-1删除`（盘上不存在）⇒ 链尾补搬数到 0 个内容物 ⇒
             * 选了「彻底删除」也不删源包（④ 8 份同一族）。
             *
             * 判据两件**盘上事实**（⛔ 不猜内容在哪一层）：
             * ① 这一单确实发布过内容物（`ContentFileCount > 0`，定稿计划记的账）；
             * ② 补出来的那一层盘上不存在。
             * 两条同时成立 ⇒ 退回记录下来的 `ContentDirectoryPath`（调用方数内容物是递归的，
             * 能数到子层里的内容物）；只有一条成立 ⇒ 照旧补层（老行为一个字不改）。
             */
            if (parentTask.ContentFileCount > 0 && !Directory.Exists(layered))
            {
                return directory;
            }

            return layered;
        }
    }
}


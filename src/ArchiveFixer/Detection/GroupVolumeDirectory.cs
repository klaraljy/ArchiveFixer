using ArchiveFixer.Helpers;
using System;
using System.Collections.Generic;
using System.IO;

namespace ArchiveFixer.Detection
{
    /// <summary>
    /// **入口包所在的那一层** —— 也就是这一组该落的那一层。
    ///
    /// <para><b>用户 2026-10-05 定的口径（他连着说了三遍，最后一遍把话说死了）</b>：</para>
    /// <list type="number">
    /// <item><description>「落在第一卷的位置 …… 你要对哪个包进行输入密码操作就解压到哪个包，
    /// 这个 zip 分卷是对后缀为 <c>.zip</c>（`111.zip`）进行操作，7z 和 rar 都是对**第一卷**操作的」。</description></item>
    /// <item><description>「第一卷 = `.z01`（第 1 片），我没有说过必须要第一卷，**你对谁输入密码继续解压，
    /// 你就解压到哪里**」。</description></item>
    /// <item><description>「这个 `111.zip` 是经过两层伪装压缩变成的 `111.rar`，而 `111.rar` 原本就在文件夹 `111` 里面，
    /// 按照程序你就应该在文件夹 `111` 的子文件夹里面得到 `111.zip`，而不是这种情况（`111.zip` 所在那一层 `111(4)`），
    /// **这种情况是你私自移动 `111.zip` 到了 `111(4)`，这是不应该存在的 bug**」。</description></item>
    /// </list>
    ///
    /// <para><b>落到代码上就是两句话</b>：</para>
    /// <list type="number">
    /// <item><description><b>入口包 = 引擎要打开、要对它输密码的那一份</b>：跨盘 ZIP 族 = 本体 <c>X.zip</c>
    /// （那一族里引擎打开的就是它，<c>.z01</c> 只是第 1 片）、7z 族 = <c>X.7z.001</c>、RAR 族 = 第 1 卷
    /// （<c>X.part1.rar</c> / <c>X.rar</c>）。</description></item>
    /// <item><description><b>产物落在入口包所在的那一层</b>（⛔ 不是"任务表里这一组第一单所在目录" ——
    /// 那是导入顺序，真机 `CCCC` 就因为它在 `111(4)` 而把产物带到了 `111(4)\111`）。
    /// 入口包还没解出来（正压在某个包里）时，它该落在**产出它的那个包自己那条链**里，
    /// 这件事由调用方（<c>ExtractionCoordinator.ResolveOwnerChainDirectory</c>）回答，⛔ 不在这里猜。</description></item>
    /// </list>
    ///
    /// <para>三族只用一个判据：<see cref="VolumeGroupDetector.TryGetVolumeIndex"/> 返回 <c>1</c> 的那一份
    /// —— 这个函数本来就是"族专属"的（<c>VolumeGroupDetector.VolumeFamily</c>）。⛔ 这里不自己写一遍
    /// 按族的规则（AGENTS.md §9.5：同一件事的真值只允许有一个出口）。</para>
    ///
    /// <para>⛔ 只读名字 + <c>File.Exists</c>：**不 Enumerate、不改名、不移动、不建链接**。
    /// 判不出 ⇒ 返回空，由调用方退回原样 —— 兜底落在"什么都不做"那一档。</para>
    /// </summary>
    public static class GroupVolumeDirectory
    {
        /// <summary>入口包：<see cref="Path"/> = 引擎要打开的那一份；<see cref="Directory"/> = 它所在那一层。</summary>
        public readonly record struct Entry(string Path, string Directory);

        /// <summary>判不出来的那一档（两个空串；⛔ 不是 <c>null</c> —— 调用方一律按"空 = 什么都不做"办）。</summary>
        private static readonly Entry Unknown = new(string.Empty, string.Empty);

        /// <summary>
        /// 在 <paramref name="memberPaths"/>（调用方保证**同一组**的成员）里找出入口包**在盘上的那一份**。
        ///
        /// <para>判据：卷序 = 1（<see cref="VolumeGroupDetector.TryGetVolumeIndex"/>），而且它与组里**另一个成员**
        /// 同族同基名（<see cref="VolumeGroupDetector.BelongsToSameGroup"/>）—— 后者把"孤零零一个文件自己算出来是 1"
        /// 那一档挡在外面（那一档没有"组"这回事）。</para>
        /// </summary>
        /// <returns>找得到 ⇒ 入口包与它所在目录；不在盘上 / 没有卷成员 / 路径没目录部分 ⇒ 两个空串。</returns>
        public static Entry Resolve(IEnumerable<string?>? memberPaths)
        {
            if (memberPaths == null)
            {
                return Unknown;
            }

            var names = new List<string>();
            var paths = new List<string>();

            foreach (string? path in memberPaths)
            {
                string name = FileNameHelper.GetFileName(path);

                if (name.Length > 0 && VolumeGroupDetector.TryGetVolumeIndex(name) != null)
                {
                    names.Add(name);
                    paths.Add(path!);
                }
            }

            for (int i = 0; i < names.Count; i++)
            {
                if (VolumeGroupDetector.TryGetVolumeIndex(names[i]) != 1)
                {
                    continue;
                }

                string? directory = FileNameHelper.GetDirectoryName(paths[i]);

                if (string.IsNullOrWhiteSpace(directory) || !File.Exists(paths[i]))
                {
                    continue;
                }

                for (int j = 0; j < names.Count; j++)
                {
                    if (j != i && VolumeGroupDetector.BelongsToSameGroup(names[j], names[i]))
                    {
                        return new Entry(paths[i], directory!);
                    }
                }
            }

            return Unknown;
        }
    }
}

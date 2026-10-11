using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// **一轮结束时，"这一轮解出来的东西"到底是哪一类** —— 用户 2026-10-11 定的**轮次模型**（本程序的基石）：
    ///
    /// <para><b>一个轮 = 第一大步 + 第二大步。</b></para>
    /// <list type="number">
    /// <item><description><b>第一大步 = 单一压缩包的全部解压</b>：把**非分卷**的包一路解到底（包里还有单一包就继续解）；
    /// 遇到**分卷**就停手、原样留着 —— 那是第二大步的事。</description></item>
    /// <item><description><b>第二大步 = 分卷压缩包的解压，只解一层</b>：用**分卷专属那把尺子**（族 / 卷号 / 入口包 / 规范名）；
    /// **位置 = 入口包那一层**（对哪个包输入密码，就在那个地方设工作区）；
    /// **只有走到第二大步，才对分卷动手**。</description></item>
    /// <item><description><b>轮判定（本类）= 按"解出来的东西是什么"决定下一轮从哪一步开始</b>：
    /// 是**用户要的内容物** ⇒ 到此为止（进删除档）；是**单一压缩包** ⇒ 开下一轮的**第一大步**；
    /// 还是**分卷** ⇒ 直接开下一轮的**第二大步**（⛔ 不再过一次第一大步）。</description></item>
    /// </list>
    ///
    /// <para><b>为什么这是基石（用户原话的意思）</b>：同一个产物在不同轮里**角色不同** ——
    /// 这一轮第二大步的输出，下一轮可能当"第一大步的输入"、也可能当"第二大步的输入"
    /// （他打的比方是 `1+=1` 的运算符重载：那个 `1` 既是被加数、又参与赋值，**结果不是终点、它还会参与计算**）。
    /// ⇒ 每到轮边界**必须按盘上事实重新分类一次**，⛔ 不许把上一轮的角色当继承属性带过去。</para>
    ///
    /// <para><b>判据只转调既有那几把尺子</b>（⛔ 本类不新造）：
    /// "是不是分卷的一片" = <see cref="VolumeGroupDetector.TryGetVolumeIndex"/>；
    /// "这一组入口包是哪一份" = <see cref="GroupVolumeDirectory.Resolve"/>；
    /// "是不是归档" = 魔数 <see cref="VolumeContentInference.SniffFormat"/>（⛔ 不看后缀；
    /// 认不出 ⇒ 当**内容物**，绝不当归档 —— 「判不出 ⇒ 什么都不做」）。</para>
    /// </summary>
    public static class RoundProductClassifier
    {
        /// <summary>下一轮**从哪一步开始**。</summary>
        public enum NextStep
        {
            /// <summary>没有还要解的东西了 —— 这一轮就是终点（接下来是删除档）。</summary>
            Finished,

            /// <summary>下一轮先跑**第一大步**（单一压缩包的全部解压）。</summary>
            SinglePackageStep,

            /// <summary>下一轮先跑**第二大步**（分卷压缩包，只解一层）。</summary>
            VolumeStep
        }

        /// <summary>一轮产物的分类结果（一次算好，给日志与下一轮的步选择共用）。</summary>
        public sealed record Summary(
            int ContentCount,
            IReadOnlyList<string> SinglePackages,
            IReadOnlyList<string> VolumeEntries,
            NextStep Step)
        {
            /// <summary>一行看得见的轮结论（用户口径：轮边界必须能看日志定位）。</summary>
            public string Describe(int round) =>
                $"第 {round} 轮结束：单一压缩包 {SinglePackages.Count} 个 / 分卷 {VolumeEntries.Count} 组 / "
                + $"其余都是内容物（{ContentCount} 个）⇒ "
                + (Step switch
                {
                    NextStep.SinglePackageStep => "下一轮从「第一大步」（单一压缩包全部解压）开始",
                    NextStep.VolumeStep => "下一轮从「第二大步」（分卷，只解一层）开始",
                    _ => "没有还要解的东西了，到此为止（接下来按设置处理源包与过程物）"
                });
        }

        /// <summary>
        /// 把**候选清单**逐个分类（⛔ 不存在的文件直接跳过；判不出 ⇒ 归内容物）。
        ///
        /// <para>分卷只登记**每一组的入口包**（引擎要打开、要对它输密码的那一份）——
        /// 一组只算一组，⛔ 不是一片一行。</para>
        /// </summary>
        public static Summary Classify(IEnumerable<string>? candidates)
        {
            var singles = new List<string>();
            var volumeMembers = new List<string>();
            int content = 0;

            foreach (string? candidate in candidates ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate))
                {
                    continue;
                }

                string name = FileNameHelper.GetFileName(candidate);

                /*
                 * ⛔ **"这一份是什么"问底层那一个出口**（`FileNature.Inspect` —— 内容 / 名字 / 盘上四类事实一张卡），
                 * ⛔ 不在这里自己拼名字、自己读魔数（用户 2026-10-11：「统筹到同一组上面去…先看看底层能够识别到文件是什么东西」）。
                 */
                FileNatureCard card = FileNature.Inspect(candidate);

                /*
                 * 分卷那一支的判据 = 卡上那句：内容**可证不是**分卷成员 ⇒ 不参与（它就是一份普通的单卷归档 / 别的文件）；
                 * 是分卷 / **判不出** ⇒ 参与（判不出照旧按名字 —— ⛔ 不许把"判不出"当"不是"）。
                 */
                if (card.ParticipatesInVolumeHandling && card.VolumeIndexByName != null)
                {
                    // 是分卷的一片 —— 先收进池子，稍后按"组"折算成入口包。
                    volumeMembers.Add(candidate);
                    continue;
                }

                if (VolumeContentInference.SniffFormat(candidate) != VolumeContentFormat.Unknown)
                {
                    singles.Add(candidate);
                    continue;
                }

                // 魔数也认不出 ⇒ **内容物**（⛔ 不当归档：判不出 ⇒ 什么都不做）。
                content++;
            }

            var entries = new List<string>();

            foreach (IGrouping<string, string> group in volumeMembers.GroupBy(
                         member => BuildVolumeGroupKey(member),
                         StringComparer.OrdinalIgnoreCase))
            {
                /*
                 * ⚠ **组一定要计上**（"这一轮确实解出了分卷"是个事实）；入口包只用来**显示**。
                 * ⛔ 原来那句兜底拿"尺子算出来的第 1 卷名"跟**脏名原文**比 —— 脏名永远不等
                 * （实测 `111_.par删t1.ra除r` 因此被算成"不是第 1 卷" ⇒ 那一组报 0，
                 * 轮判定行写着"分卷 0 组"，等于没判出来）。改成问**卷号**（既有那把尺子）。
                 */
                string entry = ResolveEntryPackage(group.ToList()) ?? group.First();

                if (!entries.Contains(entry, StringComparer.OrdinalIgnoreCase))
                {
                    entries.Add(entry);
                }
            }

            NextStep step = singles.Count > 0
                ? NextStep.SinglePackageStep
                : entries.Count > 0
                    ? NextStep.VolumeStep
                    : NextStep.Finished;

            return new Summary(content, singles, entries, step);
        }

        /// <summary>同一组的键 = 目录 + 族 + 基名（⛔ 不看卷号 —— 卷号正是组内区分用的）。</summary>
        private static string BuildVolumeGroupKey(string path)
        {
            string directory = Path.GetDirectoryName(path) ?? string.Empty;
            string name = FileNameHelper.GetFileName(path);
            string baseName = FileNameHelper.GetArchiveBaseName(name);

            return directory + "|" + baseName;
        }

        /// <summary>
        /// 这一组的**入口包**（唯一出口 <see cref="GroupVolumeDirectory.Resolve"/>）：
        /// 认得出 ⇒ 那一份的路径；认不出 ⇒ <c>null</c>（调用方不登记这一组，⛔ 不猜）。
        /// </summary>
        private static string? ResolveEntryPackage(IReadOnlyList<string> members)
        {
            GroupVolumeDirectory.Entry entry = GroupVolumeDirectory.Resolve(members);

            if (entry.Path.Length > 0 && File.Exists(entry.Path))
            {
                return entry.Path;
            }

            // 入口包认不出时退回这一组里**卷号 = 1** 的那一份（既有那把尺子；⛔ 不比名字 ——
            // 脏名跟"尺子算出来的规范名"永远不相等，实测踩到过）。判不出 ⇒ null。
            foreach (string member in members)
            {
                if (VolumeGroupDetector.TryGetVolumeIndex(FileNameHelper.GetFileName(member)) == 1)
                {
                    return member;
                }
            }

            return null;
        }
    }
}

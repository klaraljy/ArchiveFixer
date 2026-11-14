using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Services;

/// <summary>
/// 本批「其余物 / 删除操作」的**批首准入与口径**（生命周期：批首定一次；失败模型：只读设置与运行选项，不碰盘）。
///
/// <para>"本次选项"优先（<see cref="IRestHandlingStepHost.RunOptions"/>），没有才读设置；「空间不足」模式
/// **覆盖**这一档（源包命运在别处定，这里管过程物），运行期覆盖、⛔ 不写设置。</para>
///
/// <para>批首定一次而不是每任务现读：同一批里不许"前半段按一个档、后半段按另一个"。
/// <see cref="Mode"/> 的初始值是 <see cref="RestHandlingModes.Keep"/> —— 没经过批首的路径
/// （例如单独解压一个包）永远按 Keep 走，不会顺手删东西。</para>
/// </summary>
internal sealed class RestHandlingBatchPolicy
{
    private readonly IRestHandlingStepHost _host;

    internal RestHandlingBatchPolicy(IRestHandlingStepHost host)
    {
        _host = host;
    }

    /// <summary>本批生效的「删除操作」档（<see cref="RestHandlingModes"/>；批首定一次）。</summary>
    internal string Mode { get; set; } = RestHandlingModes.Keep;

    /// <summary>
    /// 批首定一次"本批的删除操作按哪一档走"（用户中途改了设置也只影响下一批，同一批口径一致）。
    ///
    /// <para>⚠ 2026-09-25 第 33 条：**「本次选项」优先**（<see cref="_host.RunOptions"/> 里那一档）——
    /// 用户在确认框的折叠区里改了「删除操作」，这一次就必须按他选的跑；
    /// 没有本次选项（无界面宿主 / 勾了"以后不再询问"）时才读设置。</para>
    /// </summary>
    internal void Prepare()
    {
        /*
         * ⚠ 「空间不足」模式**覆盖**这一档（用户 2026-09-27 拍板的口径：
         * 源包 → 放入其余物 + 其余物 → 彻底删除，运行期自动覆盖、⛔ 不写设置）。
         *
         * 注意覆盖的**不是**"是否删源包"那件事 —— 源包在这个模式下的命运在
         * PostProcessSuccessAsync 里已经定了（定稿 + 校验通过 → 当场删）。
         * 这一档管的是**过程物**（内层包、暂存残渣）：它们仍然按链尾统一处理，
         * 所以这里给它落到"彻底删除"。
         */
        if (_host.SpaceTightThisBatch)
        {
            /*
             * 2026-11-06 口径：空间不足模式的“最后一层过程物”固定保留。
             * 成功层的过程物/源包由 `PostProcessSuccessAsync` 的 SpaceTight 分支逐层删除；
             * 其余物这里只保留，不再套普通“删除操作”，否则失败时最后一份证据也被删掉。
             */
            Mode = RestHandlingModes.Keep;

            /*
             * 「空间不足」模式**不走逐层回收这一档**：它有自己的那一支（`PostProcessSuccessAsync`
             * 的第一支，连**源包**都在每层当场删），措辞与门槛都是它自己那套。
             * ⛔ 置 false 只是"别让普通档那一支抢它的活"，那个模式的判据一个字都没改。
             */
            _host.LayerReclaimThisBatch = false;

            return;
        }

        Mode = _host.RunOptions != null
            ? RestHandlingModes.Normalize(_host.RunOptions.RestHandling)
            : RestHandlingModes.Normalize(_host.Settings.RestHandlingAfterVerify);

        /*
         * ⛔ **2026-10-15 口径变更：逐层回收与③页「删除操作」档位解绑**（用户 2026-10-14 口径逐字：
         * 「删除操作只针对**最后一层过程物**……**其他的过程物统一彻底删除**（因为他们都是成功的情况下），
         * 统一彻底删除」—— 见 `docs\大改动-2026-10-14-口径与冲突.md` §2.1 / §2.2 / §18.1）。
         *
         * 老口径（2026-10-03 的「B 档位分叉」）是"只有彻底删除档才逐层回收、不动其余物档集中进其余物、回收站档
         * 逐层进回收站"。回收站那一档已整条下线，而**"成功过的过程物"在哪个档下都不该留着**：
         * 用户 2026-10-14 的论证是「删掉的是**成功过的那几层**——它们可以从留下的那一层重做，所以不是证据」。
         * ⇒ 这一位恒为 true（普通档），动作恒为**当场永久删**。
         * ⛔ 那四道门槛（定稿 + 可证完整 + 未取消 + 「半套分卷」）一条都没放宽，改的只是"要不要做"。
         *
         * ⚠ **「最后一层过程物」不在这一位管**（用户口径：那一份默认**留下**，归③页「删除操作」那一档）：
         * 链停下来的那一层，它的输入包是"这一层要解的那个东西"，按红线**一个字节都不动** ——
         * 见 `_host.PurgeLayerSourcePackage` 的入口判据与 `PostProcessSuccessAsync` 里"只有跑成了才回收"。
         * ⇒ 「彻底删除」档目前对"最后一层过程物"还没有动作（**已知实现缺口**，下一刀）。
         */
        _host.LayerReclaimThisBatch = true;

        // 链内记账每批一清（它只服务"这一批这一条链"）。
        _host.ChainInnerLayerInputs.Clear();
    }

    /// <summary>
    /// 本批「其余物」的范围那一句话（口径的唯一来源）。
    ///
    /// <para>「空间不足」模式必须**另说一句**：那种模式下源包根本不进其余物（校验通过即删），
    /// 照普通档说"源包 + 过程物"会让用户以为源包还在其余物里躺着 ——
    /// 而他会去找一个根本不存在的东西（2026-09-27 真机上正是这类自相矛盾的日志挨了骂）。</para>
    /// </summary>
    internal string DescribeScopeForBatch()
    {
        if (!_host.SpaceTightThisBatch)
        {
            return RestHandlingLog.DescribeRestScope(_host.EffectiveSourceHandling);
        }

        return _host.SpaceTightKeepSourceThisBatch
            ? "过程物（源包在本模式下「一个字节都不动」：不搬进其余物、也不删除）"
            : "过程物（源包在本模式下不进其余物：定稿 + 校验通过后立刻永久删除）";
    }

    /// <summary>
    /// 批首写一条"其余物这一批会怎么处理"的日志（用户 2026-09-25 第 32 条：
    /// 他要的是**当场看得懂**，而不是跑完发现"其余物还在"却不知道为什么）。
    /// </summary>
    internal void LogBatchPolicy()
    {
        string mode = RestHandlingModes.Normalize(Mode);

        /*
         * 范围这句话**跟着源包那一档说**（2026-09-27 用户真机：他选了"源包留在原地"，
         * 这条却写"彻底删除（源包 + 过程物）"，两句自相矛盾）。判据来自 `_host.EffectiveSourceHandling`，
         * ⛔ 不许在这里另读一遍设置。
         */
        string scope = DescribeScopeForBatch();

        if (_host.SpaceTightThisBatch)
        {
            /*
             * 这一批的"为什么这么办"写在最前面（用户 2026-09-26 教训：同一份日志里两句话自相矛盾
             * 比少写一句更糟）。它必须同时说清三件事：覆盖了什么、**没写设置**、以及源包的下场。
             *
             * ⚠ 两档分开说（用户 2026-09-27 加了「不删原包」安全档）：会删的那一档说"立刻删除"，
             * 安全档说"一个字节都不动" —— 一条文案盖两种行为就是撒谎。
             */
            _host.AppendLog(
                "WARN",
                "「空间不足」模式（本次运行，不写设置）：本批并发由空间自己定"
                + "（慢盘上少开几个反而更快）；"
                + (_host.SpaceTightKeepSourceThisBatch
                    ? "「不删原包」也开着 —— 源包一个字节都不动（不搬、不删），"
                      + "只把过程物在任务成功后彻底删掉。⚠ 这一档不回收源包那份空间，需要的余量更大。"
                    : "每个包定稿 + 校验通过后立刻永久删除它的源包"
                      + "（不进回收站、不可恢复），收回的空间马上给后面的包用。")
                + "失败 / 部分完成 / 取消的任务一个字节都不删。");

            _host.AppendLog(
                "INFO",
                "空间不足模式的其余物：最后一层过程物固定保留；"
                + "成功层的源包/过程物按逐层回收删除，失败/部分完成的内容物不发布。");

            return;
        }

        if (string.Equals(mode, RestHandlingModes.Keep, StringComparison.OrdinalIgnoreCase))
        {
            _host.AppendLog(
                "INFO",
                $"其余物：本批保留（③页「删除操作」= 留下）。过程物先集中到落点下的「其余物」："
                + $"整条链全部成功时直接彻底删除（{scope}）；失败 / 部分完成 / 有未展开内层包时"
                + "留在其余物里，随时可手工删。");
            return;
        }

        _host.AppendLog(
            "WARN",
            $"其余物：本批任务成功后会彻底删除（{scope}，不进回收站、不可恢复）。"
            + "失败 / 部分完成 / 未展开的内层包也按这一档彻底删除。");
    }
}

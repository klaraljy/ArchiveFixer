using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using System;
using System.Collections.Generic;

namespace ArchiveFixer.Extraction;

/// <summary>
/// **试开结果判定**：把引擎的返回值读成"这一次成立 / 不成立 / 认出这是加密归档"。
///
/// <para><b>为什么独立成层</b>：同一句引擎原话在三条入口（整组试开 / 跨盘顺序试拼 / 通用分片顺序试开）
/// 里的含义必须一致 —— 尤其「引擎认得出这一组，只是头加密读不出清单」是**肯定**回答，
/// 而「只看到一段裸分片流」是**失败**。这两条判据只在这里回答，⛔ 上层不许自己再判一遍。</para>
///
/// <para>生命周期：纯静态、无状态；只读 <see cref="ArchiveListResult"/> / <see cref="ArchiveOperationResult"/>，
/// 不碰文件系统、不调引擎、不改请求。</para>
/// </summary>
internal static class VolumeProbeJudgment
{
    /// <summary>这一次完整性测试是不是真的成功。</summary>
    internal static bool IsSuccess(ArchiveOperationResult result) => result.Success;

    /// <summary>
    /// 这一次测试失败后**还要不要接着试下一个密码**（整组试开口径：密码错 / 部分完成 = 换密码再试）。
    /// </summary>
    internal static bool MayRetryWithNextPassword(ArchiveOperationResult result) =>
        result.IsWrongPassword || result.IsPartiallyCompleted;

    /// <summary>
    /// 这一次测试的失败是不是**密码这一档**（顺序试开 / 试拼口径：顺序错与密码错在引擎那里是同一句话）。
    /// </summary>
    internal static bool IsPasswordFailure(ArchiveOperationResult result) =>
        result.IsWrongPassword || result.IsNeedPassword;

    /// <summary>
    /// 列目录结果 → 试开结论（<see cref="VolumeProbeTrialResult.Failure"/> 为 null = 成立）。
    /// </summary>
    internal static VolumeProbeTrialResult JudgeList(ArchiveListResult list)
    {
        if (!list.Success)
        {
            /*
             * ===== 「引擎认得出这一组，只是头加密读不出清单」= **肯定**回答（用户 2026-09-29 真机副本取证）=====
             *
             * 现场：`amb909.7.01`（2 GiB，带 7z 魔数）+ `amb909.z.2`（1.89 GB 裸续卷）本来就是一组**完整**的两卷包，
             * 只是当年造包时开了 `-mhe`（文件名也加密）。对**卷齐**的这一组，7-Zip 报的是
             * `Cannot open encrypted archive. Wrong password?`（引擎的结构化结论 = `EncryptedHeaders`）；
             * 而"卷不齐 / 拿别的文件顶替"报的是 `Cannot open the file as [7z] archive` + `Unexpected end of archive`
             * （两条都在本地副本上实测过，见 AGENTS.md §11）。
             *
             * 老写法只认 `list.Success` → 这一组**永远**"试不成立" → 一个字节都不动 →
             * 管线随后如实报「分卷缺失」。可真正的原因是「缺密码」：**诊断说错了**，
             * 用户被指去满盘找卷，而卷就躺在同一个目录里。
             *
             * 凭什么敢把它当肯定回答：7z 的"下一份头"写在**最后一卷**里，位置由第一张卷第 0 字节的 Start Header
             * 给出（现场 `NextHeaderOffset = 4038274896`、头长 66 → 头正好落在第二卷的最后一字节上）。
             * 引擎能报出"这是加密归档"，就说明它按第一张卷声明的偏移**真读到了**那一份头 —— 这一组成立；
             * 它给不出的只是条目的名字。⛔ 判据仍然只由**这一次硬链接试开**回答（与"列得出清单"同一类证据），
             * 不是新造的第二套"这是不是分卷"判据；试不出（打不开 / 通用分片）照旧一个字节都不动。
             */
            if (string.Equals(list.ErrorType, EngineErrorTypes.EncryptedHeaders, StringComparison.OrdinalIgnoreCase))
            {
                return new VolumeProbeTrialResult(null, encryptedArchive: true);
            }

            return new VolumeProbeTrialResult(
                string.IsNullOrWhiteSpace(list.Message) ? "引擎列不出来" : list.Message,
                false);
        }

        /*
         * ⛔ `IsRawSplitStream` 必须当成**失败**：那是 7-Zip 说"这不是归档，是一段通用分片"
         * —— 也就是"后续卷没找到、只看到裸字节流"。它退出码是 0，不判这一位就会把
         * "什么都没读到"当成"顺序成立"（同一个坑在管线上踩过，见 ArchiveListResult 的注释）。
         */
        if (list.IsRawSplitStream)
        {
            return new VolumeProbeTrialResult("引擎只看到一段裸分片流（后续卷没接上）", false);
        }

        return new VolumeProbeTrialResult(null, false);
    }

    /// <summary>
    /// 试开结论 → <see cref="VolumeGroupResolver"/> 要的端口结论（<see cref="VolumeTrialOutcome"/>）。
    /// </summary>
    internal static VolumeTrialOutcome ToTrialOutcome(VolumeProbeOutcome outcome)
    {
        var ordered = new List<string>();

        foreach (VolumeCandidate volume in outcome.OrderedVolumes)
        {
            ordered.Add(volume.Path);
        }

        return new VolumeTrialOutcome
        {
            Confirmed = outcome.Confirmed,
            Attempted = outcome.Attempted,
            SkipReason = outcome.SkipReason,
            NeedsPassword = outcome.NeedsPassword,
            OrderedVolumePaths = ordered,
            Attempts = outcome.Attempts,
            Reason = outcome.Reason
        };
    }
}

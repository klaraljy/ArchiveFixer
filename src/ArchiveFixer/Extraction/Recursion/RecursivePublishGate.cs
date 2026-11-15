using ArchiveFixer.Engines;
using ArchiveFixer.Models;
using System;

namespace ArchiveFixer.Extraction;

/// <summary>
/// 这一层**真的解开了**之后的发布闸门：候选解完之后校验这一层的产物
/// （判据唯一出口 = <see cref="OutputVerifier.Verify"/>）。
///
/// <para>量产物 / 落点校验 / 探内层包 / 出层报告仍在既有出口；本类只承接"候选解完之后校验这一层"
/// 这一步，⛔ 不另写一套尺子。宿主 <see cref="RecursiveExtractor"/> 保留同名转发给候选循环用。</para>
/// </summary>
internal static class RecursivePublishGate
{
    /// <summary>
    /// **候选解完之后校验这一层的产物**（判据唯一出口 = <see cref="OutputVerifier.Verify"/>）。
    ///
    /// <para>与单层路 <c>VerifyStageProductsAsync</c> 用的是**同一个**出口、同一套规则
    /// （0/0 判失败、"实际多于预期也算通过"、拿不到清单 ⇒ 只做非空校验…），
    /// ⛔ 这里不许另写一套尺子。</para>
    ///
    /// <para>清单取"解开它的那个候选"自己列出来的那一份（<c>candidateManifest</c>）：
    /// 加密头包用错密码时引擎也会回一份残缺清单，拿它当预期会把解得好好的判成不完整 ——
    /// 与层报告里"采信成功那个候选的清单"是同一条口径。</para>
    /// </summary>
    internal static bool CandidateProductsVerified(
        WorkItem item,
        LayerManifest candidateManifest,
        out string message,
        out OutputVerificationResult verification)
    {
        /*
         * 清单取不到 ⇒ 与收尾那一处**逐字同一处置**：给一份 `Success = false` 的预期，
         * `OutputVerifier` 会退化成"只做非空校验"（⛔ 不是"期望 0 个 / 0 字节"——
         * 那样等于把任何产物都放过）。
         */
        ArchiveListResult expected = candidateManifest.Available
            ? candidateManifest.ToExpected()
            : new ArchiveListResult
            {
                Success = false,
                Message = candidateManifest.Reason
            };

        verification = OutputVerifier.Verify(item.Layer.OutputPath, expected);

        message = verification.Message;
        return verification.Verified;
    }
}

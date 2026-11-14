using System;
using System.Collections.Generic;

namespace ArchiveFixer.Models
{
    /// <summary>
    /// 批末汇总框的**三档严重度**（用户 2026-09-29 要求："最后的弹窗，他没有颜色的区分……
    /// 现在最后的总结也要分颜色，一般情况下是蓝白字黑，加上两种就是橙白字黑和红白字黑"）。
    ///
    /// <para>它只描述"这一批的结果有多糟"，配色由显示层（<c>Views\AppDialogWindow</c>）按它取。
    /// ⛔ 判定**只读机器终态**（<see cref="ArchiveTask.Outcome"/> / <see cref="ArchiveTask.OutputVerification"/>），
    /// 绝不比对 <c>StatusText</c> 里的中文文案（AGENTS.md §7：统计与裁决不得依赖中文比较 ——
    /// 文案改一个字就会让整批的颜色悄悄变错，而且没有任何编译错误）。</para>
    /// </summary>
    public enum BatchSummarySeverity
    {
        /// <summary>全成功：没有任何失败、也没有"部分完成 / 跳过 / 取消 / 没轮到"。蓝底白字。</summary>
        Success = 0,

        /// <summary>没失败，但有"部分完成 / 跳过 / 取消 / 没轮到"这类非致命问题。橙底白字。</summary>
        Partial = 1,

        /// <summary>有失败（含校验判否 / 空间不足 / 密码试完）。红底白字。</summary>
        Failed = 2
    }
}

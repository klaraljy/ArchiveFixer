using ArchiveFixer.Helpers;
using ArchiveFixer.Security;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace ArchiveFixer.Storage;
/// <summary>
/// "这次删除没有发生"的原因。枚举给机器判定，中文说明一律放各结果的 <c>Message</c>。
///
/// 排在前面的都是**安全前置**（见 <see cref="DeleteSafetyGuard"/>），
/// 出现任何一个都表示"一个字节都没动"，调用方可以放心地告诉用户"没有删除任何东西"。
/// </summary>
public enum DeleteBlockReason
{
    /// <summary>删除成功，没有任何阻拦。</summary>
    None = 0,

    /// <summary>调用方没有传入"已获用户确认"标志（默认拒绝）。</summary>
    NoConfirmation,

    /// <summary>没有指定允许的根目录 —— 无法校验落点，拒绝。</summary>
    RootNotSpecified,

    /// <summary>目标路径为空。</summary>
    EmptyTarget,

    /// <summary>路径无法规范化（非法字符 / 超长 / 带 \\?\ 前缀等），"确认不了"就是拒绝。</summary>
    UnresolvablePath,

    /// <summary>目标就是允许根本身（那等于把整棵树端掉）。</summary>
    IsAllowedRootItself,

    /// <summary>目标是驱动器根（<c>C:\</c>）。</summary>
    IsDriveRoot,

    /// <summary>目标不在允许根之内（或无法确认在之内）。</summary>
    OutsideAllowedRoot,

    /// <summary>目标不存在（可能已被移动或删除）；本次没有执行删除。</summary>
    TargetNotFound,

    /// <summary>目标本身是符号链接 / 联接点。</summary>
    TargetIsReparsePoint,

    /// <summary>目标到允许根之间的某一级目录是符号链接 / 联接点（落点可能被解析到根之外）。</summary>
    ReparsePointInPath,

    /// <summary>目标目录的子树里有符号链接 / 联接点（递归删除可能跟着跑出去）。</summary>
    ReparsePointInSubtree,

    /// <summary>安全检查读不到文件属性，无法确认安全 —— 保守拒绝。</summary>
    SafetyProbeUnavailable,

    /// <summary>子树条目数超过安全检查上限，没扫完就不能保证安全 —— 保守拒绝。</summary>
    SubtreeTooLarge,

    /// <summary>回收站不可用（网络盘 / 策略禁用 / 卷没有回收站）：**不降级为永久删除**。</summary>
    RecycleBinUnavailable,

    /// <summary>删除动作本身失败（占用、权限、系统错误、用户取消）。</summary>
    DeleteFailed,

    /// <summary>系统报告成功，但目标仍然存在 —— 按失败处理，不假装删掉了。</summary>
    TargetStillExists,

    /// <summary>调用参数不成立（没有任何有效的删除选项）。</summary>
    InvalidRequest
}

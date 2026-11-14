using System;

namespace ArchiveFixer.Services.Password;

/// <summary>
/// 密码列表记忆的**失败模型**：一句给人看的原因、一个可选的界面通知、以及"本次禁止写回"这道闸门。
///
/// <para>为什么把它单列：这些状态是同一件事的三面 —— 出问题时既要留一句人话（界面提示条 / 日志），
/// 又要在必要时通知界面（<see cref="RememberedListWarning"/>），还要能从此**禁止再写**
/// （<see cref="TextStoreWriteBlocked"/>：拿旧记忆覆盖用户真实密码列表就是事故，见 2026-11-01 真机）。
/// 以前它们散在服务的字段里，谁都可以直接赋值；现在只有这里能改，读写口径只有一处。</para>
/// </summary>
internal sealed class PasswordListHealth
{
    /// <summary>
    /// 记忆相关的**一句人话**（读不出来 / 写不下去时的原因）。空 = 一切正常。
    /// 界面在提示条上显示它，**不弹错误框、不阻断**（用户 2026-09-24 要求）。
    /// </summary>
    internal string LastListWarning { get; private set; } = string.Empty;

    /// <summary>
    /// 本次启动**禁止再写 passwords.txt / 元数据 / 加密记忆**。
    ///
    /// <para>只在一种情况下为 true：主清单不见了、同目录的 <c>.bak</c> 也恢复不了，
    /// 但还有"主清单确实存在过"的证据（元数据/备份）。这一档宁可不写，也不能写错。</para>
    /// </summary>
    internal bool TextStoreWriteBlocked { get; set; }

    /// <summary>记忆出问题时通知界面（只给提示条用）。订阅者少一处也不影响正确性 —— 事实本身在 <see cref="LastListWarning"/> 里。</summary>
    internal event Action<string>? RememberedListWarning;

    /// <summary>加载开始时清掉上一轮的原因（这不是新失败，所以**不**触发通知）。</summary>
    internal void ResetWarning()
    {
        LastListWarning = string.Empty;
    }

    /// <summary>
    /// 记一句人话并通知界面。凡是"用户需要知道为什么"的失败/降级都走这里，
    /// 避免出现"字段改了、事件没发"或反过来的两本账。
    /// </summary>
    internal void Warn(string message)
    {
        LastListWarning = message;
        RememberedListWarning?.Invoke(message);
    }
}

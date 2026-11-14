namespace ArchiveFixer.Storage;

/// <summary>读记忆的结论。**失败时条目一律为空**，调用方按"没有可用记忆"继续。</summary>
public enum PasswordListLoadStatus
{
    /// <summary>还没有这个文件（第一次运行）：不是错误，也不是"记忆为空"。</summary>
    NoFile = 0,

    /// <summary>成功读到了一份可用的记忆。</summary>
    Loaded = 1,

    /// <summary>解不开：换机器 / 换 Windows 账号 / 文件被改过 / 不是本机加密的。</summary>
    Undecryptable = 2,

    /// <summary>不是我们的格式（版本号读不懂、JSON 结构对不上）。</summary>
    NotOurFormat = 3,

    /// <summary>读不动（权限、被占用、半截文件、IO 错误）。</summary>
    Unreadable = 4,

    /// <summary>用户关掉了「记住密码列表」：**不读**。</summary>
    Disabled = 5
}


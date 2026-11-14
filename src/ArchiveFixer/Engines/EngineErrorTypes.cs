namespace ArchiveFixer.Engines;

/// <summary>
/// 引擎层**跨引擎共用**的错误类型词表（设计.md §二十 的回退规则用的就是这一组名字）。
///
/// 为什么单独一份：这些字符串是"引擎 → 调度层"的契约 ——
/// <c>ExtractionCoordinator</c> 靠 <c>"WrongPassword"</c> 驱动密码候选循环、
/// 靠 <c>"EncryptedHeaders"</c> 落到"文件名已加密"状态。每个引擎各自写一份字面量，
/// 就会出现"7-Zip 写对了、第二个引擎拼错一个字母"这种**只在真样本上才暴露**的分歧。
/// 所以：**值只在这里出现**，两个引擎的解析器都引用它。
/// （解析逻辑本身仍然各待在自己的引擎目录里 —— AGENTS.md §3.1 禁止项②的边界不变。）
/// </summary>
public static class EngineErrorTypes
{
    /// <summary>密码错误 / 缺正确密码。**解压与测试路径上它是密码候选循环的驱动信号**，不要改写它。</summary>
    public const string WrongPassword = "WrongPassword";

    /// <summary>需要密码但当前没给（内容可读、条目加密）。</summary>
    public const string NeedPassword = "NeedPassword";

    /// <summary>归档加密了**文件名**（RAR <c>-hp</c> / 7z <c>-mhe</c>）：连条目名都读不出来。</summary>
    public const string EncryptedHeaders = "EncryptedHeaders";

    /// <summary>归档损坏 / 校验和不符 / 数据不全。</summary>
    public const string CorruptedArchive = "CorruptedArchive";

    /// <summary>
    /// **引擎同一句话里同时给出了"密码不对"与"数据坏了"两种可能**，无法只挑一个下结论。
    ///
    /// <para>为什么要单独一类（用户 2026-09-30 真机，RAR 侧的 <c>Item37SafetyTests</c> 场景）：
    /// RAR 1.5–4.x 的 <c>-p</c> 包在**密码不对**时，UnRAR 根本分不出"密码错"与"数据坏" ——
    /// 英文版打 <c>Checksum error in the encrypted file X. Corrupt file or wrong password.</c>、
    /// 中文版打「在加密文件 X 里校验和错误。文件已损坏或密码错误。」，两者退出码都是
    /// <c>3</c>（<see cref="UnRarExitCodes.DataError"/>，见 <c>UnRarOutputParser</c> 的判定）。
    /// 判成 <see cref="CorruptedArchive"/> 的后果是**当场不再试其余密码候选**（"换密码没有帮助"），
    /// 11 个候选只试了第 1 个；判成 <see cref="WrongPassword"/> 又会把真损坏说成密码错。
    /// 所以它既不是"损坏"也不是"密码错"，而是**两义**。</para>
    ///
    /// <para>它与 7-Zip 侧同一个口径：`CRC Failed in encrypted file. Wrong password?` 那句
    /// 在 <c>SevenZipOutputParser</c> 里也从不单独定原因（见 <c>Item37SafetyTests</c> 与
    /// <c>SevenZipExitCodeTests</c>）。两个引擎各自的**检测**方式不同（7-Zip 靠原话关键字、
    /// RAR 靠"退出码 3 + 这个包确实是加密包"这两条结构化事实），但**处置只有一套**：
    /// 继续试候选 + 结论与日志同时保留两种可能 + 把引擎原话带出来。</para>
    /// </summary>
    public const string PasswordOrCorrupted = "PasswordOrCorrupted";

    /// <summary>分卷缺失，且**已经确知缺了哪几卷**（不变量 7 要求报出来）。</summary>
    public const string VolumeMissing = "VolumeMissing";

    /// <summary>文件名像分卷、但首卷看不到（数不出缺号，只能告诉用户"先找齐第一卷"）。</summary>
    public const string MissingFirstVolume = "MissingFirstVolume";

    /// <summary>引擎不认识 / 不支持这个格式（可换引擎）。</summary>
    public const string UnsupportedFormat = "UnsupportedFormat";

    /// <summary>引擎不支持某个特性（可换引擎）。</summary>
    public const string UnsupportedFeature = "UnsupportedFeature";

    /// <summary>引擎当前不可用（外部程序没装 / 被删）。</summary>
    public const string EngineUnavailable = "EngineUnavailable";

    /// <summary>引擎解析器拒绝（输出对不上预期）。</summary>
    public const string ParserRejected = "ParserRejected";

    /// <summary>已知兼容性问题（可换引擎）。</summary>
    public const string KnownCompatibilityIssue = "KnownCompatibilityIssue";

    /// <summary>权限不足。</summary>
    public const string AccessDenied = "AccessDenied";

    /// <summary>输出路径冲突。</summary>
    public const string OutputConflict = "OutputConflict";

    /// <summary>路径过长。</summary>
    public const string PathTooLong = "PathTooLong";

    /// <summary>命令行参数错误。</summary>
    public const string CommandLineError = "CommandLineError";

    /// <summary>内存不足。</summary>
    public const string OutOfMemory = "OutOfMemory";

    /// <summary>
    /// **磁盘空间不足导致写不下去**（用户 2026-09-27 真机：递归内层包撞空间不足，
    /// 批末诊断却把这一单归到"其他"）。
    ///
    /// <para>为什么不复用 <see cref="AccessDenied"/>：两者的**用户动作完全不同** ——
    /// 权限不足要去改目录权限 / 关掉占用的程序，空间不足要去清空间 / 换盘（AGENTS.md §11.3
    /// 那一整套空间口径也都是按"空间不足"这一档给建议的）。混成一档就会把用户引向错的方向。</para>
    ///
    /// <para>它必须**排在"权限不足"之前**判：7-Zip 写不下去时打的是
    /// <c>ERROR: Can not create file : &lt;路径&gt;</c>，而紧跟着的系统错误文本才是
    /// <c>There is not enough space on the disk.</c> —— 只看前半句会误判成权限不足。
    /// 判据仍只读**引擎 / 系统给的原话**（英文关键字），⛔ 不比对任何中文文案。</para>
    ///
    /// <para>不可换引擎那一档（与 <c>AccessDenied</c> 同类）：换个引擎写同一个盘照样写不下去。</para>
    /// </summary>
    public const string NoDiskSpace = "NoDiskSpace";

    /// <summary>退出码 1 一类：发生非致命错误，产物是**部分**的（绝不算成功，不变量 6）。</summary>
    public const string NonFatalError = "NonFatalError";

    /// <summary>发生致命错误但分不出更具体的原因。</summary>
    public const string FatalError = "FatalError";

    /// <summary>用户取消。</summary>
    public const string Cancelled = "Cancelled";

    /// <summary>超时。</summary>
    public const string TimedOut = "TimedOut";

    /// <summary>没有结论时的落点，**不编造原因**。</summary>
    public const string UnknownError = "UnknownError";
}

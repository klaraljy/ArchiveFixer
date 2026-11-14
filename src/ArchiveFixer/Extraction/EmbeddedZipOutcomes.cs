namespace ArchiveFixer.Extraction;

/// <summary>
/// 直读的**失败 / 回落模型**：把"为什么没做成"落成既有结果类型上的那几个事实位，只此一处。
///
/// <para>两种失败语义刻意分开（调用方靠它们决定"回落到抠取 + 7z"还是"直接判失败"）：</para>
/// <list type="bullet">
/// <item><description><see cref="Unsupported"/>：**回落信号**。判据不成立（截断 / ZIP64 缺字段 /
/// 方法不支持 / 老式 ZipCrypto …），抠出来交给 7z 仍可能成功，所以调用方可以原地回落；</description></item>
/// <item><description><see cref="Failure"/>：**已开始做之后的失败**（数据层损坏 / 预算 / 落点 / 磁盘）。
/// 这类回落只会白拷一份等大的副本，所以调用方不许再读一遍。</description></item>
/// </list>
///
/// <para>⛔ 这里只装配结果对象，不做任何判断：判断在扫描 / 解析 / 写盘各处，结论原样带过来。</para>
/// </summary>
internal static class EmbeddedZipOutcomes
{
    /// <summary>不支持直读（回落信号）；<paramref name="reason"/> 为空时给一句兜底说明。</summary>
    internal static EmbeddedZipProbeResult Unsupported(string reason)
    {
        return new EmbeddedZipProbeResult
        {
            Supported = false,
            Reason = string.IsNullOrWhiteSpace(reason) ? "直读不支持这个内嵌归档" : reason
        };
    }

    /// <summary>
    /// 失败结果。<paramref name="unsupported"/> = 调用方可以回落；<paramref name="corrupted"/> =
    /// 数据层已确认损坏（CRC32 对不上），**不许**回落重复读一遍。
    /// </summary>
    internal static EmbeddedZipExtractResult Failure(string message, bool unsupported, bool corrupted = false)
    {
        return new EmbeddedZipExtractResult
        {
            Success = false,
            Unsupported = unsupported,
            Corrupted = corrupted,
            Message = string.IsNullOrWhiteSpace(message) ? "直读失败" : message
        };
    }
}

namespace ArchiveFixer.Extraction;

/// <summary>
/// 用哪种字节编码把密码字符串变成 <b>密钥派生的输入字节</b>。
///
/// <para><b>为什么这不是"可选优化"而是必须有的两档</b>（2026-09-25 真机取证，见 <c>修改日志.md</c> 第 29 条）：
/// ZIP 的 AES 加密没有"密码字符串"这个概念，只有<b>字节</b> —— 同一个中文密码，
/// 打包方写进去的字节不同，派生出来的密钥就完全不同。实测：</para>
/// <list type="bullet">
/// <item><description>WinRAR（以及 7-Zip 的解码侧）用 **ANSI 代码页（中文 Windows = 936/GBK）** 的字节：
/// 本机造的 AES-256 包用 <c>中文密码ABC</c>，按 GBK 字节派生出的校验值 <c>0x0B81</c> 与包里存的一致，
/// 按 UTF-8 派生的是 <c>0x391E</c>（对不上）。</description></item>
/// <item><description>百度网盘分享打包出来的 ZIP 用的是 **UTF-8** 字节（它的通用位标志 bit11 就写着
/// <c>Encrypt UTF8</c>，7-Zip 列表里能直接看到）。</description></item>
/// </list>
///
/// <para>而 7-Zip <b>不管那个 bit11</b>：实测把 WinRAR 的包手工置上 bit11，7-Zip 照样能解开
/// （说明它始终按 ANSI 派生）。结论：**UTF-8 打包的加密包，7-Zip 永远打不开，密码再对也报"密码错误"** ——
/// 这正是用户 2026-09-25 报的"密码还是对不上，我那个密码是中文的就没有办法了吗"。
/// 所以本读取器两档都试，谁通过校验用谁。</para>
/// </summary>
public enum ZipAesPasswordEncoding
{
    /// <summary>UTF-8 字节（百度网盘分享包、7-Zip 自己造的 7z 包这套习惯）。</summary>
    Utf8,

    /// <summary>系统 ANSI 代码页字节（中文 Windows = 936/GBK；WinRAR、老式 ZIP 工具这套习惯）。</summary>
    AnsiCodePage
}

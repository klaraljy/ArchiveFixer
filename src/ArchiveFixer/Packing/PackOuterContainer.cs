namespace ArchiveFixer.Packing
{
    /// <summary>
    /// 外层容器三选一（用户 2026-09-23 决定）。
    ///
    /// <para>它取代了早先的 <c>bool SkipOuterRar</c>：那句布尔只有两种状态，而用户要的是三选一，
    /// 于是"外层用 7z 容器"这条路根本没有位置（勾了 = 不做外层，不勾 = 必须 rar）。
    /// <b>默认仍是 <see cref="Rar"/></b>（用户 2026-09-22 的原始要求：结果是一个带密码的 rar）。</para>
    ///
    /// <para>为什么"外层 7z"必须存在：<c>Rar.exe</c> / <c>WinRAR.exe</c> 是**共享软件**，
    /// RARLAB 的 EULA 明确禁止随其它软件包分发（AGENTS.md §3.1），所以"本机没装 WinRAR"是一种
    /// 常态而不是异常。此时除了"装 WinRAR"与"什么都不做"，还必须有第三条**不需要额外安装**的路
    /// —— 7-Zip 是 LGPL、本来就随程序分发。</para>
    /// </summary>
    public enum PackOuterContainer
    {
        /// <summary>外层做 <c>B.rar</c>（<b>默认</b>）—— 需要本机**已装**的 <c>Rar.exe</c>，程序绝不分发它。</summary>
        Rar = 0,

        /// <summary>外层做 <c>B.7z</c>（<c>-mhe=on</c>：连文件名一起加密）—— 用内置 7-Zip，**无需额外安装**。</summary>
        SevenZip = 1,

        /// <summary>不做外层：结果就是 B 里的那组 7z 加密分卷（本机没装 WinRAR 时最省的出路）。</summary>
        None = 2
    }
}

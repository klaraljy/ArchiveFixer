namespace ArchiveFixer.Engines;

/// <summary>
/// 7-Zip 的两个文件（<c>7z.exe</c> + <c>7z.dll</c>）总是成对解析：自选那一档的 dll 取 exe 所在目录，
/// 内置那一档两个都在 <c>tools\7zip</c>。
///
/// <para>做成一条结果而不是两个独立字段：旧实现里"exe 解析了、dll 还是空"的半截状态要靠两个 null
/// 一起判，成对返回之后这种状态不可能出现。</para>
///
/// <para>⛔ 即使文件不存在也返回"应该在哪"（与旧口径一致）：是否可用由
/// <c>SevenZipExists</c> / <c>SevenZipDllExists</c> 现查，不由这里抛异常。</para>
/// </summary>
internal sealed record SevenZipLocation(
    string ExecutablePath,
    string DllPath,
    bool IsCustom);

namespace ArchiveFixer.Engines;

/// <summary>
/// 纯函数式的进度解析签名：给一段输出，回答"这是不是进度，是的话到哪了"。
/// **实现只允许出现在各自的引擎目录里**（<c>Engines/SevenZip/</c>、<c>Engines/WinRar/</c>）。
/// </summary>
public delegate ArchiveProgress? EngineProgressParser(string segment);

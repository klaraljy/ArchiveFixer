using System.Collections.Generic;

namespace ArchiveFixer.Engines
{
    /// <summary>列目录结果。资源预算（M5）与"解压前预检路径"都要靠它。</summary>
    public sealed class ArchiveListResult
    {
        public bool Success { get; init; }

        public IReadOnlyList<ArchiveEntry> Entries { get; init; } = new List<ArchiveEntry>();

        /// <summary>所有文件条目的解压后大小合计（目录不计）。</summary>
        public long TotalUncompressedSize { get; init; }

        public int FileCount { get; init; }

        public int DirectoryCount { get; init; }

        public bool IsEncrypted { get; init; }

        public bool IsMultiVolume { get; init; }

        /// <summary>引擎读到的卷号（1 起；0 = 没读到）。单卷归档也可能报 volume 1。</summary>
        public int VolumeIndex { get; init; }

        /// <summary>
        /// 引擎把这一单当成**通用分片流**（7-Zip 的 <c>Type = Split</c>）而不是一个归档。
        ///
        /// <para>为什么要单独标出来（用户 2026-09-25 第 36 条追加的真机取证）：一组分卷的**第一卷**名字被改坏时
        /// （他那个包里是 <c>Code Complete-BZ.7z(删掉.001</c> —— 塞了「删掉」两个字、右括号还被吃掉了），
        /// 7-Zip 顺着名字找不到同组的后续卷，就退化成"通用分片"：清单里只有一条，
        /// **条目就是文件自己**（名字 = 文件名去掉 <c>.001</c>，大小 = 这个卷本身），
        /// 而且**照解不误、退出码 0** —— 解出来是一个 5 GB 的垃圾文件，而结果校验拿"清单那一条"
        /// 与"盘上那一个文件"比，两边一样大，还会判**通过**（"成功"这个词被用错）。
        /// 有了这一位，管线就能在**写盘之前**把它判成「分卷缺失」（不变量 7、不变量 6 的同一精神）。</para>
        ///
        /// <para>默认 false：假引擎与其它引擎不置这一位，它们的行为与从前逐字相同。</para>
        /// </summary>
        public bool IsRawSplitStream { get; init; }

        /// <summary>引擎名字 + 版本，用于"结果可追溯"（AGENTS.md §6 第 14 条）。</summary>
        public string EngineId { get; init; } = string.Empty;

        public string EngineVersion { get; init; } = string.Empty;

        /// <summary>失败原因（成功时为空）。</summary>
        public string Message { get; init; } = string.Empty;

        public string ErrorType { get; init; } = "None";

        public static ArchiveListResult Failure(string errorType, string message, string engineId, string engineVersion)
        {
            return new ArchiveListResult
            {
                Success = false,
                ErrorType = errorType,
                Message = message,
                EngineId = engineId,
                EngineVersion = engineVersion
            };
        }
    }
}

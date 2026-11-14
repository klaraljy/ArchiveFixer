using ArchiveFixer.Models;
using ArchiveFixer.Storage;
using System;
using System.IO;

namespace ArchiveFixer.Packing
{
    public enum PackingTargetMode
    {
        /// <summary>
        /// **默认**：源文件夹（或单文件）**旁边**，也就是源所在的那个目录。
        ///
        /// <para>用户原话："默认放在源文件目录旁边"（他当天先说了"默认不去选择位置就将压缩至选择的
        /// 目录位置"，随后明确纠正为源旁边 —— 从此**不跟①页「输出位置」**）。</para>
        /// </summary>
        Local = 0,

        /// <summary>指定位置（用户在⑤页挑一个目录）。</summary>
        Custom = 1
    }

    /// <summary>打包里的**原包操作**（用户 2026-09-26；⛔ 与解压那套完全独立、各自记忆）。</summary>
}

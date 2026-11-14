using ArchiveFixer.Helpers;
using ArchiveFixer.Security;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace ArchiveFixer.Storage;
/// <summary>
/// 删除前的安全检查所需要的文件系统信息。
///
/// 抽成接口只为一件事：**让安全检查可以被单测**。造符号链接要管理员权限、造联接点要外部命令，
/// 在测试里都不稳定（见 ExtractionMaintenanceTests 里同样的说明），
/// 所以"路径上有联接点"这类判定必须能靠注入的假探针来验证。
/// </summary>
public interface IDeleteFileSystemProbe
{
    /// <summary>读属性；不存在 / 读不到一律返回 null（调用方按"无法确认"处理）。</summary>
    FileAttributes? TryGetAttributes(string? path);

    /// <summary>列目录里的全部条目（文件 + 目录）；读不到返回 false。</summary>
    bool TryListChildren(string? directory, out IReadOnlyList<string> children);

    /// <summary>列目录里的直接子目录；读不到返回 false。</summary>
    bool TryListChildDirectories(string? directory, out IReadOnlyList<string> directories);
}

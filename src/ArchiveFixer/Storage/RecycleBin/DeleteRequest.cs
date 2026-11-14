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
/// <summary>一次删除请求：删哪个路径、为什么删。</summary>
public sealed class DeleteRequest
{
    public string Path { get; init; } = string.Empty;

    /// <summary>为什么要删。会原样写进删除日志（§3.2 要求日志含"路径 + 理由"）。</summary>
    public string Reason { get; init; } = string.Empty;

    public DeleteRequest()
    {
    }

    public DeleteRequest(string path, string reason = "")
    {
        Path = path ?? string.Empty;
        Reason = reason ?? string.Empty;
    }
}

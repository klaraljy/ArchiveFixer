using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Extraction;
/// <summary>目标位置占用探测。默认查真实文件系统；单测可注入"什么都占不到"的实现做纯规划。</summary>
public interface IArtifactTargetProbe
{
    /// <summary>目标位置是否已被文件或目录占用。</summary>
    bool Exists(string path);

    /// <summary>源是不是目录（决定加序号时怎么拆扩展名）；null = 不知道。</summary>
    bool? IsDirectory(string path);
}

using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Extraction
{    /// <summary>"什么都占不到"的探测：用于纯规划（测试与"只看计划不碰盘"的场景）。</summary>
    public sealed class EmptyArtifactTargetProbe : IArtifactTargetProbe
    {
        public static EmptyArtifactTargetProbe Instance { get; } = new();

        public bool Exists(string path)
        {
            return false;
        }

        public bool? IsDirectory(string path)
        {
            return null;
        }
    }

}

using ArchiveFixer.Helpers;
using ArchiveFixer.Security;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace ArchiveFixer.Storage
{    /// <summary>判断"这个路径能不能进回收站"。同样可注入，便于验证"不可用时不降级"。</summary>
    public interface IRecycleBinAvailabilityProbe
    {
        /// <summary>可用返回 true；不可用时 <paramref name="reason"/> 说明原因。</summary>
        bool IsRecycleBinUsable(string fullPath, out string reason);
    }

}

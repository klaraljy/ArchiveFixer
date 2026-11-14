using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;


namespace ArchiveFixer.ViewModels
{
    /// <summary>
    /// 导入路径的语义（用户 2026-09-24 第 12 条亲自拍板）。
    ///
    /// <para>用户原话："每次我新选择了其他的，无论是什么，你都要将列表彻底清空" ——
    /// 「添加文件 / 添加文件夹」= **替换**（先把整张表清掉再加），
    /// 想往现有列表里加只能用显式入口「文件 → 追加到列表」（<see cref="Append"/>）。</para>
    ///
    /// <para>为什么替换必须是默认：旧行为是"追加 + 按路径去重"，于是用户上一次选的那批
    /// 一直赖在列表里 —— 他以为"我重新选了，列表就是我刚选的这些"，
    /// 点一键处理却把旧的那批一起跑了（原话："你为什么列表里面还是有"）。</para>
    /// </summary>
    public enum ImportMode
    {
        /// <summary>替换整张表（默认，「添加文件 / 添加文件夹」与拖放都走这条）。</summary>
        Replace = 0,

        /// <summary>追加到现有列表（只有菜单「文件 → 追加到列表」这一条显式入口）。</summary>
        Append = 1
    }
}


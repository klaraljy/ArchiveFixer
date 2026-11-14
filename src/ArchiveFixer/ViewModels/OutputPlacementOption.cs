using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Input;


namespace ArchiveFixer.ViewModels;

/// <summary>
/// 输出位置：**两档互斥**（用户 2026-09-24 第 13 条删掉了"摊平"那两档）。
///
/// <para>
/// 为什么不直接摆一个复选框：产品里"落到哪"本来就是二选一，
/// 落到设置里仍然是**原来那两个布尔**（不新增设置项）。
/// 旧版本的四选一里有两档已被用户亲手删除（解压到当前目录 / 直接解到指定目录），
/// 界面上再也选不到它们；旧配置里剩下的那两个布尔组合由
/// <see cref="ArchiveFixer.Extraction.OutputPlacement.FromLegacyFlags"/> 迁移到这两档。
/// </para>
/// </summary>
public enum OutputPlacementOption
{
    /// <summary>以包名命名的子文件夹（111\222.rar → 111\222\内容物）。默认。</summary>
    ArchiveNamedSubfolder,

    /// <summary>指定位置 + 同名子文件夹（→ 333\222\内容物；选中文件夹时用该文件夹的名字）。</summary>
    CustomNamedSubfolder
}


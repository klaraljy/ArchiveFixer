using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using ArchiveFixer.Views;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;


namespace ArchiveFixer.ViewModels
{
    /// <summary>
    /// 「本次选项」面板的询问结果（规格 <c>docs/输出与整理模型.md</c> §9）。
    ///
    /// <para>
    /// 为什么要区分"没弹"和"用户取消"：两者的正确行为**相反** ——
    /// 没弹（无 UI 宿主 / 勾过"不再询问"）要"按设置走、继续跑"；
    /// 用户明确取消则**这一次不许开跑**。把它们都压成 <c>null</c> 就会出现
    /// "用户在面板上点了取消，结果整批照跑"这种最不该有的形态。
    /// </para>
    /// </summary>
    internal enum OneClickOptionsOutcome
    {
        /// <summary>没弹面板：无 UI 宿主，或本次运行里已经勾过"以后不再询问"。**按设置走，继续跑**。</summary>
        NotShown = 0,

        /// <summary>用户确认了本次选项。</summary>
        Confirmed = 1,

        /// <summary>用户取消（点「取消」或直接关掉面板）。**这一次一键处理不执行**。</summary>
        Cancelled = 2
    }
}


using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;

namespace ArchiveFixer.Extraction
{    /// <summary>一条待归置项被跳过的原因。</summary>
    public enum ArtifactSkipReason
    {
        /// <summary>没有跳过。</summary>
        None = 0,

        /// <summary>源路径为空。</summary>
        EmptySource,

        /// <summary>同一个源被规划了两次。</summary>
        DuplicateSource,

        /// <summary>源本来就在其余物目录里（已经归置过，不需要再搬）。</summary>
        SourceInsideArtifactDirectory,

        /// <summary>
        /// 源在**工作区那棵树**里（程序自己的中间产物，不是用户的源包）—— 用户 2026-09-30：
        /// 工作区默认建在目标目录里面，任何"把东西搬出目标目录"的动作都必须排除它。
        /// </summary>
        SourceInsideWorkspace,

        /// <summary>相对路径不合法（越界 <c>..</c> / 绝对路径 / 盘符 / UNC / 保留名 / 结尾点空格等）。</summary>
        InvalidRelativePath,

        /// <summary>源与目标同一个位置（搬到原地）。</summary>
        SourceEqualsTarget,

        /// <summary>目标落在源目录内部（会自己套自己）。</summary>
        TargetInsideSource,

        /// <summary>源文件已经不存在（用户在批次中途删了 / 上一轮已经搬走）。</summary>
        SourceMissing,

        /// <summary>
        /// 源是**我们自己产出的内容物**（这一单是续解出来的**内层包**）—— 它不是用户的**原包**，
        /// ⛔ 不参与源包处理、一个字节都不搬（用户口令：「**原包就是原包**」）。
        ///
        /// <para>真机 `EEEE` 2026-10-09 19:14 那一趟：`111.rar` 解出来的入口包 `111.zip` 落在
        /// `…\111\111\111.zip`，下一轮它自己成了一单 ⇒ 这里把**我们刚产出的入口包**当"源包"搬进其余物，
        /// 批末「彻底删除」再把它删掉 ⇒ 组入口包没了、第一大步永远闭不了环。</para>
        /// </summary>
        SourceIsOurOwnContent,

        /// <summary>
        /// 源是**分卷成员**（内容自证）—— 其余物永远不能当第二大步入口：这一片必须原地留着，
        /// 由第二大步组认领/收片；⛔ 不搬进其余物。
        /// </summary>
        VolumeGroupMemberLeaveInPlace,

        /// <summary>没有给出目标目录，整批无法规划。</summary>
        TargetDirectoryMissing
    }

}

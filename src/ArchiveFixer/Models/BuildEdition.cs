using System;

namespace ArchiveFixer.Models
{
    /// <summary>
    /// **这一份构建是哪个发行档**（用户 2026-09-27 定：发行版要把"测试期专用"的开关去掉，本地开发版留着）。
    ///
    /// <para>背景：①页那个「不删原包」是**测试期专用**的安全档（他原话："这个我想好像在测试完成之后就不用留着……
    /// 打包软件的时候我会让你删掉的"、"发行版要删，我们本地的不用"）。
    /// 但本地那套绿色目录要接着用真机测试，直接删代码会把测试能力一起删掉 ——
    /// 所以做成**编译期开关**：`ArchiveFixerEdition=Release` 时不带这个功能，其余情况都带。</para>
    ///
    /// <para>⛔ 两条纪律：</para>
    /// <list type="number">
    /// <item><description>只有 <c>scripts/package.ps1</c>（生成发行包）会传 <c>ArchiveFixerEdition=Release</c>；
    /// 本机构建 / 测试 / `dotnet run` 一律是开发版 —— 谁也别在 csproj 里把它设成默认值。</description></item>
    /// <item><description>判据**只准读这里**（`KeepSourceOptionEnabled`）：界面显不显示那颗勾、
    /// 运行期要不要认这个开关，都从这一处出发 —— ⛔ 不许在别处再写一遍 `#if`。</description></item>
    /// </list>
    /// </summary>
    internal static class BuildEdition
    {
#if RELEASE_EDITION
        /// <summary>发行版：不带「不删原包」安全档（那个开关是测试期专用的）。</summary>
        internal const bool KeepSourceOptionEnabled = false;

        /// <summary>发行档的名字（进「帮助 → 关于」与日志，方便一眼看出跑的是哪一份）。</summary>
        internal const string Name = "发行版";
#else
        /// <summary>开发版（本机测试用）：带「不删原包」安全档。</summary>
        internal const bool KeepSourceOptionEnabled = true;

        /// <summary>发行档的名字（进「帮助 → 关于」与日志，方便一眼看出跑的是哪一份）。</summary>
        internal const string Name = "开发版";
#endif
    }
}

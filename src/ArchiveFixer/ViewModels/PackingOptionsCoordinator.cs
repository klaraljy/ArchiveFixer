using ArchiveFixer.Engines;
using ArchiveFixer.Packing;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// ⑤打包页的「选项」那一组：页面输入 → 请求、开始之前的输入校验、确认弹窗那两档的合并与写回设置。
///
/// <para>⛔ 这里是请求的**唯一出口**：摘要、确认弹窗预览、真正开跑都从这里拿
/// <see cref="PackingRequest"/>，别处不许自己拼。密码只在这里被读进请求，**不落盘、不进日志**。</para>
/// </summary>
internal sealed class PackingOptionsCoordinator
{
    private readonly PackingViewModel _vm;
    private readonly ToolLocator _tools;

    internal PackingOptionsCoordinator(PackingViewModel viewModel, ToolLocator tools)
    {
        _vm = viewModel;
        _tools = tools;
    }

    /// <summary>按当前输入 + 那一套选项造请求（摘要、预览、真正开跑都用它）。</summary>
    internal PackingRequest BuildRequest(PackingRunOptions options) => new()
    {
        SourceFolder = _vm.SourceFolder,

        // 分卷大小 0 = 自动（第 46 条：按卷数算），落点与命名由 PackingPaths 推。
        VolumeSizeBytes = 0,
        Password = _vm.Password,
        OuterPassword = _vm.PasswordsAreSame ? string.Empty : _vm.OuterPassword,
        OuterContainer = _vm.OuterContainer,
        RunOptions = options ?? new PackingRunOptions()
    };

    /// <summary>
    /// 开始之前把输入挡一遍：返回该显示给用户的那句话（空串 = 通过）。
    ///
    /// <para>顺序照旧：源 → 密码 → 外层密码 → 外层容器做不做得出来。最后这一条是
    /// "连内置 7-Zip 都没有"（程序装坏了那种）——**先拦下**，别让他确认一件注定失败的事
    /// （服务那边也会报同一件事，这里只是不浪费他一次点击）。</para>
    /// </summary>
    internal string ValidateStart()
    {
        if (string.IsNullOrWhiteSpace(_vm.SourceFolder))
        {
            return "请先选择要打包的文件夹（或一个文件）。";
        }

        if (!PackingPasswordPolicy.IsUsable(_vm.Password))
        {
            return PackingPasswordPolicy.RequiredMessage;
        }

        if (!_vm.PasswordsAreSame && !PackingPasswordPolicy.IsUsable(_vm.OuterPassword))
        {
            return PackingPasswordPolicy.OuterRequiredMessage;
        }

        if (_vm.EffectiveOuterContainer.Impossible)
        {
            return "本机既没有 Rar.exe，也没有可用的 7-Zip —— 打不了包。"
                   + $"请检查程序目录里的 tools\\7zip（{_tools.SevenZipExePath}），或重新解压一份程序。";
        }

        return string.Empty;
    }

    /// <summary>
    /// 弹窗里那**两档操作**与页面那套落点合并（⛔ 弹窗不许偷偷改落点）。
    /// </summary>
    internal PackingRunOptions MergeConfirmed(PackingRunOptions pagePlacement, PackingRunOptions confirmed) => new()
    {
        TargetMode = pagePlacement.TargetMode,
        CustomOutputDirectory = pagePlacement.CustomOutputDirectory,
        SourceHandling = confirmed.SourceHandling,
        RestHandling = confirmed.RestHandling
    };

    /// <summary>把这次真正用的那一套写回设置（"相应的保存记忆操作"）。</summary>
    internal void SaveConfirmed(PackingRunOptions merged)
    {
        merged.SaveTo(_vm.Settings);
        _vm.SettingsChanged?.Invoke();
    }
}

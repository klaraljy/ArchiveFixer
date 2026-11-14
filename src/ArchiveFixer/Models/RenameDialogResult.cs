namespace ArchiveFixer.Models;

/// <summary>「改名…」小窗的返回：模式 + 后缀/查找/替换。null 表示取消。</summary>
public readonly record struct RenameDialogResult(
    string Mode,
    string Extension,
    string FindText,
    string ReplaceText);

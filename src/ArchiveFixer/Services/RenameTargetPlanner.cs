using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using System;
using System.IO;

namespace ArchiveFixer.Services;

/// <summary>
/// 「这一份该叫什么名字」的规划入口：按操作类型分派到纯名字规则（<see cref="RenameNameRules"/>）
/// 或按内容事实修正（<see cref="RenameDetectedFormatFixer"/>），并处理分卷「不许改名」的闸门。
///
/// <para>从 <see cref="RenameService"/> 抽出的协作类（「命名规则」的入口）：只算名字，
/// ⛔ 不碰任务状态、不执行落盘；分卷修复的判据仍在 <c>VolumeNameRepair.Plan</c>
/// （⛔ 这里不新造第二把尺子）。</para>
///
/// <para><see cref="AutoRenamePath"/> 是"落点被占时取下一个空名字"的既有出口，
/// 编号风格与 <see cref="SafePathHelper.AutoRenameFilePath"/> 一致。</para>
/// </summary>
internal static class RenameTargetPlanner
{
    internal static string BuildNewPath(ArchiveTask task, RenameOptions options)
    {
        if (task == null)
        {
            return string.Empty;
        }

        options ??= new RenameOptions();
        options.Normalize();

        string oldPath = task.CurrentPath;

        if (string.IsNullOrWhiteSpace(oldPath))
        {
            oldPath = task.OriginalPath;
        }

        if (string.IsNullOrWhiteSpace(oldPath))
        {
            return string.Empty;
        }

        string directory = Path.GetDirectoryName(oldPath) ?? string.Empty;
        string fileName = Path.GetFileName(oldPath);

        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        string newFileName;

        /*
         * 分卷文件只允许**一种**改名：「名字被改坏的第一卷」被修回标准名
         * （那一条只走下面的 FixByDetectedFormat 分支，判据是 VolumeNameRepair.Plan）。
         * 「添加 / 替换最后一个 / 删除最后一个 / 删除多个后缀」这四种对分卷一律**原样返回** ——
         * 7z / WinRAR 只认 .001 这一套命名，改一下整组就再也解不开
         * （以前这道闸门只长在 FixByDetectedFormat 里，2026-09-26 审计补全）。
         */
        bool isVolumePart = LegacyVolumeNames.IsVolumePart(fileName);
        bool isFormatFix = string.Equals(options.OperationType, "FixByDetectedFormat", StringComparison.OrdinalIgnoreCase);
        bool isTextReplace = string.Equals(options.OperationType, "ReplaceFileNameText", StringComparison.OrdinalIgnoreCase);

        /*
         * 「替换文件名里的文字」对分卷**豁免**这条闸门：打包者塞的字往往在**每一卷**的名字里
         * （`set.7z(删掉.001/.002/.003`），把这几个字一起去掉**正是**修法 ——
         * 而用户是把整组勾上再点那颗按钮的，预览表里逐行列着"哪一行改成什么"，看得见才点得下去。
         */
        if (isVolumePart && !isFormatFix && !isTextReplace)
        {
            return oldPath;
        }

        switch (options.OperationType)
        {
            case "AddExtension":
                newFileName = RenameNameRules.BuildAddExtensionFileName(fileName, options.TargetExtension);
                break;

            case "ReplaceLastExtension":
                newFileName = RenameNameRules.BuildReplaceLastExtensionFileName(fileName, options.TargetExtension);
                break;

            case "DeleteLastExtension":
                newFileName = RenameNameRules.BuildDeleteExtensionFileName(fileName, 1);
                break;

            case "ReplaceFileNameText":
                newFileName = RenameNameRules.BuildReplaceTextFileName(fileName, options.FindText, options.ReplaceText);
                break;

            case "FixByDetectedFormat":
                newFileName = RenameDetectedFormatFixer.BuildFixByDetectedFormatFileName(task, options);
                break;

            default:
                newFileName = RenameDetectedFormatFixer.BuildFixByDetectedFormatFileName(task, options);
                break;
        }

        if (string.IsNullOrWhiteSpace(newFileName))
        {
            return oldPath;
        }

        return Path.Combine(directory, newFileName);
    }

    internal static string AutoRenamePath(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return string.Empty;
        }

        if (!File.Exists(targetPath) && !Directory.Exists(targetPath))
        {
            return targetPath;
        }

        string? directory = Path.GetDirectoryName(targetPath);
        string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(targetPath);
        string extension = Path.GetExtension(targetPath);

        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Environment.CurrentDirectory;
        }

        for (int i = 1; i <= 9999; i++)
        {
            string candidate = Path.Combine(
                directory,
                $"{fileNameWithoutExtension}({i}){extension}");

            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        string fallback = Path.Combine(
            directory,
            $"{fileNameWithoutExtension}_{ArchiveFixer.Helpers.Clock.Now:yyyyMMddHHmmssfff}{extension}");

        return fallback;
    }
}

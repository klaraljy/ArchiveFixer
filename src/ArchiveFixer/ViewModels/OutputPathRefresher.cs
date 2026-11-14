using ArchiveFixer.Models;
using ArchiveFixer.Services;
using System.Collections.Generic;

namespace ArchiveFixer.ViewModels;

/// <summary>按当前设置重算未产出任务的落点。</summary>
internal static class OutputPathRefresher
{
    internal static void Refresh(MainViewModel vm)
    {
        var options = new ExtractOptions
        {
            ExtractToOriginalDirectory = vm.Settings.ExtractToOriginalDirectory,
            CustomOutputDirectory = vm.SelectedOutputDirectory,
            KeepArchiveNameFolder = vm.Settings.KeepArchiveNameFolder,
            TestBeforeExtract = vm.Settings.TestBeforeExtract,
            OverwriteMode = vm.Settings.OverwriteMode,
            UseGlobalPassword = vm.Settings.UseGlobalPasswordForAllTasks,
            GlobalPassword = vm.GlobalPassword,
            TryPasswordList = true,
            CancelOnFirstSuccess = true
        };

        options.Normalize();

        foreach (ArchiveTask task in vm.Tasks)
        {
            if (HasExtractionResult(task))
            {
                continue;
            }

            try
            {
                task.OutputPath = vm.PathService.BuildOutputPath(task, options);
            }
            catch
            {
                task.OutputPath = string.Empty;
            }
        }
    }

    private static bool HasExtractionResult(ArchiveTask task)
        => task.Status is StatusText.ExtractSuccess or StatusText.Overwritten or StatusText.PartiallyCompleted;
}

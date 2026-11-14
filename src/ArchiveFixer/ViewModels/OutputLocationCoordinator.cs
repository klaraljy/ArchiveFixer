using ArchiveFixer.Models;
using System;
using System.ComponentModel;

namespace ArchiveFixer.ViewModels;

/// <summary>①页输出位置那一行与②页落点控件的同步/复制。</summary>
internal sealed class OutputLocationCoordinator
{
    private readonly MainViewModel _vm;

    public OutputLocationCoordinator(MainViewModel vm)
    {
        _vm = vm;
    }

    internal void NotifyOutputPlacementChangedEverywhere()
    {
        _vm.SettingsEditor?.NotifyOutputPlacementChanged();
        _vm.RaiseOutputLocationChanged();
    }

    internal void SyncOutputLocationFromSettings(string? rememberedDirectory = null)
    {
        string folder = rememberedDirectory ?? _vm.Settings?.CustomOutputDirectory ?? string.Empty;

        if (!string.Equals(_vm.SelectedOutputDirectory, folder, StringComparison.Ordinal))
        {
            _vm.SetSelectedOutputDirectoryRaw(folder);
            _vm.RefreshOutputPaths();
        }

        _vm.RaiseOutputLocationChanged();
    }

    internal void OnSettingsEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e == null)
        {
            return;
        }

        if (string.Equals(e.PropertyName, nameof(SettingsViewModel.OutputPlacement), StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, nameof(SettingsViewModel.CustomOutputDirectory), StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, nameof(SettingsViewModel.Settings), StringComparison.Ordinal))
        {
            SyncOutputLocationFromSettings();
        }
    }

    internal void CopyOutputLocation()
    {
        if (_vm.Settings == null || _vm.Settings.ExtractToOriginalDirectory ||
            string.IsNullOrWhiteSpace(_vm.SelectedOutputDirectory))
        {
            _vm.AppendLog("INFO", StatusText.OutputLocationCopyNothingLog);
            return;
        }

        if (_vm.LogExport.CopySanitizedToClipboard(_vm.SelectedOutputDirectory))
        {
            _vm.AppendLog(
                "INFO",
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.OutputLocationCopyLogFormat,
                    _vm.SelectedOutputDirectory));
        }
    }

    internal void OpenOutputDirectory()
    {
        string directory = _vm.SelectedOutputDirectory;

        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = _vm.Tasks.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.OutputPath))?.OutputPath ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(directory))
        {
            _vm.DialogService.ShowWarning(
                "还没有可打开的输出目录。"
                + "先在「设置 → 输出位置」里选一档（或先对某个包跑一次解压），再回来打开。");
            return;
        }

        _vm.PathService.OpenDirectory(directory);
    }
}


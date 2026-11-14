using ArchiveFixer.Engines;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// ⑦页「引擎」那一组的预览：优先级列表（顺序、检测状态、路径、版本、当前在用）、
/// 一句话选择结论，以及外层 rar 用的是哪一份 <c>Rar.exe</c>。
///
/// <para>⛔ 刻意**不**用 <c>ToolLocator.Default</c> / <c>EngineRuntimeSettings</c> 去探测：
/// 那是运行时的全局解析结果。用户在设置界面改路径、还没轮到落盘时，界面必须显示"改完之后会怎样"，
/// 而点「取消」时又不能把这个改动漏到运行时（那正是"取消了却生效了"的经典缺陷）。
/// 所以这里用一个**临时**的 <see cref="ToolLocator"/> + 注册表 + 选择器，只做预览、不产生副作用。</para>
///
/// <para>列表顺序也必须用**设置里那份**优先级，不能用全局运行时那份：用户点了「下移」之后、
/// 还没点「保存」之前，界面要立刻反映新顺序（实测踩过：这里原来读的是全局 ⇒ 点「下移」看着毫无反应）。</para>
/// </summary>
internal sealed class SettingsEnginePreview
{
    private readonly Func<AppSettings> _getSettings;
    private readonly Action<string> _notify;
    private readonly Action<string> _setMessage;

    private string _engineSelectionSummary = string.Empty;
    private string _rarToolStatusText = string.Empty;

    internal SettingsEnginePreview(
        Func<AppSettings> getSettings,
        Action<string> notify,
        Action<string> setMessage)
    {
        _getSettings = getSettings;
        _notify = notify;
        _setMessage = setMessage;
    }

    /// <summary>引擎优先级列表（界面上的顺序 = 落盘的顺序）。</summary>
    internal ObservableCollection<EngineOptionItem> Engines { get; } = new();

    /// <summary>"当前在用哪个引擎"的一句话（按能力 + 优先级算出来的，与真正执行时同一份逻辑）。</summary>
    internal string EngineSelectionSummary => _engineSelectionSummary;

    /// <summary>
    /// 工具状态那一行："当前用的是自选的那一份，还是本机已装目录里的那一份"（不变量 14）。
    /// </summary>
    internal string RarToolStatusText => _rarToolStatusText;

    /// <summary>
    /// 重算引擎列表（顺序、检测状态、路径、版本、当前在用）。
    /// </summary>
    internal void RefreshEngineList()
    {
        try
        {
            AppSettings settings = _getSettings();

            settings.EnginePriority = EngineIds.Normalize(settings.EnginePriority);

            var previewTools = new ToolLocator
            {
                CustomSevenZipExePath = settings.CustomSevenZipExePath ?? string.Empty,
                CustomUnRarExePath = settings.CustomUnRarExePath ?? string.Empty
            };

            EngineRegistry registry = EngineRegistry.CreateDefault(previewTools);
            var selector = new EngineSelector(registry, settings.EnginePriority);

            EngineSelection forRar = selector.Explain("RAR5", EngineOperation.Extract);
            EngineSelection forGeneric = selector.Explain("ZIP", EngineOperation.Extract);

            IReadOnlyList<IArchiveEngine> ordered = registry.EnginesInPriorityOrder(settings.EnginePriority);

            Engines.Clear();

            for (int i = 0; i < ordered.Count; i++)
            {
                IArchiveEngine engine = ordered[i];

                bool inUse = (forRar.Selected != null &&
                              string.Equals(forRar.Selected.Id, engine.Id, StringComparison.OrdinalIgnoreCase))
                             || (forGeneric.Selected != null &&
                                 string.Equals(forGeneric.Selected.Id, engine.Id, StringComparison.OrdinalIgnoreCase));

                Engines.Add(new EngineOptionItem
                {
                    Id = engine.Id,
                    DisplayName = engine.DisplayName,
                    Version = engine.Version,
                    Path = DescribeEnginePath(engine.Id, previewTools),
                    SourceText = DescribeEngineSource(engine.Id, previewTools),
                    IsAvailable = engine.IsAvailable,
                    Order = i + 1,
                    IsInUse = inUse,
                    UsageText = BuildUsageText(engine.Id, forRar, forGeneric),
                    CanMoveUp = i > 0,
                    CanMoveDown = i < ordered.Count - 1
                });
            }

            SetEngineSelectionSummary(BuildEngineSelectionSummary(forRar, forGeneric));
        }
        catch (Exception ex)
        {
            ArchiveFixer.Helpers.AppDiagnostics.Problem("设置落盘", "降级处理", ex);
            // 设置页不该因为"探测引擎"整个打不开：报一句就够，其余设置照常可改。
            SetEngineSelectionSummary("引擎检测失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 重算"外层 rar 这一步用的是哪一份 Rar.exe"那一行（用户 2026-09-23 要求能看出
    /// **用的是自选的那份还是已装目录那份**）。
    ///
    /// <para>与 <see cref="RefreshEngineList"/> 同一口径：临时 ToolLocator，只预览、不产生副作用
    /// （改完路径还没点保存时界面要显示"改完之后会怎样"，点「取消」不能生效）。</para>
    /// </summary>
    internal void RefreshRarStatus()
    {
        try
        {
            var previewTools = new ToolLocator
            {
                CustomRarExePath = _getSettings().CustomRarExePath ?? string.Empty
            };

            SetRarToolStatusText(StatusText.SettingsRarExePathStatusPrefix + previewTools.DescribeRarResolution());
        }
        catch (Exception ex)
        {
            ArchiveFixer.Helpers.AppDiagnostics.Problem("设置落盘", "降级处理", ex);
            // 设置页不该因为探测失败整个打不开：报一句就够，其余设置照常可改。
            SetRarToolStatusText(StatusText.SettingsRarExePathStatusPrefix + "检测失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 上移 / 下移一位。<paramref name="delta"/> 为 -1 上移、+1 下移。
    /// 只改顺序，不增删引擎（列表里没有"删掉某个引擎"这一档）。
    /// </summary>
    internal void MoveEngine(object? parameter, int delta)
    {
        if (parameter is not EngineOptionItem item)
        {
            return;
        }

        AppSettings settings = _getSettings();
        List<string> priority = EngineIds.Normalize(settings.EnginePriority);

        int index = priority.FindIndex(id => string.Equals(id, item.Id, StringComparison.OrdinalIgnoreCase));
        int target = index + delta;

        if (index < 0 || target < 0 || target >= priority.Count)
        {
            return;
        }

        string moved = priority[index];
        priority[index] = priority[target];
        priority[target] = moved;

        settings.EnginePriority = priority;

        RefreshEngineList();

        _setMessage($"引擎顺序已调整：{string.Join(" → ", priority.Select(DescribeEngineIdForMessage))}（改完立刻自动存）");
    }

    private void SetEngineSelectionSummary(string value)
    {
        string normalized = value ?? string.Empty;

        if (string.Equals(_engineSelectionSummary, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _engineSelectionSummary = normalized;
        _notify(nameof(SettingsViewModel.EngineSelectionSummary));
    }

    private void SetRarToolStatusText(string value)
    {
        string normalized = value ?? string.Empty;

        if (string.Equals(_rarToolStatusText, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _rarToolStatusText = normalized;
        _notify(nameof(SettingsViewModel.RarToolStatusText));
    }

    private static string DescribeEnginePath(string engineId, ToolLocator tools)
    {
        return string.Equals(engineId, EngineIds.WinRar, StringComparison.OrdinalIgnoreCase)
            ? tools.UnRarExePath
            : tools.SevenZipExePath;
    }

    private static string DescribeEngineSource(string engineId, ToolLocator tools)
    {
        if (string.Equals(engineId, EngineIds.WinRar, StringComparison.OrdinalIgnoreCase))
        {
            if (tools.IsUsingCustomUnRarPath)
            {
                return "用户自选路径";
            }

            return tools.IsUsingWinRarInstallation ? "本机已装 WinRAR 目录" : "程序内置 tools\\unrar";
        }

        return tools.IsUsingCustomPath ? "用户自选路径" : "程序内置 tools\\7zip";
    }

    private static string BuildUsageText(string engineId, EngineSelection forRar, EngineSelection forGeneric)
    {
        bool rar = forRar.Selected != null &&
                   string.Equals(forRar.Selected.Id, engineId, StringComparison.OrdinalIgnoreCase);

        bool generic = forGeneric.Selected != null &&
                       string.Equals(forGeneric.Selected.Id, engineId, StringComparison.OrdinalIgnoreCase);

        if (rar && generic)
        {
            return "当前在用：所有格式";
        }

        if (rar)
        {
            return "当前在用：RAR 包";
        }

        if (generic)
        {
            return "当前在用：zip / 7z 等其它格式";
        }

        return "当前未参与（能力或优先级排在后面）";
    }

    private static string BuildEngineSelectionSummary(EngineSelection forRar, EngineSelection forGeneric)
    {
        string rar = forRar.Selected == null ? "没有可用引擎" : forRar.Describe();
        string generic = forGeneric.Selected == null ? "没有可用引擎" : forGeneric.Describe();

        string text = $"当前在用：RAR 包 → {rar}；zip / 7z 等其它格式 → {generic}。";

        if (forRar.SkippedUnavailable.Count > 0 || forGeneric.SkippedUnavailable.Count > 0)
        {
            text += " 排在前面的引擎没检测到时会被自动跳过，不会因此打不开包。";
        }

        return text;
    }

    private static string DescribeEngineIdForMessage(string id)
    {
        if (string.Equals(id, EngineIds.WinRar, StringComparison.OrdinalIgnoreCase))
        {
            return "UnRAR";
        }

        return string.Equals(id, EngineIds.SevenZip, StringComparison.OrdinalIgnoreCase) ? "7-Zip" : id;
    }
}

using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// 多层展开的空间风险：那句话（日志与确认框同源）与批首 WARN（从 ExtractionCoordinator.ExtractionScheduleStep.cs 搬出）。
/// </summary>
internal sealed class ExtractionMultiLayerSpaceRiskReporter
{
    private readonly ExtractionCoordinator _host;

    internal ExtractionMultiLayerSpaceRiskReporter(ExtractionCoordinator host)
    {
        _host = host;
    }


    /// <summary>那句话（日志与确认框**同一段文案**，免得两处说法分叉）。</summary>
    internal static string DescribeMultiLayerSpaceRisk(MultiLayerSpaceRisk risk)
    {
        return string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            StatusText.MultiLayerSpaceRiskFormat,
            risk.MaxLayers,
            TaskSpaceEstimate.FormatSize(risk.TotalDemandBytes),
            TaskSpaceEstimate.FormatSize(risk.AvailableBytes),
            TaskSpaceEstimate.FormatSize(risk.ShortfallBytes));
    }

    /// <summary>批首那一条 WARN（用户要求"开工前就要看得见"；不成立时一个字都不写）。</summary>
    internal void LogMultiLayerSpaceRisk(ExtractionSchedulePlan plan)
    {
        MultiLayerSpaceRisk risk = _host.Schedule.EvaluateMultiLayerSpaceRisk(plan);

        if (!risk.Applies)
        {
            return;
        }

        _host.AppendLog("WARN", DescribeMultiLayerSpaceRisk(risk));
    }
}


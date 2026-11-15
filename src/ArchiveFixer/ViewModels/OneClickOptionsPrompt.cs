using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;


namespace ArchiveFixer.ViewModels;

/// <summary>一次「本次选项」询问的结果：结论 + （确认时的）快照。</summary>
internal readonly struct OneClickOptionsPrompt
{
    private OneClickOptionsPrompt(OneClickOptionsOutcome outcome, OneClickRunOptions? options)
    {
        Outcome = outcome;
        Options = options;
    }

    public OneClickOptionsOutcome Outcome { get; }

    /// <summary>只有 <see cref="OneClickOptionsOutcome.Confirmed"/> 时非 null。</summary>
    public OneClickRunOptions? Options { get; }

    public static OneClickOptionsPrompt NotShown() =>
        new(OneClickOptionsOutcome.NotShown, null);

    public static OneClickOptionsPrompt Confirmed(OneClickRunOptions options) =>
        new(OneClickOptionsOutcome.Confirmed, options);

    public static OneClickOptionsPrompt Cancelled() =>
        new(OneClickOptionsOutcome.Cancelled, null);
}


using ArchiveFixer.BindingModels;
using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Security;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;


namespace ArchiveFixer.ViewModels;

/// <summary>
/// 引擎回退与暂存校验（第 12 块，同一 partial 类）。
///
/// <para>实现已整块搬进 <see cref="EngineFallbackHandler"/>
/// （<c>ViewModels/Extraction/Steps/EngineFallbackHandler.cs</c>），本文件只保留同名薄转发、
/// 一处最小 internal 访问器与嵌套类型 <see cref="StageProductVerification"/>（类型身份不变）；
/// ⛔ 判据、日志顺序、await 顺序与所有调用点一个字不改。</para>
/// </summary>
internal sealed partial class ExtractionCoordinator
{
    // ===== 搬出后的入口薄转发（实现见 Extraction/Steps/EngineFallbackHandler.cs）=====
    private EngineFallbackHandler? _engineFallbackHandler;

    private EngineFallbackHandler EngineFallback => _engineFallbackHandler ??= new EngineFallbackHandler(this);

    /// <summary>薄转发：实现见 <see cref="EngineFallbackHandler.ResolveFailedEngineName"/>。</summary>
    internal string ResolveFailedEngineName(ArchiveOperationResult? lastResult) =>
        EngineFallback.ResolveFailedEngineName(lastResult);

    /// <summary>
    /// 薄转发：实现见 <see cref="EngineFallbackHandler.FinishFallbackSuccessAsync"/>
    /// （"先校验，再说话"的顺序、判否时不动结论的处置全在那边）。
    /// </summary>
    internal Task<bool> FinishFallbackSuccessAsync(
        ArchiveTask task,
        string engineArchivePath,
        string password,
        string engineOutputPath,
        string outputRedirectNote,
        bool sharedOutputRoot,
        bool oneClickRun,
        CancellationToken cancellationToken) =>
        EngineFallback.FinishFallbackSuccessAsync(
            task,
            engineArchivePath,
            password,
            engineOutputPath,
            outputRedirectNote,
            sharedOutputRoot,
            oneClickRun,
            cancellationToken);

    /// <summary>薄转发：实现见 <see cref="EngineFallbackHandler.VerifyStageProductsAsync"/>。</summary>
    internal Task<StageProductVerification> VerifyStageProductsAsync(
        ArchiveTask task,
        string engineArchivePath,
        string password,
        string stageDirectory,
        CancellationToken cancellationToken) =>
        EngineFallback.VerifyStageProductsAsync(
            task,
            engineArchivePath,
            password,
            stageDirectory,
            cancellationToken);

    /// <summary>薄转发：实现见 <see cref="EngineFallbackHandler.DiscardStageProductsAsync"/>。</summary>
    internal Task DiscardStageProductsAsync(
        ArchiveTask task,
        string stageDirectory,
        CancellationToken cancellationToken) =>
        EngineFallback.DiscardStageProductsAsync(task, stageDirectory, cancellationToken);

    /// <summary>薄转发：实现见 <see cref="EngineFallbackHandler.PrepareStageDirectoryAsync"/>。</summary>
    internal Task<string?> PrepareStageDirectoryAsync(
        ArchiveTask task,
        string stageDirectory,
        CancellationToken cancellationToken) =>
        EngineFallback.PrepareStageDirectoryAsync(task, stageDirectory, cancellationToken);

    /// <summary>
    /// 薄转发：实现见 <see cref="EngineFallbackHandler.DiscardStaleCarvedArtifact"/>
    /// （`ExtractTaskMainStep` 那一处调用点不改，仍走同一个 private 名字）。
    /// </summary>
    private void DiscardStaleCarvedArtifact(ArchiveTask task) => EngineFallback.DiscardStaleCarvedArtifact(task);

    // ===== 搬出去的那份实现要用的私有成员（只加最小 internal 访问器；⛔ 方法语义一个字不改）=====

    /// <summary>
    /// 访问器：给 <see cref="EngineFallbackHandler"/> 算抠取副本的落点
    /// （定义在 `ExtractionCoordinator.EmbeddedZipStep.cs` 的 private 方法）。
    /// </summary>
    internal string BuildEmbeddedArchivePathForEngineFallback(ArchiveTask task) => BuildEmbeddedArchivePath(task);

    /// <summary>一个候选的产物校验结论（清单 + 校验，见 <see cref="VerifyStageProductsAsync"/>）。</summary>
    internal sealed class StageProductVerification
    {
        public StageProductVerification(ArchiveListResult list, OutputVerificationResult verification)
        {
            List = list;
            Verification = verification;
        }

        /// <summary>这一轮列目录拿到的清单（定稿那一步复用，避免再列一次）。</summary>
        public ArchiveListResult List { get; }

        /// <summary>产物校验结论（判否时 <c>FailureMessage</c> 里带着预期与实际的三个数字）。</summary>
        public OutputVerificationResult Verification { get; }
    }
}

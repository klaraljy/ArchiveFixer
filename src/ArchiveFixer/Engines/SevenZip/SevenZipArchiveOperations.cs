using ArchiveFixer.Models;
using ArchiveFixer.Password;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Engines.SevenZip;

/// <summary>
/// 7-Zip 的**测试 / 解压两个公开操作**：入参校验、输出目录准备、参数拼装、调用一次运行、
/// 再用错误分类把结果修饰成该操作的样子。
///
/// <para>本类不含进程、输出泵、超时、收尸 —— 那些在 <see cref="SevenZipProcessSession"/>；
/// 也不含错误文本解析 —— 那在 <see cref="SevenZipResultClassifier"/>。
/// 校验失败时用的是与运行期失败**同一套**结果工厂（<see cref="ArchiveOperationResult.CreateFailure"/>），
/// 所以调用方拿到的形状与旧实现一致。</para>
/// </summary>
internal static class SevenZipArchiveOperations
{
    internal static async Task<ArchiveOperationResult> TestAsync(
        ToolLocator tools,
        string archivePath,
        string password,
        CancellationToken cancellationToken,
        EngineProgressContext? progressContext)
    {
        if (!tools.SevenZipExists)
        {
            return SevenZipResultClassifier.CreateEngineMissingResult(tools.SevenZipExePath);
        }

        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
        {
            return ArchiveOperationResult.CreateFailure(
                -1,
                string.Empty,
                string.Empty,
                StatusText.TestFailed,
                "压缩包文件不存在",
                "UnknownError",
                TimeSpan.Zero,
                PasswordMasker.Mask(password));
        }

        List<string> arguments = SevenZipCommandLine.BuildTestArguments(archivePath, password);

        ArchiveOperationResult result = await SevenZipProcessSession.RunAsync(
                tools,
                arguments,
                password,
                cancellationToken,
                progressContext)
            .ConfigureAwait(false);

        SevenZipResultClassifier.ApplyTestOutcome(result, archivePath, arguments);

        return result;
    }

    internal static async Task<ArchiveOperationResult> ExtractAsync(
        ToolLocator tools,
        string archivePath,
        string outputPath,
        string password,
        ExtractOptions options,
        CancellationToken cancellationToken,
        EngineProgressContext? progressContext)
    {
        options ??= new ExtractOptions();
        options.Normalize();

        if (!tools.SevenZipExists)
        {
            return SevenZipResultClassifier.CreateEngineMissingResult(tools.SevenZipExePath);
        }

        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
        {
            return ArchiveOperationResult.CreateFailure(
                -1,
                string.Empty,
                string.Empty,
                StatusText.ExtractFailed,
                "压缩包文件不存在",
                "UnknownError",
                TimeSpan.Zero,
                PasswordMasker.Mask(password));
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return ArchiveOperationResult.CreateFailure(
                -1,
                string.Empty,
                string.Empty,
                StatusText.ExtractFailed,
                "输出目录为空",
                "UnknownError",
                TimeSpan.Zero,
                PasswordMasker.Mask(password));
        }

        try
        {
            Directory.CreateDirectory(outputPath);
        }
        catch (Exception ex)
        {
            string safeMessage = PasswordMasker.Sanitize(ex.Message);

            return ArchiveOperationResult.CreateFailure(
                -1,
                string.Empty,
                safeMessage,
                StatusText.AccessDenied,
                "无法创建输出目录：" + safeMessage,
                "AccessDenied",
                TimeSpan.Zero,
                PasswordMasker.Mask(password));
        }

        List<string> arguments = SevenZipCommandLine.BuildExtractArguments(
            archivePath,
            outputPath,
            password,
            options);

        ArchiveOperationResult result = await SevenZipProcessSession.RunAsync(
                tools,
                arguments,
                password,
                cancellationToken,
                progressContext)
            .ConfigureAwait(false);

        SevenZipResultClassifier.ApplyExtractOutcome(result);

        return result;
    }
}

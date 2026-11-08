using ArchiveFixer.Detection;
using ArchiveFixer.Models;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Engines.WinRar
{
    /// <summary>WinRAR 双面文件直解结果；失败是"这条快路没走成"，调用方应回落。</summary>
    public sealed class WinRarDoubleSidedExtractResult
    {
        public bool Attempted { get; init; }

        public bool Success { get; init; }

        public string Message { get; init; } = string.Empty;

        public string? OutputDirectory { get; init; }

        public ArchiveOperationResult? EngineResult { get; init; }
    }

    /// <summary>
    /// 双面文件的 **WinRAR 可选快路**：给源文件建同卷硬链接别名（只补真实后缀），
    /// 让用户自装的 WinRAR 从原文件直接解压，不抠那份等大的 ZIP 副本。
    ///
    /// <para>⛔ 不改名、不复制、不删除源文件；别名在 finally 删除。⛔ 只负责执行，
    /// 产物是否可信仍由调用方的清单/校验决定；失败一律回落现有抠取 + 引擎。</para>
    /// </summary>
    public sealed class WinRarDoubleSidedExtractor
    {
        private readonly WinRarProcessRunner _runner;

        public WinRarDoubleSidedExtractor()
            : this(new WinRarProcessRunner())
        {
        }

        public WinRarDoubleSidedExtractor(WinRarProcessRunner runner)
        {
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        }

        public bool IsAvailable => _runner.IsAvailable;

        public async Task<WinRarDoubleSidedExtractResult> TryExtractAsync(
            string sourcePath,
            string outputDirectory,
            string? password = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                return new WinRarDoubleSidedExtractResult
                {
                    Attempted = false,
                    Message = "源文件不存在"
                };
            }

            EmbeddedArchiveInfo info = EmbeddedArchiveDetector.Detect(sourcePath);

            if (!info.Found || info.IsVolumePart || info.Offset <= 0)
            {
                return new WinRarDoubleSidedExtractResult
                {
                    Attempted = false,
                    Message = "不是可直解的单卷双面文件"
                };
            }

            if (!_runner.IsAvailable)
            {
                return new WinRarDoubleSidedExtractResult
                {
                    Attempted = false,
                    Message = "本机没有 WinRAR"
                };
            }

            string extension = string.IsNullOrWhiteSpace(info.SuggestedExtension)
                ? ".zip"
                : info.SuggestedExtension;
            string aliasDirectory = ResolveAliasDirectory(sourcePath, outputDirectory);
            string aliasPath = Path.Combine(
                aliasDirectory,
                "af-winrar-" + Guid.NewGuid().ToString("N") + extension);

            try
            {
                if (!WinRarHardlinkAlias.TryCreate(sourcePath, aliasPath, out string aliasError))
                {
                    return new WinRarDoubleSidedExtractResult
                    {
                        Attempted = false,
                        Message = "无法建立 WinRAR 后缀别名：" + aliasError
                    };
                }

                ArchiveOperationResult result = await _runner.ExtractAsync(
                        aliasPath,
                        outputDirectory,
                        password,
                        cancellationToken)
                    .ConfigureAwait(false);

                return new WinRarDoubleSidedExtractResult
                {
                    Attempted = true,
                    Success = result.Success,
                    Message = result.Message,
                    OutputDirectory = outputDirectory,
                    EngineResult = result
                };
            }
            finally
            {
                WinRarHardlinkAlias.TryDelete(aliasPath);
                TryDeleteEmptyDirectory(aliasDirectory);
            }
        }

        private static string ResolveAliasDirectory(string sourcePath, string outputDirectory)
        {
            string sourceRoot = Path.GetPathRoot(Path.GetFullPath(sourcePath)) ?? string.Empty;
            string outputRoot = Path.GetPathRoot(Path.GetFullPath(outputDirectory)) ?? string.Empty;

            if (string.Equals(sourceRoot, outputRoot, StringComparison.OrdinalIgnoreCase))
            {
                return Path.Combine(outputDirectory, ".af-winrar-alias-" + Guid.NewGuid().ToString("N")[..8]);
            }

            return Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(sourcePath))!,
                ".af-winrar-alias-" + Guid.NewGuid().ToString("N")[..8]);
        }

        private static void TryDeleteEmptyDirectory(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            try
            {
                if (Directory.Exists(directory) &&
                    Directory.GetFileSystemEntries(directory).Length == 0)
                {
                    Directory.Delete(directory);
                }
            }
            catch
            {
            }
        }
    }
}

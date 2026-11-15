using ArchiveFixer.Detection;
using ArchiveFixer.Models;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Engines;

/// <summary>
/// 「只看文件头」的格式识别 —— <see cref="EngineRouter"/> 的默认实现（M6 依赖环收口）。
///
/// <para><b>它为什么够用</b>：门面问格式只用结论里的 <see cref="DetectResult.Format"/> 与
/// <see cref="DetectResult.IsKnownFormat"/>，而按魔数认容器（7Z / RAR4 / RAR5 / ZIP 三态 /
/// GZIP / BZIP2 / XZ / TAR / ISO …）全部由 <see cref="ArchiveFormatProbe.ProbeHeader"/> 回答，
/// 读入量与完整识别那条路相同（见 <see cref="HeaderReadLength"/>）。</para>
///
/// <para><b>它与完整识别的关系</b>：生产入口注入的是
/// <c>Services.ArchiveDetectService</c>（文件头 + 尾部内嵌归档 + 顺序扫 + 缓存 + 加密判读），
/// 这里只是"没有注入时"的默认档 —— 结论不会比完整识别更"乐观"：认不出来就是 Unknown，
/// 门面照旧把这类文件交给通用引擎去试，<b>绝不允许</b>因为识别不到把包卡住。</para>
///
/// <para>⚠ 本文件属于 <c>Engines/</c>：不得引用 WPF，也不许反过来引用 <c>Services</c>（M6）。</para>
/// </summary>
internal sealed class HeaderArchiveDetector : IArchiveDetector
{
    /// <summary>
    /// 文件头读入量：34 KB（与 <c>Services.ArchiveContentIdentifier</c> 同一个口径）。
    ///
    /// <para>为什么不是 512 字节：TAR 的 <c>ustar</c> 标记在偏移 257、ISO 9660 的 <c>CD001</c>
    /// 在偏移 <c>0x8001</c>（32769）—— 这两条结构判据都在这 34 KB 里。
    /// 长度与识别服务**必须一致**，否则同一个文件在两条路上会得出不同格式，
    /// 而分派与识别各说各话正是这个程序最不该出现的缺陷。</para>
    /// </summary>
    private const int HeaderReadLength = 34816;

    /// <inheritdoc />
    public async Task<DetectResult> DetectAsync(string archivePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            return DetectResultFactory.CreateUnknown("文件路径为空");
        }

        if (!File.Exists(archivePath))
        {
            return DetectResultFactory.CreateUnknown("文件不存在");
        }

        try
        {
            byte[] header = await ReadHeaderAsync(archivePath, cancellationToken).ConfigureAwait(false);

            return header.Length == 0
                ? DetectResultFactory.CreateUnknown("文件为空")
                : ArchiveFormatProbe.ProbeHeader(header);
        }
        catch (OperationCanceledException)
        {
            // 取消不是"格式未知"：如实往上传，让调用方按取消处理（插件的取消语义由它自己决定）。
            throw;
        }
        catch
        {
            /*
             * 读不到文件头（被占用 / 权限 / 设备掉线 / 读到一半被删）就是"不知道"。
             * ⛔ 不抛：格式识别失败从来没有资格把解压卡住 —— 交给通用引擎去试，
             * 由它给出真实结论（这与识别服务里"读取失败 → Unknown"的既有口径一致）。
             */
            return DetectResultFactory.CreateUnknown("读取文件头失败");
        }
    }

    /// <summary>
    /// 读文件头（与 <c>Services.ArchiveContentIdentifier.ReadHeaderAsync</c> 同口径：
    /// 共享读写 + 允许删除、异步流、短文件按实际长度返回）。
    /// </summary>
    private static async Task<byte[]> ReadHeaderAsync(string filePath, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[HeaderReadLength];

        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 4096,
            useAsync: true);

        int read = await stream.ReadAsync(buffer.AsMemory(0, HeaderReadLength), cancellationToken);

        if (read <= 0)
        {
            return Array.Empty<byte>();
        }

        return read == HeaderReadLength ? buffer : buffer.Take(read).ToArray();
    }
}

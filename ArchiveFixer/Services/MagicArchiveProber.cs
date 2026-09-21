using ArchiveFixer.Detection;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Services
{
    /// <summary>
    /// 用现有的魔数识别服务实现 <see cref="IArchiveProber"/>。
    ///
    /// 为什么要有这层适配：
    /// 递归解压（Extraction 层）需要"这个文件是不是归档"的判断，
    /// 而魔数识别在 Services 层。让 Extraction 直接依赖 Services 会形成反向依赖
    /// （Services 里的解压编排本来就要用 Extraction），所以中间放一个 Detection 层的接口，
    /// 由 Services 提供实现 —— 依赖方向保持单向。
    /// </summary>
    public sealed class MagicArchiveProber : IArchiveProber
    {
        private readonly ArchiveDetectService _detectService;

        public MagicArchiveProber()
            : this(new ArchiveDetectService())
        {
        }

        public MagicArchiveProber(ArchiveDetectService detectService)
        {
            _detectService = detectService ?? new ArchiveDetectService();
        }

        public async Task<bool> IsArchiveAsync(string filePath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return false;
            }

            Models.DetectResult result = await _detectService.DetectAsync(filePath, cancellationToken);

            /*
             * 只认"已知格式的归档"。
             * 不把 Unknown 当归档：递归展开时把一堆无格式文件当成压缩包去试解，只会拖慢速度、
             * 刷一堆无意义的失败。真要强解未知格式，那是单层流程里 UnknownFormatAction 的事。
             */
            return result.IsArchive && result.IsKnownFormat;
        }
    }
}

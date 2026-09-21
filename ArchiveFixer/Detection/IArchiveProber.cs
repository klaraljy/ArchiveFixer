using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Detection
{
    /// <summary>
    /// 判断一个文件是不是归档。放在 Detection 层，避免 Extraction 反向依赖 Services。
    ///
    /// 为什么是接口而不是直接用某个服务：
    /// <see cref="ArchiveFixer.Extraction.RecursiveExtractor"/> 要在"解压出来的产物目录"里
    /// 反复回答"这堆文件里哪些还是压缩包"，这是递归展开的**唯一判据**。
    /// 一旦它直接 new 一个具体的识别服务，递归核心就没法脱离 GUI / 文件系统被测，
    /// 也没法在"只认魔数"和"再看一眼后缀"之间换策略。
    ///
    /// 实现方的边界：
    /// 1. 调用方会**逐个文件**调用它（一层产物里可能上万个文件），所以实现必须便宜；
    ///    真正的完整解析（列目录、试密码）属于引擎，不属于这里。
    /// 2. 只回答"是不是归档"，**不要**顺手做改名、不要动文件，更不要解压。
    /// 3. 判断结果允许保守（宁可漏判也不要误报）：漏判只是少展开一层，
    ///    误报会让递归核心拿一个非归档去调引擎，白跑一轮并留下一条失败记录。
    /// </summary>
    public interface IArchiveProber
    {
        /// <summary>
        /// 判断给定文件是否是归档。
        /// 约定：文件不存在、大小为 0、读不出来（占用 / 权限）时返回 false，**不要抛异常** ——
        /// 递归过程中一个文件读不了不该让整次展开失败。
        /// </summary>
        Task<bool> IsArchiveAsync(string filePath, CancellationToken cancellationToken = default);
    }
}

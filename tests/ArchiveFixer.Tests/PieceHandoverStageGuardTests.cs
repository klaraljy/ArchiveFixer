using System;
using System.IO;
using ArchiveFixer.Extraction;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **"接片"那一档该搬还是该只建链接**（真机 CCCC 2026-10-06 18:13，用户原话：
    /// 「**出现了解压失败的文字，明明成功了**」）。
    ///
    /// <para><b>现场</b>：`111(2)_.zip` 这一单自己解出来的那一片 `111.z01` 被"接片"那一档
    /// 从**它自己的暂存目录**里搬走 ⇒ 收尾链紧接着读暂存目录读到空
    /// ⇒「校验未通过：输出目录是空目录，没有产物」⇒ 列表显示「解压失败」+ 源包一个字节都不处理。
    /// 触发它是一段竞态：同一批另一单恰好在**两次接片调用之间**把这一组的入口包落到落点层
    /// ⇒ 第一次接片不认识那一层（什么都不做）、第二次认识 ⇒ 搬走的就是暂存目录里那一份。</para>
    ///
    /// <para><b>红检</b>：把 <see cref="ExtractionCoordinator.ShouldMovePieceIntoGroup"/> 里的
    /// "暂存目录那一档不搬"撤掉（恢复成只看"是不是过程物目录"）⇒
    /// <c>暂存目录里的那一份_只建链接绝不搬走</c> 当场变红。</para>
    /// </summary>
    public class PieceHandoverStageGuardTests : IDisposable
    {
        private readonly string _root;

        public PieceHandoverStageGuardTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "af-piece-handover-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch
            {
                // 临时目录清不掉不影响结论。
            }
        }

        /// <summary>
        /// ⛔ **本单自己的暂存目录里的那一份：只建链接，绝不搬走** —— 那是收尾链（结果校验 / 定稿）
        /// 要读的那一份，搬走 = 把自己这一单唯一的产物拿走（真机就是这样变成「解压失败」的）。
        /// </summary>
        [Fact]
        public void 暂存目录里的那一份_只建链接绝不搬走()
        {
            string stage = Path.Combine(_root, "111(2)_", ".ArchiveFixer.work", "111(2)_.zip-abc", "stage");
            string piece = Path.Combine(stage, "111.z01");

            Assert.False(ExtractionCoordinator.ShouldMovePieceIntoGroup(piece, stage));

            // 更深一层（暂存目录的子目录）也算暂存目录。
            Assert.False(ExtractionCoordinator.ShouldMovePieceIntoGroup(
                Path.Combine(stage, "111(2)_", "111.z01"),
                stage));
        }

        /// <summary>
        /// 同一个工作区里**递归层目录**那一份照旧用"移动"（那些目录收尾会整份删掉，
        /// 留一份在里面等于接了一个马上就要消失的名字）。
        /// </summary>
        [Fact]
        public void 递归层目录里的那一份_照旧搬走()
        {
            string stage = Path.Combine(_root, "111(2)_", ".ArchiveFixer.work", "111(2)_.zip-abc", "stage");
            string recursive = Path.Combine(
                _root, "111(2)_", ".ArchiveFixer.work", "recursive", "111(2)__xyz", "layer-000", "output");

            Assert.True(ExtractionCoordinator.ShouldMovePieceIntoGroup(
                Path.Combine(recursive, "111.z01"),
                stage));
        }

        /// <summary>
        /// 其余物里的那一份同样是"会被整份删掉的过程物目录" ⇒ 搬；而**用户目录里的那一份**
        /// 两条都不成立 ⇒ 只建链接（⛔ 名字与字节都不许动）。
        /// </summary>
        [Fact]
        public void 其余物那一份_搬走_用户源目录那一份_只建链接()
        {
            string stage = Path.Combine(_root, "111(2)_", ".ArchiveFixer.work", "111(2)_.zip-abc", "stage");

            Assert.True(ExtractionCoordinator.ShouldMovePieceIntoGroup(
                Path.Combine(_root, "111", "111", "其余物", "111.zip"),
                stage));

            Assert.False(ExtractionCoordinator.ShouldMovePieceIntoGroup(
                Path.Combine(_root, "111(4)", "111.z03"),
                stage));
        }

        /// <summary>
        /// 暂存目录**算不出来**（判不出）⇒ 落回老口径（只看"是不是过程物目录"），
        /// ⛔ 不因为判不出就把"该搬的"变成"不该搬的"。
        /// </summary>
        [Fact]
        public void 暂存目录算不出来_落回老口径()
        {
            Assert.True(ExtractionCoordinator.ShouldMovePieceIntoGroup(
                Path.Combine(_root, "111(2)_", ".ArchiveFixer.work", "recursive", "111(2)__xyz", "layer-000", "output", "111.z01"),
                string.Empty));
        }

        /// <summary>
        /// ⛔ **别的"还没收场"的那条链里的片：也只许建链接**（2026-10-11 定案到行）。
        ///
        /// <para><b>为什么</b>：收拢那一档（`TryGatherGroupPiecesInto` → `TryAdoptUnresolvedVolumePiece`）
        /// 传进来的"暂存目录"是**消费方**的，而被收的那一片常常属于**另一条还在跑的链**
        /// （躺在它自己的 `…\recursive\&lt;那一单&gt;_&lt;ts&gt;_&lt;hash&gt;\layer-000\output\` 里）。
        /// 按老判据它是"过程物目录里的东西"、又不在**调用方**的暂存目录里 ⇒ 判成"搬"
        /// ⇒ 那条链的定稿/校验读到空 ⇒ **假「解压失败」**
        /// （全量并行负载下实测：两单落 `Failed`、整组凑不齐；同一份夹具单跑 6/6 全绿）。</para>
        ///
        /// <para><b>红检</b>：把第 ③ 条撤掉（调用时不喂"还在跑的链"那一份清单）⇒ 本次断言当场变红。</para>
        /// </summary>
        [Fact]
        public void 别的还没收场那条链里的片_也只建链接()
        {
            string callerStage = Path.Combine(_root, "1112__", ".ArchiveFixer.work", "1112__.rLLLLar-abc", "stage");
            string otherChainWork = Path.Combine(_root, "111__", ".ArchiveFixer.work");

            string piece = Path.Combine(
                otherChainWork, "recursive", "111___20261011_075556_xyz", "layer-000", "output", "111_.part1.rar");

            // 不喂"还在跑的链"⇒ 老口径：它在过程物目录里 ⇒ 搬（既有行为，⛔ 一个字没改）。
            Assert.True(ExtractionCoordinator.ShouldMovePieceIntoGroup(piece, callerStage));

            // 喂上"那条链还没收场"⇒ 只建链接：搬走 = 把那条链唯一的产物拿走。
            Assert.False(ExtractionCoordinator.ShouldMovePieceIntoGroup(
                piece,
                callerStage,
                new[] { otherChainWork }));

            // 对照：别的链**已经收场**（不在清单里）⇒ 照旧按老口径搬。
            Assert.True(ExtractionCoordinator.ShouldMovePieceIntoGroup(
                piece,
                callerStage,
                new[] { Path.Combine(_root, "1113__", ".ArchiveFixer.work") }));
        }
    }
}

using ArchiveFixer.Storage;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Extraction;

/// <summary>
/// 工作区的**层暂存树**：每一层一个 <c>layer-NNN\output</c>；层清单、层目录的创建、
/// 以及"谁是叶子 / 谁的父层是谁"的拓扑判断都只在这里。
///
/// <para>层号取 <see cref="Layers"/>.Count，所以"层号连续、不跳号"是结构上保证的。
/// 父子关系不额外记一份，而是从 <see cref="WorkspaceLayer.InputPath"/> 反推：
/// 每一层解的那个归档一定位于"解出它的那一层"的产物目录里 ——
/// 这样"谁是叶子"只有一个事实来源，不会出现"递归那边记一棵树、发布这边又算一遍"的漂移。</para>
/// </summary>
internal sealed class WorkspaceStaging
{
    /// <summary>每层的产物子目录名。固定叫 output，方便"某一层的产物"被直接指认。</summary>
    private const string OutputDirectoryName = "output";

    private readonly List<WorkspaceLayer> _layers = new();
    private readonly string _taskDirectory;

    internal WorkspaceStaging(string taskDirectory)
    {
        _taskDirectory = taskDirectory;
    }

    /// <summary>已经创建的层，按层号升序。</summary>
    internal IReadOnlyList<WorkspaceLayer> Layers => _layers;

    /// <summary>创建下一层目录（layer-000、layer-001…）并返回。</summary>
    internal WorkspaceLayer CreateNextLayer(string inputArchivePath)
    {
        int depth = _layers.Count;

        string layerDirectory = Path.Combine(
            _taskDirectory,
            ExtractionWorkspace.LayerDirectoryPrefix + depth.ToString("D3"));

        string outputDirectory = Path.Combine(layerDirectory, OutputDirectoryName);

        if (!WorkspaceTree.EnsureHiddenDirectory(outputDirectory))
        {
            throw new IOException($"无法创建工作区层目录：{outputDirectory}");
        }

        var layer = new WorkspaceLayer
        {
            Depth = depth,
            DirectoryPath = layerDirectory,
            OutputPath = outputDirectory,
            InputPath = inputArchivePath ?? string.Empty
        };

        _layers.Add(layer);

        return layer;
    }

    /// <summary>
    /// 要发布的层 = **叶子层**：没有任何别的层是从它的产物里解出来的。
    /// 返回值按层号升序（<see cref="_layers"/> 本来就是按创建顺序排的），
    /// 让同名文件的落位顺序稳定、可复现。
    /// </summary>
    internal IReadOnlyList<WorkspaceLayer> ResolveLeafLayers()
    {
        var leaves = new List<WorkspaceLayer>();

        foreach (WorkspaceLayer layer in _layers)
        {
            bool hasChild = _layers.Any(other =>
                !ReferenceEquals(other, layer) &&
                WorkspacePathRules.IsSameOrChildPath(other.InputPath, layer.OutputPath));

            if (!hasChild)
            {
                leaves.Add(layer);
            }
        }

        return leaves;
    }

    /// <summary>
    /// 这一层的输入归档是从哪一层的产物里解出来的：取"产物目录是它的祖先"的那一层里**层号最大**的
    /// （层号升序建层，所以直接倒着找第一个命中的就是父层）。
    /// 找不到返回 null（第 0 层、或路径关系不成立）。
    /// </summary>
    internal static WorkspaceLayer? ResolveParentLayer(
        WorkspaceLayer layer,
        IReadOnlyList<WorkspaceLayer> candidates)
    {
        for (int i = candidates.Count - 1; i >= 0; i--)
        {
            WorkspaceLayer candidate = candidates[i];

            if (candidate.Depth >= layer.Depth)
            {
                continue;
            }

            if (WorkspacePathRules.IsSameOrChildPath(layer.InputPath, candidate.OutputPath))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// 工作区已经删掉（或本来就不在）之后丢掉层清单：那些层目录都不在盘上了，
    /// 清单不能继续描述一份已经不存在的现状。
    /// </summary>
    internal void ForgetLayers()
    {
        _layers.Clear();
    }
}

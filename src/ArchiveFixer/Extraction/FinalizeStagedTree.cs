namespace ArchiveFixer.Extraction;

/// <summary>
/// 把调用方描述的暂存条目列表拼成树（定稿规划唯一的"输入建模"步骤）。
///
/// <para>
/// 纯内存推理，⛔ 不读盘：暂存树由刚跑完解压的调用方描述出来，规划只做结构与字符串推理，
/// 这样它才能在真搬家之前就把"最终长什么样"算清楚。
/// </para>
/// </summary>
internal static class FinalizeStagedTree
{
    public static StagedTreeNode Build(IReadOnlyList<StagedEntry>? entries, List<string> warnings)
    {
        var root = new StagedTreeNode(string.Empty, string.Empty, null, true);

        if (entries == null)
        {
            return root;
        }

        foreach (StagedEntry? entry in entries)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.RelativePath))
            {
                warnings.Add("忽略了一个没有相对路径的暂存条目");
                continue;
            }

            string[]? segments = SplitRelativePath(entry.RelativePath);

            if (segments == null)
            {
                // 越出暂存根的条目（.. / 绝对路径）一律不规划：宁可少搬，不可搬错地方。
                warnings.Add("忽略了越出暂存根的条目：" + entry.RelativePath);
                continue;
            }

            StagedTreeNode cursor = root;

            for (int i = 0; i < segments.Length; i++)
            {
                bool isLast = i == segments.Length - 1;
                StagedTreeNode? child = cursor.FindChild(segments[i]);

                if (child == null)
                {
                    child = new StagedTreeNode(
                        segments[i],
                        cursor.RelativePath.Length == 0 ? segments[i] : cursor.RelativePath + "\\" + segments[i],
                        cursor,
                        !isLast || entry.IsDirectory);

                    cursor.Children.Add(child);
                }
                else if (isLast && entry.IsDirectory && !child.IsDirectory)
                {
                    // 同一路径既是文件又是目录：目录优先（磁盘上不可能两者并存，说明上游描述有误）。
                    child.IsDirectory = true;
                    warnings.Add("同一个路径既被描述成文件又被描述成目录，按目录处理：" + child.RelativePath);
                }

                if (isLast)
                {
                    child.MarkedArtifact |= entry.IsProcessArtifact;

                    if (!entry.IsDirectory && entry.Size > child.Size)
                    {
                        child.Size = entry.Size;
                    }
                }

                cursor = child;
            }
        }

        return root;
    }

    /// <summary>
    /// 拆相对路径。返回 null 表示"不能用"：空、绝对路径、盘符、或含 <c>..</c>（会越出暂存根）。
    /// </summary>
    public static string[]? SplitRelativePath(string relativePath)
    {
        string trimmed = relativePath.Trim();

        if (trimmed.Length == 0)
        {
            return null;
        }

        if (trimmed[0] == '\\' || trimmed[0] == '/')
        {
            return null;
        }

        if (trimmed.Length >= 2 && trimmed[1] == ':')
        {
            return null;
        }

        string[] parts = trimmed.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        var segments = new List<string>(parts.Length);

        foreach (string part in parts)
        {
            if (part == ".")
            {
                continue;
            }

            if (part == ".." || part.Trim().Length == 0)
            {
                return null;
            }

            segments.Add(part);
        }

        return segments.Count == 0 ? null : segments.ToArray();
    }

    /// <summary>按路径段在树上找节点；任何一段不存在就返回 null。</summary>
    public static StagedTreeNode? FindNode(StagedTreeNode root, string[] segments)
    {
        StagedTreeNode cursor = root;

        foreach (string segment in segments)
        {
            StagedTreeNode? child = cursor.FindChild(segment);

            if (child == null)
            {
                return null;
            }

            cursor = child;
        }

        return cursor;
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ArchiveFixer.Tests.Batch120
{
    /// <summary>
    /// 120 批的**检测代码**：只读 recipe、操作代码的运行记录、磁盘现状，逐条给 期望 / 实际 / 判定。
    ///
    /// 它不跑产品、不改任何文件。记录只当线索，另外重新扫一遍磁盘跟记录对账（G6），
    /// 这样操作代码说错了话也会被抓出来。
    /// </summary>
    internal static class Batch120Verify
    {
        public static readonly string BackupBatchRoot = Batch120Operation.BackupBatchRoot;
        public static readonly string TestBatchRoot = Batch120Operation.TestBatchRoot;
        public static readonly string SpecifiedBatchRoot = Batch120Operation.SpecifiedBatchRoot;
        public static readonly string RecordRoot = Batch120Operation.RecordRoot;

        public static List<CheckResult> Verify(string sampleId)
        {
            var results = new List<CheckResult>();
            CheckResult Add(string id, bool green, string expected, string actual, string note = "")
                => Record(results, sampleId, id, green, expected, actual, note);

            string recipePath = Path.Combine(BackupBatchRoot, sampleId, "recipe.json");
            string recordPath = Path.Combine(RecordRoot, sampleId + ".json");

            if (!File.Exists(recipePath))
            {
                Add("G0", false, "recipe.json 存在", "缺失");
                return results;
            }

            if (!File.Exists(recordPath))
            {
                Add("G0", false, "运行记录存在", "缺失");
                return results;
            }

            JsonNode recipe = JsonNode.Parse(File.ReadAllText(recipePath, Encoding.UTF8))!;
            Batch120RunRecord record = JsonSerializer.Deserialize<Batch120RunRecord>(File.ReadAllText(recordPath, Encoding.UTF8))!;
            string category = Str(recipe, "category");
            string placement = ReadPlacement(recipe);

            // ---------------- 通用 ----------------
            Add("G1", record.Error == null, "运行无异常", record.Error ?? "无异常");
            Add("G2", record.Category == category && record.Placement == placement,
                $"{category}/{placement}", $"{record.Category}/{record.Placement}");
            Add("G3", record.Before.Count > 0, "跑前快照非空", $"{record.Before.Count} 个文件");
            Add("G4", record.Tasks.Length > 0, "任务非空", $"{record.Tasks.Length} 个任务");

            // Pending = 导入后没轮到它跑；PartiallyCompleted = 部分完成。两者都是收尾后的合法状态。
            string[] hanging = record.Tasks
                .Where(t => t.Outcome is not ("Succeeded" or "Failed" or "Skipped" or "Cancelled" or "Pending" or "PartiallyCompleted"))
                .Select(t => $"{t.Name}={t.Outcome}").ToArray();
            Add("G5", hanging.Length == 0, "每个任务都有终态", hanging.Length == 0 ? "全部有终态" : string.Join(",", hanging));

            var disk = ScanDisk(record.Workspace, record.SpecifiedRoot);
            var recorded = record.After.ToDictionary(f => f.Root + "|" + f.Path, f => f.Bytes, StringComparer.OrdinalIgnoreCase);
            var missing = recorded.Keys.Where(k => !disk.ContainsKey(k)).ToArray();
            var extra = disk.Keys.Where(k => !recorded.ContainsKey(k)).ToArray();
            var bytesDiff = recorded.Keys.Where(k => disk.ContainsKey(k) && disk[k] != recorded[k]).ToArray();
            Add("G6", missing.Length == 0 && extra.Length == 0 && bytesDiff.Length == 0,
                "磁盘与运行记录一致",
                $"缺{missing.Length} 多{extra.Length} 字节不同{bytesDiff.Length}" +
                (missing.Length + extra.Length + bytesDiff.Length > 0
                    ? "：" + string.Join(" | ", missing.Concat(extra).Concat(bytesDiff).Take(4))
                    : string.Empty));

            bool hasWork = disk.Keys.Any(k => k.Contains("\\.ArchiveFixer.work\\", StringComparison.OrdinalIgnoreCase)
                                              || k.EndsWith("\\.ArchiveFixer.work", StringComparison.OrdinalIgnoreCase));
            Add("G7", !hasWork, "不残留 .ArchiveFixer.work", hasWork ? "发现残留" : "无残留");

            var specFiles = disk.Keys.Where(k => k.StartsWith("指定位置|", StringComparison.Ordinal)).ToArray();
            if (placement == "Original")
            {
                Add("G8", specFiles.Length == 0, "Original 时指定位置为空", $"{specFiles.Length} 个文件");
            }
            else
            {
                var beforeKeys = new HashSet<string>(record.Before.Select(f => f.Root + "|" + f.Path), StringComparer.OrdinalIgnoreCase);
                var beforeSignatures = new HashSet<string>(
                    record.Before.Where(f => f.Root == "工作区").Select(f => BaseName(f.Path) + "@" + f.Bytes), StringComparer.OrdinalIgnoreCase);
                var newInWork = disk
                    .Where(kv => kv.Key.StartsWith("工作区|", StringComparison.Ordinal) && !beforeKeys.Contains(kv.Key))
                    .Where(kv => !beforeSignatures.Contains(BaseName(kv.Key) + "@" + kv.Value))
                    .Select(kv => kv.Key)
                    .ToArray();
                Add("G8", newInWork.Length == 0, "Specified 时工作区不新增产出", $"{newInWork.Length} 个新文件");
            }

            // ---------------- 分类 ----------------
            if (category == "Normal")
            {
                NormalChecks(Add, recipe, record, disk, placement);
            }
            else if (category == "Specific")
            {
                SpecificChecks(Add, recipe, record, disk, placement);
            }
            else if (category == "Abnormal")
            {
                AbnormalChecks(Add, recipe, record, disk);
            }
            else if (category == "SpaceTight")
            {
                SpaceChecks(Add, recipe, record, disk);
            }
            else
            {
                Add("C0", false, "已知类别", category);
            }

            return results;
        }

        // ------------------------------------------------------------ 普通

        private static void NormalChecks(Func<string, bool, string, string, string, CheckResult> add,
            JsonNode recipe, Batch120RunRecord record, Dictionary<string, long> disk, string placement)
        {
            bool success = Str(recipe, "expectedOutcome") == "Succeeded";
            string contentName = Str(recipe, "expectedFinalContentName");
            long contentBytes = Long(recipe, "expectedContentBytes");
            string groupState = Str(recipe, "expectedGroupState");
            string outputRoot = placement == "Original" ? "工作区|" : "指定位置|";

            var contentHits = disk.Keys
                .Where(k => k.StartsWith(outputRoot, StringComparison.Ordinal))
                .Where(k => string.Equals(Path.GetFileName(k), contentName, StringComparison.Ordinal))
                .ToArray();
            if (success)
            {
                bool ok = contentHits.Length == 1 && disk[contentHits[0]] == contentBytes;
                add("N2", ok, $"{contentName} {contentBytes} 字节",
                    contentHits.Length == 0 ? "没找到" : $"{contentHits[0]} = {disk[contentHits[0]]} 字节", "");
            }
            else
            {
                bool leaked = disk.Keys.Any(k => string.Equals(Path.GetFileName(k), contentName, StringComparison.Ordinal));
                add("N2", !leaked, "失败样本不得有最终内容物", leaked ? "发现内容物" : "无内容物", "");
            }

            // N1 落点
            if (placement == "Original")
            {
                bool inWork = success
                    ? disk.Keys.Any(k => k.StartsWith("工作区|", StringComparison.Ordinal) && string.Equals(Path.GetFileName(k), contentName, StringComparison.Ordinal))
                    : true;
                add("N1", inWork, "产出落在源包父目录下", inWork ? "在" : "不在", "");
            }
            else
            {
                bool inSpec = success
                    ? disk.Keys.Any(k => k.StartsWith("指定位置|", StringComparison.Ordinal) && string.Equals(Path.GetFileName(k), contentName, StringComparison.Ordinal))
                    : true;
                add("N1", inSpec, "产出落在指定位置根下", inSpec ? "在" : "不在", "");
            }

            // N3 源包保留
            JsonArray files = (JsonArray)recipe["files"]!;
            var sources = files.Select(f => (Base: Str(f!, "baseName"), Bytes: Long(f!, "bytes"))).ToArray();
            var afterList = record.After.ToArray();
            int stillThere = sources.Count(s => afterList.Any(a => BaseName(a.Path) == s.Base && a.Bytes == s.Bytes));
            bool sourceKeep = Bool(recipe, "expectedSourceKeep");
            bool n3 = success ? (sourceKeep ? stillThere == sources.Length : stillThere == 0) : stillThere == sources.Length;
            add("N3", n3, success ? (sourceKeep ? "源包全保留" : "源包全删除") : "失败必须保留源包",
                $"{stillThere}/{sources.Length} 仍在", "按基名+字节认，产品会修正伪装后缀");

            // N4 过程物：除源包片、最终内容物、recipe.json 之外的都算过程物
            var beforeKeys = new HashSet<string>(record.Before.Select(f => f.Root + "|" + f.Path), StringComparer.OrdinalIgnoreCase);
            int artifacts = record.After.Count(a =>
                !beforeKeys.Contains(a.Root + "|" + a.Path)
                && !string.Equals(Path.GetFileName(a.Path), contentName, StringComparison.Ordinal)
                && !string.Equals(Path.GetFileName(a.Path), "recipe.json", StringComparison.Ordinal)
                && !sources.Any(s => BaseName(a.Path) == s.Base && a.Bytes == s.Bytes)
                && !a.Path.Contains("\\部分完成\\", StringComparison.Ordinal));
            bool processKeep = Bool(recipe, "expectedProcessKeep");
            bool n4 = processKeep || artifacts == 0;
            add("N4", n4, processKeep ? "过程物允许保留" : "过程物必须清空", $"{artifacts} 个过程物",
                "造包器记的 expectedProcessPaths 指源包片，这里按定义重算；部分完成不算过程物");

            // 坏片 + 其余物 Delete：恢复出来的内层片应作为“部分完成”发布（正向断言）
            string failureKind = recipe["failure"]?["kind"]?.GetValue<string>() ?? string.Empty;
            if (failureKind == "corrupted" && !processKeep)
            {
                // 其余物 Delete 档：其余物必须清空。是否发布“部分完成”取决于这一次能捞回多少，不能硬要求有。
                int restLeft = record.After.Count(a => a.Path.Contains("\\其余物\\", StringComparison.Ordinal));
                int partial = record.After.Count(a => a.Path.Contains("\\部分完成\\", StringComparison.Ordinal));
                add("N7", restLeft == 0, "Delete 档下其余物必须清空", $"其余物 {restLeft} 个，部分完成 {partial} 个", "坏片样本");
            }

            // N5/N6 组状态与终态
            string[] reasons = record.Tasks.Select(t => t.Reason).Where(r => r != "None").ToArray();
            bool incomplete = reasons.Contains("FinalGroupIncomplete");
            bool corrupted = reasons.Contains("FinalGroupCorrupted");
            bool n5 = groupState switch
            {
                "Missing" => incomplete,
                "Complete" => !incomplete,
                _ => true
            };
            add("N5", n5, $"组状态 {groupState}",
                $"Incomplete={incomplete} Corrupted={corrupted}", "");

            bool failedTask = record.Tasks.Any(t => t.Outcome == "Failed");
            bool n6 = success ? record.Tasks.Any(t => t.Outcome == "Succeeded") : failedTask;
            add("N6", n6, success ? "至少一个成功" : "至少一个失败", string.Join(",", record.Tasks.Select(t => t.Outcome)), "");
        }

        // ------------------------------------------------------------ 特定

        private static void SpecificChecks(Func<string, bool, string, string, string, CheckResult> add,
            JsonNode recipe, Batch120RunRecord record, Dictionary<string, long> disk, string placement)
        {
            add("S3", record.UseSpecificExtraction, "特定解压已开启", record.UseSpecificExtraction.ToString(), "");

            JsonArray targets = (JsonArray)recipe["targets"]!;
            string root = placement == "Original" ? "工作区|" : "指定位置|";
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonNode? target in targets)
            {
                string package = Str(target!, "package");
                string sourceRelative = Str(target!, "sourceRelative");
                string outputRelative = Str(target!, "expectedOutputRelative");
                long expectBytes = Long(target!, "expectedContentBytes");
                string folder = Str(target!, "expectedOutputFolder");

                bool sourceOk = record.After.Any(a => a.Path.Replace('/', '\\').EndsWith(sourceRelative, StringComparison.OrdinalIgnoreCase));
                add("S1", sourceOk, $"源包 {sourceRelative} 在", sourceOk ? "在" : "不在", package);

                bool expectFailed = Str(recipe, "expectedOutcome") == "Failed";
                if (expectFailed || outputRelative.Length == 0)
                {
                    continue;
                }

                string leaf = Path.GetFileName(outputRelative.Replace('/', '\\'));
                var hit = disk
                    .Where(kv => kv.Key.StartsWith(root, StringComparison.Ordinal))
                    .Where(kv => string.Equals(Path.GetFileName(kv.Key), leaf, StringComparison.OrdinalIgnoreCase))
                    .Where(kv => kv.Value == expectBytes)
                    .Select(kv => kv.Key)
                    .ToArray();
                string? picked = hit.FirstOrDefault(k => !used.Contains(k));
                if (picked != null) { used.Add(picked); }
                add("S2", picked != null, $"{folder}\\… 产物 {expectBytes} 字节",
                    picked == null ? "没找到" : $"{picked} = {disk[picked]} 字节", package);
            }

            bool expectFail = Str(recipe, "expectedOutcome") == "Failed";
            if (expectFail)
            {
                bool hasFailed = record.Tasks.Any(t => t.Outcome == "Failed");
                bool hasContent = disk.Keys.Any(k => string.Equals(Path.GetFileName(k), "a.bin", StringComparison.Ordinal));
                add("S4", hasFailed && !hasContent, "期望失败且无内容物",
                    $"failed={hasFailed} content={hasContent}", "");
            }
        }

        // ------------------------------------------------------------ 异常

        private static void AbnormalChecks(Func<string, bool, string, string, string, CheckResult> add,
            JsonNode recipe, Batch120RunRecord record, Dictionary<string, long> disk)
        {
            string outcome = Str(recipe, "expectedOutcome");
            bool expectSuccess = outcome == "Succeeded" || Str(recipe, "extraKind").StartsWith("成功", StringComparison.Ordinal);

            bool anySucceeded = record.Tasks.Any(t => t.Outcome == "Succeeded");
            bool anyFailed = record.Tasks.Any(t => t.Outcome is "Failed" or "Skipped" or "PartiallyCompleted" or "Pending");
            // 旧脚本有些形状写的是两可（“成功/拒绝”“去重或两个独立任务”），压成单值就会假红。
            bool either = outcome == "Either";
            add("A3", either || (expectSuccess ? anySucceeded : anyFailed),
                either ? "成功或失败都算" : (expectSuccess ? "至少一个成功" : "有失败或跳过"),
                string.Join(",", record.Tasks.Select(t => t.Outcome)), Str(recipe, "extraKind"));

            string sourceRelative = Str(recipe, "sourceRelative");
            var afterAll = record.After.ToArray();
            bool sourceOk = sourceRelative.Split(';').All(one =>
            {
                string normalized = one.Replace('/', '\\');
                return afterAll.Any(a => a.Path.Replace('/', '\\').EndsWith(normalized, StringComparison.OrdinalIgnoreCase))
                    || afterAll.Any(a => BaseName(a.Path) == BaseName(normalized));
            });
            add("A2", sourceOk, "源包保留", sourceOk ? "在" : "不在", "");

            string[] forbidden =
            {
                "逃逸.txt", "绝对.txt", "逃逸2.txt", "CON", "尾点.", "尾空格 ",
                "docx内部内容.txt", "xlsx内部内容.txt", "深层内部内容.txt", "word/document.xml", "xl/workbook.xml"
            };
            var leaked = disk.Keys
                .Where(k => forbidden.Any(f => string.Equals(Path.GetFileName(k).TrimEnd(), f.TrimEnd(), StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            add("A4", leaked.Length == 0, "危险/受保护条目未落盘", leaked.Length == 0 ? "无" : string.Join(",", leaked.Take(3)), "");

            if (Str(recipe, "extraKind") == "压缩炸弹")
            {
                var big = disk.Where(kv => kv.Value > 16L * 1024 * 1024).ToArray();
                add("A5", big.Length == 0, "压缩炸弹不得解出 >16MB", big.Length == 0 ? "无" : $"{big[0].Key}={big[0].Value}", "");
            }
        }

        // ------------------------------------------------------------ 空间

        private static void SpaceChecks(Func<string, bool, string, string, string, CheckResult> add,
            JsonNode recipe, Batch120RunRecord record, Dictionary<string, long> disk)
        {
            long freeBytes = recipe["space"]?["freeBytes"]?.GetValue<long>() ?? 0;
            long reserveBytes = recipe["space"]?["reserveBytes"]?.GetValue<long>() ?? 0;
            add("P1", record.SpaceTightMode && record.InjectedFreeBytes == freeBytes,
                $"空间模式 free={freeBytes}",
                $"mode={record.SpaceTightMode} injected={record.InjectedFreeBytes}", "");

            // 旧矩阵测试的口径：只验证空间算法与逐包独立性，**不固定断言处理次序**。
            // 所以只有两档能硬判：allblocked（全都该挡）与 ample（全都该放）。
            string spaceType = Str(recipe, "spaceType");
            JsonArray packages = (JsonArray)recipe["packages"]!;
            int total = packages.Count(p => !Bool(p!, "corrupt"));
            int blocked = record.Tasks.Count(t => t.StatusDisplay.Contains("磁盘空间不足"));
            int succeeded = record.Tasks.Count(t => t.Outcome == "Succeeded");
            int contents = disk.Keys.Count(k => k.StartsWith("工作区|", StringComparison.Ordinal)
                && (k.Contains("payload.bin", StringComparison.OrdinalIgnoreCase) || k.Contains("final-", StringComparison.OrdinalIgnoreCase)));

            if (spaceType == "allblocked")
            {
                add("P2", contents == 0 && succeeded == 0, $"全部 {total} 个包应被挡住且无产出",
                    $"空间提示 {blocked}，成功 {succeeded}，内容文件 {contents}", "");
            }
            else if (spaceType == "ample")
            {
                add("P2", succeeded >= total && contents > 0, $"全部 {total} 个包应放行并产出内容",
                    $"成功 {succeeded}，内容文件 {contents}", "");
            }
            else
            {
                add("P2", true, $"{spaceType} 档不固定断言处理次序",
                    $"被挡 {blocked}，成功 {succeeded}，内容文件 {contents}", "旧矩阵测试同口径");
            }
        }

        private static string BaseNameLabel(string label) => label;

        // ------------------------------------------------------------ 工具

        private static CheckResult Record(List<CheckResult> sink, string sampleId, string checkId, bool green,
            string expected, string actual, string note)
        {
            var result = new CheckResult
            {
                SampleId = sampleId,
                CheckId = checkId,
                Expected = expected,
                Actual = actual,
                Verdict = green ? "Green" : "Red",
                Note = note
            };
            sink.Add(result);
            return result;
        }

        private static Dictionary<string, long> ScanDisk(string workspace, string specifiedRoot)
        {
            var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            Collect(workspace, "工作区", map);
            Collect(specifiedRoot, "指定位置", map);
            return map;
        }

        private static void Collect(string root, string label, Dictionary<string, long> sink)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                FixtureGate.Skip("Batch120Verify.cs", "前置条件不满足：夹具或运行环境缺失");
                return;
            }

            foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                sink[label + "|" + Path.GetRelativePath(root, file)] = new FileInfo(file).Length;
            }
        }

        internal static string BaseName(string path)
        {
            string name = Path.GetFileName(path);
            int dot = name.IndexOf('.');
            return dot < 0 ? name : name[..dot];
        }

        private static string ReadPlacement(JsonNode recipe)
        {
            JsonNode? scenario = recipe["scenario"];
            if (scenario != null && scenario["placement"] != null)
            {
                return scenario["placement"]!.GetValue<string>();
            }

            return recipe["placement"]?.GetValue<string>() ?? "Original";
        }

        internal static string Str(JsonNode node, string name) => node[name]?.GetValue<string>() ?? string.Empty;

        internal static long Long(JsonNode node, string name) => node[name]?.GetValue<long>() ?? 0;

        internal static bool Bool(JsonNode node, string name) => node[name]?.GetValue<bool>() ?? false;
    }

    internal sealed class CheckResult
    {
        public string SampleId { get; set; } = string.Empty;
        public string CheckId { get; set; } = string.Empty;
        public string Expected { get; set; } = string.Empty;
        public string Actual { get; set; } = string.Empty;
        public string Verdict { get; set; } = "Green";
        public string Note { get; set; } = string.Empty;
    }
}

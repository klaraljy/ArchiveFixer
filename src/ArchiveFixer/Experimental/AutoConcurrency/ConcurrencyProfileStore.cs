// STATUS: 未完成 / 暂停（2026-11-05）。自动并发实验代码，不接入产品路径。
using ArchiveFixer.Extraction;
using ArchiveFixer.Storage;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ArchiveFixer.Experimental.AutoConcurrency;

/// <summary>
/// 本地 profile 存取（SQL Server DOP feedback 的“持久化已验证值”类比）。
/// 键=卷根 + 设备类别；换盘自然失效；损坏/读不出 ⇒ 返回 null。
/// </summary>
public static class ConcurrencyProfileStore
{
    public static string BuildKey(string? targetRoot, StorageDeviceKind device)
    {
        string normalized = string.IsNullOrWhiteSpace(targetRoot)
            ? string.Empty
            : Path.GetFullPath(targetRoot).TrimEnd('\\').ToUpperInvariant();

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized + "|" + device));
        return Convert.ToHexString(hash, 0, 12).ToLowerInvariant();
    }

    public static ConcurrencyProfile? Load(
        string directory,
        string? targetRoot,
        StorageDeviceKind device)
    {
        try
        {
            string path = PathFor(directory, targetRoot, device);

            if (!File.Exists(path))
            {
                return null;
            }

            ConcurrencyProfile? profile = JsonSerializer.Deserialize<ConcurrencyProfile>(
                File.ReadAllText(path, Encoding.UTF8));

            return profile != null && profile.LastGoodLimit >= 1 && profile.LastGoodLimit <= 8
                ? profile
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static void Save(
        string directory,
        string? targetRoot,
        StorageDeviceKind device,
        ConcurrencyProfile profile)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string path = PathFor(directory, targetRoot, device);
            string temp = path + ".tmp";
            string json = JsonSerializer.Serialize(profile);
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            FileOperations.Move(temp, path, overwrite: true);
        }
        catch
        {
            // 写不进去不影响本批；下次重新学习。
        }
    }

    public static string PathFor(string directory, string? targetRoot, StorageDeviceKind device) =>
        Path.Combine(directory, "concurrency-" + BuildKey(targetRoot, device) + ".json");
}

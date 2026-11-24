using System;
using System.IO;
using System.Text;

namespace Growth.Algorithms.Generation;

/// <summary>
/// 生成结果缓存（规划稿 §10.4）：user://generated/{seed}_{tier}_gv{n}.json。
/// 命中直接返回 JSON（0 ms 生成）；容量超限按最后访问时间淘汰（LRU）。
/// 缓存内容与同 seed 现场生成逐字一致，不破坏决定性。
/// </summary>
public sealed class GenerationCache
{
    private readonly string _dir;
    private readonly int _maxEntries;

    public GenerationCache(string dir, int maxEntries)
    {
        _dir = dir ?? throw new ArgumentNullException(nameof(dir));
        _maxEntries = Math.Max(1, maxEntries);
        Directory.CreateDirectory(_dir);
    }

    public string DirectoryPath => _dir;
    public int MaxEntries => _maxEntries;

    public int Count
    {
        get
        {
            if (!Directory.Exists(_dir))
            {
                return 0;
            }
            return Directory.GetFiles(_dir, "*.json").Length;
        }
    }

    public bool TryGet(long seed, string tier, int generatorVersion, out string json)
    {
        string path = PathFor(seed, tier, generatorVersion);
        if (!File.Exists(path))
        {
            json = "";
            return false;
        }
        try
        {
            json = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrEmpty(json))
            {
                return false;
            }
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            return true;
        }
        catch (IOException)
        {
            json = "";
            return false;
        }
    }

    public void Put(long seed, string tier, int generatorVersion, string json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return;
        }
        Directory.CreateDirectory(_dir);
        string path = PathFor(seed, tier, generatorVersion);
        File.WriteAllText(path, json, new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        Trim();
    }

    public void Clear()
    {
        if (!Directory.Exists(_dir))
        {
            return;
        }
        foreach (string file in Directory.GetFiles(_dir, "*.json"))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // 缓存删除失败不影响游戏。
            }
        }
    }

    private void Trim()
    {
        string[] files = Directory.GetFiles(_dir, "*.json");
        if (files.Length <= _maxEntries)
        {
            return;
        }
        Array.Sort(files, (a, b) => File.GetLastWriteTimeUtc(a).CompareTo(File.GetLastWriteTimeUtc(b)));
        int remove = files.Length - _maxEntries;
        for (int i = 0; i < remove; i++)
        {
            try
            {
                File.Delete(files[i]);
            }
            catch (IOException)
            {
                // 忽略：下次写入再尝试淘汰。
            }
        }
    }

    private string PathFor(long seed, string tier, int generatorVersion)
    {
        string safeTier = string.IsNullOrEmpty(tier) ? "normal" : tier;
        return Path.Combine(_dir, $"{seed}_{safeTier}_gv{generatorVersion}.json");
    }
}

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Growth.Algorithms.Scoring;

/// <summary>一个评分锚点：D_raw 实测值 → 目标分。</summary>
public sealed class ScoreAnchor
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public double DRaw { get; set; }
    public int Score { get; set; }
}

/// <summary>
/// 锚点快照（tests/fixtures/score_anchors.json 的 C# 契约）。
/// 内置副本供运行时评分使用；文件快照用于回归校验，二者必须一致。
/// </summary>
public sealed class ScoreAnchors
{
    public int ScoringVersion { get; set; } = 2;
    public List<ScoreAnchor> Anchors { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    private static readonly Lazy<ScoreAnchors> LazyDefault = new(Builtin);

    public static ScoreAnchors Default => LazyDefault.Value;

    public string ToJson()
    {
        return JsonSerializer.Serialize(this, JsonOptions);
    }

    public static ScoreAnchors FromJson(string json)
    {
        return JsonSerializer.Deserialize<ScoreAnchors>(json, JsonOptions)
            ?? throw new InvalidOperationException("score_anchors JSON 解析失败");
    }

    /// <summary>
    /// 内置锚点：v5 原始值刻度（2026-11-25，方案 C）。
    /// Score = 原始值 + ScoreOffset；难度档由 BranchScoreSchemes.TierForScore 按分数划分。
    /// </summary>
    private static ScoreAnchors Builtin()
    {
        return new ScoreAnchors
        {
            ScoringVersion = 5,
            Anchors = new List<ScoreAnchor>
            {
                new() { Id = "A0", Name = "空场直达", DRaw = 0.0, Score = 7 },
                new() { Id = "L1", Name = "Level_01_move", DRaw = 10.6930, Score = 18 },
                new() { Id = "L2", Name = "Level_02_petals", DRaw = 19.0357, Score = 26 },
                new() { Id = "L3", Name = "Level_03_obstacles", DRaw = 38.1363, Score = 45 },
                new() { Id = "L4", Name = "Level_04_GrowingFlowers", DRaw = 40.4512, Score = 47 },
                new() { Id = "L5", Name = "Level_05_CounterIt", DRaw = 50.5430, Score = 58 },
            },
        };
    }
}

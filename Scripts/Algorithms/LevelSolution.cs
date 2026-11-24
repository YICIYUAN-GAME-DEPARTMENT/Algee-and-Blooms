using System.Collections.Generic;

namespace Growth.Algorithms;

/// <summary>求解 / 生成的路线契约（solver_version = 1）。</summary>
public sealed class LevelSolution
{
    public string Status { get; set; } = "UNKNOWN";
    public int Steps { get; set; }
    public string Route { get; set; } = "";
    public int SolverVersion { get; set; } = 1;
}

/// <summary>评分契约（scoring_version = 2）：难度 0–200 + 质量分 + 指标向量 + 事后标签。</summary>
public sealed class LevelScore
{
    /// <summary>兼容旧字段，等于 Difficulty。</summary>
    public int Total { get; set; }
    public int Difficulty { get; set; }
    public int Quality { get; set; }
    public int ScoringVersion { get; set; } = 2;
    public Dictionary<string, double> Metrics { get; set; } = new();
    public List<string> Tags { get; set; } = new();
    public Dictionary<string, string> TagEvidence { get; set; } = new();
    public int TargetScore { get; set; }
    public bool TargetMiss { get; set; }
    /// <summary>C 类指标（M14/M16）是否因预算熔断使用了保守下界。</summary>
    public bool CEstimated { get; set; }
}

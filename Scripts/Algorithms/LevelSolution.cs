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

/// <summary>评分契约（scoring_version = 5）：难度 0–200 的原始值刻度 + 质量分 + 指标向量 + 事后标签。</summary>
public sealed class LevelScore
{
    /// <summary>兼容旧字段，等于 Difficulty。</summary>
    public int Total { get; set; }
    public int Difficulty { get; set; }
    public int Quality { get; set; }
    public int ScoringVersion { get; set; } = 5;
    public Dictionary<string, double> Metrics { get; set; } = new();
    public List<string> Tags { get; set; } = new();
    public Dictionary<string, string> TagEvidence { get; set; } = new();
    /// <summary>生成目标步数（真实最短路）。</summary>
    public int TargetSteps { get; set; }
    /// <summary>目标步数未命中（最近候选仍超出容差）。</summary>
    public bool TargetMiss { get; set; }
    /// <summary>C 类指标（M14/M16）是否因预算熔断使用了保守下界。</summary>
    public bool CEstimated { get; set; }
}

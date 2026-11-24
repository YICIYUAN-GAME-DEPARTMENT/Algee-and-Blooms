using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Godot;
using Growth.Algorithms.Generation;

namespace Growth.Tools;

/// <summary>
/// PhysarumBench — 对比经典路线优先 / 黏菌枝干两种生成模型的成关率与枝干结构。
/// 运行：godot --headless --path . res://tools/PhysarumBench.tscn
/// </summary>
public partial class PhysarumBench : Node
{
    private struct Row
    {
        public string Tier;
        public string Model;
        public bool Success;
        public double Ms;
        public int Attempts;
        public bool Aborted;
        public string Reason;
        public int Steps;
        public int TargetSteps;
        public int Score;
        public double Top;
        public double Ratio;
        public bool TopTrue;
    }

    public override void _Ready()
    {
        var tiers = new[] { "normal", "expert" };
        var models = new[] { GeneratorModel.RouteFirst, GeneratorModel.Physarum };
        var rows = new List<Row>();
        foreach (string tier in tiers)
        {
            foreach (GeneratorModel model in models)
            {
                for (long seed = 1; seed <= 5; seed++)
                {
                    Row row = Run(tier, model, seed, out var reasons);
                    rows.Add(row);
                    string reasonText = string.Join(",", reasons.Select(kv => $"{kv.Key}x{kv.Value}"));
                    GD.Print($"[Gen] tier={tier} model={model} seed={seed} ok={row.Success} " +
                             $"ms={row.Ms:F0} attempts={row.Attempts} aborted={row.Aborted} " +
                             $"steps={row.Steps}/{row.TargetSteps} score={row.Score} " +
                             $"branchTop={row.Top:F1} ratio={row.Ratio:F2} topTrue={row.TopTrue} " +
                             $"reasons={reasonText}");
                }
            }
        }
        Summarize(rows);
        GetTree().Quit(0);
    }

    private static Row Run(string tier, GeneratorModel model, long seed,
        out Dictionary<string, int> reasons)
    {
        var cfg = new GeneratorConfig
        {
            Seed = seed,
            Difficulty = tier,
            GeneratorModel = model,
            ObstacleModel = ObstacleModel.PoissonCluster,
        };
        GenerationBudget budget = GenerationBudget.For(GenerationBudgetTier.Loading);
        var job = new GenerationJob(cfg, budget);
        long start = Stopwatch.GetTimestamp();
        GenerationResult result = job.RunToCompletion();
        double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        reasons = new Dictionary<string, int>(job.ReasonCounts);
        var row = new Row
        {
            Tier = tier,
            Model = model.ToString(),
            Success = result != null && result.Success,
            Ms = ms,
            Attempts = job.Attempts,
            Aborted = job.BudgetAborted,
            Reason = result?.Reason ?? "",
            Steps = result?.Solution?.Steps ?? 0,
            TargetSteps = job.TargetSteps,
            Score = result?.Score?.Difficulty ?? 0,
            Top = Metric(result, "branch_top"),
            Ratio = Metric(result, "branch_ratio"),
            TopTrue = Metric(result, "branch_top_true") > 0.5,
        };
        return row;
    }

    private static double Metric(GenerationResult result, string key)
    {
        return result?.Metrics != null && result.Metrics.TryGetValue(key, out double v) ? v : 0;
    }

    private static void Summarize(List<Row> rows)
    {
        var groups = new Dictionary<string, (int Ok, int Total, double Ms, double Steps)>();
        foreach (Row row in rows)
        {
            string key = row.Tier + "/" + row.Model;
            groups.TryGetValue(key, out var g);
            groups[key] = (
                g.Ok + (row.Success ? 1 : 0),
                g.Total + 1,
                g.Ms + row.Ms,
                g.Steps + row.Steps);
        }
        foreach (KeyValuePair<string, (int Ok, int Total, double Ms, double Steps)> kv in groups)
        {
            GD.Print($"[Summary] {kv.Key} ok={kv.Value.Ok}/{kv.Value.Total} " +
                     $"avgMs={kv.Value.Ms / kv.Value.Total:F0} " +
                     $"avgSteps={kv.Value.Steps / Math.Max(1, kv.Value.Ok):F1}");
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Growth.Algorithms.Generation;
using Growth.Algorithms.Scoring;

namespace Growth.Tools;

/// <summary>
/// ScoreDistBench — 原始分刻度下的生成分布：均值 / 中位数 / 众数 / 分位数 / 难度档占比。
/// 运行：godot --headless --path . res://tools/ScoreDistBench.tscn [-- --seeds=10]
/// </summary>
public partial class ScoreDistBench : Node
{
    public override void _Ready()
    {
        int seeds = 8;
        var tiers = new List<string> { "easy", "normal", "hard", "expert" };
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--seeds="))
            {
                int.TryParse(arg.Substring("--seeds=".Length), out seeds);
            }
            else if (arg.StartsWith("--tiers="))
            {
                tiers = arg.Substring("--tiers=".Length).Split(',').Select(t => t.Trim()).ToList();
            }
        }
        seeds = Math.Clamp(seeds, 1, 200);
        var all = new List<int>();
        var summaries = new List<string>();
        foreach (string tier in tiers)
        {
            foreach (GeneratorModel model in new[] { GeneratorModel.RouteFirst, GeneratorModel.Physarum })
            {
                var scores = new List<int>();
                for (long seed = 1; seed <= seeds; seed++)
                {
                    var cfg = new GeneratorConfig
                    {
                        Seed = seed,
                        Difficulty = tier,
                        GeneratorModel = model,
                        ObstacleModel = ObstacleModel.PoissonCluster,
                    };
                    var job = new GenerationJob(cfg, GenerationBudget.For(GenerationBudgetTier.Loading));
                    GenerationResult result = job.RunToCompletion();
                    if (result != null && result.Success)
                    {
                        scores.Add(result.Score.Difficulty);
                    }
                }
                if (scores.Count == 0)
                {
                    summaries.Add($"{tier}/{model}: 无成功样本");
                    continue;
                }
                scores.Sort();
                double mean = scores.Average();
                int median = scores[scores.Count / 2];
                int mode = Mode(scores);
                summaries.Add($"{tier}/{model} n={scores.Count} mean={mean:F1} median={median} " +
                    $"mode={mode} p25={Percentile(scores, 0.25)} p75={Percentile(scores, 0.75)} " +
                    $"p90={Percentile(scores, 0.90)} min={scores[0]} max={scores[^1]}");
                summaries.Add($"  档位: Easy={CountTier(scores, "Easy")} Normal={CountTier(scores, "Normal")} " +
                    $"Hard={CountTier(scores, "Hard")} Expert={CountTier(scores, "Expert")}");
                int floor = FloorOf(tier);
                var kept = scores.Where(s => s >= floor).ToList();
                if (kept.Count > 0)
                {
                    summaries.Add($"  筛选(≥{floor}): n={kept.Count} mean={kept.Average():F1} " +
                        $"median={Percentile(kept, 0.5)} mode={Mode(kept)} min={kept[0]} max={kept[^1]} " +
                        $"Hard={CountTier(kept, "Hard")} Expert={CountTier(kept, "Expert")}");
                }
                all.AddRange(kept);
            }
        }
        foreach (string line in summaries)
        {
            GD.Print($"[Dist] {line}");
        }
        if (all.Count > 0)
        {
            all.Sort();
            GD.Print($"[Dist] 全体 n={all.Count} mean={all.Average():F1} median={Percentile(all, 0.5)} " +
                $"mode={Mode(all)} p25={Percentile(all, 0.25)} p75={Percentile(all, 0.75)} " +
                $"p90={Percentile(all, 0.90)} min={all[0]} max={all[^1]}");
            PrintHistogram(all);
        }
        GetTree().Quit(0);
    }

    private static void PrintHistogram(List<int> scores)
    {
        int[] edges = { 0, 10, 20, 30, 40, 45, 50, 55, 60, 70, 80, 90, 200 };
        for (int i = 0; i < edges.Length - 1; i++)
        {
            int lo = edges[i];
            int hi = edges[i + 1];
            int count = scores.Count(s => s >= lo && s < hi);
            string bar = new string('#', Math.Min(60, count));
            GD.Print($"[Hist] [{lo,3},{hi,3}) {count,3} {bar}");
        }
    }

    private static int FloorOf(string tier)
    {
        return tier switch
        {
            "normal" => 30,
            "hard" => 45,
            "expert" => 48,
            _ => 0,
        };
    }

    private static int CountTier(List<int> scores, string tier)
    {
        return scores.Count(s => BranchScoreSchemes.TierForScore(s) == tier);
    }

    private static int Percentile(List<int> sorted, double p)
    {
        if (sorted.Count == 0)
        {
            return 0;
        }
        int index = (int)Math.Round((sorted.Count - 1) * p);
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }

    private static int Mode(List<int> scores)
    {
        return scores.GroupBy(s => s).OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
            .First().Key;
    }
}

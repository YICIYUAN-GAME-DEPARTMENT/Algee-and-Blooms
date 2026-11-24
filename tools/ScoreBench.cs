using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Growth.Algorithms;
using Growth.Algorithms.Generation;
using Growth.Algorithms.Scoring;

namespace Growth.Tools;

/// <summary>
/// ScoreBench — v5 计分原型：静态枝干先验 vs 逐步动态后验。
/// 运行：godot --headless --path . res://tools/ScoreBench.tscn
/// </summary>
public partial class ScoreBench : Node
{
    private static readonly (int Id, string Name, string Route)[] Anchors =
    {
        (1, "Level_01_move", "DDDDDDDDD"),
        (2, "Level_02_petals", "DDDDDSWDDDDD"),
        (3, "Level_03_obstacles", "DSDDDAAAWWDDDDDWDDS"),
        (4, "Level_04_GrowingFlowers", "DDDDDSSDDDSSSSSSS"),
        (5, "Level_05_CounterIt", "DDWDAWAWWDWDDDDWDDD"),
    };

    public override void _Ready()
    {
        GD.Print("=== 锚点关（原始值刻度：Easy/Normal/Hard/Expert）===");
        foreach ((int id, string name, string route) in Anchors)
        {
            LevelSpec spec = LevelSpec.FromJson(
                FileAccess.GetFileAsString($"res://tests/fixtures/anchors/level_{id:00}.json"));
            (BranchPosteriorFeatures f, MetricVector v) = Measure(spec, route);
            Report($"L{id} {name}", spec, route, f, v);
        }
        if (OS.GetCmdlineUserArgs().Contains("--anchors-only"))
        {
            GetTree().Quit(0);
            return;
        }
        GD.Print("=== 生成关（normal / expert × 两种模型 × 5 seeds）===");
        foreach (string tier in new[] { "normal", "expert" })
        {
            foreach (GeneratorModel model in new[] { GeneratorModel.RouteFirst, GeneratorModel.Physarum })
            {
                var rows = new List<(BranchPosteriorFeatures F, MetricVector V, bool Ok)>();
                for (long seed = 1; seed <= 5; seed++)
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
                    if (result == null || !result.Success)
                    {
                        continue;
                    }
                    (BranchPosteriorFeatures f, MetricVector v) =
                        Measure(result.Spec, result.Solution.Route, seed);
                    rows.Add((f, v, true));
                    Report($"{tier}/{model}/seed{seed}", result.Spec, result.Solution.Route, f, v);
                }
                Summarize($"{tier}/{model}", rows);
            }
        }
        GetTree().Quit(0);
    }

    private static (BranchPosteriorFeatures, MetricVector) Measure(
        LevelSpec spec, string route, long seed = 0)
    {
        var options = new MetricOptions
        {
            EnableFalseAffordance = false,
            EnableFunnel = false,
            EnableNaiveStrategies = false,
            EnableSolveStates = false,
            EnableRequiredFood = false,
        };
        MetricVector v = MetricExtractor.Extract(spec, route, options);
        BranchPosteriorFeatures f = BranchPosteriorScorer.Extract(spec, route, seed);
        return (f, v);
    }

    private static void Report(string tag, LevelSpec spec, string route,
        BranchPosteriorFeatures f, MetricVector v)
    {
        GD.Print($"[Score] {tag} steps={route.Length} dCore={v.DCore:F1} " +
                 $"branches={f.BranchCount} sig={f.SignificantBranches} " +
                 $"top={f.TopThickness:F0} ratio={f.RatioSecondTop:F2} " +
                 $"trueIdx={f.TrueBranchIndex} fakeMass={f.FakeMass:F2} topFake={f.TopFakeMass:F2} " +
                 $"H0={f.PriorEntropy:F2} initAlive={f.InitialAliveMass:F2} staticFake={f.StaticFakeMass:F2} " +
                 $"auc={f.SurvivalAuc:F2} endAlive={f.AliveMassEnd:F2} Hdrop={f.EntropyDrop:F2} " +
                 $"kl={f.KlBits:F2} deathMass={f.DeathMass:F2} lateDeath={f.LateDeathMass:F2} " +
                 $"trapDepth={f.TrapDepth:F1} lateTrap={f.LateTrapDepth:F1} " +
                 $"wDeath={f.WeightedDeathFrac:F2} collapse={f.CollapseStep:F2} " +
                 $"A={BranchScoreSchemes.StaticDeception(f, v):F1} " +
                 $"B={BranchScoreSchemes.PosteriorCollapse(f, v):F1} " +
                 $"C={BranchScoreSchemes.Hybrid(f, v):F4}" +
                 $"({TierOf(BranchScoreSchemes.Hybrid(f, v))}) " +
                 $"D={BranchScoreSchemes.Information(f):F1}");
    }

    private static void Summarize(string tag,
        List<(BranchPosteriorFeatures F, MetricVector V, bool Ok)> rows)
    {
        if (rows.Count == 0)
        {
            GD.Print($"[Summary] {tag} 无成功样本");
            return;
        }
        double fake = 0, auc = 0, late = 0, hdrop = 0, kl = 0, dcore = 0;
        double a = 0, b = 0, c = 0, d = 0;
        int minC = 999;
        int maxC = 0;
        int easy = 0, normal = 0, hard = 0, expert = 0;
        foreach ((BranchPosteriorFeatures f, MetricVector v, bool _) in rows)
        {
            fake += f.FakeMass;
            auc += f.SurvivalAuc;
            late += f.LateDeathMass;
            double h0 = Math.Max(1e-9, f.PriorEntropy);
            hdrop += Math.Clamp(f.EntropyDrop / h0, 0, 1);
            kl += Math.Clamp(f.KlBits / 3.0, 0, 1);
            dcore += v.DCore;
            a += BranchScoreSchemes.StaticDeception(f, v);
            b += BranchScoreSchemes.PosteriorCollapse(f, v);
            double rawC = BranchScoreSchemes.Hybrid(f, v);
            c += rawC;
            d += BranchScoreSchemes.Information(f);
            int scoreC = BranchScoreSchemes.ScoreForRaw(rawC);
            minC = Math.Min(minC, scoreC);
            maxC = Math.Max(maxC, scoreC);
            switch (BranchScoreSchemes.TierForScore(scoreC))
            {
                case "Easy": easy++; break;
                case "Normal": normal++; break;
                case "Hard": hard++; break;
                default: expert++; break;
            }
        }
        int n = rows.Count;
        GD.Print($"[Summary] {tag} n={n} fake={fake / n:F2} auc={auc / n:F2} " +
                 $"late={late / n:F2} HdropN={hdrop / n:F2} klN={kl / n:F2} dCore={dcore / n:F1} " +
                 $"rawA={a / n:F1} rawB={b / n:F1} rawC={c / n:F1} rawD={d / n:F1}");
        GD.Print($"[Tier] {tag} C score range {minC}–{maxC} " +
                 $"Easy={easy} Normal={normal} Hard={hard} Expert={expert}");
    }

    private static string TierOf(double raw)
    {
        return BranchScoreSchemes.TierForScore(BranchScoreSchemes.ScoreForRaw(raw));
    }

    // ── 方案实现见 Scripts/Algorithms/Scoring/BranchScoreSchemes.cs ──
}

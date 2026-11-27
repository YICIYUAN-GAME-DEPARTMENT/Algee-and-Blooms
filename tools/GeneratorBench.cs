using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;
using Growth.Algorithms;
using Growth.Algorithms.Generation;

namespace Growth.Tools;

/// <summary>
/// GeneratorBench — 生成器 headless 跑批（S0 基线工具）。
/// 运行：
///   godot --headless --path . res://tools/GeneratorBench.tscn -- \
///     --seeds=60 --tiers=easy,normal,hard --out=res://tests/fixtures/bench
/// 输出：
///   bench_gv{N}.json   逐 seed 确定性数据（键序固定、无时间戳，可 diff）
///   summary_gv{N}.json 汇总统计（含耗时，允许浮动）
/// `--seeds` 为**每个 tier** 的 seed 数；默认 3 tier × 60 = 180。
/// </summary>
public partial class GeneratorBench : Node
{
    private const int ScoringVersion = 2;
    private static readonly string[] TagNames =
    {
        "false_shortcut", "gate_sequence", "forced_truncation", "overshoot_return",
        "mirror_fork", "mutual_exclusion", "body_key", "last_gasp",
        "tight_budget", "no_mechanism", "dense_maze",
    };

    public override void _Ready()
    {
        Dictionary<string, string> args = ParseArgs(OS.GetCmdlineUserArgs());
        int seedsPerTier = GetInt(args, "seeds", 60);
        string[] tiers = GetString(args, "tiers", "easy,normal,hard")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string outDir = GetString(args, "out", "res://tests/fixtures/bench");
        int width = GetInt(args, "width", 0);
        int height = GetInt(args, "height", 0);
        int candidates = GetInt(args, "candidates", 1);
        int maxAttempts = GetInt(args, "attempts", 32);
        int searchAttempts = GetInt(args, "search", 10);
        string budgetName = GetString(args, "budget", "unlimited");
        GenerationBudget genBudget = budgetName is "unlimited" or "none"
            ? GenerationBudget.Unlimited()
            : GenerationBudget.For(GenerationBudget.ParseTier(budgetName));
        int genver = GetInt(args, "genver", GenerationService.GeneratorVersion);
        bool checkDeterminism = GetInt(args, "determinism", 1) != 0;
        bool gate = GetInt(args, "gate", 1) != 0;
        bool adaptive = GetInt(args, "adaptive", 1) != 0;
        string modelName = GetString(args, "model", "poisson");
        ObstacleModel obstacleModel = modelName switch
        {
            "blue_noise" or "blue" => ObstacleModel.BlueNoise,
            "noise_cluster" or "noise" => ObstacleModel.NoiseCluster,
            "wall_growth" or "wall" => ObstacleModel.WallGrowth,
            _ => ObstacleModel.PoissonCluster,
        };
        GeneratorModel genModel = GetString(args, "generator", "route_first") switch
        {
            "template" or "structured" => GeneratorModel.TemplateField,
            "physarum" or "flow" => GeneratorModel.Physarum,
            "portfolio" or "hybrid" or "auto" => GeneratorModel.Portfolio,
            _ => GeneratorModel.RouteFirst,
        };

        if (tiers.Length == 0)
        {
            GD.PushError("GeneratorBench: --tiers 为空");
            GetTree().Quit(1);
            return;
        }

        var seedRows = new JsonArray();
        var timings = new List<double>();
        var perTier = new Dictionary<string, List<int>>();
        foreach (string tier in tiers)
        {
            perTier[tier] = new List<int>();
        }

        int ok = 0;
        int failed = 0;
        bool determinismOk = true;
        var sizeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var tagCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var allScores = new List<int>();
        var targetDeltas = new List<int>();
        var targetTotals = new Dictionary<string, int>(StringComparer.Ordinal);
        var targetMisses = new Dictionary<string, int>(StringComparer.Ordinal);
        int targetMissCount = 0;
        var workUnits = new List<double>();
        var stepValues = new List<int>();
        int budgetAborted = 0;
        var reasonCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var sourceCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var endpointAgg = new Dictionary<string, EndpointAgg>(StringComparer.Ordinal);
        foreach (string tag in TagNames)
        {
            tagCounts[tag] = 0;
        }

        foreach (string tier in tiers)
        {
            int tierOk = 0;
            for (long seed = 1; seed <= seedsPerTier; seed++)
            {
                var cfg = new GeneratorConfig
                {
                    Seed = seed,
                    Width = width,
                    Height = height,
                    Difficulty = tier,
                    Candidates = candidates,
                    MaxAttempts = maxAttempts,
                    SearchAttempts = searchAttempts,
                    GeneratorModel = genModel,
                    ObstacleModel = obstacleModel,
                    LevelIndex = (int)(seed - 1),
                    EnableFairnessGate = gate,
                    AdaptiveBudget = adaptive,
                };

                var watch = Stopwatch.StartNew();
                var job = new GenerationJob(cfg, genBudget);
                GenerationResult result = job.RunToCompletion();
                watch.Stop();
                timings.Add(watch.Elapsed.TotalMilliseconds);
                foreach (KeyValuePair<string, int> rc in job.ReasonCounts)
                {
                    reasonCounts[rc.Key] = reasonCounts.TryGetValue(rc.Key, out int rn)
                        ? rn + rc.Value
                        : rc.Value;
                }

                var row = new JsonObject
                {
                    ["seed"] = seed,
                    ["tier"] = tier,
                    ["width"] = width,
                    ["height"] = height,
                };

                if (!result.Success)
                {
                    row["status"] = "gen_fail";
                    row["reason"] = result.Reason;
                    row["attempts_used"] = result.Attempts;
                    failed++;
                    seedRows.Add(row);
                    continue;
                }

                string specJson = result.Spec.ToJson();
                bool same = true;
                if (checkDeterminism)
                {
                    var secondCfg = new GeneratorConfig
                    {
                        Seed = seed,
                        Width = width,
                        Height = height,
                        Difficulty = tier,
                        Candidates = candidates,
                        MaxAttempts = maxAttempts,
                        SearchAttempts = searchAttempts,
                        GeneratorModel = genModel,
                        ObstacleModel = obstacleModel,
                        LevelIndex = (int)(seed - 1),
                        EnableFairnessGate = gate,
                        AdaptiveBudget = adaptive,
                    };
                    var secondJob = new GenerationJob(secondCfg, genBudget);
                    GenerationResult second = secondJob.RunToCompletion();
                    same = second.Success
                        && second.Spec.ToJson() == specJson
                        && second.Solution.Route == result.Solution.Route;
                }
                determinismOk &= same;

                bool replayOk = Replay(specJson, result.Solution.Route);
                row["status"] = replayOk ? "ok" : "replay_fail";
                row["width"] = result.Spec.Width;
                row["height"] = result.Spec.Height;
                row["schema_version"] = result.Spec.SchemaVersion;
                row["tier_label"] = result.Spec.Tier;
                row["generation_source"] = result.Spec.GenerationSource;
                row["param_signature"] = result.Spec.ParamSignature;
                row["target_steps"] = result.Spec.TargetSteps;
                row["attempts_used"] = result.Attempts;
                row["steps"] = result.Solution.Steps;
                row["floor_count"] = result.Spec.Floor.Count;
                row["food_count"] = result.Spec.Foods.Count;
                row["mech_count"] = result.Spec.MechCenters.Count;
                row["route"] = result.Solution.Route;
                row["metrics"] = MetricsToJson(result.Metrics);
                var tagsArr = new JsonArray();
                foreach (string tag in result.Spec.Tags)
                {
                    tagsArr.Add(tag);
                    tagCounts[tag] = tagCounts.TryGetValue(tag, out int tc) ? tc + 1 : 1;
                }
                row["tags"] = tagsArr;
                var evidenceObj = new JsonObject();
                foreach (KeyValuePair<string, string> kv in result.Spec.TagEvidence)
                {
                    evidenceObj[kv.Key] = kv.Value;
                }
                row["tag_evidence"] = evidenceObj;
                row["score"] = result.Score?.Difficulty ?? 0;
                row["quality"] = result.Score?.Quality ?? 0;
                row["target_steps"] = result.TargetSteps;
                row["target_delta"] = result.TargetDelta;
                row["target_miss"] = result.TargetMiss;
                row["archive_cells"] = result.ArchiveCells;
                row["budget_aborted"] = result.BudgetAborted;
                row["work_units"] = Round3(result.WorkUnits);
                row["ms"] = Round3(watch.Elapsed.TotalMilliseconds);
                row["spec_sha256"] = Sha256(specJson);
                row["deterministic"] = same;

                EndpointDiag diag = AnalyzeEndpoints(result.Spec, result.Solution.Route);
                row["spawn"] = new JsonArray(diag.Spawn.X, diag.Spawn.Y);
                row["goal"] = new JsonArray(diag.Goal.X, diag.Goal.Y);
                row["spawn_margin"] = diag.SpawnMargin;
                row["goal_margin"] = diag.GoalMargin;
                row["spawn_degree"] = diag.SpawnDegree;
                row["goal_degree"] = diag.GoalDegree;
                row["separation"] = diag.Separation;
                row["loop_rank"] = diag.LoopRank;
                row["choice_cells"] = diag.ChoiceCells;
                var decoyArr = new JsonArray();
                foreach (double offset in diag.DecoyOffsets)
                {
                    decoyArr.Add(Round3(offset));
                }
                row["decoy_offsets"] = decoyArr;

                if (replayOk)
                {
                    ok++;
                    tierOk++;
                    int score = result.Score?.Difficulty ?? 0;
                    perTier[tier].Add(score);
                    allScores.Add(score);
                    workUnits.Add(result.WorkUnits);
                    if (result.BudgetAborted)
                    {
                        budgetAborted++;
                    }
                    if (result.TargetSteps > 0)
                    {
                        targetDeltas.Add(result.TargetDelta);
                        targetTotals[tier] = targetTotals.TryGetValue(tier, out int tt) ? tt + 1 : 1;
                        if (result.TargetMiss)
                        {
                            targetMissCount++;
                            targetMisses[tier] = targetMisses.TryGetValue(tier, out int tm) ? tm + 1 : 1;
                        }
                    }
                    stepValues.Add(result.Solution.Steps);
                    if (!endpointAgg.TryGetValue(tier, out EndpointAgg agg))
                    {
                        agg = new EndpointAgg();
                        endpointAgg[tier] = agg;
                    }
                    agg.Add(diag);
                    string sizeKey = $"{result.Spec.Width}x{result.Spec.Height}";
                    sizeCounts[sizeKey] = sizeCounts.TryGetValue(sizeKey, out int sc) ? sc + 1 : 1;
                    string source = result.Spec.GenerationSource;
                    if (!string.IsNullOrEmpty(source))
                    {
                        sourceCounts[source] = sourceCounts.TryGetValue(source, out int src) ? src + 1 : 1;
                    }
                }
                else
                {
                    failed++;
                }
                seedRows.Add(row);
            }
            GD.Print($"[GeneratorBench] tier={tier} seeds={seedsPerTier} ok={tierOk} fail={seedsPerTier - tierOk}");
        }

        var bench = new JsonObject
        {
            ["meta"] = new JsonObject
            {
                ["generator_version"] = genver,
                ["scoring_version"] = ScoringVersion,
                ["seeds_per_tier"] = seedsPerTier,
                ["tiers"] = ToJsonArray(tiers),
                ["width"] = width,
                ["height"] = height,
                ["candidates"] = candidates,
                ["max_attempts"] = maxAttempts,
                ["search_attempts"] = searchAttempts,
                ["budget_tier"] = budgetName,
                ["generator"] = genModel.ToString(),
                ["model"] = modelName,
                ["gate"] = gate,
                ["adaptive"] = adaptive,
                ["determinism_checked"] = checkDeterminism,
            },
            ["seeds"] = seedRows,
        };

        var sortedTimings = new List<double>(timings);
        sortedTimings.Sort();
        double avg = Mean(sortedTimings);
        var summary = new JsonObject
        {
            ["generator_version"] = genver,
            ["scoring_version"] = ScoringVersion,
            ["seeds_per_tier"] = seedsPerTier,
            ["tiers"] = ToJsonArray(tiers),
            ["n"] = seedRows.Count,
            ["ok"] = ok,
            ["fail"] = failed,
            ["determinism_ok"] = determinismOk,
            ["ms"] = new JsonObject
            {
                ["avg"] = Round3(avg),
                ["p50"] = Round3(Percentile(sortedTimings, 0.50)),
                ["p95"] = Round3(Percentile(sortedTimings, 0.95)),
                ["max"] = Round3(sortedTimings.Count > 0 ? sortedTimings[^1] : 0),
            },
            ["per_tier"] = PerTierJson(perTier, seedsPerTier),
            ["score_hist"] = ScoreHistJson(perTier),
            ["score_hist5"] = ScoreHist5Json(allScores),
            ["score_stats"] = ScoreStatsJson(allScores),
            ["step_stats"] = StepStatsJson(stepValues),
            ["target"] = TargetJson(targetTotals, targetMisses, targetMissCount, targetDeltas),
            ["budget"] = BudgetJson(workUnits, budgetAborted),
            ["size_hist"] = SizeHistJson(sizeCounts),
            ["tag_coverage"] = TagCoverageJson(tagCounts, ok),
            ["fail_reasons"] = ReasonJson(reasonCounts),
            ["source_counts"] = SizeHistJson(sourceCounts),
            ["endpoint_stats"] = EndpointStatsJson(endpointAgg),
            ["determinism_checked"] = checkDeterminism,
            ["budget_tier"] = budgetName,
            ["search_attempts"] = searchAttempts,
            ["adaptive"] = adaptive,
        };

        string benchPath = outDir.TrimEnd('/') + $"/bench_gv{genver}.json";
        string summaryPath = outDir.TrimEnd('/') + $"/summary_gv{genver}.json";
        WriteJson(benchPath, bench);
        WriteJson(summaryPath, summary);

        GD.Print($"[GeneratorBench] n={seedRows.Count} ok={ok} fail={failed} determinism={determinismOk}");
        GD.Print($"[GeneratorBench] ms avg={avg:F2} p50={Percentile(sortedTimings, 0.50):F2} " +
                 $"p95={Percentile(sortedTimings, 0.95):F2} max={(sortedTimings.Count > 0 ? sortedTimings[^1] : 0):F2}");
        if (allScores.Count > 0)
        {
            var stats = ScoreStats(allScores);
            GD.Print($"[GeneratorBench] score mean={stats.Mean:F1} median={stats.Median:F1} " +
                     $"sd={stats.StdDev:F1} skew={stats.Skew:F2} min={stats.Min} max={stats.Max} " +
                     $"mode5={stats.ModeBucket5}");
            ScoreStatsResult stepStats = ScoreStats(stepValues);
            GD.Print($"[GeneratorBench] steps min={stepStats.Min} median={stepStats.Median:F0} max={stepStats.Max}");
            GD.Print($"[GeneratorBench] target miss={targetMissCount / (double)Math.Max(1, targetDeltas.Count):P1} " +
                     $"mean_delta={(targetDeltas.Count > 0 ? targetDeltas.Sum() / (double)targetDeltas.Count : 0):F1}");
        }
        if (reasonCounts.Count > 0)
        {
            foreach (KeyValuePair<string, int> kv in reasonCounts.OrderByDescending(kv => kv.Value).Take(5))
            {
                GD.Print($"[GeneratorBench] fail_reason {kv.Key} x{kv.Value}");
            }
        }
        GD.Print($"[GeneratorBench] out={benchPath}");

        GetTree().Quit(failed == 0 && determinismOk ? 0 : 1);
    }

    private sealed class EndpointDiag
    {
        public Cell Spawn;
        public Cell Goal;
        public int SpawnMargin;
        public int GoalMargin;
        public int SpawnDegree;
        public int GoalDegree;
        public int Separation;
        public int LoopRank;
        public int ChoiceCells;
        public List<double> DecoyOffsets = new();
    }

    private sealed class EndpointAgg
    {
        public int Count;
        public int SpawnMarginSum;
        public int GoalMarginSum;
        public int SpawnDegSum;
        public int GoalDegSum;
        public int SeparationSum;
        public int LoopRankSum;
        public int ChoiceCellsSum;
        public int DecoyCount;
        public int DecoyNearStart;

        public void Add(EndpointDiag d)
        {
            Count++;
            SpawnMarginSum += d.SpawnMargin;
            GoalMarginSum += d.GoalMargin;
            SpawnDegSum += d.SpawnDegree;
            GoalDegSum += d.GoalDegree;
            SeparationSum += d.Separation;
            LoopRankSum += d.LoopRank;
            ChoiceCellsSum += d.ChoiceCells;
            DecoyCount += d.DecoyOffsets.Count;
            foreach (double offset in d.DecoyOffsets)
            {
                if (offset < 1.0 / 3.0)
                {
                    DecoyNearStart++;
                }
            }
        }
    }

    /// <summary>
    /// 端点 / 诱饵诊断：到可走区外接框的边距、度数、静态度数环边、路线可选步，
    /// 以及每片诱饵花瓣在路线上的最近位置（0 = 起点侧，1 = 终点侧）。
    /// </summary>
    private static EndpointDiag AnalyzeEndpoints(LevelSpec spec, string route)
    {
        var diag = new EndpointDiag { Spawn = spec.Spawn, Goal = spec.Goal };
        var walkable = new HashSet<Cell>();
        foreach (Cell c in spec.Floor)
        {
            if (!spec.Obstacles.Contains(c))
            {
                walkable.Add(c);
            }
        }
        if (walkable.Count == 0)
        {
            return diag;
        }
        int minX = int.MaxValue;
        int minY = int.MaxValue;
        int maxX = int.MinValue;
        int maxY = int.MinValue;
        foreach (Cell c in walkable)
        {
            minX = Math.Min(minX, c.X);
            minY = Math.Min(minY, c.Y);
            maxX = Math.Max(maxX, c.X);
            maxY = Math.Max(maxY, c.Y);
        }

        int Margin(Cell c)
        {
            return Math.Min(Math.Min(c.X - minX, maxX - c.X), Math.Min(c.Y - minY, maxY - c.Y));
        }

        int Degree(Cell c)
        {
            int deg = 0;
            foreach (Cell n in GridModel.Neighbors(c))
            {
                if (walkable.Contains(n))
                {
                    deg++;
                }
            }
            return deg;
        }

        diag.SpawnMargin = Margin(spec.Spawn);
        diag.GoalMargin = Margin(spec.Goal);
        diag.SpawnDegree = Degree(spec.Spawn);
        diag.GoalDegree = Degree(spec.Goal);
        diag.Separation = Math.Abs(spec.Spawn.X - spec.Goal.X) + Math.Abs(spec.Spawn.Y - spec.Goal.Y);
        int degreeSum = 0;
        foreach (Cell c in walkable)
        {
            degreeSum += Degree(c);
        }
        diag.LoopRank = degreeSum / 2 - walkable.Count + 1;

        var routeCells = new List<Cell> { spec.Spawn };
        Cell head = spec.Spawn;
        foreach (char ch in route)
        {
            Vector2I step = DirFor(ch);
            head += new Cell(step.X, step.Y);
            routeCells.Add(head);
        }
        var grid = new GridModel(spec.Origin, spec.Width, spec.Height);
        foreach (Cell c in spec.Floor)
        {
            grid.Floor.Add(c);
        }
        foreach (Cell c in spec.Obstacles)
        {
            grid.Obstacles.Add(c);
        }
        Dictionary<Cell, int> distGoal = grid.BfsDistMap(spec.Goal, new HashSet<Cell>());
        var onRoute = new HashSet<Cell>(routeCells);
        for (int i = 1; i < routeCells.Count; i++)
        {
            Cell cell = routeCells[i];
            if (!distGoal.TryGetValue(cell, out int d))
            {
                continue;
            }
            int options = 0;
            foreach (Cell n in GridModel.Neighbors(cell))
            {
                if (walkable.Contains(n) && distGoal.TryGetValue(n, out int dn) && dn == d - 1)
                {
                    options++;
                }
            }
            if (options >= 2)
            {
                diag.ChoiceCells++;
            }
        }

        int steps = Math.Max(route.Length, 1);
        foreach (Cell food in spec.Foods)
        {
            if (onRoute.Contains(food))
            {
                continue;
            }
            int nearestIndex = 0;
            int nearest = int.MaxValue;
            for (int i = 0; i < routeCells.Count; i++)
            {
                int md = Math.Abs(routeCells[i].X - food.X) + Math.Abs(routeCells[i].Y - food.Y);
                if (md < nearest)
                {
                    nearest = md;
                    nearestIndex = i;
                }
                if (nearest == 0)
                {
                    break;
                }
            }
            diag.DecoyOffsets.Add(nearestIndex / (double)steps);
        }
        return diag;
    }

    private static JsonObject EndpointStatsJson(Dictionary<string, EndpointAgg> agg)
    {
        var obj = new JsonObject();
        foreach (string tier in agg.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            EndpointAgg a = agg[tier];
            int n = Math.Max(1, a.Count);
            obj[tier] = new JsonObject
            {
                ["n"] = a.Count,
                ["avg_spawn_margin"] = Round3(a.SpawnMarginSum / (double)n),
                ["avg_goal_margin"] = Round3(a.GoalMarginSum / (double)n),
                ["avg_spawn_degree"] = Round3(a.SpawnDegSum / (double)n),
                ["avg_goal_degree"] = Round3(a.GoalDegSum / (double)n),
                ["avg_separation"] = Round3(a.SeparationSum / (double)n),
                ["avg_loop_rank"] = Round3(a.LoopRankSum / (double)n),
                ["avg_choice_cells"] = Round3(a.ChoiceCellsSum / (double)n),
                ["decoy_count"] = a.DecoyCount,
                ["decoy_near_start_ratio"] = a.DecoyCount > 0
                    ? Round3(a.DecoyNearStart / (double)a.DecoyCount)
                    : 0,
            };
        }
        return obj;
    }

    private static bool Replay(string specJson, string route)
    {
        LevelSpec spec = LevelSpec.FromJson(specJson);
        var state = new RuleState();
        state.Setup(spec);
        state.Start();
        foreach (char ch in route)
        {
            Godot.Collections.Dictionary step = state.Step(DirFor(ch));
            if (!step["accepted"].AsBool()
                || (!step["won"].AsBool() && step["deadlock_msg"].AsString().Length > 0))
            {
                return false;
            }
        }
        return state.Head.Equals(spec.Goal);
    }

    private static Vector2I DirFor(char ch)
    {
        return ch switch
        {
            'A' => new Vector2I(-1, 0),
            'D' => new Vector2I(1, 0),
            'W' => new Vector2I(0, -1),
            'S' => new Vector2I(0, 1),
            _ => Vector2I.Zero,
        };
    }

    private static JsonObject MetricsToJson(Dictionary<string, double> metrics)
    {
        var obj = new JsonObject();
        foreach (KeyValuePair<string, double> kv in metrics.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            obj[kv.Key] = Round3(kv.Value);
        }
        return obj;
    }

    private static JsonObject PerTierJson(Dictionary<string, List<int>> perTier, int seedsPerTier)
    {
        var obj = new JsonObject();
        foreach (string tier in perTier.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            List<int> scores = perTier[tier];
            var values = scores.Select(s => (double)s).ToList();
            double mean = Mean(values);
            obj[tier] = new JsonObject
            {
                ["n"] = scores.Count,
                ["ok"] = scores.Count,
                ["fail"] = Math.Max(0, seedsPerTier - scores.Count),
                ["ok_ratio"] = seedsPerTier > 0 ? Round3(scores.Count / (double)seedsPerTier) : 0,
                ["min"] = scores.Count > 0 ? scores.Min() : 0,
                ["max"] = scores.Count > 0 ? scores.Max() : 0,
                ["mean"] = Round3(mean),
                ["stddev"] = Round3(StdDev(values, mean)),
            };
        }
        return obj;
    }

    private static JsonObject ReasonJson(Dictionary<string, int> counts)
    {
        var obj = new JsonObject();
        foreach (KeyValuePair<string, int> kv in counts.OrderByDescending(kv => kv.Value)
                     .ThenBy(kv => kv.Key, StringComparer.Ordinal))
        {
            obj[kv.Key] = kv.Value;
        }
        return obj;
    }

    private static JsonObject ScoreHistJson(Dictionary<string, List<int>> perTier)
    {
        var counts = new SortedDictionary<int, int>();
        foreach (List<int> scores in perTier.Values)
        {
            foreach (int score in scores)
            {
                int bucket = Math.Max(0, score) / 10 * 10;
                counts[bucket] = counts.TryGetValue(bucket, out int n) ? n + 1 : 1;
            }
        }
        var obj = new JsonObject();
        foreach (KeyValuePair<int, int> kv in counts)
        {
            obj[$"{kv.Key}-{kv.Key + 9}"] = kv.Value;
        }
        return obj;
    }

    private static JsonObject SizeHistJson(Dictionary<string, int> counts)
    {
        var obj = new JsonObject();
        foreach (KeyValuePair<string, int> kv in counts.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            obj[kv.Key] = kv.Value;
        }
        return obj;
    }

    private static JsonObject TagCoverageJson(Dictionary<string, int> counts, int total)
    {
        var obj = new JsonObject();
        foreach (KeyValuePair<string, int> kv in counts.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            obj[kv.Key] = new JsonObject
            {
                ["count"] = kv.Value,
                ["ratio"] = total > 0 ? Round3(kv.Value / (double)total) : 0,
            };
        }
        return obj;
    }

    private static JsonObject ScoreHist5Json(List<int> scores)
    {
        var counts = new SortedDictionary<int, int>();
        foreach (int score in scores)
        {
            int bucket = Math.Max(0, score) / 5 * 5;
            counts[bucket] = counts.TryGetValue(bucket, out int n) ? n + 1 : 1;
        }
        var obj = new JsonObject();
        foreach (KeyValuePair<int, int> kv in counts)
        {
            obj[$"{kv.Key}-{kv.Key + 4}"] = kv.Value;
        }
        return obj;
    }

    private readonly struct ScoreStatsResult
    {
        public readonly double Mean;
        public readonly double Median;
        public readonly double StdDev;
        public readonly double Skew;
        public readonly int Min;
        public readonly int Max;
        public readonly int ModeBucket5;
        public readonly double P05;
        public readonly double P95;

        public ScoreStatsResult(double mean, double median, double stdDev, double skew,
            int min, int max, int modeBucket5, double p05, double p95)
        {
            Mean = mean;
            Median = median;
            StdDev = stdDev;
            Skew = skew;
            Min = min;
            Max = max;
            ModeBucket5 = modeBucket5;
            P05 = p05;
            P95 = p95;
        }
    }

    private static ScoreStatsResult ScoreStats(List<int> scores)
    {
        if (scores.Count == 0)
        {
            return new ScoreStatsResult(0, 0, 0, 0, 0, 0, 0, 0, 0);
        }
        var values = new List<double>(scores.Count);
        foreach (int s in scores)
        {
            values.Add(s);
        }
        double mean = Mean(values);
        double stdDev = StdDev(values, mean);
        double skew = 0;
        if (stdDev > 0)
        {
            double m3 = 0;
            foreach (double v in values)
            {
                m3 += Math.Pow(v - mean, 3);
            }
            skew = (m3 / values.Count) / Math.Pow(stdDev, 3);
        }
        var sorted = new List<int>(scores);
        sorted.Sort();
        double median = sorted.Count % 2 == 0
            ? (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2.0
            : sorted[sorted.Count / 2];

        var counts = new SortedDictionary<int, int>();
        foreach (int score in scores)
        {
            int bucket = Math.Max(0, score) / 5 * 5;
            counts[bucket] = counts.TryGetValue(bucket, out int n) ? n + 1 : 1;
        }
        int modeBucket = 0;
        int modeCount = -1;
        foreach (KeyValuePair<int, int> kv in counts)
        {
            if (kv.Value > modeCount)
            {
                modeCount = kv.Value;
                modeBucket = kv.Key;
            }
        }
        return new ScoreStatsResult(mean, median, stdDev, skew,
            sorted[0], sorted[^1], modeBucket, sorted[(int)(sorted.Count * 0.05)], sorted[(int)(sorted.Count * 0.95)]);
    }

    private static JsonObject ScoreStatsJson(List<int> scores)
    {
        ScoreStatsResult stats = ScoreStats(scores);
        return new JsonObject
        {
            ["n"] = scores.Count,
            ["mean"] = Round3(stats.Mean),
            ["median"] = Round3(stats.Median),
            ["stddev"] = Round3(stats.StdDev),
            ["skew"] = Round3(stats.Skew),
            ["min"] = stats.Min,
            ["max"] = stats.Max,
            ["p05"] = stats.P05,
            ["p95"] = stats.P95,
            ["mode_bucket_5"] = $"{stats.ModeBucket5}-{stats.ModeBucket5 + 4}",
        };
    }

    private static JsonObject StepStatsJson(List<int> steps)
    {
        ScoreStatsResult stats = ScoreStats(steps);
        return new JsonObject
        {
            ["n"] = steps.Count,
            ["mean"] = Round3(stats.Mean),
            ["median"] = Round3(stats.Median),
            ["stddev"] = Round3(stats.StdDev),
            ["min"] = stats.Min,
            ["max"] = stats.Max,
            ["p05"] = stats.P05,
            ["p95"] = stats.P95,
        };
    }

    private static JsonObject TargetJson(Dictionary<string, int> totals, Dictionary<string, int> misses,
        int missCount, List<int> deltas)
    {
        var perTier = new JsonObject();
        foreach (string tier in totals.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            int total = totals[tier];
            int miss = misses.TryGetValue(tier, out int m) ? m : 0;
            perTier[tier] = new JsonObject
            {
                ["n"] = total,
                ["miss"] = miss,
                ["miss_ratio"] = total > 0 ? Round3(miss / (double)total) : 0,
            };
        }
        return new JsonObject
        {
            ["n"] = deltas.Count,
            ["miss"] = missCount,
            ["miss_ratio"] = deltas.Count > 0 ? Round3(missCount / (double)deltas.Count) : 0,
            ["mean_delta"] = deltas.Count > 0 ? Round3(deltas.Sum() / (double)deltas.Count) : 0,
            ["per_tier"] = perTier,
        };
    }

    private static JsonObject BudgetJson(List<double> units, int aborted)
    {
        var sorted = new List<double>(units);
        sorted.Sort();
        return new JsonObject
        {
            ["aborted"] = aborted,
            ["aborted_ratio"] = units.Count > 0 ? Round3(aborted / (double)units.Count) : 0,
            ["units_mean"] = units.Count > 0 ? Round3(units.Sum() / units.Count) : 0,
            ["units_p95"] = Round3(Percentile(sorted, 0.95)),
            ["units_max"] = Round3(sorted.Count > 0 ? sorted[^1] : 0),
        };
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var arr = new JsonArray();
        foreach (string value in values)
        {
            arr.Add(value);
        }
        return arr;
    }

    private static double Mean(List<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }
        double sum = 0;
        foreach (double v in values)
        {
            sum += v;
        }
        return sum / values.Count;
    }

    private static double StdDev(List<double> values, double mean)
    {
        if (values.Count == 0)
        {
            return 0;
        }
        double variance = 0;
        foreach (double v in values)
        {
            variance += (v - mean) * (v - mean);
        }
        return Math.Sqrt(variance / values.Count);
    }

    /// <summary>与 CoreTests 同口径：索引 = (int)(count * p)，clamp 到末尾。</summary>
    private static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0)
        {
            return 0;
        }
        int index = Math.Min(sorted.Count - 1, (int)(sorted.Count * p));
        return sorted[index];
    }

    private static double Round3(double value)
    {
        return Math.Round(value, 3, MidpointRounding.AwayFromZero);
    }

    private static string Sha256(string text)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void WriteJson(string resPath, JsonNode node)
    {
        string globalPath = ProjectSettings.GlobalizePath(resPath);
        string dir = Path.GetDirectoryName(globalPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(globalPath, node.ToJsonString(options) + "\n", new UTF8Encoding(false));
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string arg in args)
        {
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }
            int eq = arg.IndexOf('=');
            if (eq < 0)
            {
                map[arg[2..]] = "1";
            }
            else
            {
                map[arg[2..eq]] = arg[(eq + 1)..];
            }
        }
        return map;
    }

    private static int GetInt(Dictionary<string, string> args, string key, int fallback)
    {
        return args.TryGetValue(key, out string raw) && int.TryParse(raw, out int value) ? value : fallback;
    }

    private static string GetString(Dictionary<string, string> args, string key, string fallback)
    {
        return args.TryGetValue(key, out string raw) && !string.IsNullOrEmpty(raw) ? raw : fallback;
    }
}

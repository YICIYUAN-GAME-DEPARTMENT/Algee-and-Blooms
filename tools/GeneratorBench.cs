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
    private const int GeneratorVersion = 1;
    private const int ScoringVersion = 2;

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
        int genver = GetInt(args, "genver", GeneratorVersion);

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
                };

                var watch = Stopwatch.StartNew();
                GenerationResult result = RouteFirstGenerator.GenerateCore(cfg);
                watch.Stop();
                timings.Add(watch.Elapsed.TotalMilliseconds);

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
                GenerationResult second = RouteFirstGenerator.GenerateCore(cfg);
                bool same = second.Success
                    && second.Spec.ToJson() == specJson
                    && second.Solution.Route == result.Solution.Route;
                determinismOk &= same;

                bool replayOk = Replay(specJson, result.Solution.Route);
                row["status"] = replayOk ? "ok" : "replay_fail";
                row["width"] = result.Spec.Width;
                row["height"] = result.Spec.Height;
                row["schema_version"] = result.Spec.SchemaVersion;
                row["tier_label"] = result.Spec.Tier;
                row["param_signature"] = result.Spec.ParamSignature;
                row["target_score"] = result.Spec.TargetScore;
                row["attempts_used"] = result.Attempts;
                row["steps"] = result.Solution.Steps;
                row["floor_count"] = result.Spec.Floor.Count;
                row["food_count"] = result.Spec.Foods.Count;
                row["mech_count"] = result.Spec.MechCenters.Count;
                row["route"] = result.Solution.Route;
                row["metrics"] = MetricsToJson(result.Metrics);
                row["score"] = result.Score?.Difficulty ?? 0;
                row["quality"] = result.Score?.Quality ?? 0;
                row["spec_sha256"] = Sha256(specJson);
                row["deterministic"] = same;

                if (replayOk)
                {
                    ok++;
                    tierOk++;
                    perTier[tier].Add(result.Score?.Difficulty ?? 0);
                    string sizeKey = $"{result.Spec.Width}x{result.Spec.Height}";
                    sizeCounts[sizeKey] = sizeCounts.TryGetValue(sizeKey, out int sc) ? sc + 1 : 1;
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
            ["per_tier"] = PerTierJson(perTier),
            ["score_hist"] = ScoreHistJson(perTier),
            ["size_hist"] = SizeHistJson(sizeCounts),
        };

        string benchPath = outDir.TrimEnd('/') + $"/bench_gv{genver}.json";
        string summaryPath = outDir.TrimEnd('/') + $"/summary_gv{genver}.json";
        WriteJson(benchPath, bench);
        WriteJson(summaryPath, summary);

        GD.Print($"[GeneratorBench] n={seedRows.Count} ok={ok} fail={failed} determinism={determinismOk}");
        GD.Print($"[GeneratorBench] ms avg={avg:F2} p50={Percentile(sortedTimings, 0.50):F2} " +
                 $"p95={Percentile(sortedTimings, 0.95):F2} max={(sortedTimings.Count > 0 ? sortedTimings[^1] : 0):F2}");
        GD.Print($"[GeneratorBench] out={benchPath}");

        GetTree().Quit(failed == 0 && determinismOk ? 0 : 1);
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

    private static JsonObject PerTierJson(Dictionary<string, List<int>> perTier)
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
                ["min"] = scores.Count > 0 ? scores.Min() : 0,
                ["max"] = scores.Count > 0 ? scores.Max() : 0,
                ["mean"] = Round3(mean),
                ["stddev"] = Round3(StdDev(values, mean)),
            };
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

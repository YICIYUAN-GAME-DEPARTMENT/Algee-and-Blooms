using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using Growth.Algorithms.Scoring;

namespace Growth.Algorithms.Generation;

/// <summary>
/// GDScript 侧生成入口（S6/S7）：缓存 → 分帧任务 → 结果 JSON。
/// 用法（GDScript）：
///   var svc = load("res://Scripts/Algorithms/Generation/GenerationService.cs").new()
///   svc.call("SetupCache", "", 50)
///   var ready := bool(svc.call("Begin", seed, "normal", "loading"))
///   while not ready: ready = bool(svc.call("Advance", 4.0))
///   var data: Dictionary = JSON.parse_string(str(svc.call("ResultJson")))
/// 返回 JSON 字段：ok / spec_json / route / steps / score / target_steps /
/// target_delta / target_miss / budget_aborted / attempts / work_units / from_cache / reason。
/// </summary>
public partial class GenerationService : RefCounted
{
    public const int GeneratorVersion = 7;
    private const string DefaultCacheDir = "user://generated";

    private GenerationCache _cache;
    private GenerationJob _job;
    private long _seed;
    private string _tier = "normal";
    private string _budgetTier = "background";
    private ObstacleModel _obstacleModel = ObstacleModel.PoissonCluster;
    private GeneratorModel _generatorModel = GeneratorModel.RouteFirst;
    private bool _done;
    private bool _fromCache;
    private string _resultJson = "";
    private string _error = "";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };

    /// <summary>配置缓存目录（空 = user://generated）；返回缓存容量。</summary>
    public int SetupCache(string dir, int maxEntries)
    {
        string path = string.IsNullOrEmpty(dir) ? ProjectSettings.GlobalizePath(DefaultCacheDir) : dir;
        _cache = new GenerationCache(path, maxEntries);
        return _cache.MaxEntries;
    }

    public string CacheDir()
    {
        return _cache?.DirectoryPath ?? "";
    }

    /// <summary>切换障碍模型（blue_noise / noise_cluster / poisson / wall_growth）。</summary>
    public string SetObstacleModel(string model)
    {
        _obstacleModel = model switch
        {
            "blue_noise" or "blue" => ObstacleModel.BlueNoise,
            "noise_cluster" or "noise" => ObstacleModel.NoiseCluster,
            "wall_growth" or "wall" => ObstacleModel.WallGrowth,
            _ => ObstacleModel.PoissonCluster,
        };
        return ModelKey();
    }

    private string ModelKey()
    {
        return _obstacleModel switch
        {
            ObstacleModel.BlueNoise => "blue_noise",
            ObstacleModel.NoiseCluster => "noise_cluster",
            ObstacleModel.WallGrowth => "wall_growth",
            _ => "poisson",
        };
    }

    /// <summary>切换生成模型（route_first / physarum）。</summary>
    public string SetGeneratorModel(string model)
    {
        _generatorModel = model is "physarum" or "branch" ? GeneratorModel.Physarum : GeneratorModel.RouteFirst;
        return GenKey();
    }

    private string GenKey()
    {
        return _generatorModel == GeneratorModel.Physarum ? "physarum" : "route_first";
    }

    private string CacheTier()
    {
        return _tier + "-" + ModelKey() + "-" + GenKey();
    }

    /// <summary>开始一次生成；缓存命中时立即完成并返回 true。</summary>
    public bool Begin(long seed, string tier, string budgetTier)
    {
        _seed = seed;
        _tier = string.IsNullOrEmpty(tier) ? "normal" : tier;
        _budgetTier = string.IsNullOrEmpty(budgetTier) ? "background" : budgetTier;
        _job = null;
        _done = false;
        _fromCache = false;
        _resultJson = "";
        _error = "";

        if (_cache != null && _cache.TryGet(_seed, CacheTier(), GeneratorVersion, out string cached))
        {
            _resultJson = cached;
            _done = true;
            _fromCache = true;
            return true;
        }

        var cfg = new GeneratorConfig
        {
            Seed = _seed,
            Difficulty = _tier,
            ObstacleModel = _obstacleModel,
            GeneratorModel = _generatorModel,
        };
        GenerationBudget budget = GenerationBudget.For(GenerationBudget.ParseTier(_budgetTier));
        _job = new GenerationJob(cfg, budget);
        return false;
    }

    /// <summary>推进到本帧预算耗尽或完成；返回是否已完成。</summary>
    public bool Advance(double frameBudgetMs)
    {
        if (_done)
        {
            return true;
        }
        if (_job == null)
        {
            _done = true;
            _error = "未调用 Begin";
            return true;
        }
        _job.Advance(Math.Max(0.05, frameBudgetMs));
        if (!_job.Done)
        {
            return false;
        }
        GenerationResult result = _job.Result;
        _resultJson = Serialize(_seed, _tier, result, _job.BudgetAborted);
        if (result != null && result.Success && _cache != null)
        {
            _cache.Put(_seed, CacheTier(), GeneratorVersion, _resultJson);
        }
        if (result == null || !result.Success)
        {
            _error = result?.Reason ?? "生成失败";
        }
        _done = true;
        return true;
    }

    /// <summary>同步生成（首关兜底 / 工具）：内部一次性跑完预算。</summary>
    public string GenerateNow(long seed, string tier, string budgetTier)
    {
        Begin(seed, tier, budgetTier);
        Advance(double.MaxValue);
        return _resultJson;
    }

    public bool Done()
    {
        return _done;
    }

    public bool FromCache()
    {
        return _fromCache;
    }

    public string ResultJson()
    {
        return _resultJson;
    }

    public string StatusJson()
    {
        var status = new System.Collections.Generic.Dictionary<string, object>
        {
            ["done"] = _done,
            ["from_cache"] = _fromCache,
            ["attempts"] = _job?.Attempts ?? 0,
            ["work_units"] = _job?.WorkUnits ?? 0.0,
            ["target_steps"] = _job?.TargetSteps ?? 0,
            ["budget_aborted"] = _job?.BudgetAborted ?? false,
            ["obstacle_model"] = ModelKey(),
            ["generator_model"] = GenKey(),
            ["error"] = _error,
        };
        return JsonSerializer.Serialize(status, JsonOptions);
    }

    private string Serialize(long seed, string tier, GenerationResult result, bool aborted)
    {
        var cached = new CachedGeneration
        {
            Seed = seed,
            Tier = tier,
            ObstacleModel = ModelKey(),
            GeneratorModel = GenKey(),
            GeneratorVersion = GeneratorVersion,
            Success = result != null && result.Success,
            SpecJson = result?.Spec?.ToJson() ?? "",
            Route = result?.Solution?.Route ?? "",
            Steps = result?.Solution?.Steps ?? 0,
            Score = result?.Score?.Difficulty ?? 0,
            ScoreTier = result?.Score != null
                ? BranchScoreSchemes.TierForScore(result.Score.Difficulty) : "",
            Tags = result?.Spec?.Tags != null ? new System.Collections.Generic.List<string>(result.Spec.Tags) : new(),
            TargetSteps = result?.TargetSteps ?? 0,
            TargetDelta = result?.TargetDelta ?? 0,
            TargetMiss = result?.TargetMiss ?? false,
            BudgetAborted = result?.BudgetAborted ?? aborted,
            Attempts = result?.Attempts ?? 0,
            WorkUnits = result?.WorkUnits ?? 0.0,
            Reason = result?.Reason ?? "",
        };
        return JsonSerializer.Serialize(cached, JsonOptions);
    }
}

/// <summary>缓存 / GDScript 传输用的生成结果快照（snake_case）。</summary>
public sealed class CachedGeneration
{
    public long Seed { get; set; }
    public string Tier { get; set; } = "";
    public string ObstacleModel { get; set; } = "";
    public string GeneratorModel { get; set; } = "";
    public int GeneratorVersion { get; set; }
    public bool Success { get; set; }
    public string SpecJson { get; set; } = "";
    public string Route { get; set; } = "";
    public int Steps { get; set; }
    public int Score { get; set; }
    public string ScoreTier { get; set; } = "";
    public System.Collections.Generic.List<string> Tags { get; set; } = new();
    public int TargetSteps { get; set; }
    public int TargetDelta { get; set; }
    public bool TargetMiss { get; set; }
    public bool BudgetAborted { get; set; }
    public int Attempts { get; set; }
    public double WorkUnits { get; set; }
    public string Reason { get; set; } = "";
}

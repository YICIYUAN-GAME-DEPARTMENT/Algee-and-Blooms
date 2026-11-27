using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using Growth.Algorithms.Scoring;

namespace Growth.Algorithms.Generation;

/// <summary>
/// GDScript 侧生成入口（S6/S7）：缓存 → 后台生成线程 → 结果 JSON。
/// 生成在低于普通优先级的后台线程执行，主线程每帧只轮询进度，
/// 保证加载屏的进度条 / 小游戏不被生成器抢占。
/// 用法（GDScript）：
///   var svc = load("res://Scripts/Algorithms/Generation/GenerationService.cs").new()
///   svc.call("SetupCache", "", 50)
///   var ready := bool(svc.call("Begin", seed, "normal", "loading"))
///   while not ready:
///       await get_tree().process_frame
///       ready = bool(svc.call("Advance", 4.0))
///   var data: Dictionary = JSON.parse_string(str(svc.call("ResultJson")))
/// 返回 JSON 字段：ok / spec_json / route / steps / score / target_steps /
/// target_delta / target_miss / budget_aborted / attempts / work_units / from_cache / reason。
/// </summary>
public partial class GenerationService : RefCounted
{
    // v15：收敛每次尝试的 S/E 求解次数并提前退出（性能修正）；缓存需重新生成。
    public const int GeneratorVersion = 15;
    private const string DefaultCacheDir = "user://generated";

    private GenerationCache _cache;
    private GenerationJob _job;
    private System.Threading.Thread _worker;
    private volatile bool _workerFaulted;
    private long _seed;
    private string _tier = "normal";
    private string _budgetTier = "background";
    private int _levelIndex;
    private ObstacleModel _obstacleModel = ObstacleModel.PoissonCluster;
    private GeneratorModel _generatorModel = GeneratorModel.RouteFirst;
    private GeneratorModel _effectiveModel = GeneratorModel.RouteFirst;
    private string _source = "";
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

    /// <summary>切换生成模型（route_first / physarum / template / portfolio）。</summary>
    public string SetGeneratorModel(string model)
    {
        _generatorModel = model switch
        {
            "physarum" or "branch" => GeneratorModel.Physarum,
            "template" or "field" or "interaction" => GeneratorModel.TemplateField,
            "portfolio" or "auto" or "hybrid" => GeneratorModel.Portfolio,
            _ => GeneratorModel.RouteFirst,
        };
        _effectiveModel = _generatorModel == GeneratorModel.Portfolio
            ? GeneratorModel.RouteFirst
            : _generatorModel;
        return GenKey();
    }

    /// <summary>设置当前关卡序号（Portfolio 用 hash(seed, index) 做确定性调度）。</summary>
    public void SetLevelIndex(int index)
    {
        _levelIndex = index;
    }

    private string GenKey()
    {
        return _effectiveModel switch
        {
            GeneratorModel.Physarum => "physarum",
            GeneratorModel.TemplateField => "template",
            _ => "route_first",
        };
    }

    private string CacheTier()
    {
        string source = string.IsNullOrEmpty(_source) ? "" : "-" + _source;
        return _tier + "-" + ModelKey() + "-" + GenKey() + source;
    }

    /// <summary>开始一次生成；缓存命中时立即完成并返回 true。</summary>
    public bool Begin(long seed, string tier, string budgetTier)
    {
        // 同一服务只保留一个后台任务：先等旧线程在安全点退出，避免两套阶段/取消标记互相覆盖。
        StopWorker();
        _seed = seed;
        _tier = string.IsNullOrEmpty(tier) ? "normal" : tier;
        _budgetTier = string.IsNullOrEmpty(budgetTier) ? "background" : budgetTier;
        _job = null;
        _done = false;
        _fromCache = false;
        _resultJson = "";
        _error = "";

        // v4 Portfolio：先按 hash(seed, index) 定来源，再决定实际生成引擎（缓存键包含来源）。
        _effectiveModel = _generatorModel;
        _source = "";
        if (_generatorModel == GeneratorModel.Portfolio)
        {
            GenerationSource source = GenerationPortfolio.Choose(_seed, _levelIndex, _tier);
            _source = GenerationPortfolio.Key(source);
            _effectiveModel = source == GenerationSource.Structured
                ? GeneratorModel.TemplateField
                : GeneratorModel.RouteFirst;
        }

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
            GeneratorModel = _effectiveModel,
            GenerationSource = _source,
            LevelIndex = _levelIndex,
            // 模板模式多给候选：后台生成不占主线程，用更多候选换质量（加载屏有小游戏等待）。
            Candidates = _effectiveModel == GeneratorModel.TemplateField ? 6 : 4,
        };
        GenerationBudget budget = GenerationBudget.For(GenerationBudget.ParseTier(_budgetTier));
        _job = new GenerationJob(cfg, budget);
        _workerFaulted = false;
        _worker = new System.Threading.Thread(RunWorker)
        {
            IsBackground = true,
            Name = "AlgeeGeneration",
            // 低于普通优先级：主线程的小游戏 / 渲染优先拿到 CPU。
            Priority = System.Threading.ThreadPriority.BelowNormal,
        };
        _worker.Start();
        return false;
    }

    /// <summary>轮询后台生成是否完成；frameBudgetMs 仅为兼容旧调用，主线程不做生成计算。</summary>
    public bool Advance(double frameBudgetMs)
    {
        _ = frameBudgetMs;
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
        if (_worker != null && _worker.IsAlive)
        {
            return false;
        }
        return FinalizeJob();
    }

    /// <summary>离开加载屏时中止后台生成；下一次 Begin 会等待旧线程退出。</summary>
    public void Cancel()
    {
        if (_done)
        {
            return;
        }
        GenerationTrace.RequestCancel();
    }

    private void StopWorker()
    {
        if (_worker == null)
        {
            return;
        }
        if (_worker.IsAlive)
        {
            GenerationTrace.RequestCancel();
            _worker.Join();
        }
        _worker = null;
    }

    private void RunWorker()
    {
        try
        {
            _job?.RunToCompletion();
        }
        catch (Exception ex)
        {
            _workerFaulted = true;
            _error = "生成异常：" + ex.Message;
        }
    }

    /// <summary>后台线程结束（或同步跑完）后，在主线程序列化并写缓存。</summary>
    private bool FinalizeJob()
    {
        if (_worker != null)
        {
            _worker.Join();
            _worker = null;
        }
        if (_workerFaulted)
        {
            _done = true;
            return true;
        }
        GenerationResult result = _job.Result;
        _resultJson = Serialize(_seed, _tier, result, _job.BudgetAborted);
        if (result != null && result.Success && _cache != null)
        {
            _cache.Put(_seed, CacheTier(), GeneratorVersion, _resultJson);
        }
        if (result == null || !result.Success)
        {
            _error = string.IsNullOrEmpty(_error) ? (result?.Reason ?? "生成失败") : _error;
        }
        _done = true;
        return true;
    }

    /// <summary>同步生成（首关兜底 / 工具）：等后台任务跑完后直接取结果。</summary>
    public string GenerateNow(long seed, string tier, string budgetTier)
    {
        Begin(seed, tier, budgetTier);
        if (!_done)
        {
            if (_worker != null)
            {
                _worker.Join();
                _worker = null;
            }
            FinalizeJob();
        }
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
            ["candidates_used"] = _job?.CandidatesUsed ?? 0,
            ["adaptive_stop"] = _job?.AdaptiveStopped ?? false,
            ["work_units"] = _job?.WorkUnits ?? 0.0,
            ["target_steps"] = _job?.TargetSteps ?? 0,
            ["budget_aborted"] = _job?.BudgetAborted ?? false,
            ["obstacle_model"] = ModelKey(),
            ["generator_model"] = GenKey(),
            ["error"] = _error,
        };
        return JsonSerializer.Serialize(status, JsonOptions);
    }

    /// <summary>加载屏进度：百分比 / 当前阶段 / 尝试次数（分帧推进时每帧读取）。</summary>
    public string ProgressJson()
    {
        double percent = _done ? 1.0 : (_job?.ProgressFraction ?? 0.0);
        string phase;
        if (_done)
        {
            phase = _fromCache ? "缓存命中" : (_error.Length > 0 ? "生成失败" : "完成");
        }
        else
        {
            phase = _job?.Phase ?? "生成候选";
        }
        var progress = new System.Collections.Generic.Dictionary<string, object>
        {
            ["done"] = _done,
            ["from_cache"] = _fromCache,
            ["percent"] = Math.Clamp(percent, 0.0, 1.0),
            ["phase"] = phase,
            ["attempts"] = _job?.Attempts ?? 0,
            ["candidates_used"] = _job?.CandidatesUsed ?? 0,
            ["adaptive_stop"] = _job?.AdaptiveStopped ?? false,
            ["work_units"] = _job?.WorkUnits ?? 0.0,
            ["target_steps"] = _job?.TargetSteps ?? 0,
            ["budget_aborted"] = _job?.BudgetAborted ?? false,
            ["generator_model"] = GenKey(),
            ["error"] = _error,
        };
        return JsonSerializer.Serialize(progress, JsonOptions);
    }

    private string Serialize(long seed, string tier, GenerationResult result, bool aborted)
    {
        var features = new System.Collections.Generic.Dictionary<string, double>();
        if (result?.Metrics != null)
        {
            foreach (System.Collections.Generic.KeyValuePair<string, double> kv in result.Metrics)
            {
                if (kv.Key.StartsWith("ix_"))
                {
                    features[kv.Key.Substring(3)] = kv.Value;
                }
                else if (kv.Key is "fairness_auc" or "fairness_min")
                {
                    features[kv.Key] = kv.Value;
                }
                else if (kv.Key.StartsWith("fair_", System.StringComparison.Ordinal))
                {
                    features[kv.Key] = kv.Value;
                }
            }
        }
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
            GenerationMode = result?.GenerationMode ?? "route_first",
            TemplateId = result?.TemplateId ?? "route_first",
            GenerationSource = result?.Spec?.GenerationSource ?? "",
            Steps = result?.Solution?.Steps ?? 0,
            Score = result?.Score?.Difficulty ?? 0,
            ScoreTier = result?.Score != null
                ? BranchScoreSchemes.TierForScore(result.Score.Difficulty) : "",
            Tags = result?.Spec?.Tags != null ? new System.Collections.Generic.List<string>(result.Spec.Tags) : new(),
            Features = features,
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
    /// <summary>template / physarum / fallback_physarum / fallback / route_first。</summary>
    public string GenerationMode { get; set; } = "";
    /// <summary>模板 id（模板或降级时填写）。</summary>
    public string TemplateId { get; set; } = "";
    /// <summary>v4 调度来源：structured / noise / hybrid。</summary>
    public string GenerationSource { get; set; } = "";
    public int Steps { get; set; }
    public int Score { get; set; }
    public string ScoreTier { get; set; } = "";
    public System.Collections.Generic.List<string> Tags { get; set; } = new();
    /// <summary>评分 v3 交互特征（H / Deception / Cuts / Contention / L_tight 等）。</summary>
    public System.Collections.Generic.Dictionary<string, double> Features { get; set; } = new();
    public int TargetSteps { get; set; }
    public int TargetDelta { get; set; }
    public bool TargetMiss { get; set; }
    public bool BudgetAborted { get; set; }
    public int Attempts { get; set; }
    public double WorkUnits { get; set; }
    public string Reason { get; set; } = "";
}

using System;
using System.Collections.Generic;
using Godot;
using Growth.Algorithms.Model;

namespace Growth.Algorithms.Generation;

/// <summary>
/// 机关优先生成器（生成算法 v3 §3.3 P1–P6）：
///   1. 保留地形 / 障碍基底（P0）；
///   2. 按档位采样模板并实例化机关场（P1，允许机关压路线 / 封最短路）；
///   3. 固定机关场后枚举 S/E 对，用 RuleState 行走得到见证路线并按 R1 交互特征排序（P2/P3）；
///   4. 对 Top-K 做精确求解、路线重放、公平性硬约束与评分（P4/P6）；
///   5. 全部失败时降级到 RouteFirstGenerator，并标记 generation_mode = fallback（P7）。
/// 不复制规则；随机全部来自 DeterministicRng。
/// </summary>
public static class InteractionPlanner
{
    internal static RouteFirstGenerator.AttemptResult RunAttempt(
        GeneratorConfig cfg, int targetSteps, int cand, int attempt)
    {
        RouteFirstGenerator.Band band = RouteFirstGenerator.GetBand(cfg.Difficulty);
        var rng = new DeterministicRng(
            (ulong)cfg.Seed * 0x9E3779B97F4A7C15UL
            + (ulong)cand * 0x94D049BB133111EBUL
            + (ulong)attempt * 0xBF58476D1CE4E5B9UL
            + 3UL);

        (int width, int height) = cfg.Width > 0 && cfg.Height > 0
            ? (cfg.Width, cfg.Height)
            : SizeLadder.Choose(rng, cfg.Difficulty);
        double areaFactor = GenerationBudget.AreaFactor(width, height);

        TemplateDef def = string.IsNullOrEmpty(cfg.TemplateId)
            ? TemplateLibrary.Sample(rng, cfg.Difficulty)
            : TemplateLibrary.ById(cfg.TemplateId);
        if (def == null)
        {
            return Fail("未知模板", areaFactor);
        }

        RouteFirstGenerator.BaseLayout layout = RouteFirstGenerator.BuildBase(
            cfg, rng, targetSteps, width, height, band.MinSteps, band.MaxSteps, out string baseReason);
        if (layout == null)
        {
            return Fallback(cfg, targetSteps, cand, attempt, areaFactor, def, baseReason);
        }
        GenerationTrace.Report("地形与障碍");

        var obstacleSet = new HashSet<Cell>(layout.Obstacles);
        var walkable = new List<Cell>();
        foreach (Cell c in layout.Floor)
        {
            if (!obstacleSet.Contains(c))
            {
                walkable.Add(c);
            }
        }
        if (walkable.Count < 4)
        {
            return Fallback(cfg, targetSteps, cand, attempt, areaFactor, def, "可走格过少");
        }

        TemplateInstance instance = TemplateLibrary.Instantiate(def, rng, targetSteps, walkable,
            cfg.Difficulty);
        if (instance == null)
        {
            return Fallback(cfg, targetSteps, cand, attempt, areaFactor, def, "模板实例化失败");
        }
        GenerationTrace.Report("模板机关场");

        LevelSpec field = BuildFieldSpec(cfg, layout, walkable, instance, targetSteps);
        List<PairCandidate> pairs = PairSelector.Select(field, instance, targetSteps,
            band.MinSteps, band.MaxSteps, rng);
        if (pairs.Count == 0)
        {
            return Fallback(cfg, targetSteps, cand, attempt, areaFactor, def, "未找到 S/E 对");
        }
        GenerationTrace.Report("选择出生点与终点");

        foreach (PairCandidate pair in pairs)
        {
            if (GenerationTrace.Expired)
            {
                break;
            }
            GenerationResult result = VerifyAndScore(pair, band, targetSteps, def, instance);
            if (result != null)
            {
                return new RouteFirstGenerator.AttemptResult(true, result, "", areaFactor);
            }
        }
        return Fallback(cfg, targetSteps, cand, attempt, areaFactor, def, "模板候选全部失败");
    }

    private static LevelSpec BuildFieldSpec(GeneratorConfig cfg, RouteFirstGenerator.BaseLayout layout,
        IReadOnlyList<Cell> walkable, TemplateInstance instance, int targetSteps)
    {
        var spec = new LevelSpec
        {
            Seed = cfg.Seed,
            Name = cfg.Difficulty,
            Tier = cfg.Difficulty,
            Origin = Cell.Zero,
            Width = layout.Grid.Width,
            Height = layout.Grid.Height,
            Floor = layout.Floor,
            Obstacles = layout.Obstacles,
            Spawn = walkable[0],
            Goal = walkable[1],
            InitialMaxLen = instance.InitialMaxLen,
            FoodLenGain = instance.FoodLenGain,
            GrowthInterval = instance.GrowthInterval,
            MechanismCap = instance.MechanismCap,
            TargetSteps = targetSteps,
            ParamSignature = ParamSampler.BuildSignature(
                instance.InitialMaxLen, instance.FoodLenGain, instance.GrowthInterval,
                instance.MechanismCap, instance.InitialLevels),
        };
        spec.MechCenters.AddRange(instance.Centers);
        spec.MechInitialLevels.AddRange(instance.InitialLevels);
        return spec;
    }

    /// <summary>精确验证 + 公平性硬约束 + 评分；失败返回 null。</summary>
    private static GenerationResult VerifyAndScore(PairCandidate pair, RouteFirstGenerator.Band band,
        int targetSteps, TemplateDef def, TemplateInstance instance)
    {
        // PairSelector 已在时间规则下精确求解并产出见证路线；这里只重放校验，
        // 不再二次求解（同一路线重复求解不会提升质量，只会翻倍成本）。
        LevelSpec spec = pair.Spec;
        string route = pair.Route;
        int steps = pair.Steps;
        if (string.IsNullOrEmpty(route) || steps < band.MinSteps)
        {
            return null;
        }
        if (!RouteFirstGenerator.ReplayVerify(spec, route))
        {
            return null;
        }

        GenerationTrace.Report("公平性校验");
        int horizon = Math.Max(steps + 12, targetSteps + 12);
        var model = new TimeExpandedModel(spec, horizon);
        var danger = new PerceivedDangerField(model);
        var fairness = new FairnessEvaluator(spec, model, danger).Evaluate(route);
        if (!fairness.Passed())
        {
            return null;
        }

        GenerationTrace.Report("评分");
        var result = new GenerationResult
        {
            Success = true,
            Spec = spec,
            Solution = new LevelSolution
            {
                Status = "SOLVABLE",
                Steps = steps,
                Route = route,
            },
            GenerationMode = "template",
            TemplateId = def.Id,
        };
        result.Score = RouteFirstGenerator.ComputeScore(spec, route, result.Metrics,
            RouteFirstGenerator.BuildScoreOptions(Math.Max(spec.Width, spec.Height)), 0);
        result.Metrics["fairness_auc"] = fairness.Auc;
        result.Metrics["fairness_min"] = fairness.MinMuFair;
        result.Metrics["template_prior"] = instance.Prior;
        result.Metrics["template_mechs"] = spec.MechCenters.Count;
        result.Metrics["interaction_threat"] = pair.Threat;
        result.Metrics["interaction_temptation"] = pair.Temptation;
        result.Metrics["interaction_forks"] = pair.ForkSteps;
        result.Metrics["interaction_truncations"] = pair.Truncations;
        result.Metrics["decoy_foods"] = pair.DecoyFoods;
        result.Metrics["route_foods"] = pair.RouteFoods;
        return result;
    }

    /// <summary>
    /// P7 降级：模板场失败时产出经典路线兜底候选。
    /// 黏菌兜底由 <see cref="GenerationJob"/> 在整轮没有模板候选时统一尝试，
    /// 避免每次模板尝试失败都跑一遍昂贵的黏菌生成。
    /// </summary>
    private static RouteFirstGenerator.AttemptResult Fallback(GeneratorConfig cfg, int targetSteps,
        int cand, int attempt, double areaFactor, TemplateDef def, string reason)
    {
        GenerationTrace.Report("降级到经典路线");
        RouteFirstGenerator.AttemptResult fallback =
            RouteFirstGenerator.RunAttempt(cfg, targetSteps, cand, attempt);
        if (!fallback.Success)
        {
            return new RouteFirstGenerator.AttemptResult(false, null, reason, areaFactor);
        }
        fallback.Candidate.GenerationMode = "fallback";
        fallback.Candidate.TemplateId = def.Id;
        fallback.Candidate.Metrics["fallback_reason"] = 1.0;
        return new RouteFirstGenerator.AttemptResult(true, fallback.Candidate, "", areaFactor);
    }

    private static RouteFirstGenerator.AttemptResult Fail(string reason, double areaFactor)
    {
        return new RouteFirstGenerator.AttemptResult(false, null, reason, areaFactor);
    }
}

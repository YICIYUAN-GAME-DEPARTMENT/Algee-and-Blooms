using System;
using System.Diagnostics;
using Growth.Algorithms.Model;
using Growth.Algorithms.Scoring;

namespace Growth.Algorithms.Generation;

/// <summary>
/// 可暂停的生成任务（规划稿 §10.1 / §10.3）：
/// 每次 Advance 只推进若干次构建尝试，顺序与同步 GenerateCore 完全一致，
/// 因此分帧 / 预取不会改变 seed 决定性；墙钟熔断只作为安全阀并标记 budget_aborted。
/// </summary>
public sealed class GenerationJob
{
    private readonly GeneratorConfig _cfg;
    private readonly GenerationBudget _budget;
    private readonly int _candidates;
    private readonly int _attemptsPerCandidate;
    private readonly int _searchAttempts;
    private readonly int _targetSteps;
    private readonly MapElitesArchive _archive;
    private readonly System.Collections.Generic.Dictionary<string, int> _reasonCounts = new();

    private int _cand;
    private int _attempt;
    private int _successes;
    private int _totalAttempts;
    private double _units;
    private double _workMs;
    private string _lastReason = "";
    private volatile bool _done;
    private bool _aborted;
    private GenerationResult _result;
    private GenerationResult _fallbackBest;
    private int _fallbackDelta = int.MaxValue;
    private bool _fallbackMiss = true;
    private int _fallbackCount;
    private int _bestSelectedQuality = int.MinValue;
    private int _staleCandidates;
    private bool _stoppedEarly;

    public GenerationJob(GeneratorConfig cfg, GenerationBudget budget)
    {
        _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
        _budget = budget ?? GenerationBudget.Unlimited();
        // 直接调用方（工具 / 测试）也可能传 Portfolio：先按 seed + 关卡序号解析成具体引擎，
        // 与 GenerationService 的解析结果保持一致。
        if (_cfg.GeneratorModel == GeneratorModel.Portfolio)
        {
            GenerationSource source =
                GenerationPortfolio.Choose(_cfg.Seed, _cfg.LevelIndex, _cfg.Difficulty);
            _cfg.GenerationSource = GenerationPortfolio.Key(source);
            _cfg.GeneratorModel = source == GenerationSource.Structured
                ? GeneratorModel.TemplateField
                : GeneratorModel.RouteFirst;
        }
        GenerationTrace.Clear();
        _candidates = Math.Max(
            _budget.MaxCandidates > 0 ? Math.Min(cfg.Candidates, _budget.MaxCandidates) : cfg.Candidates,
            1);
        _attemptsPerCandidate = Math.Max(cfg.MaxAttempts, 1);
        int search = Math.Clamp(cfg.SearchAttempts, 1, _attemptsPerCandidate);
        _searchAttempts = _budget.MaxSearchAttempts > 0
            ? Math.Min(search, _budget.MaxSearchAttempts)
            : search;
        _targetSteps = SampleTargetSteps(cfg);
        _archive = new MapElitesArchive(_targetSteps);
    }

    public bool Done => _done;
    public bool BudgetAborted => _aborted;
    public int Attempts => _totalAttempts;
    public double WorkUnits => _units;
    public int TargetSteps => _targetSteps;
    public bool AdaptiveStopped => _stoppedEarly;
    public int CandidatesUsed => _cand;
    public GenerationResult Result => _result;
    public System.Collections.Generic.IReadOnlyDictionary<string, int> ReasonCounts => _reasonCounts;
    /// <summary>当前正在进行的阶段（由生成器上报，供加载屏显示）。</summary>
    public string Phase => GenerationTrace.PhaseOnly;
    /// <summary>已经产出的降级候选数（仅作诊断，不参与存档优先级）。</summary>
    public int Fallbacks => _fallbackCount;
    /// <summary>单次尝试的最长耗时（诊断用，验证加载屏卡顿上界）。</summary>
    public double MaxAttemptMilliseconds { get; private set; }

    /// <summary>估算进度 0–1：尝试次数与工作单元取较大者，完成后为 1。</summary>
    public double ProgressFraction
    {
        get
        {
            if (_done)
            {
                return 1.0;
            }
            int plannedAttempts = Math.Max(1,
                _candidates * Math.Min(_attemptsPerCandidate, Math.Max(_searchAttempts, 1)));
            double byAttempts = _totalAttempts / (double)plannedAttempts;
            double byUnits = _budget.MaxUnits > 0 ? _units / _budget.MaxUnits : 0.0;
            return Math.Min(0.95, Math.Max(byAttempts, byUnits));
        }
    }

    private static int SampleTargetSteps(GeneratorConfig cfg)
    {
        if (cfg.TargetSteps > 0)
        {
            return cfg.TargetSteps;
        }
        var band = RouteFirstGenerator.StepRange(cfg.Difficulty);
        var rng = new DeterministicRng((ulong)cfg.Seed * 0xD6E8FEB86659FD93UL + TierSalt(cfg.Difficulty));
        return band.Min + rng.NextInt(band.Max - band.Min + 1);
    }

    private static ulong TierSalt(string tier)
    {
        return tier switch
        {
            "easy" => 0x1UL,
            "normal" => 0x2UL,
            "hard" => 0x3UL,
            "expert" => 0x4UL,
            _ => 0x2UL,
        };
    }

    /// <summary>推进生成，直到本帧预算耗尽或任务完成；返回本次消耗的毫秒数。</summary>
    public double Advance(double frameBudgetMs)
    {
        if (_done)
        {
            return 0;
        }
        double start = NowMs();
        double used = 0;
        // 分帧模式每次只推进一次尝试，确保两次尝试之间一定有渲染机会；
        // 超大预算（同步 GenerateNow / 工具）仍一次跑完。
        int attemptLimit = frameBudgetMs >= 1000.0 ? int.MaxValue : 1;
        int ran = 0;
        while (!_done && ran < attemptLimit)
        {
            StepAttempt();
            ran++;
            used = NowMs() - start;
            if (used >= frameBudgetMs)
            {
                break;
            }
        }
        _workMs += used;
        CheckBreakers();
        return used;
    }

    /// <summary>同步跑完（工具 / 测试 / 首关兜底；后台线程也走这条）。</summary>
    public GenerationResult RunToCompletion()
    {
        while (!_done)
        {
            if (GenerationTrace.Cancelled)
            {
                _done = true;
                _aborted = true;
                return _result;
            }
            double t0 = NowMs();
            StepAttempt();
            _workMs += NowMs() - t0;
            CheckBreakers();
        }
        return _result;
    }

    private void StepAttempt()
    {
        if (_done)
        {
            return;
        }
        if (_cand >= _candidates)
        {
            Finish(false);
            return;
        }

        _totalAttempts++;
        GenerationTrace.Report("生成候选");
        GenerationTrace.BeginAttempt(_budget.AttemptMilliseconds);
        long attemptStarted = Stopwatch.GetTimestamp();
        RouteFirstGenerator.AttemptResult attempt = _cfg.GeneratorModel switch
        {
            GeneratorModel.Physarum => PhysarumGenerator.RunAttempt(_cfg, _targetSteps, _cand, _attempt),
            GeneratorModel.TemplateField => InteractionPlanner.RunAttempt(_cfg, _targetSteps, _cand, _attempt),
            _ => RouteFirstGenerator.RunAttempt(_cfg, _targetSteps, _cand, _attempt),
        };
        double attemptMs = (Stopwatch.GetTimestamp() - attemptStarted) * (1000.0 / Stopwatch.Frequency);
        if (attemptMs > MaxAttemptMilliseconds)
        {
            MaxAttemptMilliseconds = attemptMs;
        }
        _units += attempt.AreaFactor;

        if (!attempt.Success)
        {
            _lastReason = attempt.Reason;
            _reasonCounts[attempt.Reason] = _reasonCounts.TryGetValue(attempt.Reason, out int n) ? n + 1 : 1;
            NextAttempt();
            return;
        }

        // 降级结果（黏菌 / 经典路线）只作兜底：不进入存档、不计成功次数，继续找模板候选。
        string mode = attempt.Candidate.GenerationMode;
        if (mode == "fallback" || mode == "fallback_physarum")
        {
            _fallbackCount++;
            int fallbackDelta = Math.Abs(attempt.Candidate.Solution.Steps - _targetSteps);
            int fallbackQuality = attempt.Candidate.Score?.Quality ?? 0;
            int bestQuality = _fallbackBest?.Score?.Quality ?? -1;
            if (fallbackDelta < _fallbackDelta
                || (fallbackDelta == _fallbackDelta && fallbackQuality > bestQuality))
            {
                _fallbackBest = attempt.Candidate;
                _fallbackDelta = fallbackDelta;
                _fallbackMiss = fallbackDelta > MapElitesArchive.TargetTolerance;
            }
            NextAttempt();
            return;
        }

        _archive.Add(attempt.Candidate);
        _successes++;
        int delta = Math.Abs(attempt.Candidate.Solution.Steps - _targetSteps);
        if (delta <= MapElitesArchive.TargetTolerance || _successes >= _searchAttempts)
        {
            NextCandidate();
        }
        else
        {
            NextAttempt();
        }
    }

    private void NextAttempt()
    {
        _attempt++;
        if (_attempt >= _attemptsPerCandidate)
        {
            NextCandidate();
        }
    }

    private void NextCandidate()
    {
        _cand++;
        _attempt = 0;
        _successes = 0;
        if (_cand >= _candidates)
        {
            Finish(false);
            return;
        }
        if (TryAdaptiveStop())
        {
            _stoppedEarly = true;
            Finish(false);
        }
    }

    /// <summary>
    /// 自适应预算：候选数达到档位下限、且连续若干候选没有刷新“当前最优候选”的质量，
    /// 同时当前最优已进入目标步数带时，提前结束本轮生成。
    /// 质量持续提升时不会触发，因此 hard/expert 仍可跑满高预算。
    /// </summary>
    private bool TryAdaptiveStop()
    {
        if (!_cfg.AdaptiveBudget || _archive.Count == 0)
        {
            return false;
        }
        (int floor, int staleLimit) = _cfg.Difficulty switch
        {
            "hard" => (3, 1),
            "expert" => (4, 1),
            _ => (2, 1),
        };
        // 以“目标带优先”的存档选择结果衡量质量趋势，避免被偏离目标的高分候选误导。
        GenerationResult best = _archive.Select(out _, out bool miss);
        int quality = best?.Score?.Quality ?? 0;
        if (quality > _bestSelectedQuality)
        {
            _bestSelectedQuality = quality;
            _staleCandidates = 0;
        }
        else
        {
            _staleCandidates++;
        }
        return _cand >= floor && !miss && _staleCandidates >= staleLimit;
    }

    private void CheckBreakers()
    {
        if (_done)
        {
            return;
        }
        if (_budget.MaxUnits > 0 && _units >= _budget.MaxUnits)
        {
            Finish(true);
            return;
        }
        if (_budget.MaxMilliseconds > 0 && _workMs >= _budget.MaxMilliseconds)
        {
            Finish(true);
        }
    }

    private void Finish(bool aborted)
    {
        if (_done)
        {
            return;
        }
        _done = true;
        _aborted = aborted;

        GenerationResult best = null;
        int delta = int.MaxValue;
        bool miss = true;
        DesignFairnessReport fairness = null;
        bool usedFallback = false;

        if (_cfg.EnableFairnessGate)
        {
            // Top-K：按目标步数接近度取前 3 个候选，优先采用通过设计公平门的那个。
            System.Collections.Generic.List<MapElitesArchive.RankedCandidate> ranked =
                _archive.SelectRanked(3);
            DesignFairnessOptions gateOptions = DesignFairnessOptions.ForTier(_cfg.Difficulty);
            // 当前三个生成器都把规范路线替换为精确求解最短路，可跳过重复全图求解。
            gateOptions.AssumeCanonicalOptimal = true;
            DesignFairnessReport firstReport = null;
            foreach (MapElitesArchive.RankedCandidate candidate in ranked)
            {
                DesignFairnessReport report = DesignFairnessGate.Evaluate(
                    candidate.Result.Spec, candidate.Result.Solution.Route, gateOptions);
                firstReport ??= report;
                // 支配捷径 / 无解才是“必须换候选”的硬失败；
                // 诱饵无害、机关装饰等软失败直接采用下一个可用候选，避免重复昂贵证明。
                bool hardFailure = report.Violations.Contains("dominance_shortcut")
                    || report.Violations.Contains("unsolvable");
                if (report.Passed || !hardFailure)
                {
                    best = candidate.Result;
                    delta = candidate.Delta;
                    miss = candidate.Miss;
                    fairness = report;
                    break;
                }
            }
            if (best == null && ranked.Count > 0)
            {
                // 全部候选都有硬失败：仍返回第一个，避免整轮失败（执行侧会看到 fair_passed=0）。
                MapElitesArchive.RankedCandidate first = ranked[0];
                best = first.Result;
                delta = first.Delta;
                miss = first.Miss;
                fairness = firstReport;
            }
            fairness ??= firstReport;
        }
        else
        {
            best = _archive.Select(out delta, out miss);
        }

        // 整轮没有任何模板候选时，先尝试一次黏菌，再落到经典路线兜底。
        if (best == null && _cfg.GeneratorModel == GeneratorModel.TemplateField)
        {
            best = TryPhysarumFallback(out delta, out miss);
            usedFallback = best != null;
        }
        if (best == null && _fallbackBest != null)
        {
            GenerationTrace.Report("降级到经典路线");
            best = _fallbackBest;
            delta = _fallbackDelta;
            miss = _fallbackMiss;
            usedFallback = true;
        }
        if (best == null)
        {
            _result = new GenerationResult
            {
                Success = false,
                Reason = string.IsNullOrEmpty(_lastReason) ? "多次尝试未找到可验证关卡" : _lastReason,
                Attempts = _totalAttempts,
                TargetSteps = _targetSteps,
                TargetMiss = true,
                BudgetAborted = aborted,
                WorkUnits = _units,
            };
            return;
        }

        best.Attempts = _totalAttempts;
        best.TargetSteps = _targetSteps;
        best.TargetDelta = delta;
        best.TargetMiss = miss;
        best.ArchiveCells = _archive.Count;
        best.BudgetAborted = aborted;
        best.WorkUnits = _units;
        best.Score.TargetSteps = _targetSteps;
        best.Score.TargetMiss = miss;
        best.Spec.BudgetUnits = (int)Math.Round(_units, MidpointRounding.AwayFromZero);
        if (best.Metrics != null)
        {
            best.Metrics["candidates_used"] = _cand;
            if (_stoppedEarly)
            {
                best.Metrics["adaptive_stop"] = 1.0;
            }
        }
        if (usedFallback)
        {
            best.Metrics["fallback_selected"] = 1.0;
        }
        if (string.IsNullOrEmpty(best.Spec.GenerationSource))
        {
            best.Spec.GenerationSource = !string.IsNullOrEmpty(_cfg.GenerationSource)
                ? _cfg.GenerationSource
                : _cfg.GeneratorModel switch
                {
                    GeneratorModel.TemplateField => "structured",
                    _ => "noise",
                };
        }
        AttachInteractionFeatures(best);
        RouteFirstGenerator.ApplyBranchScore(best, _cfg.Seed);
        ApplyFairness(best, fairness);
        _result = best;
    }

    /// <summary>把设计公平门结果写入指标（含 Score.Metrics，供 HUD / 工具读取）。</summary>
    private static void ApplyFairness(GenerationResult result, DesignFairnessReport report)
    {
        if (result == null || report == null || result.Metrics == null)
        {
            return;
        }
        foreach (System.Collections.Generic.KeyValuePair<string, double> kv in report.ToMetrics())
        {
            result.Metrics[kv.Key] = kv.Value;
            if (result.Score != null)
            {
                result.Score.Metrics[kv.Key] = kv.Value;
            }
        }
        result.Metrics["fair_violations"] = report.Violations.Count;
        for (int i = 0; i < report.Violations.Count; i++)
        {
            result.Metrics["fair_violation_" + report.Violations[i]] = 1.0;
        }
    }

    /// <summary>
    /// <summary>
    /// 模板整轮失败后先尝试若干次黏菌枝干；成功则作为首选降级候选，
    /// 失败再交给已收集的经典路线兜底（<see cref="_fallbackBest"/>）。
    /// </summary>
    private GenerationResult TryPhysarumFallback(out int delta, out bool miss)
    {
        delta = int.MaxValue;
        miss = true;
        GenerationTrace.Report("降级到黏菌");
        // 最后一次兜底给若干次黏菌尝试；不受模板时间片限制（取消仍会中止）。
        GenerationTrace.BeginAttempt(0);
        int tries = Math.Clamp(_attemptsPerCandidate, 1, 3);
        for (int i = 0; i < tries; i++)
        {
            long started = Stopwatch.GetTimestamp();
            RouteFirstGenerator.AttemptResult physarum =
                PhysarumGenerator.RunAttempt(_cfg, _targetSteps, _candidates + i,
                    _attemptsPerCandidate + i);
            double attemptMs = (Stopwatch.GetTimestamp() - started) * (1000.0 / Stopwatch.Frequency);
            if (attemptMs > MaxAttemptMilliseconds)
            {
                MaxAttemptMilliseconds = attemptMs;
            }
            _units += physarum.AreaFactor;
            if (!physarum.Success || physarum.Candidate == null)
            {
                continue;
            }
            GenerationResult candidate = physarum.Candidate;
            candidate.GenerationMode = "fallback_physarum";
            // 保持"本次请求的模板"可追溯（与经典路线兜底一致）；实际模型由 generation_mode 区分。
            if (!string.IsNullOrEmpty(_cfg.TemplateId))
            {
                candidate.TemplateId = _cfg.TemplateId;
            }
            candidate.Metrics["fallback_reason"] = 1.0;
            _fallbackCount++;
            delta = Math.Abs(candidate.Solution.Steps - _targetSteps);
            miss = delta > MapElitesArchive.TargetTolerance;
            return candidate;
        }
        return null;
    }

    /// <summary>为最终关卡附加评分 v3 交互特征（H / Deception / Cuts / Contention / L_tight）。</summary>
    private static void AttachInteractionFeatures(GenerationResult result)
    {
        if (result?.Spec == null || result.Solution == null)
        {
            return;
        }
        InteractionScore interaction = InteractionScore.Evaluate(result.Spec, result.Solution.Route);
        foreach (System.Collections.Generic.KeyValuePair<string, double> kv in interaction.ToDictionary())
        {
            result.Metrics["ix_" + kv.Key] = kv.Value;
        }
    }

    private static double NowMs()
    {
        return Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency);
    }
}

/// <summary>
/// 生成阶段的轻量上报（生成器 → 加载屏）。
/// 生成在后台线程执行，阶段字符串用 volatile 静态字段共享（同一时刻只跑一个任务），
/// 截止时间与取消标记由生成线程自己维护，主线程只读阶段。
/// </summary>
internal static class GenerationTrace
{
    private static volatile string _phase;
    private static volatile bool _cancelled;
    [ThreadStatic] private static long _deadlineTicks;

    public static string PhaseOnly => string.IsNullOrEmpty(_phase) ? "生成候选" : _phase;

    /// <summary>任务是否已被取消（离开加载屏 / 被新任务取代）。</summary>
    public static bool Cancelled => _cancelled;

    /// <summary>当前尝试是否已超出单次时间片（由生成器在安全点协作检查）。</summary>
    public static bool Expired =>
        _cancelled || (_deadlineTicks > 0 && Stopwatch.GetTimestamp() >= _deadlineTicks);

    public static void Report(string phase)
    {
        _phase = phase;
    }

    /// <summary>开始一次尝试；milliseconds ≤ 0 表示不限制。</summary>
    public static void BeginAttempt(double milliseconds)
    {
        _deadlineTicks = milliseconds > 0
            ? Stopwatch.GetTimestamp() + (long)(milliseconds * Stopwatch.Frequency / 1000.0)
            : 0;
    }

    /// <summary>请求中止当前任务；生成器在下一个安全点退出。</summary>
    public static void RequestCancel()
    {
        _cancelled = true;
    }

    public static void Clear()
    {
        _phase = null;
        _deadlineTicks = 0;
        _cancelled = false;
    }
}

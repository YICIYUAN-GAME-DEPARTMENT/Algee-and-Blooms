using System;
using System.Diagnostics;

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
    private bool _done;
    private bool _aborted;
    private GenerationResult _result;

    public GenerationJob(GeneratorConfig cfg, GenerationBudget budget)
    {
        _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
        _budget = budget ?? GenerationBudget.Unlimited();
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
    public GenerationResult Result => _result;
    public System.Collections.Generic.IReadOnlyDictionary<string, int> ReasonCounts => _reasonCounts;

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
        while (!_done)
        {
            StepAttempt();
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

    /// <summary>同步跑完（工具 / 测试 / 首关兜底）。</summary>
    public GenerationResult RunToCompletion()
    {
        while (!_done)
        {
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
        RouteFirstGenerator.AttemptResult attempt = _cfg.GeneratorModel == GeneratorModel.Physarum
            ? PhysarumGenerator.RunAttempt(_cfg, _targetSteps, _cand, _attempt)
            : RouteFirstGenerator.RunAttempt(_cfg, _targetSteps, _cand, _attempt);
        _units += attempt.AreaFactor;

        if (!attempt.Success)
        {
            _lastReason = attempt.Reason;
            _reasonCounts[attempt.Reason] = _reasonCounts.TryGetValue(attempt.Reason, out int n) ? n + 1 : 1;
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
        }
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

        GenerationResult best = _archive.Select(out int delta, out bool miss);
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
        RouteFirstGenerator.ApplyBranchScore(best, _cfg.Seed);
        _result = best;
    }

    private static double NowMs()
    {
        return Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency);
    }
}

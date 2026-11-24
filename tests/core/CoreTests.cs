using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using Growth.Algorithms;
using Growth.Algorithms.Generation;
using Growth.Algorithms.Scoring;

namespace Growth.Tests;

/// <summary>
/// 引擎内 headless 断言场景：C# 规则 / 求解器回归。
/// 运行：godot --headless --path . tests/core/CoreTests.tscn
/// 退出码：0=全部通过，1=存在失败。
/// </summary>
public partial class CoreTests : Node
{
    private int _passed;
    private int _failed;

    private static readonly (int Id, string Path)[] LevelScenes =
    {
        (1, "res://Scenes/Levels/Level_01_move.tscn"),
        (2, "res://Scenes/Levels/Level_02_petals.tscn"),
        (3, "res://Scenes/Levels/Level_03_obstacles.tscn"),
        (4, "res://Scenes/Levels/Level_04_GrowingFlowers.tscn"),
        (5, "res://Scenes/Levels/Level_05_CounterIt.tscn"),
    };

    public override async void _Ready()
    {
        TestMechanicShapes();
        TestMovementAndTruncation();
        TestFood();
        TestGrowthAndShield();
        TestMetrics();
        TestGenerator();
        await TestGoldenLevels();
        TestScoreAnchors();

        GD.Print($"=== CoreTests: {_passed} passed, {_failed} failed ===");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }

    private void Check(bool condition, string name)
    {
        if (condition)
        {
            _passed++;
            GD.Print($"PASS {name}");
        }
        else
        {
            _failed++;
            GD.PushError($"FAIL {name}");
        }
    }

    private static Godot.Collections.Array RectFloor(int width, int height)
    {
        var cells = new Godot.Collections.Array();
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                cells.Add(new Vector2I(x, y));
            }
        }
        return cells;
    }

    private static RuleState MakeState(
        int width,
        int height,
        Vector2I spawn,
        Vector2I goal,
        Godot.Collections.Array foods,
        Godot.Collections.Array mechCenters,
        Godot.Collections.Array mechLevels,
        int maxLen,
        int interval,
        int gain,
        int cap)
    {
        var state = new RuleState();
        state.Setup(
            new Vector2I(0, 0),
            new Vector2I(width, height),
            RectFloor(width, height),
            new Godot.Collections.Array(),
            spawn,
            goal,
            foods,
            mechCenters,
            mechLevels,
            maxLen,
            interval,
            gain,
            cap);
        return state;
    }

    private void TestMechanicShapes()
    {
        Check(MechanicShapes.CoverLevel(new Cell(0, 0)) == 0, "形状: 中心=0");
        Check(MechanicShapes.CoverLevel(new Cell(1, 0)) == 1, "形状: 十字=1");
        Check(MechanicShapes.CoverLevel(new Cell(1, 1)) == 2, "形状: 3x3 角=2");
        Check(MechanicShapes.CoverLevel(new Cell(2, 0)) == 3, "形状: 菱形边=3");
        Check(MechanicShapes.CoverLevel(new Cell(2, 1)) == 4, "形状: 5x5 去角=4");
        Check(MechanicShapes.CoverLevel(new Cell(2, 2)) == 6, "形状: 5x5 角=6");
        Check(MechanicShapes.CoverLevel(new Cell(3, 0)) == 5, "形状: 菱形外=5");
    }

    private void TestMovementAndTruncation()
    {
        // 食物放在 (0,1) 防止"身长达上限且 E 超出 L 半径"触发死局，从而能测到身长上限与截断。
        var foods = new Godot.Collections.Array { new Vector2I(0, 1) };
        RuleState st = MakeState(5, 5, new Vector2I(0, 0), new Vector2I(4, 4),
            foods, new Godot.Collections.Array(), new Godot.Collections.Array(),
            3, 6, 2, 4);
        st.Start();

        Check(st.Step(new Vector2I(1, 0))["accepted"].AsBool(), "移动: 第 1 步");
        Check(st.Step(new Vector2I(1, 0))["accepted"].AsBool(), "移动: 第 2 步");
        Check(!st.Step(new Vector2I(1, 0))["accepted"].AsBool(), "移动: 身长达上限拒绝");
        Check(st.GetSteps() == 2, "移动: 无效移动不计步");
        Check(st.GetTrail().Count == 3, "移动: 轨迹长度 3");

        Godot.Collections.Dictionary trunc = st.Step(new Vector2I(-1, 0));
        Check(trunc["accepted"].AsBool() && trunc["truncated"].AsBool(), "截断: 踏入身体");
        Check(st.GetSteps() == 3, "截断: 计 1 步");
        Check(st.GetTrail().Count == 2, "截断: 轨迹回缩");
    }

    private void TestFood()
    {
        var foods = new Godot.Collections.Array { new Vector2I(1, 0) };
        RuleState st = MakeState(5, 5, new Vector2I(0, 0), new Vector2I(4, 4),
            foods, new Godot.Collections.Array(), new Godot.Collections.Array(),
            3, 6, 2, 4);
        st.Start();

        Godot.Collections.Dictionary res = st.Step(new Vector2I(1, 0));
        Check(res["accepted"].AsBool() && res["ate_food"].AsBool(), "食物: 吃到");
        Check(st.GetMaxLen() == 5, "食物: 身长 +2");
        Check(st.GetFoodEaten() == 1, "食物: 计数");
    }

    private void TestGrowthAndShield()
    {
        var centers = new Godot.Collections.Array { new Vector2I(1, 1) };
        var levels = new Godot.Collections.Array { 0 };
        RuleState st = MakeState(5, 5, new Vector2I(0, 0), new Vector2I(4, 4),
            new Godot.Collections.Array(), centers, levels,
            9, 3, 2, 1);
        st.Start();

        st.Step(new Vector2I(1, 0));
        st.Step(new Vector2I(1, 0));
        st.Step(new Vector2I(1, 0));
        Check(st.GetMaxMechLevel() == 1, "机关: 第 3 步生长到 lv1");
        Check(st.GetClaimedCells().Count == 4, "机关: 身体挡住中心格（shielded）");

        st.Step(new Vector2I(-1, 0));
        st.Step(new Vector2I(-1, 0));
        st.Step(new Vector2I(-1, 0));
        Check(st.GetClaimedCells().Count == 5, "机关: 满级后刷新补占中心格");
    }

    /// <summary>指标提取器手算单测（空场直线）+ RuleState 快照往返。</summary>
    private void TestMetrics()
    {
        var spec = new LevelSpec
        {
            Origin = Cell.Zero,
            Width = 10,
            Height = 10,
            Spawn = new Cell(0, 0),
            Goal = new Cell(5, 0),
            InitialMaxLen = 12,
            FoodLenGain = 2,
            GrowthInterval = 6,
            MechanismCap = 4,
        };
        for (int y = 0; y < 10; y++)
        {
            for (int x = 0; x < 10; x++)
            {
                spec.Floor.Add(new Cell(x, y));
            }
        }

        var options = new MetricOptions
        {
            EnableFalseAffordance = false,
            EnableFunnel = false,
            EnableNaiveStrategies = false,
        };
        MetricVector v = MetricExtractor.Extract(spec, "DDDDD", options);
        Check(v.RouteValid, "指标: 空场路线合法");
        Check(v.M01Steps == 5, "指标: M01 steps=5");
        Check(v.M02MinSlack == 6, "指标: M02 min_slack=6");
        Check(v.M03EndSlack == 6, "指标: M03 end_slack=6");
        Check(Math.Abs(v.M04FoodTightness) < 1e-9, "指标: M04 无食物=0");
        Check(Math.Abs(v.M05LenBuffer - 2.0) < 1e-9, "指标: M05 len_buffer=2");
        Check(v.M06ForcedTruncations == 0, "指标: M06=0");
        Check(Math.Abs(v.M07DetourRatio - 1.0) < 1e-9, "指标: M07 detour=1");
        Check(Math.Abs(v.M08CorridorRatio) < 1e-9, "指标: M08 corridor=0");
        Check(Math.Abs(v.M09Branching - 2.8) < 1e-9, "指标: M09 branching=2.8");
        Check(Math.Abs(v.M10Openness - 1.0) < 1e-9, "指标: M10 openness=1");
        Check(v.M12GateLayers == 0, "指标: M12 无机关=0");
        Check(!v.M13LateGrowth, "指标: M13 无生长");
        Check(v.M16FunnelWidth == 1, "指标: M16 关闭时=1");
        Check(v.M19Size == 10, "指标: M19 size=10");
        Check(v.M18ChoiceEntropy > 7.0, "指标: M18 熵>7");

        var state = new RuleState();
        state.Setup(spec);
        state.Start();
        state.Step(new Vector2I(1, 0));
        state.Step(new Vector2I(1, 0));
        RuleState.RuleSnapshot snapshot = state.CaptureState();
        int stepsBefore = state.GetSteps();
        Cell headBefore = state.Head;
        state.Step(new Vector2I(1, 0));
        state.Step(new Vector2I(-1, 0));
        state.RestoreState(snapshot);
        Check(state.GetSteps() == stepsBefore && state.Head.Equals(headBefore),
            "指标: 快照恢复 trail/steps/head");
    }

    private void TestGenerator()
    {
        var bands = new[] { "easy", "normal", "hard" };
        var scoresByBand = new Dictionary<string, List<int>>();
        var timings = new List<double>();
        int failed = 0;
        int replayFailed = 0;

        foreach (string band in bands)
        {
            var scores = new List<int>();
            for (long seed = 1; seed <= 60; seed++)
            {
                var cfg = new GeneratorConfig
                {
                    Seed = seed,
                    Width = 12,
                    Height = 12,
                    Difficulty = band,
                    Candidates = 1,
                    MaxAttempts = 32,
                };
                ulong t0 = Time.GetTicksUsec();
                GenerationResult result = RouteFirstGenerator.GenerateCore(cfg);
                timings.Add((Time.GetTicksUsec() - t0) / 1000.0);
                if (!result.Success)
                {
                    failed++;
                    if (failed <= 5)
                    {
                        GD.PushError($"生成失败: band={band} seed={seed} reason={result.Reason}");
                    }
                    continue;
                }

                LevelSpec spec = LevelSpec.FromJson(result.Spec.ToJson());
                if (!ReplaySpec(spec, result.Solution.Route))
                {
                    replayFailed++;
                    if (replayFailed <= 5)
                    {
                        GD.PushError($"生成重放失败: band={band} seed={seed} route={result.Solution.Route}");
                    }
                    continue;
                }
                scores.Add(result.Score.Difficulty);
            }
            scoresByBand[band] = scores;
        }

        Check(failed == 0, $"生成: 180 个 seed 全部成功（失败 {failed}）");
        Check(replayFailed == 0, $"生成: 全部路线可重放通关（失败 {replayFailed}）");

        var all = new List<int>();
        foreach (string band in bands)
        {
            List<int> scores = scoresByBand[band];
            all.AddRange(scores);
            double mean = 0;
            foreach (int s in scores)
            {
                mean += s;
            }
            mean /= Math.Max(1, scores.Count);
            GD.Print($"生成分布: {band} n={scores.Count} mean={mean:F1}");
        }

        if (all.Count > 1)
        {
            int min = int.MaxValue;
            int max = int.MinValue;
            double mean = 0;
            foreach (int s in all)
            {
                min = Math.Min(min, s);
                max = Math.Max(max, s);
                mean += s;
            }
            mean /= all.Count;
            double variance = 0;
            foreach (int s in all)
            {
                variance += (s - mean) * (s - mean);
            }
            variance /= all.Count;
            double stddev = Math.Sqrt(variance);
            GD.Print($"生成分布: 全体 n={all.Count} min={min} max={max} mean={mean:F1} stddev={stddev:F1}");

            Check(max - min >= 15, $"生成: 难度分范围 {max - min} >= 15");
            Check(stddev >= 4.0, $"生成: 难度分标准差 {stddev:F1} >= 4");
            double meanEasy = Mean(scoresByBand["easy"]);
            double meanHard = Mean(scoresByBand["hard"]);
            Check(meanHard >= meanEasy + 5.0, $"生成: hard 均值 {meanHard:F1} 高于 easy {meanEasy:F1} 至少 5");
        }

        if (timings.Count > 0)
        {
            timings.Sort();
            double avg = 0;
            foreach (double t in timings)
            {
                avg += t;
            }
            avg /= timings.Count;
            double p95 = timings[Math.Min(timings.Count - 1, (int)(timings.Count * 0.95))];
            GD.Print($"生成性能: avg={avg:F2} ms p95={p95:F2} ms max={timings[^1]:F2} ms");
            Check(p95 < 50.0, $"生成性能: p95 {p95:F2} ms < 50 ms");
        }
    }

    private static double Mean(List<int> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }
        double sum = 0;
        foreach (int v in values)
        {
            sum += v;
        }
        return sum / values.Count;
    }

    private static bool ReplaySpec(LevelSpec spec, string route)
    {
        var st = new RuleState();
        st.Setup(spec);
        st.Start();
        foreach (char ch in route)
        {
            Godot.Collections.Dictionary step = st.Step(DirForChar(ch));
            if (!step["accepted"].AsBool()
                || (!step["won"].AsBool() && step["deadlock_msg"].AsString().Length > 0))
            {
                return false;
            }
        }
        return st.Head.Equals(spec.Goal);
    }

    private async Task TestGoldenLevels()
    {
        string json = FileAccess.GetFileAsString("res://tests/fixtures/levels_expected.json");
        if (string.IsNullOrEmpty(json))
        {
            Check(false, "黄金路线: 无法读取 fixture");
            return;
        }

        var expected = new Dictionary<int, (string Status, int Steps, string Route)>();
        using (JsonDocument doc = JsonDocument.Parse(json))
        {
            foreach (JsonElement level in doc.RootElement.GetProperty("levels").EnumerateArray())
            {
                int id = level.GetProperty("level_id").GetInt32();
                expected[id] = (
                    level.GetProperty("status").GetString() ?? "",
                    level.GetProperty("steps").GetInt32(),
                    level.GetProperty("route").GetString() ?? "");
            }
        }

        foreach ((int id, string path) in LevelScenes)
        {
            PackedScene packed = GD.Load<PackedScene>(path);
            if (packed == null)
            {
                Check(false, $"黄金路线: 无法加载 {path}");
                continue;
            }

            Node level = packed.Instantiate();
            level.Set("story_start", "");
            level.Set("story_win", "");
            level.Set("story_fail", "");
            AddChild(level);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

            GodotObject state = level.Get("state").AsGodotObject();
            if (state == null)
            {
                Check(false, $"黄金路线: 关卡 {id} 未构建 LevelState");
                level.QueueFree();
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                continue;
            }

            Godot.Collections.Dictionary result = state.Call("solve_exact", 800_000).AsGodotDictionary();
            string status = result.TryGetValue("status", out Variant statusValue) ? statusValue.AsString() : "";
            int steps = result.TryGetValue("steps", out Variant stepsValue) ? stepsValue.AsInt32() : -1;
            string route = result.TryGetValue("route", out Variant routeValue) ? routeValue.AsString() : "";

            Check(expected.TryGetValue(id, out var exp), $"黄金路线: 关卡 {id} 存在快照");
            Check(status == exp.Status, $"黄金路线: 关卡 {id} status={status}");
            Check(steps == exp.Steps, $"黄金路线: 关卡 {id} steps={steps}");
            Check(route == exp.Route, $"黄金路线: 关卡 {id} route={route}");

            state.Call("start");
            bool replayOk = true;
            foreach (char ch in exp.Route)
            {
                Vector2I dir = DirForChar(ch);
                GodotObject step = state.Call("step", dir).AsGodotObject();
                if (step == null || !step.Get("accepted").AsBool())
                {
                    replayOk = false;
                    break;
                }
            }
            Vector2I head = state.Get("head").AsVector2I();
            Vector2I goal = state.Get("goal_cell").AsVector2I();
            Check(replayOk && head == goal, $"黄金路线: 关卡 {id} 路线重放通关");

            level.QueueFree();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    private static Vector2I DirForChar(char ch)
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

    /// <summary>锚点回归：快照与内置一致、5 关 D_raw 可复现、分数命中 5 / 10 / 25 / 45 / 100。</summary>
    private void TestScoreAnchors()
    {
        string expectedJson = FileAccess.GetFileAsString("res://tests/fixtures/levels_expected.json");
        var routes = new Dictionary<int, string>();
        using (JsonDocument doc = JsonDocument.Parse(expectedJson))
        {
            foreach (JsonElement level in doc.RootElement.GetProperty("levels").EnumerateArray())
            {
                routes[level.GetProperty("level_id").GetInt32()] =
                    level.GetProperty("route").GetString() ?? "";
            }
        }

        string anchorsJson = FileAccess.GetFileAsString("res://tests/fixtures/score_anchors.json");
        Check(!string.IsNullOrEmpty(anchorsJson), "锚点: 快照存在");
        if (string.IsNullOrEmpty(anchorsJson))
        {
            return;
        }
        ScoreAnchors snapshot = ScoreAnchors.FromJson(anchorsJson);
        Check(snapshot.ScoringVersion == 2, "锚点: scoring_version=2");
        Check(snapshot.Anchors.Count == ScoreAnchors.Default.Anchors.Count, "锚点: 快照与内置数量一致");
        for (int i = 0; i < Math.Min(snapshot.Anchors.Count, ScoreAnchors.Default.Anchors.Count); i++)
        {
            ScoreAnchor a = snapshot.Anchors[i];
            ScoreAnchor b = ScoreAnchors.Default.Anchors[i];
            Check(a.Id == b.Id && a.Score == b.Score && Math.Abs(a.DRaw - b.DRaw) < 0.001,
                $"锚点: {a.Id} 快照与内置一致");
        }

        var targets = new Dictionary<int, int> { [1] = 5, [2] = 10, [3] = 25, [4] = 45, [5] = 100 };
        var tolerances = new Dictionary<int, int> { [1] = 2, [2] = 2, [3] = 3, [4] = 3, [5] = 2 };
        var options = new MetricOptions
        {
            FunnelNodeBudget = 200_000,
            SolveNodeBudget = 800_000,
        };
        ScoreSpline spline = ScoreSpline.Default;

        for (int id = 1; id <= 5; id++)
        {
            string specJson = FileAccess.GetFileAsString($"res://tests/fixtures/anchors/level_{id:00}.json");
            LevelSpec spec = LevelSpec.FromJson(specJson);
            Check(spec.Floor.Count > 0, $"锚点: L{id} spec dump 可读回");
            Check(!spec.Spawn.Equals(spec.Goal), $"锚点: L{id} S/E 不同");

            MetricVector v = MetricExtractor.Extract(spec, routes[id], options);
            Check(v.RouteValid, $"锚点: L{id} 规范路线重放合法");

            ScoreAnchor anchor = null;
            foreach (ScoreAnchor candidate in snapshot.Anchors)
            {
                if (candidate.Id == $"L{id}")
                {
                    anchor = candidate;
                    break;
                }
            }
            Check(anchor != null, $"锚点: L{id} 快照条目存在");
            if (anchor != null)
            {
                Check(Math.Abs(v.DRaw - anchor.DRaw) < 0.01,
                    $"锚点: L{id} D_raw 与快照一致 ({v.DRaw:F3})");
            }

            int score = spline.InterpolateRounded(v.DRaw);
            Check(Math.Abs(score - targets[id]) <= tolerances[id],
                $"锚点: L{id} 分数 {score} 命中 {targets[id]}±{tolerances[id]}");
            GD.Print($"[Anchor] L{id} D={v.DRaw:F3} score={score} " +
                $"M14={v.M14FalseAffordance} M16={v.M16FunnelWidth} CEst={(v.CEstimated ? 1 : 0)}");
        }
    }
}

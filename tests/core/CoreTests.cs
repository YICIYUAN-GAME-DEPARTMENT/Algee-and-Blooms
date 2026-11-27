using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using Growth.Algorithms;
using Growth.Algorithms.Generation;
using Growth.Algorithms.Model;
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
        TestNegativeMechLevels();
        TestParams();
        TestLevelSpecV2();
        TestMetrics();
        TestCoverageCone();
        TestTimeExpandedModel();
        TestTimeExpandedModelVsRuleState();
        TestDangerField();
        TestFairness();
        TestDesignFairnessGate();
        TestGenerationPortfolio();
        TestDeadEndInjection();
        TestAdaptiveBudget();
        TestTemplateLibrary();
        TestInteractionPlanner();
        TestTemplateService();
        TestGenerationProgress();
        TestInteractionMetrics();
        TestScoreCalibration();
        TestPairLoopStructure();
        TestFallbackRate();
        TestGeneratedStructure();
        TestTagger();
        TestMapElites();
        TestGenerationJob();
        TestPhysarumGenerator();
        TestGenerationCache();
        TestGenerator();
        await TestGoldenLevels();
        TestScoreAnchors();
        TestLargeSize();

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

    /// <summary>负初始阶段：不占格、每周期 +1、到 0 才开始覆盖；R1 时刻表同步。</summary>
    private void TestNegativeMechLevels()
    {
        Check(MechanicShapes.MinLevel == -10, "负阶段: MinLevel=-10");
        LevelSpec spec = MakeSpec(5, 5, new Cell(0, 0), new Cell(4, 4), 20, 2, 2, 1,
            mechCenters: new[] { new Cell(2, 2) }, mechLevels: new[] { -2 });
        var state = new RuleState();
        state.Setup(spec);
        state.Start();

        Check(state.GetMechLevels()[0].AsInt32() == -2, "负阶段: 开局阶段 -2");
        Check(state.GetMaxMechLevel() == -2, "负阶段: 最大阶段为负");
        Check(state.GetClaimedCells().Count == 0, "负阶段: 开局不占格");
        Check(state.CoverSteps.TryGetValue(new Cell(2, 2), out int centerStage) && centerStage == 2,
            "负阶段: 核心覆盖阶段后移 2 次");

        state.Step(new Vector2I(1, 0));
        state.Step(new Vector2I(1, 0));
        Check(state.GetMechLevels()[0].AsInt32() == -1, "负阶段: 一轮生长后 -1");
        Check(state.GetClaimedCells().Count == 0, "负阶段: -1 仍不占格");

        state.Step(new Vector2I(1, 0));
        state.Step(new Vector2I(1, 0));
        Check(state.GetMechLevels()[0].AsInt32() == 0, "负阶段: 两轮生长后到 0");
        Check(state.GetClaimedCells().Count == 1, "负阶段: 0 级只占核心");

        state.Step(new Vector2I(0, 1));
        state.Step(new Vector2I(0, 1));
        Check(state.GetMechLevels()[0].AsInt32() == 1, "负阶段: 三轮生长后 1");
        Check(state.GetClaimedCells().Count == 5, "负阶段: 1 级占十字 5 格");

        var model = new TimeExpandedModel(spec, 20);
        model.TryIndexOf(new Cell(2, 2), out int centerIndex);
        Check(model.CoverStageOf(new Cell(2, 2)) == 2, "负阶段: R1 覆盖阶段与 RuleState 一致");
        Check(!model.IsMechAt(1, centerIndex), "负阶段: R1 一轮生长后未覆盖核心");
        Check(model.IsMechAt(2, centerIndex), "负阶段: R1 两轮生长后覆盖核心");
    }

    /// <summary>参数采样可行域（§8.2）与签名格式。</summary>
    private void TestParams()
    {
        Check(ParamSampler.Feasible(3, 4, 4, 1, 8, 0), "参数: 短图可行组合");
        Check(!ParamSampler.Feasible(7, 2, 8, 4, 8, 0), "参数: 大 L0 + 短图拒绝");
        Check(!ParamSampler.Feasible(3, 2, 8, 4, 12, 0), "参数: 周期装不下 cap 拒绝");
        Check(!ParamSampler.Feasible(3, 4, 4, 1, 8, 6), "参数: 地板不足拒绝");
        Check(ParamSampler.Feasible(6, 2, 4, 2, 12, 0), "参数: 中段可行组合");
        Check(ParamSampler.Feasible(7, 4, 4, 4, 38, 0), "参数: 20×20 长路线可行");

        var rng = new DeterministicRng(42);
        ParamSet param = ParamSampler.Sample(rng, 16, 2);
        Check(param != null, "参数: 采样成功");
        if (param != null)
        {
            Check(param.InitialMaxLen >= 3 && param.InitialMaxLen <= 7, "参数: L0 在 3–7");
            Check(param.FoodLenGain >= 2 && param.FoodLenGain <= 4, "参数: Gain 在 2–4");
            Check(param.GrowthInterval >= 4 && param.GrowthInterval <= 8, "参数: I 在 4–8");
            Check(param.MechanismCap >= 1 && param.MechanismCap <= 4, "参数: cap 在 1–4");
            Check(param.InitialLevels.Count == 2, "参数: 机关初始阶段数量");
            Check(param.Signature.Split('|').Length == 5, "参数: 签名格式");
        }
    }

    /// <summary>LevelSpec schema v2 往返与 v1 兼容。</summary>
    private void TestLevelSpecV2()
    {
        var spec = new LevelSpec
        {
            Seed = 7,
            Tier = "hard",
            Width = 14,
            Height = 14,
            Origin = new Cell(-2, -3),
            Spawn = new Cell(0, 0),
            Goal = new Cell(3, 3),
            InitialMaxLen = 5,
            FoodLenGain = 3,
            GrowthInterval = 6,
            MechanismCap = 2,
            TargetSteps = 16,
            BudgetUnits = 12,
            ParamSignature = "5|3|6|2|0,1",
            Tags = { "tight_budget", "gate_sequence" },
            TagEvidence = { ["gate_sequence"] = "g=2" },
        };
        spec.Floor.Add(new Cell(0, 0));
        spec.Floor.Add(new Cell(3, 3));
        spec.Foods.Add(new Cell(1, 1));
        spec.MechCenters.Add(new Cell(2, 2));
        spec.MechInitialLevels.Add(1);

        LevelSpec back = LevelSpec.FromJson(spec.ToJson());
        Check(back.SchemaVersion == 2, "Schema: v2 往返版本");
        Check(back.Tier == "hard", "Schema: tier 往返");
        Check(back.ParamSignature == "5|3|6|2|0,1", "Schema: 参数签名往返");
        Check(back.TargetSteps == 16 && back.BudgetUnits == 12, "Schema: 目标步数 / 预算往返");
        Check(back.Tags.Count == 2 && back.TagEvidence.Count == 1, "Schema: 标签与证据往返");

        string v1Json = FileAccess.GetFileAsString("res://tests/fixtures/anchors/level_01.json");
        LevelSpec v1 = LevelSpec.FromJson(v1Json);
        Check(v1.SchemaVersion == 1, "Schema: v1 快照读回");
        Check(v1.Tags.Count == 0 && v1.TagEvidence.Count == 0, "Schema: v1 标签默认空");
        Check(v1.ParamSignature == "", "Schema: v1 签名默认空");
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
        Check(v.M16FunnelWidth == 0 && !v.FunnelMeasured, "指标: M16 关闭时不参与评分");
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

    /// <summary>M1：覆盖锥（单中心 / cap / 初始阶段 / 并集）。</summary>
    private void TestCoverageCone()
    {
        var narrow = new CoverageCone(new Cell(5, 5), 0, 1);
        Check(narrow.StageAt(new Cell(5, 5)) == 0, "覆盖锥: 中心 stage=0");
        Check(narrow.StageAt(new Cell(6, 5)) == 1, "覆盖锥: 十字 stage=1");
        Check(narrow.StageAt(new Cell(6, 6)) == CoverageCone.Never, "覆盖锥: cap=1 不覆盖 3x3 角");
        Check(narrow.TimeAt(new Cell(6, 5), 3) == 3, "覆盖锥: 覆盖时刻 = stage×I");
        Check(narrow.TimeAt(new Cell(6, 6), 3) == CoverageCone.Never, "覆盖锥: 不覆盖格时刻为 Never");

        var wide = new CoverageCone(new Cell(0, 0), 1, 4);
        Check(wide.StageAt(new Cell(0, 0)) == 0, "覆盖锥: 初始阶段抵消中心");
        Check(wide.StageAt(new Cell(1, 0)) == 0, "覆盖锥: stage 不为负");
        Check(wide.StageAt(new Cell(1, 1)) == 1, "覆盖锥: 3x3 角 stage=1");
        Check(wide.StageAt(new Cell(2, 1)) == 3, "覆盖锥: 5x5 去角 stage=3");
        Check(wide.StageAt(new Cell(2, 2)) == CoverageCone.Never, "覆盖锥: 超出 cap 不覆盖");

        var union = CoverageCone.Union(new[]
        {
            new CoverageCone(new Cell(0, 0), 0, 4),
            new CoverageCone(new Cell(2, 0), 0, 4),
        });
        Check(union.TryGetValue(new Cell(1, 0), out int stage) && stage == 1,
            "覆盖锥: 并集取最小 stage");
        Check(!union.ContainsKey(new Cell(9, 9)), "覆盖锥: 并集不含远处格");
    }

    /// <summary>M1：R1 时间展开（Latest / slack / 可行核 / 单调性 / 食物层）。</summary>
    private void TestTimeExpandedModel()
    {
        var spec = MakeSpec(5, 5, new Cell(0, 0), new Cell(4, 0), 5, 6, 2, 4,
            foods: new[] { new Cell(2, 2), new Cell(4, 4) });
        var model = new TimeExpandedModel(spec, 20);

        Check(model.GrowthCount(0) == 0, "R1: g(0)=0");
        Check(model.GrowthCount(model.GrowthInterval) == 0, "R1: 无机关时 g_max=0");
        Check(model.GrowthCount(5) == 0, "R1: 未到周期不生长");
        Check(model.LengthAt(0) == 5 && model.LengthAt(2) == 9, "R1: L(k)=L0+ΔL·k");

        Check(model.IsReachable(0, 0, new Cell(0, 0)), "R1: 开局可达出生点");
        Check(!model.IsReachable(0, 0, new Cell(4, 4)), "R1: 开局不可达远处");
        Check(!model.IsReachable(0, 8, new Cell(4, 4)), "R1: 吃花层不进 k=0");
        Check(model.IsReachable(1, 8, new Cell(4, 4)), "R1: 进入食物格使 k=1");

        Check(model.LatestTime(0, new Cell(0, 0)) == 16, "R1: 空场 Latest(S)=T−dist");
        Check(model.LatestTime(0, new Cell(4, 4)) == 16, "R1: 后续吃花可扩大身长（Latest 取上界）");
        Check(model.LatestTime(0, new Cell(4, 4)) == model.LatestTime(2, new Cell(4, 4)),
            "R1: Latest 随 k 收敛到同一上界");
        Check(model.Slack(0, 0, new Cell(0, 0)) == 16, "R1: slack = Latest − t");

        var shortSpec = MakeSpec(3, 3, new Cell(0, 0), new Cell(2, 2), 3, 6, 2, 4);
        var shortModel = new TimeExpandedModel(shortSpec, 20);
        Check(shortModel.LatestTime(0, new Cell(0, 0)) == -1, "R1: 身长不足时无法到达终点");
        Check(!shortModel.IsViable(0, 0, new Cell(0, 0)), "R1: 身长不足时开局不可行");

        int monotonicBreaks = 0;
        for (int y = 0; y < 5; y++)
        {
            for (int x = 0; x < 5; x++)
            {
                var cell = new Cell(x, y);
                for (int k = 0; k < model.FoodCount; k++)
                {
                    if (model.LatestTime(k + 1, cell) < model.LatestTime(k, cell))
                    {
                        monotonicBreaks++;
                    }
                }
            }
        }
        Check(monotonicBreaks == 0, $"R1: Latest 随 k 单调（逆向 {monotonicBreaks}）");

        // 单格走廊：侧边机关在第 3 步覆盖唯一通路 -> 只能开局后短窗口通过。
        var gateSpec = MakeSpec(5, 3, new Cell(0, 0), new Cell(4, 0), 12, 3, 2, 1,
            mechCenters: new[] { new Cell(2, 1) }, mechLevels: new[] { 0 },
            obstacles: new[]
            {
                new Cell(0, 1), new Cell(1, 1), new Cell(2, 1), new Cell(3, 1), new Cell(4, 1),
                new Cell(0, 2), new Cell(1, 2), new Cell(2, 2), new Cell(3, 2), new Cell(4, 2),
            });
        var gate = new TimeExpandedModel(gateSpec, 20);
        Check(gate.GrowthCount(3) == 1 && gate.CoverStageOf(new Cell(2, 0)) == 1,
            "R1: 走廊机关第 3 步生长");
        Check(gate.LatestTime(0, new Cell(0, 0)) == 1, "R1: 闸门只允许开局后短窗口通过");
        Check(gate.LatestTime(0, new Cell(1, 0)) == 2, "R1: 闸门前一格 Latest=2");
        Check(!gate.IsViable(0, 2, new Cell(0, 0)), "R1: 晚一步出发不可行");
        Check(gate.IsViable(0, 1, new Cell(1, 0)), "R1: 窗口内到达闸门前可行");
    }

    /// <summary>M1：R1 与 RuleState 的 coverStep / 覆盖时刻逐格一致，且真实路线前缀落在可行核内。</summary>
    private void TestTimeExpandedModelVsRuleState()
    {
        Dictionary<int, string> routes = LoadGoldenRoutes();
        foreach (int id in new[] { 4, 5 })
        {
            string specJson = FileAccess.GetFileAsString($"res://tests/fixtures/anchors/level_{id:00}.json");
            LevelSpec spec = LevelSpec.FromJson(specJson);
            var state = new RuleState();
            state.Setup(spec);
            if (!routes.TryGetValue(id, out string route))
            {
                Check(false, $"R1: L{id} 缺少黄金路线");
                continue;
            }
            int horizon = Math.Max(TimeExpandedModel.DefaultHorizon(spec), route.Length + 8);
            var model = new TimeExpandedModel(spec, horizon);

            Check(model.GMax == state.GMax, $"R1: L{id} g_max 与 RuleState 一致");
            int stageMismatch = 0;
            int timeMismatch = 0;
            int interval = Math.Max(spec.GrowthInterval, 1);
            for (int y = spec.Origin.Y; y < spec.Origin.Y + spec.Height; y++)
            {
                for (int x = spec.Origin.X; x < spec.Origin.X + spec.Width; x++)
                {
                    var cell = new Cell(x, y);
                    int expected = state.CoverSteps.TryGetValue(cell, out int coverStep)
                        ? coverStep
                        : TimeExpandedModel.INF;
                    if (model.CoverStageOf(cell) != expected)
                    {
                        stageMismatch++;
                    }
                    int expectedTime = expected >= TimeExpandedModel.INF
                        ? TimeExpandedModel.INF
                        : expected * interval;
                    if (model.CoverTimeOf(cell) != expectedTime)
                    {
                        timeMismatch++;
                    }
                }
            }
            Check(stageMismatch == 0, $"R1: L{id} coverStep 逐格一致（差异 {stageMismatch}）");
            Check(timeMismatch == 0, $"R1: L{id} 覆盖时刻逐格一致（差异 {timeMismatch}）");
            Check(model.CoverStageOf(spec.Spawn) == TimeExpandedModel.INF, $"R1: L{id} 出生点不被覆盖");
            Check(model.CoverStageOf(spec.Goal) == TimeExpandedModel.INF, $"R1: L{id} 终点不被覆盖");

            var replay = new RuleState();
            replay.Setup(spec);
            replay.Start();
            int k = 0;
            bool sound = true;
            for (int i = 0; i < route.Length; i++)
            {
                Vector2I dir = DirForChar(route[i]);
                var stepDir = new Cell(dir.X, dir.Y);
                Cell next = replay.Head + stepDir;
                RuleState.StepOutcome outcome = replay.StepCore(stepDir);
                if (!outcome.Accepted)
                {
                    sound = false;
                    break;
                }
                if (outcome.Truncated)
                {
                    // R1 不建模身体 / 截断；截断目标格的真实合法性无法在松弛层表达。
                    continue;
                }
                if (spec.Foods.Contains(next))
                {
                    k = Math.Min(k + 1, model.FoodCount);
                }
                if (!model.IsReachable(k, i + 1, replay.Head) || !model.IsViable(k, i + 1, replay.Head))
                {
                    sound = false;
                    break;
                }
            }
            Check(sound, $"R1: L{id} 真实路线前缀都在可行核内");
        }
    }

    /// <summary>M1：感知危险场（不穿墙、时间软化、确定性、值域）。</summary>
    private void TestDangerField()
    {
        var walled = MakeSpec(7, 3, new Cell(0, 2), new Cell(6, 2), 12, 6, 2, 4,
            mechCenters: new[] { new Cell(0, 0) }, mechLevels: new[] { 4 },
            obstacles: new[] { new Cell(3, 0), new Cell(3, 1), new Cell(3, 2) });
        var wallModel = new TimeExpandedModel(walled, 20);
        var wallField = new PerceivedDangerField(wallModel);
        Check(wallField.At(new Cell(1, 1), 0) > 0.0, "危险场: 机关附近有危险值");
        Check(wallField.At(new Cell(5, 1), 0) == 0.0, "危险场: 不穿墙扩散（墙后恒为 0）");

        var softened = MakeSpec(7, 1, new Cell(0, 0), new Cell(6, 0), 12, 3, 2, 4,
            mechCenters: new[] { new Cell(2, 0) }, mechLevels: new[] { 0 });
        var softModel = new TimeExpandedModel(softened, 20);
        var softField = new PerceivedDangerField(softModel);
        Check(softField.At(new Cell(3, 0), 0) < softField.At(new Cell(3, 0), 10),
            "危险场: 覆盖临近时危险升高");

        var repeatField = new PerceivedDangerField(softModel);
        int deterministicBreaks = 0;
        int rangeBreaks = 0;
        for (int t = 0; t <= softModel.Horizon; t++)
        {
            for (int x = 0; x < 7; x++)
            {
                var cell = new Cell(x, 0);
                double value = softField.At(cell, t);
                if (value < 0.0 || value > 1.0)
                {
                    rangeBreaks++;
                }
                if (Math.Abs(value - repeatField.At(cell, t)) > 1e-12)
                {
                    deterministicBreaks++;
                }
            }
        }
        Check(rangeBreaks == 0, "危险场: 值域在 [0,1]");
        Check(deterministicBreaks == 0, "危险场: 同输入同输出（确定性）");
    }

    /// <summary>M1：公平性（AUC 手算、无机关全 safe、时间陷阱判 unsafe）。</summary>
    private void TestFairness()
    {
        var ranked = new List<BranchSample>
        {
            new() { Perceived = 0.9, Unsafe = true },
            new() { Perceived = 0.8, Unsafe = false },
        };
        Check(Math.Abs(FairnessEvaluator.Auc(ranked) - 1.0) < 1e-9, "公平性: AUC 正确排序=1");
        ranked[0].Perceived = 0.2;
        Check(Math.Abs(FairnessEvaluator.Auc(ranked) - 0.0) < 1e-9, "公平性: AUC 反向=0");
        ranked[0].Perceived = 0.8;
        Check(Math.Abs(FairnessEvaluator.Auc(ranked) - 0.5) < 1e-9, "公平性: AUC 打平=0.5");
        Check(Math.Abs(FairnessEvaluator.Auc(
            new List<BranchSample> { new() { Perceived = 0.2, Unsafe = false } }) - 1.0) < 1e-9,
            "公平性: 全 safe 边界=1");
        Check(Math.Abs(FairnessEvaluator.Auc(
            new List<BranchSample> { new() { Perceived = 0.2, Unsafe = true } }) - 0.0) < 1e-9,
            "公平性: 全 unsafe 边界=0");

        var open = MakeSpec(5, 5, new Cell(0, 0), new Cell(4, 0), 12, 6, 2, 4);
        var openModel = new TimeExpandedModel(open, 20);
        var openReport = new FairnessEvaluator(open, openModel, new PerceivedDangerField(openModel))
            .Evaluate("DDDD");
        Check(openReport.BranchCount > 0, "公平性: 空场存在分支样本");
        Check(openReport.UnsafeCount == 0 && openReport.Auc == 1.0, "公平性: 空场无 unsafe 分支");
        Check(openReport.MinMuFair == 1.0 && openReport.Passed(), "公平性: 空场通过");

        // 时间陷阱：走廊旁的垂直死路在下一轮生长后入口被封，分支无法回溯。
        var trap = MakeSpec(7, 4, new Cell(0, 2), new Cell(6, 2), 12, 4, 2, 4,
            mechCenters: new[] { new Cell(3, 3) }, mechLevels: new[] { 0 },
            obstacles: new[]
            {
                new Cell(2, 0), new Cell(2, 1),
                new Cell(4, 0), new Cell(4, 1),
            });
        var trapModel = new TimeExpandedModel(trap, 24);
        var trapReport = new FairnessEvaluator(trap, trapModel, new PerceivedDangerField(trapModel))
            .Evaluate("DDDDDD");
        Check(trapReport.BranchCount > 0, "公平性: 陷阱关存在分支样本");
        Check(trapReport.UnsafeCount >= 1, "公平性: 时间陷阱分支被判 unsafe");
        Check(trapReport.Auc >= 0.0 && trapReport.Auc <= 1.0, "公平性: AUC 在 [0,1]");
        Check(trapReport.MinMuFair >= 0.0 && trapReport.MinMuFair <= 1.0, "公平性: μ_fair 在 [0,1]");
    }

    /// <summary>v4 设计公平门：支配差距 / 诱饵必败 / 机关必要。</summary>
    private void TestDesignFairnessGate()
    {
        // 1) 规范解比最优解长 4 步：应报告支配捷径。
        LevelSpec open = MakeSpec(3, 3, new Cell(0, 0), new Cell(2, 0), 20, 99, 2, 0);
        var verifyOptions = new DesignFairnessOptions
        {
            AssumeCanonicalOptimal = false,
            Strict = false,
        };
        DesignFairnessReport dominated = DesignFairnessGate.Evaluate(open, "SSDDWW", verifyOptions);
        Check(dominated.DominanceGap == 4, $"公平门: 支配差距=4（实际 {dominated.DominanceGap}）");
        Check(!dominated.DominanceFree && !dominated.Passed, "公平门: 存在支配捷径时拒绝");

        DesignFairnessReport optimal = DesignFairnessGate.Evaluate(open, "DD", verifyOptions);
        Check(optimal.DominanceFree && optimal.CanonicalNearOptimal && optimal.Passed,
            "公平门: 最优规范解通过");

        // 2) 诱饵必败：吃 (0,2) 后最短胜利 6 步 > 2 + 2。
        LevelSpec decoy = MakeSpec(5, 5, new Cell(0, 0), new Cell(2, 0), 20, 99, 2, 0,
            foods: new[] { new Cell(0, 2) });
        var strictOptions = new DesignFairnessOptions
        {
            AssumeCanonicalOptimal = true,
            Strict = true,
        };
        DesignFairnessReport decoyReport = DesignFairnessGate.Evaluate(decoy, "DD", strictOptions);
        Check(decoyReport.DecoysChecked == 1 && decoyReport.DecoysProven == 1,
            "公平门: 必败诱饵被证明");
        Check(decoyReport.Passed, "公平门: 必败诱饵通过严格门");

        // 3) 无害诱饵：吃 (1,1) 后 4 步即可胜利（≤ 2 + 2），不算陷阱。
        LevelSpec harmless = MakeSpec(5, 5, new Cell(0, 0), new Cell(2, 0), 20, 99, 2, 0,
            foods: new[] { new Cell(1, 1) });
        DesignFairnessReport harmlessReport = DesignFairnessGate.Evaluate(harmless, "DD", strictOptions);
        Check(harmlessReport.DecoysHarmless == 1 && !harmlessReport.DecoyProofPassed,
            "公平门: 无害诱饵被识别");
        Check(!harmlessReport.Passed, "公平门: 严格模式拒绝无害诱饵");

        // 4) 机关必要：初始机关封住直路 (2,0)，删除后最优 6→4。
        LevelSpec gate = MakeSpec(5, 3, new Cell(0, 0), new Cell(4, 0), 20, 99, 2, 4,
            mechCenters: new[] { new Cell(2, 0) }, mechLevels: new[] { 0 });
        DesignFairnessReport gateReport = DesignFairnessGate.Evaluate(gate, "SDDDDW", strictOptions);
        Check(gateReport.GatesNecessary == 1 && gateReport.MaxGateStepDrop >= 2,
            $"公平门: 机关必要性 (drop={gateReport.MaxGateStepDrop})");
        Check(gateReport.Passed, "公平门: 必要机关通过严格门");
    }

    /// <summary>v4 Portfolio：确定性 + 档位比例单调。</summary>
    private void TestGenerationPortfolio()
    {
        Check(GenerationPortfolio.Choose(42, 3, "hard")
            == GenerationPortfolio.Choose(42, 3, "hard"), "Portfolio: 同输入确定性");
        int structured = 0;
        for (int i = 0; i < 1000; i++)
        {
            if (GenerationPortfolio.Choose(i, i % 7, "hard") == GenerationSource.Structured)
            {
                structured++;
            }
        }
        double ratio = structured / 1000.0;
        Check(ratio > 0.5 && ratio < 0.7, $"Portfolio: hard 结构图占比≈60%（实际 {ratio:P0}）");
        Check(GenerationPortfolio.StructuredPercent("expert")
            > GenerationPortfolio.StructuredPercent("easy"), "Portfolio: 档位越高结构图占比越高");
    }

    /// <summary>v4 自适应预算：候选数受上限约束，提前停止时写入诊断指标。</summary>
    private void TestAdaptiveBudget()
    {
        var cfg = new GeneratorConfig
        {
            Seed = 11,
            Difficulty = "normal",
            Candidates = 4,
            MaxAttempts = 16,
            SearchAttempts = 6,
            EnableFairnessGate = false,
            AdaptiveBudget = true,
        };
        var job = new GenerationJob(cfg, GenerationBudget.For(GenerationBudgetTier.Loading));
        GenerationResult result = job.RunToCompletion();
        if (!result.Success)
        {
            return;
        }
        Check(job.CandidatesUsed >= 1 && job.CandidatesUsed <= 4,
            $"自适应预算: 候选数在 [1,4]（实际 {job.CandidatesUsed}）");
        Check(result.Metrics.ContainsKey("candidates_used"), "自适应预算: 写入 candidates_used");
        if (job.AdaptiveStopped)
        {
            Check(result.Metrics.ContainsKey("adaptive_stop"), "自适应预算: 写入 adaptive_stop");
        }
    }

    /// <summary>v4 结构注入：只开与主路线单点相连的深度 2 死胡同，并放诱饵花瓣。</summary>
    private void TestDeadEndInjection()
    {
        var spec = new LevelSpec
        {
            Origin = Cell.Zero,
            Width = 12,
            Height = 5,
            Spawn = new Cell(1, 2),
            Goal = new Cell(10, 2),
            InitialMaxLen = 20,
            GrowthInterval = 99,
            FoodLenGain = 2,
            MechanismCap = 0,
        };
        for (int x = 0; x < 12; x++)
        {
            spec.Floor.Add(new Cell(x, 2));
        }
        int injected = RouteFirstGenerator.InjectDeadEndForks(
            new DeterministicRng(7), spec, "DDDDDDDDD", 2);
        Check(injected == 2, $"结构注入: 注入 2 个假岔路（实际 {injected}）");
        Check(spec.Floor.Count == 12 + injected * 2,
            $"结构注入: 地板增加 {injected * 2} 格（实际 {spec.Floor.Count - 12}）");
        Check(spec.Foods.Count == injected, "结构注入: 每个岔路末端 1 片花瓣");

        var floor = new HashSet<Cell>(spec.Floor);
        foreach (Cell food in spec.Foods)
        {
            int neighbors = 0;
            foreach (Cell d in new[] { new Cell(1, 0), new Cell(-1, 0), new Cell(0, 1), new Cell(0, -1) })
            {
                if (floor.Contains(new Cell(food.X + d.X, food.Y + d.Y)))
                {
                    neighbors++;
                }
            }
            Check(neighbors == 1, $"结构注入: 花瓣 ({food.X},{food.Y}) 在死胡同末端");
        }
    }

    /// <summary>M2：模板库定义 / 参数推导 / 实例化确定性。</summary>
    private void TestTemplateLibrary()
    {
        Check(TemplateLibrary.All.Count == 9, "模板: T0–T8 共 9 个");
        var ids = new HashSet<string>();
        foreach (TemplateDef def in TemplateLibrary.All)
        {
            Check(ids.Add(def.Id), $"模板: id 唯一 {def.Id}");
        }

        var walkable = new List<Cell>();
        for (int y = 0; y < 12; y++)
        {
            for (int x = 0; x < 12; x++)
            {
                walkable.Add(new Cell(x, y));
            }
        }
        bool sawNegativeStart = false;
        foreach (TemplateDef def in TemplateLibrary.All)
        {
            TemplateInstance first = TemplateLibrary.Instantiate(def, new DeterministicRng(12345), 14, walkable);
            TemplateInstance second = TemplateLibrary.Instantiate(def, new DeterministicRng(12345), 14, walkable);
            Check(first != null, $"模板: {def.Id} 可实例化");
            if (first == null)
            {
                continue;
            }
            Check(first.InitialMaxLen >= 3 && first.GrowthInterval >= 2
                && first.MechanismCap >= 1 && first.FoodBudget >= 0,
                $"模板: {def.Id} 参数合法");
            Check(first.Centers.Count == def.Pattern.Length, $"模板: {def.Id} 机关数量符合模板");
            Check(first.InitialLevels.Count == first.Centers.Count, $"模板: {def.Id} 初始阶段数量一致");
            bool levelsOk = true;
            foreach (int level in first.InitialLevels)
            {
                if (level < MechanicShapes.MinLevel || level >= first.MechanismCap)
                {
                    levelsOk = false;
                }
                if (level < 0)
                {
                    sawNegativeStart = true;
                }
            }
            Check(levelsOk, $"模板: {def.Id} 初始阶段在 [-10,cap) 内");
            Check(second != null && SameTemplateInstance(first, second), $"模板: {def.Id} 同 seed 实例化一致");
        }
        Check(sawNegativeStart, "模板: 至少一个模板使用负起始阶段");
    }

    /// <summary>M2：机关优先管线（每模板 ≥10 个可解样本 + 可重放 + 公平性/模板字段）。</summary>
    private void TestInteractionPlanner()
    {
        var cases = new (string Id, string Tier, int Size, int Steps)[]
        {
            ("T0", "easy", 10, 10),
            ("T1", "easy", 10, 11),
            ("T2", "normal", 12, 14),
            ("T3", "normal", 12, 14),
            ("T4", "normal", 14, 16),
            ("T5", "hard", 16, 22),
            ("T6", "hard", 16, 22),
            ("T7", "hard", 16, 22),
            ("T8", "normal", 14, 16),
        };

        int replayFailed = 0;
        int templateIdFailed = 0;
        int fairnessMissing = 0;
        int templateTotal = 0;
        int templateTrunc = 0;
        int templateFork = 0;
        int templateDecoy = 0;
        foreach ((string id, string tier, int size, int steps) in cases)
        {
            int solvable = 0;
            int templateMode = 0;
            var reasons = new Dictionary<string, int>();
            for (int attempt = 0; attempt < 72 && solvable < 10; attempt++)
            {
                var cfg = new GeneratorConfig
                {
                    Seed = 900 + attempt,
                    Difficulty = tier,
                    Width = size,
                    Height = size,
                    TemplateId = id,
                };
                RouteFirstGenerator.AttemptResult attemptResult =
                    InteractionPlanner.RunAttempt(cfg, steps, 0, attempt);
                if (!attemptResult.Success)
                {
                    string reason = string.IsNullOrEmpty(attemptResult.Reason) ? "未知" : attemptResult.Reason;
                    reasons[reason] = reasons.TryGetValue(reason, out int n) ? n + 1 : 1;
                    continue;
                }

                GenerationResult candidate = attemptResult.Candidate;
                solvable++;
                if (candidate.GenerationMode == "template")
                {
                    templateMode++;
                    templateTotal++;
                    if (candidate.Metrics.TryGetValue("interaction_truncations", out double trunc) && trunc > 0)
                    {
                        templateTrunc++;
                    }
                    if (candidate.Metrics.TryGetValue("interaction_forks", out double forks) && forks >= 2)
                    {
                        templateFork++;
                    }
                    if (candidate.Metrics.TryGetValue("decoy_foods", out double decoy) && decoy > 0)
                    {
                        templateDecoy++;
                    }
                }
                if (candidate.TemplateId != id)
                {
                    templateIdFailed++;
                }
                if (!candidate.Metrics.ContainsKey("fairness_auc") && candidate.GenerationMode == "template")
                {
                    fairnessMissing++;
                }
                if (!ReplaySpec(LevelSpec.FromJson(candidate.Spec.ToJson()), candidate.Solution.Route))
                {
                    replayFailed++;
                }
            }

            Check(solvable >= 10, $"模板 {id}: 可解样本 ≥10（实际 {solvable}，模板主路径 {templateMode}）");
            Check(templateMode >= 1, $"模板 {id}: 至少 1 个走模板主路径（实际 {templateMode}）");
            GD.Print($"[Template] {id} solvable={solvable} template={templateMode} " +
                $"reasons={string.Join("|", reasons.Keys)}");
        }
        Check(replayFailed == 0, $"模板: 全部样本路线可重放（失败 {replayFailed}）");
        Check(templateIdFailed == 0, $"模板: template_id 与请求一致（失败 {templateIdFailed}）");
        Check(fairnessMissing == 0, $"模板: 成功候选带公平性指标（缺失 {fairnessMissing}）");
        Check(templateDecoy >= 1,
            $"模板: 至少 1 个模板关带路线外诱饵（{templateDecoy}/{templateTotal}，截断 {templateTrunc}）");
        Check(templateFork >= Math.Max(1, templateTotal / 2),
            $"模板: 至少一半模板关带 ≥2 步岔路（{templateFork}/{templateTotal}）");
    }

    /// <summary>M2：GenerationService 暴露 template 模型并序列化 generation_mode / template_id。</summary>
    private void TestTemplateService()
    {
        string dir = ProjectSettings.GlobalizePath("user://test_template_cache");
        if (System.IO.Directory.Exists(dir))
        {
            System.IO.Directory.Delete(dir, true);
        }
        var service = new GenerationService();
        service.SetupCache(dir, 8);
        service.SetGeneratorModel("template");
        string json = service.GenerateNow(31, "easy", "loading");
        Check(!string.IsNullOrEmpty(json), "模板服务: 返回 JSON");
        if (string.IsNullOrEmpty(json))
        {
            return;
        }
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        Check(root.GetProperty("success").GetBoolean(), "模板服务: 生成成功");
        string mode = root.GetProperty("generation_mode").GetString() ?? "";
        Check(mode == "template" || mode == "fallback", $"模板服务: generation_mode={mode}");
        string templateId = root.GetProperty("template_id").GetString() ?? "";
        Check(!string.IsNullOrEmpty(templateId), "模板服务: template_id 非空");
        JsonElement features = root.GetProperty("features");
        Check(features.TryGetProperty("h", out _), "模板服务: features 含交互 H");
        Check(features.TryGetProperty("d_interaction", out _), "模板服务: features 含 D_interaction");
        string specJson = root.GetProperty("spec_json").GetString() ?? "";
        string route = root.GetProperty("route").GetString() ?? "";
        Check(!string.IsNullOrEmpty(specJson) && !string.IsNullOrEmpty(route), "模板服务: spec / route 非空");
        if (!string.IsNullOrEmpty(specJson) && !string.IsNullOrEmpty(route))
        {
            Check(ReplaySpec(LevelSpec.FromJson(specJson), route), "模板服务: 路线可重放");
        }
    }

    /// <summary>加载流程：进度单调 / 阶段上报 / 预算放宽 / 模板候选优先于降级。</summary>
    private void TestGenerationProgress()
    {
        GenerationBudget loading = GenerationBudget.For(GenerationBudgetTier.Loading);
        Check(loading.MaxMilliseconds >= 20000 && loading.MaxUnits >= 900,
            "加载: Loading 预算已放宽（>=20s / >=900 单元）");
        Check(loading.MaxCandidates >= 4 && loading.MaxSearchAttempts >= 32,
            "加载: 候选数与搜索次数已放宽");
        Check(loading.AttemptMilliseconds == 0,
            "加载: 后台线程不再限制单次尝试时间片");

        var cfg = new GeneratorConfig
        {
            Seed = 900,
            Difficulty = "easy",
            Width = 10,
            Height = 10,
            TargetSteps = 10,
            Candidates = 1,
            MaxAttempts = 6,
            GeneratorModel = GeneratorModel.TemplateField,
            TemplateId = "T0",
        };
        var job = new GenerationJob(cfg, loading);
        double last = -1.0;
        bool monotonic = true;
        bool phaseSeen = false;
        int guard = 0;
        while (!job.Done && guard++ < 5000)
        {
            job.Advance(4.0);
            double fraction = job.ProgressFraction;
            if (fraction < last - 1e-9)
            {
                monotonic = false;
            }
            last = fraction;
            if (!string.IsNullOrEmpty(job.Phase))
            {
                phaseSeen = true;
            }
        }
        Check(job.Done, "加载: 分帧任务可完成");
        Check(monotonic, "加载: 进度单调不下降");
        Check(job.ProgressFraction >= 1.0 - 1e-9, "加载: 完成后进度为 1");
        Check(phaseSeen, "加载: 上报生成阶段");
        GD.Print($"[Progress] easy max_attempt_ms={job.MaxAttemptMilliseconds:F1}");
        Check(job.MaxAttemptMilliseconds > 0, "加载: 单次尝试耗时已计量");
        GenerationResult result = job.Result;
        Check(result != null && result.Success, "加载: 分帧生成成功");
        if (result != null && result.Success)
        {
            Check(result.GenerationMode == "template",
                $"加载: 模板候选优先于降级（实际 {result.GenerationMode}）");
        }

        // hard 模板任务：验证小时间片下仍然优先模板候选，且单次尝试不超上限。
        var hardCfg = new GeneratorConfig
        {
            Seed = 900,
            Difficulty = "hard",
            Width = 16,
            Height = 16,
            TargetSteps = 22,
            Candidates = 2,
            MaxAttempts = 16,
            GeneratorModel = GeneratorModel.TemplateField,
            TemplateId = "T4",
        };
        var hardJob = new GenerationJob(hardCfg, loading);
        GenerationResult hardResult = hardJob.RunToCompletion();
        GD.Print($"[Progress] hard max_attempt_ms={hardJob.MaxAttemptMilliseconds:F1} " +
            $"mode={hardResult?.GenerationMode} fallbacks={hardJob.Fallbacks}");
        Check(hardJob.MaxAttemptMilliseconds > 0, "加载: hard 单次尝试耗时已计量");
        Check(hardResult != null && hardResult.Success, "加载: hard 模板任务成功");

        var service = new GenerationService();
        string progressDir = ProjectSettings.GlobalizePath("user://test_progress_cache");
        if (System.IO.Directory.Exists(progressDir))
        {
            System.IO.Directory.Delete(progressDir, true);
        }
        service.SetupCache(progressDir, 4);
        service.SetGeneratorModel("template");
        bool done = service.Begin(777, "easy", "bootstrap");
        int frames = 0;
        while (!done && frames++ < 5000)
        {
            // 生成在后台线程执行：这里轮询并让出 CPU，模拟加载屏每帧只取进度。
            done = service.Advance(8.0);
            if (!done)
            {
                System.Threading.Thread.Sleep(1);
            }
        }
        Check(done, "加载: 服务分帧完成");
        string progressJson = service.ProgressJson();
        Check(!string.IsNullOrEmpty(progressJson), "加载: ProgressJson 非空");
        if (!string.IsNullOrEmpty(progressJson))
        {
            using JsonDocument doc = JsonDocument.Parse(progressJson);
            JsonElement root = doc.RootElement;
            Check(root.GetProperty("percent").GetDouble() >= 0.999, "加载: 完成时 ProgressJson 为 100%");
            Check(!string.IsNullOrEmpty(root.GetProperty("phase").GetString()), "加载: ProgressJson 带阶段");
        }
    }

    /// <summary>M4：交互指标（H / Deception / Cuts / Contention / L_tight）+ 基线扣除。</summary>
    private void TestInteractionMetrics()
    {
        Dictionary<int, string> routes = LoadGoldenRoutes();
        var options = new InteractionOptions { LookaheadCap = 8 };
        var dRaw = new double[6];
        var dInteraction = new double[6];
        for (int id = 1; id <= 5; id++)
        {
            string specJson = FileAccess.GetFileAsString($"res://tests/fixtures/anchors/level_{id:00}.json");
            LevelSpec spec = LevelSpec.FromJson(specJson);
            InteractionScore score = InteractionScore.Evaluate(spec, routes[id], options);
            dRaw[id] = score.Level.DRaw;
            dInteraction[id] = score.DInteraction;
            Check(score.Level.H >= 1 && score.Level.H <= 8, $"交互: L{id} H 在 [1,8]");
            Check(score.Level.Deception >= 0 && score.Level.Deception <= 1,
                $"交互: L{id} Deception 在 [0,1]");
            Check(score.Level.Cuts >= 0 && score.Level.Cuts <= 1, $"交互: L{id} Cuts 在 [0,1]");
            Check(score.Level.Contention >= 0 && score.Level.Contention <= 1,
                $"交互: L{id} Contention 在 [0,1]");
            Check(score.Level.LTight >= 0 && score.Level.LTight <= 1, $"交互: L{id} L_tight 在 [0,1]");
            GD.Print($"[IX] L{id} H={score.Level.H} Decep={score.Level.Deception:F3} " +
                $"Cuts={score.Level.Cuts:F3} Cont={score.Level.Contention:F3} " +
                $"LTight={score.Level.LTight:F3} D={score.Level.DRaw:F1} " +
                $"Base={score.Baseline.DRaw:F1} DInt={score.DInteraction:F1} " +
                $"trap={score.Level.AttractiveUnsafe}");
        }
        Check(dInteraction[1] < 0.01 && dInteraction[2] < 0.01 && dInteraction[3] < 0.01,
            $"交互: L1–L3 无机关，D_interaction≈0（{dInteraction[1]:F2}/{dInteraction[2]:F2}/{dInteraction[3]:F2}）");
        Check(dRaw[4] >= dRaw[1] && dRaw[5] >= dRaw[4] - 1e-9,
            $"交互: L4/L5 原始交互分不低（{dRaw[1]:F1}/{dRaw[4]:F1}/{dRaw[5]:F1}）");
        Check(dInteraction[5] > 0.0, "交互: L5 存在交互难度");

        var open = MakeSpec(5, 5, new Cell(0, 0), new Cell(4, 0), 12, 6, 2, 4);
        InteractionScore openScore = InteractionScore.Evaluate(open, "DDDD", options);
        Check(openScore.Level.DRaw < 10.0 && openScore.DInteraction < 0.01,
            $"交互: 空场直线交互≈0（D={openScore.Level.DRaw:F1}）");
    }

    /// <summary>M4：isotonic + 分段线性校准（单调、过锚点、倒挂被平滑）。</summary>
    private void TestScoreCalibration()
    {
        var calibration = ScoreCalibration.FromAnchors(new List<(double, double)>
        {
            (0, 5), (10, 15), (20, 30), (30, 40), (50, 75),
        });
        Check(Math.Abs(calibration.Map(-5) - 5) < 1e-9, "校准: 低于首锚取首分");
        Check(Math.Abs(calibration.Map(0) - 5) < 1e-9, "校准: 过首锚");
        Check(Math.Abs(calibration.Map(10) - 15) < 1e-9, "校准: 过第二锚");
        Check(Math.Abs(calibration.Map(50) - 75) < 1e-9, "校准: 过末锚");

        bool monotonic = true;
        double last = -1.0;
        for (double d = 0; d <= 60; d += 1.0)
        {
            double value = calibration.Map(d);
            if (value < last - 1e-9)
            {
                monotonic = false;
            }
            last = value;
        }
        Check(monotonic, "校准: 全程单调不减");

        var messy = ScoreCalibration.FromAnchors(new List<(double, double)>
        {
            (0, 30), (10, 10), (20, 20),
        });
        Check(messy.Map(0) <= messy.Map(10) + 1e-9 && messy.Map(10) <= messy.Map(20) + 1e-9,
            "校准: 倒挂锚点被平滑为单调");
    }

    /// <summary>确定性验证：必修花瓣保证可走完，路线外诱饵不被规范路线吃掉；L0 不随路线抬高。</summary>
    private void TestPairLoopStructure()
    {
        LevelSpec field = MakeSpec(9, 5, new Cell(0, 2), new Cell(8, 2), 8, 6, 4, 1);
        var instance = new TemplateInstance
        {
            TemplateId = "T-loop",
            InitialMaxLen = 8,
            FoodLenGain = 4,
            GrowthInterval = 6,
            MechanismCap = 1,
            FoodBudget = 3,
            AllowsFoods = true,
            Prior = 0.8,
        };
        List<PairCandidate> pairs = PairSelector.Select(field, instance, 8, 8, 8, new DeterministicRng(7));
        Check(pairs.Count > 0, "结构: 手工地图可选出 S/E 对");
        if (pairs.Count > 0)
        {
            GD.Print($"[PairLoop] n={pairs.Count} decoy={pairs[0].DecoyFoods} " +
                $"routeFoods={pairs[0].RouteFoods} forks={pairs[0].ForkSteps} " +
                $"steps={pairs[0].Steps} foods={pairs[0].Spec.Foods.Count} L0={pairs[0].Spec.InitialMaxLen}");
            Check(pairs[0].Spec.InitialMaxLen == 8,
                $"结构: L0 保持模板值不按路线抬高（实际 {pairs[0].Spec.InitialMaxLen}）");
            Check(pairs[0].RouteFoods >= 1,
                $"结构: 规范路线吃到必修花瓣（实际 {pairs[0].RouteFoods}）");
            Check(pairs[0].DecoyFoods >= 1,
                $"结构: 路线外存在诱饵花瓣（实际 {pairs[0].DecoyFoods}）");
        }
    }

    /// <summary>生成结构检查：规范路线是否出现回路（截断）与明显岔路。</summary>
    private void TestGeneratedStructure()
    {
        // 随机地形上模板命中率有限，这里只做诊断采样（结构性硬断言由 TestPairLoopStructure 保证）。
        var cases = new (string Id, string Tier, int Size, int Steps)[]
        {
            ("T3", "normal", 12, 14),
            ("T4", "hard", 16, 22),
            ("T7", "hard", 16, 22),
        };
        int total = 0;
        int withTruncation = 0;
        int withFork = 0;
        int sumTrunc = 0;
        int sumFork = 0;
        foreach ((string id, string tier, int size, int steps) in cases)
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                var cfg = new GeneratorConfig
                {
                    Seed = 900 + attempt,
                    Difficulty = tier,
                    Width = size,
                    Height = size,
                    TemplateId = id,
                };
                RouteFirstGenerator.AttemptResult attemptResult =
                    InteractionPlanner.RunAttempt(cfg, steps, 0, attempt);
                if (!attemptResult.Success
                    || attemptResult.Candidate.GenerationMode != "template")
                {
                    continue;
                }
                LevelSpec spec = LevelSpec.FromJson(attemptResult.Candidate.Spec.ToJson());
                var replay = new RuleState();
                replay.Setup(spec);
                replay.Start();
                int truncations = 0;
                int forkSteps = 0;
                foreach (char ch in attemptResult.Candidate.Solution.Route)
                {
                    Cell head = replay.Head;
                    int walkOptions = 0;
                    foreach (Cell d in new[] { new Cell(-1, 0), new Cell(1, 0), new Cell(0, -1), new Cell(0, 1) })
                    {
                        if (replay.ClassifyMove(head + d) == RuleState.MoveKind.Walk)
                        {
                            walkOptions++;
                        }
                    }
                    if (walkOptions >= 2)
                    {
                        forkSteps++;
                    }
                    Vector2I stepDir = DirForChar(ch);
                    RuleState.StepOutcome outcome = replay.StepCore(new Cell(stepDir.X, stepDir.Y));
                    if (!outcome.Accepted)
                    {
                        break;
                    }
                    if (outcome.Truncated)
                    {
                        truncations++;
                    }
                }
                total++;
                sumTrunc += truncations;
                sumFork += forkSteps;
                if (truncations > 0)
                {
                    withTruncation++;
                }
                if (forkSteps >= 2)
                {
                    withFork++;
                }
            }
        }
        GD.Print($"[Structure] n={total} truncLevels={withTruncation} forkLevels={withFork} " +
            $"avgTrunc={sumTrunc / (double)Math.Max(1, total):F2} avgFork={sumFork / (double)Math.Max(1, total):F2}");
    }

    /// <summary>降级率诊断：加载预算下 hard 模板任务的 fallback 占比（含黏菌 / 经典两档）。</summary>
    private void TestFallbackRate()
    {
        var budget = new GenerationBudget
        {
            MaxUnits = 200,
            MaxMilliseconds = 6000,
            MaxCandidates = 2,
            MaxSearchAttempts = 6,
            AttemptMilliseconds = 50,
        };
        int total = 0;
        int fallback = 0;
        int fallbackPhysarum = 0;
        int failed = 0;
        var reasons = new Dictionary<string, int>();
        for (int i = 0; i < 8; i++)
        {
            var cfg = new GeneratorConfig
            {
                Seed = 3000 + i,
                Difficulty = "hard",
                Width = 16,
                Height = 16,
                TargetSteps = 22,
                Candidates = 2,
                MaxAttempts = 24,
                SearchAttempts = 6,
                GeneratorModel = GeneratorModel.TemplateField,
            };
            var job = new GenerationJob(cfg, budget);
            GenerationResult result = job.RunToCompletion();
            foreach (KeyValuePair<string, int> kv in job.ReasonCounts)
            {
                reasons[kv.Key] = reasons.TryGetValue(kv.Key, out int n) ? n + kv.Value : kv.Value;
            }
            if (result == null || !result.Success)
            {
                failed++;
                continue;
            }
            total++;
            if (result.GenerationMode == "fallback")
            {
                fallback++;
            }
            else if (result.GenerationMode == "fallback_physarum")
            {
                fallbackPhysarum++;
            }
        }
        GD.Print($"[Fallback] n={total} fallback={fallback} fallback_physarum={fallbackPhysarum} " +
            $"failed={failed} reasons={string.Join("|", reasons.Keys)}");
        Check(failed == 0, $"降级: 8 个 hard 任务全部成功（失败 {failed}）");
        Check(fallback + fallbackPhysarum <= total / 2 + 1,
            $"降级: fallback 占比不超过一半（{fallback + fallbackPhysarum}/{total}）");
    }

    private static bool SameTemplateInstance(TemplateInstance a, TemplateInstance b)
    {
        if (a.TemplateId != b.TemplateId || a.InitialMaxLen != b.InitialMaxLen
            || a.FoodLenGain != b.FoodLenGain || a.GrowthInterval != b.GrowthInterval
            || a.MechanismCap != b.MechanismCap || a.FoodBudget != b.FoodBudget
            || a.Centers.Count != b.Centers.Count || a.InitialLevels.Count != b.InitialLevels.Count)
        {
            return false;
        }
        for (int i = 0; i < a.Centers.Count; i++)
        {
            if (!a.Centers[i].Equals(b.Centers[i]) || a.InitialLevels[i] != b.InitialLevels[i])
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>构造可重复的测试关卡（矩形地板 + 可选障碍 / 食物 / 机关）。</summary>
    private static LevelSpec MakeSpec(
        int width,
        int height,
        Cell spawn,
        Cell goal,
        int maxLen,
        int interval,
        int gain,
        int cap,
        IEnumerable<Cell> foods = null,
        IEnumerable<Cell> mechCenters = null,
        IEnumerable<int> mechLevels = null,
        IEnumerable<Cell> obstacles = null,
        int targetSteps = 0)
    {
        var spec = new LevelSpec
        {
            Origin = Cell.Zero,
            Width = width,
            Height = height,
            Spawn = spawn,
            Goal = goal,
            InitialMaxLen = maxLen,
            GrowthInterval = interval,
            FoodLenGain = gain,
            MechanismCap = cap,
            TargetSteps = targetSteps,
        };
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                spec.Floor.Add(new Cell(x, y));
            }
        }
        if (obstacles != null)
        {
            spec.Obstacles.AddRange(obstacles);
        }
        if (foods != null)
        {
            spec.Foods.AddRange(foods);
        }
        if (mechCenters != null)
        {
            spec.MechCenters.AddRange(mechCenters);
        }
        if (mechLevels != null)
        {
            spec.MechInitialLevels.AddRange(mechLevels);
        }
        return spec;
    }

    private static Dictionary<int, string> LoadGoldenRoutes()
    {
        var routes = new Dictionary<int, string>();
        string json = FileAccess.GetFileAsString("res://tests/fixtures/levels_expected.json");
        if (string.IsNullOrEmpty(json))
        {
            return routes;
        }
        using JsonDocument doc = JsonDocument.Parse(json);
        foreach (JsonElement level in doc.RootElement.GetProperty("levels").EnumerateArray())
        {
            routes[level.GetProperty("level_id").GetInt32()] =
                level.GetProperty("route").GetString() ?? "";
        }
        return routes;
    }

    /// <summary>标签分类器：无机关样例 + 重复分类一致。</summary>
    private void TestTagger()
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
        TagResult first = LevelTagger.Tag(spec, "DDDDD", v, options);
        TagResult second = LevelTagger.Tag(spec, "DDDDD", v, options);
        Check(first.Tags.Contains("no_mechanism"), "标签: 无机关标签");
        Check(first.Evidence.ContainsKey("no_mechanism"), "标签: 证据字段完整");
        Check(first.Tags.Count == second.Tags.Count, "标签: 重复分类数量一致");
        for (int i = 0; i < first.Tags.Count; i++)
        {
            Check(first.Tags[i] == second.Tags[i], $"标签: 重复分类第 {i} 项一致");
        }
    }

    /// <summary>MAP-Elites 存档（S5）：步骤目标、同格去重、选格、target_miss 回退。</summary>
    private void TestMapElites()
    {
        var archive = new MapElitesArchive(10);
        archive.Add(MakeArchiveResult(70, 12, 10, 1.0, 0.1, 50));
        archive.Add(MakeArchiveResult(75, 12, 10, 1.0, 0.1, 40));
        Check(archive.Count == 1, "存档: 相同描述符去重为 1 格");

        GenerationResult hit = archive.Select(out int delta, out bool miss);
        Check(hit != null && hit.Solution.Steps == 10 && hit.Score.Quality == 50,
            "存档: 同格保留质量更高的候选");
        Check(!miss && delta == 0, $"存档: 命中目标步数（delta={delta} miss={miss}）");

        archive.Add(MakeArchiveResult(60, 18, 4, 2.0, 0.5, 90));
        Check(archive.Count == 2, "存档: 不同描述符占用新格");
        GenerationResult best = archive.Select(out int delta2, out bool miss2);
        Check(best != null && best.Solution.Steps == 10 && delta2 == 0 && !miss2,
            "存档: 选格取离目标步数最近的格子");

        var farArchive = new MapElitesArchive(30);
        farArchive.Add(MakeArchiveResult(70, 12, 8, 1.0, 0.1, 50));
        farArchive.Add(MakeArchiveResult(80, 18, 10, 2.0, 0.5, 90));
        GenerationResult fallback = farArchive.Select(out int delta3, out bool miss3);
        Check(fallback != null && fallback.Solution.Steps == 10 && delta3 == 20 && miss3,
            "存档: 目标步数不可达时回退最近格并记 target_miss");

        var empty = new MapElitesArchive(10);
        Check(empty.Select(out _, out _) == null && empty.Count == 0, "存档: 空存档返回 null");
    }

    private static GenerationResult MakeArchiveResult(int score, int width, int steps,
        double minSlack, double mechPressure, int quality)
    {
        var spec = new LevelSpec { Width = width, Height = width };
        return new GenerationResult
        {
            Success = true,
            Spec = spec,
            Solution = new LevelSolution { Status = "SOLVABLE", Steps = steps, Route = "" },
            Score = new LevelScore { Difficulty = score, Quality = quality },
            Metrics = new Dictionary<string, double>
            {
                ["min_slack"] = minSlack,
                ["mech_pressure"] = mechPressure,
            },
        };
    }

    /// <summary>分帧任务（S6）：与同步生成逐字一致、按单元熔断仍返回可重放终态。</summary>
    private void TestGenerationJob()
    {
        foreach ((long seed, string tier) in new[] { (7L, "normal"), (42L, "expert") })
        {
            var cfg = new GeneratorConfig { Seed = seed, Difficulty = tier, Candidates = 2 };
            GenerationResult direct = RouteFirstGenerator.GenerateCore(cfg);
            GenerationResult sync = new GenerationJob(cfg, GenerationBudget.Unlimited()).RunToCompletion();
            Check(direct.Success && sync.Success, $"任务: {seed}/{tier} 同步生成成功");
            Check(sync.Spec.ToJson() == direct.Spec.ToJson()
                && sync.Solution.Route == direct.Solution.Route,
                $"任务: {seed}/{tier} 与 GenerateCore 逐字一致");

            var slicedCfg = new GeneratorConfig { Seed = seed, Difficulty = tier, Candidates = 2 };
            var sliced = new GenerationJob(slicedCfg, GenerationBudget.Unlimited());
            int guard = 0;
            while (!sliced.Done && guard++ < 10000)
            {
                sliced.Advance(1.0);
            }
            Check(sliced.Done, $"任务: {seed}/{tier} 1ms 分帧可完成");
            Check(sliced.Result != null && sliced.Result.Spec.ToJson() == sync.Spec.ToJson()
                && sliced.Result.Solution.Route == sync.Solution.Route,
                $"任务: {seed}/{tier} 1ms 分帧结果一致");
        }

        var budget = new GenerationBudget { MaxUnits = 2, MaxCandidates = 1 };
        var capped = new GenerationJob(
            new GeneratorConfig { Seed = 1, Difficulty = "easy", TargetSteps = 1 }, budget);
        GenerationResult cappedResult = capped.RunToCompletion();
        Check(capped.Done && capped.BudgetAborted, "任务: 工作单元上限触发熔断");
        Check(cappedResult != null && (cappedResult.Success || !string.IsNullOrEmpty(cappedResult.Reason)),
            "任务: 熔断仍返回终态结果");
        if (cappedResult != null && cappedResult.Success)
        {
            Check(ReplaySpec(LevelSpec.FromJson(cappedResult.Spec.ToJson()), cappedResult.Solution.Route),
                "任务: 熔断返回的关卡可重放");
        }
    }

    /// <summary>黏菌枝干生成（v5）：normal/expert 可出关、路线可重放、枝干指标随结果返回。</summary>
    private void TestPhysarumGenerator()
    {
        foreach ((long seed, string tier) in new[] { (1L, "normal"), (3L, "expert") })
        {
            var cfg = new GeneratorConfig
            {
                Seed = seed,
                Difficulty = tier,
                GeneratorModel = GeneratorModel.Physarum,
                ObstacleModel = ObstacleModel.PoissonCluster,
            };
            var job = new GenerationJob(cfg, GenerationBudget.For(GenerationBudgetTier.Loading));
            GenerationResult result = job.RunToCompletion();
            Check(result != null && result.Success, $"黏菌: {seed}/{tier} 生成成功");
            if (result == null || !result.Success)
            {
                continue;
            }
            Check(ReplaySpec(LevelSpec.FromJson(result.Spec.ToJson()), result.Solution.Route),
                $"黏菌: {seed}/{tier} 路线可重放通关");
            Check(result.Metrics.TryGetValue("branch_top", out double top) && top > 0,
                $"黏菌: {seed}/{tier} 返回枝干粗壮度");
            Check(result.Metrics.TryGetValue("branch_ratio", out double ratio) && ratio >= 0 && ratio <= 1,
                $"黏菌: {seed}/{tier} 次/主枝干比值在 [0,1]");
        }
    }

    /// <summary>生成缓存与服务（S6）：LRU、命中逐字一致、缓存路线可重放。</summary>
    private void TestGenerationCache()
    {
        string dir = ProjectSettings.GlobalizePath("user://test_generation_cache");
        var cache = new GenerationCache(dir, 2);
        cache.Clear();
        cache.Put(1, "easy", 1, "{\"a\":1}");
        Check(cache.TryGet(1, "easy", 1, out string json) && json.Contains("\"a\""), "缓存: 写入后可读");
        cache.Put(2, "easy", 1, "{}");
        cache.Put(3, "easy", 1, "{}");
        Check(cache.Count == 2, $"缓存: LRU 容量 2（实际 {cache.Count}）");

        var service = new GenerationService();
        service.SetupCache(dir, 10);
        string generated = service.GenerateNow(11, "easy", "bootstrap");
        Check(!string.IsNullOrEmpty(generated), "服务: 同步生成返回 JSON");

        var service2 = new GenerationService();
        service2.SetupCache(dir, 10);
        bool hit = service2.Begin(11, "easy", "bootstrap");
        Check(hit && service2.FromCache(), "服务: 相同 seed/tier 缓存命中");
        Check(service2.ResultJson() == generated, "服务: 缓存内容逐字一致");

        using JsonDocument doc = JsonDocument.Parse(generated);
        JsonElement root = doc.RootElement;
        Check(root.GetProperty("success").GetBoolean(), "服务: 生成结果 success=true");
        string specJson = root.GetProperty("spec_json").GetString() ?? "";
        string route = root.GetProperty("route").GetString() ?? "";
        Check(!string.IsNullOrEmpty(specJson) && !string.IsNullOrEmpty(route), "服务: spec / route 非空");
        if (!string.IsNullOrEmpty(specJson) && !string.IsNullOrEmpty(route))
        {
            Check(ReplaySpec(LevelSpec.FromJson(specJson), route), "服务: 生成路线可重放通关");
        }
        Check(root.GetProperty("work_units").GetDouble() > 0, "服务: 工作单元已计量");
        cache.Clear();
    }

    private void TestGenerator()
    {
        var bands = new[] { "easy", "normal", "hard" };
        var scoresByBand = new Dictionary<string, List<int>>();
        var timings = new List<double>();
        int failed = 0;
        int replayFailed = 0;
        int differentialFailed = 0;
        int specFieldFailed = 0;
        int targetFieldFailed = 0;
        int targetDeltaSum = 0;
        int targetSamples = 0;
        var signatures = new HashSet<string>();
        var sizes = new HashSet<int>();

        foreach (string band in bands)
        {
            var scores = new List<int>();
            for (long seed = 1; seed <= 60; seed++)
            {
                var cfg = new GeneratorConfig
                {
                    Seed = seed,
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
                if (!ReplayBoth(spec, result.Solution.Route))
                {
                    differentialFailed++;
                    if (differentialFailed <= 5)
                    {
                        GD.PushError($"StepCore 差分失败: band={band} seed={seed}");
                    }
                    continue;
                }
                signatures.Add(result.Spec.ParamSignature);
                sizes.Add(Math.Max(result.Spec.Width, result.Spec.Height));
                if (result.Spec.SchemaVersion != 2 || string.IsNullOrEmpty(result.Spec.ParamSignature))
                {
                    specFieldFailed++;
                }
                if (result.Spec.TargetSteps != result.TargetSteps
                    || result.TargetSteps <= 0)
                {
                    targetFieldFailed++;
                }
                targetDeltaSum += result.TargetDelta;
                targetSamples++;
                scores.Add(result.Score.Difficulty);
            }
            scoresByBand[band] = scores;
        }

        Check(failed == 0, $"生成: 180 个 seed 全部成功（失败 {failed}）");
        Check(replayFailed == 0, $"生成: 全部路线可重放通关（失败 {replayFailed}）");
        Check(differentialFailed == 0, $"生成: Step 与 StepCore 逐步一致（失败 {differentialFailed}）");
        Check(specFieldFailed == 0, $"生成: v2 字段完整（失败 {specFieldFailed}）");
        Check(targetFieldFailed == 0, $"生成: 目标步数 / 分级字段完整（失败 {targetFieldFailed}）");
        double targetMeanDelta = targetDeltaSum / (double)Math.Max(1, targetSamples);
        Check(targetMeanDelta <= 6.0, $"生成: 平均目标步数偏差 {targetMeanDelta:F1} <= 6");
        Check(signatures.Count >= 20, $"生成: 参数签名多样化（{signatures.Count} 种）");
        Check(sizes.Count >= 3, $"生成: 尺寸多样化（{sizes.Count} 种）");
        GD.Print($"生成参数: 签名 {signatures.Count} 种，尺寸 {string.Join(",", sizes)}");

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
            Check(p95 < 500.0, $"生成性能: p95 {p95:F2} ms < 500 ms（含枝干评分；Loading 预算 10s）");
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

    /// <summary>差分：同一路线上 Step（Godot 字典）与 StepCore（纯 C#）逐步结果必须一致。</summary>
    private static bool ReplayBoth(LevelSpec spec, string route)
    {
        var viaStep = new RuleState();
        var viaCore = new RuleState();
        viaStep.Setup(spec);
        viaStep.Start();
        viaCore.Setup(spec);
        viaCore.Start();
        foreach (char ch in route)
        {
            Vector2I dir = DirForChar(ch);
            Godot.Collections.Dictionary a = viaStep.Step(dir);
            RuleState.StepOutcome b = viaCore.StepCore(new Cell(dir.X, dir.Y));
            Vector2I aFood = a["food_cell"].AsVector2I();
            if (a["accepted"].AsBool() != b.Accepted
                || a["truncated"].AsBool() != b.Truncated
                || a["ate_food"].AsBool() != b.AteFood
                || a["grew"].AsBool() != b.Grew
                || a["won"].AsBool() != b.Won
                || a["deadlock_msg"].AsString() != (b.DeadlockMsg ?? "")
                || a["steps"].AsInt32() != viaCore.GetSteps()
                || a["finished"].AsBool() != viaCore.GetFinished()
                || aFood.X != b.FoodCell.X
                || aFood.Y != b.FoodCell.Y)
            {
                return false;
            }
        }
        return viaStep.Head.Equals(viaCore.Head);
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

    /// <summary>锚点回归：原始值刻度下 5 关分数与难度档可复现。</summary>
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
        Check(snapshot.ScoringVersion == 5, "锚点: scoring_version=5");
        Check(snapshot.Anchors.Count == 6, "锚点: 6 条实测锚点");
        Check(snapshot.Anchors.Count == ScoreAnchors.Default.Anchors.Count, "锚点: 快照与内置数量一致");
        for (int i = 0; i < Math.Min(snapshot.Anchors.Count, ScoreAnchors.Default.Anchors.Count); i++)
        {
            ScoreAnchor a = snapshot.Anchors[i];
            ScoreAnchor b = ScoreAnchors.Default.Anchors[i];
            Check(a.Id == b.Id && a.Score == b.Score && Math.Abs(a.DRaw - b.DRaw) < 0.001,
                $"锚点: {a.Id} 快照与内置一致");
        }

        Check(BranchScoreSchemes.TierForScore(26) == "Easy", "档位: L2=26 为 Easy");
        Check(BranchScoreSchemes.TierForScore(45) == "Normal", "档位: L3=45 为 Normal");
        Check(BranchScoreSchemes.TierForScore(58) == "Hard", "档位: L5=58 为 Hard");
        Check(BranchScoreSchemes.TierForScore(60) == "Expert", "档位: 60 为 Expert");

        var targets = new Dictionary<int, int> { [1] = 18, [2] = 26, [3] = 45, [4] = 47, [5] = 58 };
        var tiers = new Dictionary<int, string>
        {
            [1] = "Easy", [2] = "Easy", [3] = "Normal", [4] = "Normal", [5] = "Hard",
        };
        var options = new MetricOptions
        {
            FunnelNodeBudget = 2_000_000,
            FunnelMaxCount = 200_000,
            SolveNodeBudget = 2_000_000,
            SolveStatesNodeBudget = 2_000_000,
            RequiredFoodNodeBudget = 2_000_000,
            EnableRequiredFood = true,
        };

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
            double raw = 0;
            if (anchor != null)
            {
                BranchPosteriorFeatures branch = BranchPosteriorScorer.Extract(spec, routes[id]);
                raw = BranchScoreSchemes.Hybrid(branch, v);
                Check(Math.Abs(raw - anchor.DRaw) < 0.05,
                    $"锚点: L{id} 方案 C 原始值 {raw:F3} 与快照一致");
            }

            int score = BranchScoreSchemes.ScoreForRaw(raw);
            string tier = BranchScoreSchemes.TierForScore(score);
            Check(score == targets[id], $"锚点: L{id} 原始分 {score} == {targets[id]}");
            Check(tier == tiers[id], $"锚点: L{id} 难度档 {tier} == {tiers[id]}");
            GD.Print($"[Anchor] L{id} rawC={raw:F3} score={score}({tier}) " +
                $"dCore={v.DCore:F1} M14={v.M14FalseAffordance} CEst={(v.CEstimated ? 1 : 0)}");
        }
    }

    /// <summary>20×20 可生成 + 固定尺寸 + Loading 档开发机等效预算。</summary>
    private void TestLargeSize()
    {
        var cfg = new GeneratorConfig
        {
            Seed = 1,
            Difficulty = "expert",
            Width = 20,
            Height = 20,
            Candidates = 1,
            MaxAttempts = 16,
        };
        ulong t0 = Time.GetTicksUsec();
        GenerationResult result = RouteFirstGenerator.GenerateCore(cfg);
        double ms = (Time.GetTicksUsec() - t0) / 1000.0;
        Check(result.Success, $"大尺寸: 20×20 可生成（{ms:F0} ms）");
        if (!result.Success)
        {
            GD.PushError($"大尺寸: 20×20 失败 reason={result.Reason}");
            return;
        }
        GD.Print($"[Large] size={result.Spec.Width}x{result.Spec.Height} floor={result.Spec.Floor.Count} " +
            $"route={result.Solution.Route.Length} mechs={result.Spec.MechCenters.Count} " +
            $"param={result.Spec.ParamSignature} attempts={result.Attempts}");
        Check(result.Spec.Width == 20 && result.Spec.Height == 20, "大尺寸: 固定尺寸生效");
        Check(result.Spec.MechCenters.Count > 0, "大尺寸: 含机关");
        Check(ms < 1600.0, $"大尺寸: 20×20 耗时 {ms:F0} ms < 1600 ms（开发机 Loading 等效）");
    }
}

using System;

namespace Growth.Algorithms.Model;

/// <summary>感知危险场参数（生成算法 v3 §3.3-P4）。初值需按人工验收调整。</summary>
public sealed class DangerFieldOptions
{
    /// <summary>扩散系数 η：每轮把 η 的权重来自邻居均值。</summary>
    public double Eta = 0.35;
    /// <summary>固定扩散轮数 K（保证确定性与复杂度）。</summary>
    public int Iterations = 6;
    /// <summary>空间项权重 w_s。</summary>
    public double SpatialWeight = 0.6;
    /// <summary>时间软化项权重 w_t。</summary>
    public double TemporalWeight = 0.4;
    /// <summary>时间软化窗口（步）；≤ 0 时取生长周期 I。</summary>
    public double SoftWindow;
}

/// <summary>
/// 感知危险场 P(v,t)：把机关占格做图扩散（Neumann 边界，不穿墙），
/// 再叠加“距离被覆盖还有多久”的时间软化，输出 0–1 的危险度。
/// 固定迭代次数与固定遍历顺序，不含随机；不参与规则判定。
/// </summary>
public sealed class PerceivedDangerField
{
    private readonly TimeExpandedModel _model;
    private readonly double _eta;
    private readonly int _iterations;
    private readonly double _spatialWeight;
    private readonly double _temporalWeight;
    private readonly double _softWindow;
    private readonly double[][] _layers;

    public PerceivedDangerField(TimeExpandedModel model, DangerFieldOptions options = null)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        DangerFieldOptions opts = options ?? new DangerFieldOptions();
        _eta = Math.Clamp(opts.Eta, 0.0, 1.0);
        _iterations = Math.Max(0, opts.Iterations);
        _spatialWeight = Math.Max(0.0, opts.SpatialWeight);
        _temporalWeight = Math.Max(0.0, opts.TemporalWeight);
        _softWindow = opts.SoftWindow > 0 ? opts.SoftWindow : Math.Max(1, model.GrowthInterval);
        _layers = BuildLayers();
    }

    public int Horizon => _model.Horizon;

    /// <summary>P(v,t)，已钳制到 [0,1]；越界 / 非可走格返回 0。</summary>
    public double At(Cell c, int t)
    {
        return _model.TryIndexOf(c, out int index) ? At(index, t) : 0.0;
    }

    public double At(int index, int t)
    {
        if (index < 0 || index >= _model.CellCount)
        {
            return 0.0;
        }
        int time = Math.Clamp(t, 0, _layers.Length - 1);
        return _layers[time][index];
    }

    private double[][] BuildLayers()
    {
        int count = _model.CellCount;
        var layers = new double[_model.Horizon + 1][];
        var current = new double[count];
        var next = new double[count];

        for (int t = 0; t <= _model.Horizon; t++)
        {
            int growthStage = _model.GrowthCount(t);
            for (int index = 0; index < count; index++)
            {
                current[index] = _model.IsWalkableAt(index) && _model.IsMechAt(growthStage, index) ? 1.0 : 0.0;
            }

            for (int iteration = 0; iteration < _iterations; iteration++)
            {
                for (int index = 0; index < count; index++)
                {
                    if (!_model.IsWalkableAt(index))
                    {
                        next[index] = 0.0;
                        continue;
                    }
                    int[] neighbors = _model.WalkableNeighbors(index);
                    if (neighbors.Length == 0)
                    {
                        next[index] = current[index];
                        continue;
                    }
                    double sum = 0.0;
                    for (int i = 0; i < neighbors.Length; i++)
                    {
                        sum += current[neighbors[i]];
                    }
                    next[index] = (1.0 - _eta) * current[index] + _eta * (sum / neighbors.Length);
                }
                (current, next) = (next, current);
            }

            var layer = new double[count];
            for (int index = 0; index < count; index++)
            {
                if (!_model.IsWalkableAt(index))
                {
                    layer[index] = 0.0;
                    continue;
                }
                double spatial = current[index];
                double temporal = Temporal(index, t);
                layer[index] = Math.Clamp(_spatialWeight * spatial + _temporalWeight * temporal, 0.0, 1.0);
            }
            layers[t] = layer;
        }
        return layers;
    }

    /// <summary>时间软化：距离首次覆盖越近，危险越高（logistic）。</summary>
    private double Temporal(int index, int t)
    {
        int stage = _model.CoverStageAt(index);
        if (stage >= TimeExpandedModel.INF)
        {
            return 0.0;
        }
        double coverTime = stage * (double)_model.GrowthInterval;
        return 1.0 / (1.0 + Math.Exp(-(t - coverTime) / _softWindow));
    }
}

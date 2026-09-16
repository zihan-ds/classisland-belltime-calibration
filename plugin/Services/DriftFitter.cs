using System;
using System.Collections.Generic;
using System.Linq;

namespace BellTimeCalibration.Services;

/// <summary>
/// 偏移估计器（v0.11.0：由「趋势外推」改为「稳健中位数」）。
///
/// 背景与改造依据（2026-09-14 实机 6 个边界的写入序列）：
/// 旧实现用最小二乘拟合 o(t)=a+b·t 并外推到当前时刻写入，结果偏移在两小时内
/// 从 −1.907 s 荡到 +0.694 s 又回 −1.344 s（摆幅 ±1.5 s），而校铃钟真实漂移仅 ≈0.1 s/小时。
/// 根因：单次测量散布 ±1.5 s（校铃自身抖动，同日 7 个样本的所需偏移跨度达 3.2 s），
/// 而 3~4 小时的真实漂移只有 ~0.4 s —— **完全被噪声淹没**；从噪声里估出的斜率
/// （实测 −1.746 / −1.026 / +0.616 / +0.997 秒/小时，符号反复）被外推后放大成秒级跳变。
/// 结论：判据（起响沿闸门）是准的，应用策略错了。
///
/// 现口径：
/// <list type="bullet">
/// <item><description><b>写值 = 近期样本的中位数</b>。中位数对 ±1.5 s 的散布天然稳健，
/// 且「所需偏移」与当前偏移无关（只取决于铃的真实滞后），因此从任何起点都会收敛到同一值。</description></item>
/// <item><description>样本窗口：优先只取最近 <see cref="WindowHours"/> 小时内的样本（漂移慢变，
/// 旧样本会把已变化的基准拖住）；不足 <see cref="MinSamplesForWindow"/> 个时放宽到全部样本。</description></item>
/// <item><description>跳变检测：新样本与当前中位数相差 &gt; <see cref="JumpResetSeconds"/> 秒时清空重开
/// （人工校准 / 信号源切换）。中位数本身已抗单点噪声，故该阈值主要拦真实基准跳变。</description></item>
/// <item><description><b>趋势外推默认关闭</b>，仅在「样本数 ≥ <see cref="TrendMinSamples"/>、
/// 时间跨度 ≥ <see cref="TrendMinSpanHours"/> 小时、且拟合残差 &lt; <see cref="TrendMaxResidualSeconds"/> 秒」
/// 三项同时成立时启用；斜率仍钳制在 ±<see cref="MaxSlopeSecondsPerHour"/>。
/// 门槛的含义：只有噪声被平均到足以看出真实走时（残差 &lt;0.3 s）之后，才允许追趋势。</description></item>
/// </list>
/// 不做线程同步：调用方（CalibrationRunner）串行使用（同一时刻仅一个监听窗口）。
/// </summary>
public sealed class DriftFitter
{
    /// <summary>参与估计的最大样本数（滚动窗口，超出丢最旧）。</summary>
    public const int MaxSamples = 40;

    /// <summary>首选样本窗口（小时）：只取最近这段时间内的样本。</summary>
    public const double WindowHours = 6.0;

    /// <summary>样本窗口内至少要有这么多个样本，否则放宽到全部样本。</summary>
    public const int MinSamplesForWindow = 3;

    /// <summary>跳变阈值（秒）：新样本与当前中位数之差超过该值即重开样本段（人工校准/新基准）。</summary>
    public const double JumpResetSeconds = 2.5;

    /// <summary>启用趋势外推所需的最少样本数。</summary>
    public const int TrendMinSamples = 12;

    /// <summary>启用趋势外推所需的最小时间跨度（小时）。</summary>
    public const double TrendMinSpanHours = 6.0;

    /// <summary>启用趋势外推允许的最大拟合残差（秒）：残差大于该值说明散布仍以噪声为主，不追趋势。</summary>
    public const double TrendMaxResidualSeconds = 0.3;

    /// <summary>斜率上限（秒/小时）：钳制以抑制噪声样本导致的虚假走时速率。</summary>
    public const double MaxSlopeSecondsPerHour = 2.0;

    /// <summary>趋势启用时的最大外推时长（小时）：预测点距最后一个样本不超过该值。</summary>
    public const double MaxExtrapolationHours = 2.0;

    /// <summary>批次导入时的默认时间间隔（秒）：历史文件只存时刻与所需偏移，
    /// 导入时按此间隔递增，避免「同一时刻多个样本」把时间跨度算成 0（仅影响趋势门槛判定）。</summary>
    private const double ImportedSampleSpacingSeconds = 1.0;

    private readonly List<(DateTime At, double OffsetSec)> _samples = new();

    /// <summary>
    /// 用历史样本预置拟合器（v1.0.1，供进程重启后恢复**当天**序列）。
    /// 与逐条 <see cref="Add"/> 的区别：**不做跳变检测**——历史样本本就同属一个基准，
    /// 逐条跑跳变判定会把正常的时间漂移误判成「人工校准」而清空序列。
    /// </summary>
    /// <param name="history">历史样本（按时间升序）。</param>
    /// <returns>实际导入的样本数。</returns>
    public int Seed(IEnumerable<(DateTime At, double RequiredSec)> history)
    {
        var count = 0;
        var lastAt = DateTime.MinValue;
        foreach (var (at, required) in history)
        {
            var effective = at <= lastAt ? lastAt.AddSeconds(ImportedSampleSpacingSeconds) : at;
            lastAt = effective;

            _samples.Add((effective, required));
            if (_samples.Count > MaxSamples)
                _samples.RemoveAt(0);
            count++;
        }

        if (count > 0)
        {
            var noteBefore = LastNote;
            Recompute();
            LastNote = $"已从当天历史恢复 {count} 个样本；{LastNote ?? noteBefore}";
        }

        return count;
    }

    /// <summary>当前参与估计的样本数。</summary>
    public int SampleCount => _samples.Count;

    /// <summary>近期样本的中位数（秒）；无样本为 null。</summary>
    public double? MedianSeconds { get; private set; }

    /// <summary>近期样本相对中位数的最大偏差（秒）；无样本为 null。用于判断散度。</summary>
    public double? MaxDeviationSeconds { get; private set; }

    /// <summary>趋势是否已启用（样本数、跨度、残差三项门槛同时满足）。</summary>
    public bool TrendEnabled { get; private set; }

    /// <summary>拟合出的走时速率（秒/小时）；仅在 <see cref="TrendEnabled"/> 时有意义。</summary>
    public double? SlopeSecondsPerHour { get; private set; }

    /// <summary>最近一次操作的说明（中文，供日志）。</summary>
    public string? LastNote { get; private set; }

    /// <summary>最近一个样本相对当前中位数的残差（秒）；无样本时为 null。</summary>
    public double? LastResidualSeconds { get; private set; }

    /// <summary>
    /// 追加一次测量样本。
    /// </summary>
    /// <param name="atLocal">样本时刻（本地墙钟，通常取铃响时刻）。</param>
    /// <param name="requiredOffset">让当前误差归零所需的绝对偏移（秒）。</param>
    public void Add(DateTime atLocal, TimeSpan requiredOffset)
    {
        var sec = requiredOffset.TotalSeconds;

        // 跳变检测：与当前中位数比对（中位数已抗单点噪声，故这里拦的是真实基准跳变）
        var priorMedian = MedianSeconds;
        if (priorMedian.HasValue && Math.Abs(sec - priorMedian.Value) > JumpResetSeconds)
        {
            _samples.Clear();
            LastNote = $"样本 {sec:F3}s 与当前中位数 {priorMedian.Value:F3}s 相差 {sec - priorMedian.Value:F3}s" +
                       $"（＞跳变阈值 {JumpResetSeconds:F1}s）→ 判定人工校准/新基准，重置样本段";
        }

        _samples.Add((atLocal, sec));
        if (_samples.Count > MaxSamples)
        {
            _samples.RemoveAt(0);
        }

        Recompute();
    }

    /// <summary>
    /// 预测某时刻所需的绝对偏移（秒）；无样本返回 null。
    /// 默认返回**近期样本中位数**；仅当趋势门槛全部满足时，才在中位数基准上做温和外推。
    /// </summary>
    /// <param name="atLocal">目标时刻（本地墙钟）。</param>
    public double? PredictSeconds(DateTime atLocal)
    {
        if (_samples.Count == 0)
            return null;

        if (_samples.Count == 1)
            return _samples[0].OffsetSec;

        if (!TrendEnabled || SlopeSecondsPerHour == null || MedianSeconds == null)
            return MedianSeconds;

        // 趋势启用：以中位数为基准做温和外推（受外推时长护栏约束）
        var hours = (atLocal - _samples[^1].At).TotalHours;
        var cappedHours = Math.Clamp(hours, -MaxExtrapolationHours, MaxExtrapolationHours);
        return MedianSeconds + SlopeSecondsPerHour.Value * cappedHours;
    }

    /// <summary>重算中位数、散度与（满足门槛时的）趋势。</summary>
    private void Recompute()
    {
        // 样本窗口：优先近期，样本太少则放宽到全部
        var windowStart = _samples[^1].At.AddHours(-WindowHours);
        var windowed = _samples.Where(s => s.At >= windowStart).ToList();
        if (windowed.Count < MinSamplesForWindow)
            windowed = _samples.ToList();

        if (_samples.Count == 1)
        {
            MedianSeconds = _samples[0].OffsetSec;
            MaxDeviationSeconds = 0;
            LastResidualSeconds = 0;
            TrendEnabled = false;
            SlopeSecondsPerHour = null;
            LastNote = $"样本 1 个（{_samples[0].OffsetSec:F3}s）：先按单次测量写入，待更多样本后转中位数";
            return;
        }

        var median = ComputeMedian(windowed.Select(s => s.OffsetSec).ToList());
        if (median == null)
            return;

        MedianSeconds = median.Value;
        MaxDeviationSeconds = windowed.Max(s => Math.Abs(s.OffsetSec - median.Value));
        LastResidualSeconds = Math.Round(_samples[^1].OffsetSec - median.Value, 3);

        // 趋势门槛：样本数、时间跨度、拟合残差三项同时满足才启用
        var spanHours = (windowed[^1].At - windowed[0].At).TotalHours;
        if (windowed.Count < TrendMinSamples || spanHours < TrendMinSpanHours)
        {
            TrendEnabled = false;
            SlopeSecondsPerHour = null;
            LastNote = $"中位数 {median.Value:F3}s（{windowed.Count} 个样本，" +
                       $"最大偏差 {MaxDeviationSeconds.Value:F3}s）；趋势未启用（样本 {windowed.Count}/{TrendMinSamples}、跨度 {spanHours:F1}/{TrendMinSpanHours:F1}h）";
            return;
        }

        // 最小二乘（仅用于判断散布是否已小到允许追趋势）
        var baseAt = windowed[0].At;
        var meanX = windowed.Average(s => (s.At - baseAt).TotalHours);
        var meanY = windowed.Average(s => s.OffsetSec);
        var sxx = windowed.Sum(s => Math.Pow((s.At - baseAt).TotalHours - meanX, 2));
        var sxy = windowed.Sum(s => ((s.At - baseAt).TotalHours - meanX) * (s.OffsetSec - meanY));
        var rawSlope = sxx <= 1e-9 ? 0 : sxy / sxx;
        var intercept = meanY - rawSlope * meanX;
        var maxResidual = windowed.Max(s => Math.Abs(s.OffsetSec - (intercept + rawSlope * (s.At - baseAt).TotalHours)));

        if (maxResidual > TrendMaxResidualSeconds)
        {
            TrendEnabled = false;
            SlopeSecondsPerHour = null;
            LastNote = $"中位数 {median.Value:F3}s（{windowed.Count} 个样本，跨度 {spanHours:F1}h）；" +
                       $"趋势未启用（拟合残差 {maxResidual:F3}s ＞ {TrendMaxResidualSeconds:F1}s，散布仍以噪声为主）";
            return;
        }

        TrendEnabled = true;
        SlopeSecondsPerHour = Math.Clamp(rawSlope, -MaxSlopeSecondsPerHour, MaxSlopeSecondsPerHour);
        var clampNote = Math.Abs(rawSlope - SlopeSecondsPerHour.Value) > 1e-9 ? $"（原始斜率 {rawSlope:F2} 已钳制）" : "";
        LastNote = $"中位数 {median.Value:F3}s + 趋势 {SlopeSecondsPerHour.Value:F3} 秒/小时{clampNote}" +
                   $"（{windowed.Count} 个样本，跨度 {spanHours:F1}h，拟合残差 {maxResidual:F3}s）";
    }

    private static double? ComputeMedian(List<double> values)
    {
        if (values.Count == 0)
            return null;
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}

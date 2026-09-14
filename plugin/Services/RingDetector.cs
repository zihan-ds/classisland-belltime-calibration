using System;
using System.Collections.Generic;

namespace BellTimeCalibration.Services;

/// <summary>
/// 一个铃声突发段（v0.8.0）：由「持续高于阈值」的连续音频块构成。
/// 学校铃声是<b>持续数秒</b>的响亮声音，因此起响点（<see cref="OnsetUtc"/>）才是与课表时刻比较的物理量；
/// 段结构同时提供两个强判据：时长（人声/桌椅等瞬时声远短）与峰值（铃最响）。
/// </summary>
/// <param name="OnsetUtc">起响点（首个越过阈值的音频块墙钟，UTC）。</param>
/// <param name="PeakRms">段内峰值 RMS（线性）。</param>
/// <param name="DurationSeconds">段时长（秒）：最后一个越阈块 − 起响点。</param>
/// <param name="PeakOnsetUtc">
/// 峰值所在子段的起响点（v0.9.3）：未合并的单段等于 <see cref="OnsetUtc"/>；
/// 合并簇（<see cref="RingCandidateSelector.MergeClusters"/>）中是「贡献峰值的那一击」的起响点。
/// 用途：簇内可能先并进一段铃声前 0.5 s 内的零碎噪声，此时簇起点不是铃声起响点 ——
/// 实机 2026-09-12 08:50 窗口即因此把起响点早报了约 1.1 s。
/// </param>
public readonly record struct RingBurst(DateTime OnsetUtc, double PeakRms, double DurationSeconds, DateTime PeakOnsetUtc)
{
    /// <summary>段内峰值（dBFS）。</summary>
    public double PeakDb => 20 * Math.Log10(Math.Max(PeakRms, 1e-12));
}

/// <summary>
/// 铃声检测器（v0.8.0 重写）。
/// 与旧实现的关键差异：
/// <list type="bullet">
/// <item><description><b>噪声底只在安静期更新</b>（响铃期间冻结）：旧实现用滑动 EWMA，持续响铃会把噪声底抬上去、
/// 阈值随之抬高，于是「铃刚响的那一下」被漏掉、噪声底回落后才再次触发——实机测出的突发点落在铃声持续段中部，
/// 比真实起响点晚 2s 以上（2026-09-10 20:10 边界：插件 20:10:00.495，用户实测真值 20:09:58.175）；</description></item>
/// <item><description><b>突发 = 持续段</b>（起响点 + 峰值 + 时长），带 0.6 倍滞回与 0.25s 静音判段，
/// 一段连续铃声只产出一个候选，且候选时刻即起响点；</description></item>
/// <item><description>不再需要「冷却时间」压缩候选（连续段天然合并）。</description></item>
/// </list>
/// 时间戳一律由调用方以墙钟 <see cref="DateTime.UtcNow"/> 提供（音频回调无时间戳）。
/// </summary>
public class RingDetector
{
    /// <summary>噪声底下限：防止静音时除零/误触。</summary>
    public const double NoiseFloorMin = 1e-4;

    /// <summary>绝对突发下限（RMS），对应约 -50 dBFS。</summary>
    public const double AbsoluteMinRms = 0.0032;

    /// <summary>噪声底平滑系数：仅用于安静期更新（响铃期冻结）。</summary>
    private const double FloorAdaptRate = 0.05;

    /// <summary>段的结束滞回：低于阈值的该倍数即视为已离开响铃。</summary>
    private const double BurstEndHysteresis = 0.6;

    /// <summary>判段静音时长（秒）：连续这么久没有越阈块即结束当前段。</summary>
    private const double BurstEndSilenceSeconds = 0.25;

    /// <summary>启动自学习时长（秒）：开麦后的这段时间内无条件快速学习环境噪声底。
    /// 必要性：噪声底初值极低（−80 dBFS），若一开始就执行「响铃期冻结」，房间的任何环境声都会立刻被判为响铃，
    /// 基线再也无法更新 → 整个窗口并成一段（2026-09-10 21:30 实机：选出 18.78s 的假段、噪声底 −57 dBFS）。
    /// 监听窗口在边界前 20s 开麦，铃声出现在边界附近，因此 1s 自学习期不会吃掉真实铃声。</summary>
    private const double BootstrapSeconds = 1.0;

    private readonly double _sensitivityMultiplier;
    private readonly object _sync = new();
    private readonly List<RingBurst> _bursts = new();

    private double _noiseFloor = NoiseFloorMin;
    private float _maxRms;
    private DateTime? _firstBlockUtc;

    // 当前进行中的段
    private bool _inBurst;
    private DateTime _burstOnsetUtc;
    private DateTime _lastLoudUtc;
    private double _burstPeakRms;

    /// <summary>
    /// 构造检测器。
    /// </summary>
    /// <param name="sensitivityMultiplier">灵敏度倍数 = 噪声底判定倍数（对应配置项 DetectionSensitivity，默认 2.0）。</param>
    public RingDetector(double sensitivityMultiplier)
    {
        _sensitivityMultiplier = sensitivityMultiplier;
    }

    /// <summary>
    /// 候选铃声段列表（按起响点升序）。内部加锁返回副本，可安全跨线程读取。
    /// 捕获结束时若仍处于段中，会先结算该段（见 <see cref="Flush"/>），因此调用方拿到的段是完整的。
    /// </summary>
    public IReadOnlyList<RingBurst> Bursts
    {
        get
        {
            lock (_sync)
            {
                return _bursts.ToArray();
            }
        }
    }

    /// <summary>候选起响点列表（UTC，升序）——保持旧接口形状，供历史记录写入。</summary>
    public IReadOnlyList<DateTime> Candidates
    {
        get
        {
            lock (_sync)
            {
                var result = new DateTime[_bursts.Count];
                for (var i = 0; i < _bursts.Count; i++)
                    result[i] = _bursts[i].OnsetUtc;
                return result;
            }
        }
    }

    /// <summary>本次捕获期间观测到的峰值 RMS，换算为 dBFS（20*log10）。</summary>
    public float MaxRmsDb => ToDb(_maxRms);

    /// <summary>本次捕获结束时的噪声底，换算为 dBFS（20*log10）。</summary>
    public float NoiseFloorDb => ToDb((float)_noiseFloor);

    /// <summary>
    /// 结算进行中的段（捕获结束时调用一次）：把尚未因静音结束的段落库。
    /// </summary>
    public void Flush()
    {
        lock (_sync)
        {
            EndBurstLocked();
        }
    }

    /// <summary>
    /// 送入一块音频样本并运行检测。线程安全，可在音频回调线程直接调用。
    /// </summary>
    /// <param name="samples">本块的 PCM 样本（float，-1..1）。仅在本方法内有效。</param>
    /// <param name="wallTimeUtc">本块对应的墙钟时刻（UTC，由回调内 DateTime.UtcNow 提供）。</param>
    public void Feed(ReadOnlySpan<float> samples, DateTime wallTimeUtc)
    {
        if (samples.IsEmpty)
            return;

        double sumSq = 0;
        foreach (var s in samples)
        {
            sumSq += (double)s * s;
        }

        var rms = Math.Sqrt(sumSq / samples.Length);

        if (rms > _maxRms)
            _maxRms = (float)rms;

        // 启动自学习：开麦最初 BootstrapSeconds 秒无条件快速学习环境底，且不产生段
        _firstBlockUtc ??= wallTimeUtc;
        var bootstrapping = (wallTimeUtc - _firstBlockUtc.Value).TotalSeconds < BootstrapSeconds;
        if (bootstrapping)
        {
            _noiseFloor = _noiseFloor * (1 - FloorAdaptRate) + rms * FloorAdaptRate;
            if (_noiseFloor < NoiseFloorMin)
                _noiseFloor = NoiseFloorMin;
            return;
        }

        var threshold = _noiseFloor * _sensitivityMultiplier;
        var isLoud = rms > threshold && rms > AbsoluteMinRms;

        lock (_sync)
        {
            if (isLoud)
            {
                if (!_inBurst)
                {
                    // 起响点：首个越过阈值的块
                    _inBurst = true;
                    _burstOnsetUtc = wallTimeUtc;
                    _burstPeakRms = rms;
                }
                else if (rms > _burstPeakRms)
                {
                    _burstPeakRms = rms;
                }

                _lastLoudUtc = wallTimeUtc;
                return;
            }

            // 安静块：更新噪声底（响铃期冻结，避免持续铃声把底抬起来）
            _noiseFloor = _noiseFloor * (1 - FloorAdaptRate) + rms * FloorAdaptRate;
            if (_noiseFloor < NoiseFloorMin)
                _noiseFloor = NoiseFloorMin;

            if (_inBurst)
            {
                // 滞回：仍高于阈值×滞回系数 → 视为段内（铃声起伏不判段）
                if (rms > threshold * BurstEndHysteresis)
                {
                    _lastLoudUtc = wallTimeUtc;
                    return;
                }

                if ((wallTimeUtc - _lastLoudUtc).TotalSeconds >= BurstEndSilenceSeconds)
                    EndBurstLocked();
            }
        }
    }

    /// <summary>结算当前段并落库（调用方需持锁）。</summary>
    private void EndBurstLocked()
    {
        if (!_inBurst)
            return;

        _bursts.Add(new RingBurst(
            _burstOnsetUtc,
            _burstPeakRms,
            Math.Max(0, (_lastLoudUtc - _burstOnsetUtc).TotalSeconds),
            _burstOnsetUtc));

        _inBurst = false;
        _burstPeakRms = 0;
    }

    private static float ToDb(float v) => (float)(20 * Math.Log10(Math.Max(v, 1e-12)));
}

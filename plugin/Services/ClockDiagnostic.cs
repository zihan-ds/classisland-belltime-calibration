using System;
using System.Threading;
using ClassIsland.Core.Abstractions.Services;

namespace BellTimeCalibration.Services;

/// <summary>
/// 内核时钟自检（v1.0.4 诊断）：周期性比对「宿主内核显示时钟」与「系统墙钟」，并把
/// 「内核时钟自身滞后 L = (内核 − 墙钟) + 当前偏移」打出来。
///
/// 背景：本插件把 <c>e = t_ring（音频墙钟）− t_switch（BoundaryReached 墙钟）</c> 当作两个真值之差，
/// 由此推出「所需偏移 = 当前偏移 − e」。该推导隐含一个**从未被验证过**的前提：
/// 内核时钟 = 系统墙钟 + 当前偏移（即 <c>ExactTimeService.GetCurrentLocalDateTime()</c> 只比系统时间多一个偏移）。
/// 一旦内核时钟自己还带一段常量滞后 L，那么「内核在 B_nominal 切换」与「显示到达 B_nominal」就不再等价，
/// 换算出的偏移会**系统性偏小 L**，而插件侧完全看不出来（所有量都在墙钟域里自洽）。
///
/// 判读：
/// <list type="bullet">
/// <item><description><c>内核−墙钟 = 偏移</c>（L≈0）→ 前提成立，偏移换算没问题；</description></item>
/// <item><description><c>内核−墙钟</c> 稳定不等于偏移（L 为常量）→ 必须把 L 补进换算；</description></item>
/// <item><description>L 自身在跳变 → 内核时钟（NTP 时钟 / 时间突变保护）才是问题源。</description></item>
/// </list>
/// 只读、只写日志，不参与任何判定，可随时删除。
/// </summary>
public sealed class ClockDiagnostic
{
    private readonly IExactTimeService _exactTime;
    private readonly Func<double> _configuredOffset;
    private readonly object _sync = new();
    private Timer? _timer;

    private int _samples;
    private double _minLag = double.MaxValue;
    private double _maxLag = double.MinValue;
    private double _sumLag;
    private double _lastLag = double.NaN;

    /// <summary>
    /// 构造诊断器。
    /// </summary>
    /// <param name="exactTime">宿主精确时间服务（内核显示时钟）。</param>
    /// <param name="configuredOffset">读取当前偏移设置（内核 TimeOffsetSeconds 的值）。</param>
    public ClockDiagnostic(IExactTimeService exactTime, Func<double> configuredOffset)
    {
        _exactTime = exactTime;
        _configuredOffset = configuredOffset;
    }

    /// <summary>开始采样（默认每 5 秒一次，按需落日志）。</summary>
    public void Start()
    {
        _timer = new Timer(_ => Sample("周期"), null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    /// <summary>停止采样。</summary>
    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>
    /// 采样一次：比对内核时钟与系统墙钟，算出内核时钟自身滞后 L 并累计统计。
    /// </summary>
    /// <param name="reason">采样原因（周期 / 布防 / 边界越过）；非「周期」的采样每次都落日志。</param>
    public void Sample(string reason)
    {
        try
        {
            var wall = DateTime.Now;
            var kernel = _exactTime.GetCurrentLocalDateTime();
            var offset = _configuredOffset();
            var delta = (kernel - wall).TotalSeconds;
            var lag = delta - offset;

            var isKeyPoint = !string.Equals(reason, "周期", StringComparison.Ordinal);
            double? jump = null;

            lock (_sync)
            {
                _samples++;
                if (!double.IsNaN(_lastLag) && Math.Abs(_lastLag - lag) > 1.0)
                    jump = _lastLag;
                _lastLag = lag;
                _sumLag += lag;
                if (lag < _minLag) _minLag = lag;
                if (lag > _maxLag) _maxLag = lag;

                if (isKeyPoint || _samples % 12 == 1)
                {
                    Logger.Info(
                        $"[校时] 时钟自检（{reason}）：内核时钟={kernel:HH:mm:ss.fff}，系统墙钟={wall:HH:mm:ss.fff}，" +
                        $"偏移设置={offset:F3}s，内核−墙钟={delta:+0.000;-0.000;0.000}s，" +
                        $"内核时钟自身滞后 L={lag:+0.000;-0.000;0.000}s");
                }
            }

            if (jump.HasValue)
            {
                Logger.Warn($"[校时] 时钟自检：滞后量突变 {jump.Value:F3}s → {lag:F3}s" +
                            "（内核时钟与墙钟不是常量差，NTP 时钟或时间突变保护在起作用）。");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[校时] 时钟自检采样失败：{ex.Message}");
        }
    }

    /// <summary>汇总输出（停止时调用）：L 是否为零、是否恒定。</summary>
    public void DumpSummary()
    {
        lock (_sync)
        {
            if (_samples == 0)
            {
                Logger.Info("[校时] 时钟自检汇总：本次会话没有采样。");
                return;
            }

            var mean = _sumLag / _samples;
            var verdict = Math.Abs(mean) < 0.1
                ? "→ L≈0：内核时钟与墙钟一致，偏移换算的前提成立。"
                : $"→ **内核时钟比墙钟滞后约 {mean:F1}s**：偏移换算必须补掉该量，否则算出的偏移会系统性偏小。";
            Logger.Warn($"[校时] 时钟自检汇总：采样 {_samples} 次；L 最小={_minLag:F3}s，最大={_maxLag:F3}s，" +
                        $"平均={mean:F3}s（当前 {_lastLag:F3}s）。{verdict}");
        }
    }
}

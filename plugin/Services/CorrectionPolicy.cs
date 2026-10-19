using System;
using System.Collections.Generic;

namespace BellTimeCalibration.Services;

/// <summary>
/// 校正门控策略（纯计算、无任何外部依赖，可独立测试）。
/// v0.6.0 起判据由「原始 delta + 连续 2 次同号」改为<b>残差死区</b>：
/// <list type="bullet">
/// <item><description>残差 = 当前内核偏移 − 本次测得的绝对偏移需求 delta_abs，即「实际还差多少才对齐」；</description></item>
/// <item><description>|残差| ≥ 死区 → 立即应用本次 delta_abs（一测到就纠，误差不再长期挂着）；</description></item>
/// <item><description>|残差| &lt; 死区 → 已落在对齐带内，不动作（避免追逐校铃自身抖动）。</description></item>
/// </list>
/// 旧实现用原始 delta 比死区、并要求连续 2 次同号才应用：当内核偏移陈旧（如残留 5s）或
/// 校铃偏差逐边界变号时，误差会长期不被纠正（2026-09-10 实机：内核偏移 5.22s、实际残差 4.6s，
/// 而测量的 delta 仅 0.58s，旧门控既不满足「2 次同号」也不会纠正该陈旧偏移）。
/// 历史仅记录、不参与门控，供统计日志使用（上限 20 条，超出丢最旧）。
/// 本类不做线程同步：调用方（CalibrationRunner）串行使用（同一时刻仅一个监听窗口）。
/// </summary>
public class CorrectionPolicy
{
    /// <summary>
    /// 未观测到切换时的绝对口径换算（v1.0.4）：把「相对当前开关的残差」还原为「所需绝对偏移」。
    ///
    /// <para>delta_abs 的定义是 <c>(B_display − 偏移) − 铃响</c> —— 它描述的是「当前开关还差多少才对齐」，
    /// 而不是「偏移该设成多少」。真正的绝对所需偏移是 <c>B_display − 铃响</c>，正好等于
    /// <c>delta_abs + 当前偏移</c>。漏掉后半项会整整偏一个当前偏移（偏移越大偏得越多）。</para>
    ///
    /// <para>实机 4 条实例（记录值 + 当时偏移 = 真值；旧口径偏差恰为 4.22 / 6.68 / 7.51 / 1.37 s）：
    /// 2026-09-16 18:50（−0.79 + (−4.22) = −5.01）、2026-09-17 17:10（−0.91 + (−6.68) = −7.59）、
    /// 2026-09-17 18:50（+0.29 + (−7.51) = −7.22）、2026-09-20 18:50（+0.86 + (−1.374) = −0.51）。</para>
    /// </summary>
    /// <param name="deltaAbs">相对当前开关的残差（秒）。</param>
    /// <param name="currentOffsetSeconds">当前内核偏移（秒）。</param>
    public static TimeSpan RequiredFromAbsolute(TimeSpan deltaAbs, double currentOffsetSeconds)
        => deltaAbs + TimeSpan.FromSeconds(currentOffsetSeconds);

    /// <summary>
    /// 单次写入的钳制（v1.1.0，取代 v0.10.2 起的「大误差一律拒写」）。
    ///
    /// <para>阈值 <paramref name="limitSeconds"/> 的语义由「拒写线」改成「单步上限」：目标与当前相差多少都照走，
    /// 但一次最多走 <paramref name="limitSeconds"/>，剩下的留给下一个监听窗口 ——
    /// 用户口径「合法修正仍保持 3 s，偏移量可能超过 20 s」。拒写会让一次真实的大校准永远等不到人工确认
    /// （2026-10-14 实测：8 条大误差里多数是被坏值钉住的干净测量，真正需要人工介入的几乎没有）。</para>
    ///
    /// <para>方向由 <paramref name="targetSeconds"/> − <paramref name="currentSeconds"/> 的符号决定，
    /// 因此「目标更早」和「目标更晚」走的是同一段代码；差值恰好等于上限时不算步进（一次到位）。</para>
    /// </summary>
    /// <param name="currentSeconds">当前偏移（秒）。</param>
    /// <param name="targetSeconds">本次目标偏移（秒）。</param>
    /// <param name="limitSeconds">单次写入上限（秒，≤0 视为不限）。</param>
    /// <returns>AppliedSeconds = 本次真正写入的偏移（秒）；Stepped = 是否被钳制（没走完）。</returns>
    public static (double AppliedSeconds, bool Stepped) StepTowards(
        double currentSeconds, double targetSeconds, double limitSeconds)
    {
        var diff = targetSeconds - currentSeconds;
        if (limitSeconds <= 0 || Math.Abs(diff) <= limitSeconds)
            return (targetSeconds, false);

        return (currentSeconds + Math.Sign(diff) * limitSeconds, true);
    }

    /// <summary>
    /// 写入「应用设置·时间偏移」时的统一精度（v1.1.1.0）：两位小数，四舍五入（远离零）。
    /// 手动拟合与自动应用都经过 <see cref="SettingsOffsetApplier.Apply"/>，只在这里定义一次，
    /// 避免各调用点各写一套舍入后互不一致。
    /// </summary>
    public static double RoundOffsetSeconds(double seconds)
        => Math.Round(seconds, 2, MidpointRounding.AwayFromZero);

    private const int HistoryCap = 20;

    private readonly List<TimeSpan> _history = new();
    private double _deadZoneSeconds;

    /// <summary>最近一次判定的说明（中文，供日志输出）。</summary>
    public string? LastNote { get; private set; }

    /// <summary>
    /// 构造门控策略。
    /// </summary>
    /// <param name="deadZoneSeconds">死区秒数：|残差|小于该值不动作（钳 ≥0）。</param>
    public CorrectionPolicy(double deadZoneSeconds)
    {
        DeadZoneSeconds = deadZoneSeconds;
    }

    /// <summary>
    /// 死区秒数。可在每次使用前按当前配置刷新（构造后亦可调）。
    /// </summary>
    public double DeadZoneSeconds
    {
        get => _deadZoneSeconds;
        set => _deadZoneSeconds = Math.Max(0, value);
    }

    /// <summary>最近的历史残差（含被死区拒绝的样本；升序时间）。</summary>
    public IReadOnlyList<TimeSpan> RecentHistory => _history;

    /// <summary>
    /// 残差门控：|残差| ≥ 死区时返回 true（调用方随后应用本次 delta_abs），否则 false。
    /// </summary>
    /// <param name="residual">当前内核偏移与本次测得需求之差（秒）。</param>
    /// <returns>是否应当应用本次校正。</returns>
    public bool ShouldApply(TimeSpan residual)
    {
        _history.Add(residual);
        if (_history.Count > HistoryCap)
            _history.RemoveAt(0);

        var seconds = residual.TotalSeconds;
        if (Math.Abs(seconds) >= _deadZoneSeconds)
        {
            LastNote = $"所需偏移与当前偏移相差 {seconds:F3} 秒 ≥ 死区 {_deadZoneSeconds:F1} 秒，写入";
            return true;
        }

        LastNote = $"所需偏移与当前偏移相差 {seconds:F3} 秒 < 死区 {_deadZoneSeconds:F1} 秒，已在带内，暂不写入";
        return false;
    }
}

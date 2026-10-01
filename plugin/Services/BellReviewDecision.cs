using System;

namespace BellTimeCalibration.Services;

/// <summary>
/// 复核提醒的判定参数。刻意只带基元类型（不引用任何 ClassIsland 类型）：
/// 判定逻辑因此可脱离宿主运行，供离线工具（<c>ReplayTool notify-test</c>）直接断言。
/// </summary>
public sealed class BellReviewNotifyArgs
{
    /// <summary>边界类型（中文，如「上课」「下课」）。</summary>
    public string BoundaryKind { get; init; } = "";

    /// <summary>课表边界 B_display（显示钟面时刻，供人类阅读）。</summary>
    public DateTime BoundaryDisplay { get; init; }

    /// <summary>本次要写入的偏移与当前偏移之差（秒，带符号）。</summary>
    public double ChangeSeconds { get; init; }

    /// <summary>当前偏移（秒）。</summary>
    public double CurrentOffsetSeconds { get; init; }

    /// <summary>本次测得的「所需偏移」（秒）。</summary>
    public double RequiredOffsetSeconds { get; init; }

    /// <summary>大误差复核阈值（秒）。</summary>
    public double LimitSeconds { get; init; }

    /// <summary>人工审核提醒开关（设置项 <c>EnableReviewNotification</c>）。</summary>
    public bool NotificationEnabled { get; init; } = true;

    /// <summary>兜底说明（通常是「实测误差 e=…（铃−切换）」或「未观测到切换，按绝对口径」）。</summary>
    public string Basis { get; init; } = "";
}

/// <summary>
/// 双窗口冲突提醒的参数（v1.0.4）：开关窗口与课表窗口各自测到合格起响沿、位置却不一致，
/// 且开关窗口并不明显更强 —— 此时本次**不写入**，并弹出提醒让人工确认铃况。
/// 与 <see cref="BellReviewNotifyArgs"/> 一样刻意只带基元类型（不引用任何 ClassIsland 类型）。
/// </summary>
public sealed class BellReviewConflictArgs
{
    /// <summary>边界类型（中文：上课 / 下课）。</summary>
    public string BoundaryKind { get; init; } = "";

    /// <summary>课表边界（显示域钟面时刻）。</summary>
    public DateTime BoundaryDisplay { get; init; }

    /// <summary>开关窗口测到的铃响时刻（本地；无命中时 null）。</summary>
    public DateTime? SwitchOnsetLocal { get; init; }

    /// <summary>课表窗口测到的铃响时刻（本地；无命中时 null）。</summary>
    public DateTime? ScheduleOnsetLocal { get; init; }

    /// <summary>开关窗口命中峰值（dBFS；无命中时 null）。</summary>
    public double? SwitchPeakDb { get; init; }

    /// <summary>课表窗口命中峰值（dBFS；无命中时 null）。</summary>
    public double? SchedulePeakDb { get; init; }

    /// <summary>设置中的「人工审核提醒」开关。</summary>
    public bool NotificationEnabled { get; init; } = true;
}

/// <summary>
/// 大误差复核提醒的判定（v1.0.4）：**纯函数**，不触碰任何 ClassIsland 类型，
/// 因此可被离线工具（<c>ReplayTool notify-test</c>）直接断言，不需要起宿主、不需要音频。
///
/// 判定与 <see cref="CalibrationRunner"/> 里「拒写」用的是**同一个阈值**：阈值表达的是
/// 「一次要改这么多秒就该人工看一眼」，所以只要开关打开，「拒写」与「提醒」就同时发生，
/// 不允许出现「拒写了却不提醒」或反之。
///
/// 是否重复提醒由设置项 <c>EnableReviewNotification</c> 决定，**没有时间冷却**：
/// 用户能自己关掉提醒，就不该再被一个看不见的 30 分钟窗口挡着——那只会让行为变得不可预期
/// （同一个大误差，有时弹有时不弹，而用户无从判断为什么）。
/// </summary>
public static class NotificationReviewDecision
{
    /// <summary>
    /// 判定本次是否应当弹出人工复核提醒。
    /// </summary>
    /// <param name="changeSeconds">本次要写入的偏移与当前偏移之差（秒，按绝对值比较）。</param>
    /// <param name="limitSeconds">大误差复核阈值（秒）。</param>
    /// <param name="notificationEnabled">设置中的「人工审核提醒」开关。</param>
    /// <returns>ShouldNotify = 是否弹提醒；Why = 判定说明（写入日志）。</returns>
    public static (bool ShouldNotify, string Why) Evaluate(
        double changeSeconds, double limitSeconds, bool notificationEnabled)
    {
        var change = Math.Abs(changeSeconds);

        if (!notificationEnabled)
        {
            return (false,
                $"「人工审核提醒」已在设置中关闭，本次改动量 {change:F3}s 只记日志、不弹提醒");
        }

        // 与 CalibrationRunner 的拒写判据严格一致：严格大于阈值才算大误差（等于阈值不触发）
        if (change <= limitSeconds)
            return (false, $"改动量 {change:F3}s 未超过阈值 ±{limitSeconds:F1}s，无需复核");

        return (true, $"改动量 {change:F3}s 超过阈值 ±{limitSeconds:F1}s，弹出人工复核提醒");
    }

    /// <summary>
    /// 双窗口冲突提醒的判定（v1.0.4 增补）：**纯函数**，与 <see cref="BellReviewNotifyArgs"/> 那套阈值无关 ——
    /// 冲突本身就是「两处测量各说各话」，不存在阈值问题，只由「人工审核提醒」开关决定，且**没有冷却**
    /// （同一个冲突每次都要让用户看见，行为才可预期）。
    /// </summary>
    /// <param name="notificationEnabled">设置中的「人工审核提醒」开关。</param>
    public static (bool ShouldNotify, string Why) EvaluateConflict(bool notificationEnabled)
        => notificationEnabled
            ? (true, "双窗口测量冲突，弹出人工复核提醒（无冷却）")
            : (false, "「人工审核提醒」已在设置中关闭，冲突只记日志、不弹提醒");
}

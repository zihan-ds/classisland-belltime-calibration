using System;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared.Enums;

namespace BellTimeCalibration.Services;

/// <summary>
/// 铃声出现时刻的监听调度器（里程碑 M2）。
/// 布防语义：铃声出现的边界 = 课表状态切换点。
///   - 状态=上课中(<see cref="TimeState.OnClass"/>)：下一次边界是下课，即
///     <see cref="ClassIsland.Shared.IPC.Abstractions.Services.IPublicLessonsService.OnBreakingTimeLeftTime"/>
///     到期时刻；
///   - 状态=课间(<see cref="TimeState.Breaking"/>)：下一次边界是上课，即
///     <see cref="ClassIsland.Shared.IPC.Abstractions.Services.IPublicLessonsService.OnClassLeftTime"/>
///     到期时刻；
///   - 其余状态（放学/无有效课表/预备等）不布防。
/// 所有边界时间推算一律使用宿主内核时钟 <see cref="IExactTimeService.GetCurrentLocalDateTime"/>，
/// 禁止裸用 DateTime.Now 参与边界计算。
/// </summary>
public class CalibrationScheduler : IDisposable
{
    private readonly ILessonsService _lessonsService;
    private readonly IExactTimeService _exactTimeService;

    /// <summary>当前边界是否已布防（已触发过监听窗口）。每 tick 依据剩余时间现算复位。</summary>
    private bool _armed;

    /// <summary>布防时暂存的边界类型（中文“上课/下课”），供边界越过时随 <see cref="BoundaryReached"/> 抛出。</summary>
    private string? _pendingBoundaryKind;

    /// <summary>布防时暂存的预计边界显示时刻 B_display，供边界越过时随 <see cref="BoundaryReached"/> 抛出。</summary>
    private DateTime? _pendingBoundaryDisplay;

    /// <summary>是否已订阅宿主事件（防止 Start 被重复调用导致重复订阅）。</summary>
    private bool _subscribed;

    /// <summary>
    /// 构造调度器。服务引用由 Plugin 在宿主应用启动完成后解析并注入。
    /// </summary>
    /// <param name="lessonsService">课表服务（提供倒计时属性与状态、tick 事件）。</param>
    /// <param name="exactTimeService">精确时间服务（提供宿主内核显示时钟）。</param>
    public CalibrationScheduler(ILessonsService lessonsService, IExactTimeService exactTimeService)
    {
        _lessonsService = lessonsService;
        _exactTimeService = exactTimeService;
    }

    /// <summary>
    /// 边界逼近事件：当进入某一边界的监听窗口时触发一次（同一监听窗口内只触发一次）。
    /// 本里程碑只用于推算与日志；M3 起由响铃识别服务订阅该事件开始实际监听，
    /// M4 使用推算口径 delta = B_display - t_ring（t_ring 为物理铃墙钟时刻，M3 产生；
    /// B_display 属“显示时钟域”，t_ring 属真实墙钟，二者之差即所需偏移）。
    /// </summary>
    public event EventHandler<BoundaryApproachingEventArgs>? BoundaryApproaching;

    /// <summary>
    /// 边界到达事件：仅在“已布防”的状态切换点（铃声边界实际越过）触发一次，
    /// 携带布防时暂存的边界类型与预计显示时刻 B_display。
    /// 触发后立即复位 armed 与暂存字段；未布防的状态切换不触发。
    /// 边界到达与布防均发生在宿主课表计时器的同一线程上，按顺序执行，无需跨线程同步。
    /// </summary>
    public event EventHandler<BoundaryReachedEventArgs>? BoundaryReached;

    /// <summary>
    /// 开始运行：订阅宿主主计时器 tick 与状态切换事件（状态切换用于复位 armed 标志，
    /// 保证切换后只对下一个边界布防一次）。可安全重复调用。
    /// </summary>
    public void Start()
    {
        if (_subscribed)
            return;

        _lessonsService.PostMainTimerTicked += OnMainTimerTick;
        // 状态切换点即铃声边界：切换后旧边界的 armed 标志立即失效
        _lessonsService.OnClass += OnStateSwitched;
        _lessonsService.OnBreakingTime += OnStateSwitched;
        _lessonsService.OnAfterSchool += OnStateSwitched;

        _subscribed = true;
        Logger.Info("[校时] CalibrationScheduler 已启动并订阅宿主课表事件。");
    }

    /// <summary>
    /// 停止运行：退订所有宿主事件并复位布防标志。可安全重复调用。
    /// </summary>
    public void Dispose()
    {
        if (!_subscribed)
            return;

        _lessonsService.PostMainTimerTicked -= OnMainTimerTick;
        _lessonsService.OnClass -= OnStateSwitched;
        _lessonsService.OnBreakingTime -= OnStateSwitched;
        _lessonsService.OnAfterSchool -= OnStateSwitched;

        _subscribed = false;
        _armed = false;
        _pendingBoundaryKind = null;
        _pendingBoundaryDisplay = null;
        Logger.Info("[校时] CalibrationScheduler 已停止并退订宿主课表事件。");
    }

    /// <summary>
    /// 状态切换（铃声边界到达）处理：若当前处于布防状态，则本次切换即“布防的边界被越过”，
    /// 触发一次 <see cref="BoundaryReached"/>（携带布防时暂存的 kind 与 B_display），
    /// 随后复位 armed 与暂存字段；未布防时仅做复位，不触发事件。
    /// </summary>
    private void OnStateSwitched(object? sender, EventArgs e)
    {
        try
        {
            if (_armed && _pendingBoundaryKind != null && _pendingBoundaryDisplay != null)
            {
                BoundaryReached?.Invoke(this, new BoundaryReachedEventArgs(
                    _pendingBoundaryKind, _pendingBoundaryDisplay.Value));
            }

            _armed = false;
            _pendingBoundaryKind = null;
            _pendingBoundaryDisplay = null;
        }
        catch (Exception ex)
        {
            // 静默容错：事件订阅方异常不得影响宿主事件链；armed 复位仍需执行
            _armed = false;
            _pendingBoundaryKind = null;
            _pendingBoundaryDisplay = null;
            Logger.Error($"[校时] 状态切换处理异常：{ex}");
        }
    }

    /// <summary>
    /// 宿主主计时器 tick（约每 50ms 一次）。执行本插件的布防逻辑。
    /// 全部异常就地捕获记录，绝不向宿主 tick 链抛出。
    /// </summary>
    private void OnMainTimerTick(object? sender, EventArgs e)
    {
        try
        {
            // 总开关：关闭时复位布防标志并直接返回（重新开启后按当前剩余时间重新判定）
            if (!BellTimeCalibrationPlugin.Config.IsEnabled)
            {
                _armed = false;
                _pendingBoundaryKind = null;
                _pendingBoundaryDisplay = null;
                return;
            }

            // 依据当前状态确定“下一次边界”的剩余时间与边界类型
            TimeSpan remaining;
            string boundaryKind; // 中文边界类型：“上课” / “下课”
            switch (_lessonsService.CurrentState)
            {
                case TimeState.OnClass:
                    // 上课中 → 下一次边界是下课（最后一节下课即放学铃，同样适用）
                    remaining = _lessonsService.OnBreakingTimeLeftTime;
                    boundaryKind = "下课";

                    // v0.10.3 修复：**当天最后一节课**时内核给不出下课倒计时，必须回退。
                    // 内核算法（LessonsService.cs）：nextBreakingTimeLayoutItem = 课表里第一个
                    // 「TimeType==1（下课）且 EndTime >= now」的条目；若当天之后再无下课条目（放学即最后一个时间点），
                    // 该值为 null → OnBreakingTimeLeftTime 恒为 0（被 AtLeastZero 夹住）。
                    // 于是旧实现在 remaining<=0 时清除布防并返回，**放学校铃永远不会布防**
                    // （2026-09-13 22:30 边界实测：宿主 22:30:04 正常发出放学事件，而插件整段无日志、未开麦）。
                    // 回退口径：上课状态下的 OnClassLeftTime 就是「本节课剩余」，末节课即到放学的剩余时间。
                    // 仅在本节确实有结束时刻（>0，排除时间点缺失的 00:00 占位）时采用，
                    // 以免把 OnClassLeftTime 默认的零值误当成边界。
                    if (remaining <= TimeSpan.Zero && _lessonsService.CurrentTimeLayoutItem.EndTime > TimeSpan.Zero)
                    {
                        var classLeft = _lessonsService.OnClassLeftTime;
                        if (classLeft > TimeSpan.Zero)
                        {
                            remaining = classLeft;
                        }
                    }

                    break;
                case TimeState.Breaking:
                    // 课间 → 下一次边界是上课
                    remaining = _lessonsService.OnClassLeftTime;
                    boundaryKind = "上课";
                    break;
                default:
                    // 放学(AfterSchool)/无(无有效课表)/准备上课等状态一律不布防
                    _armed = false;
                    _pendingBoundaryKind = null;
                    _pendingBoundaryDisplay = null;
                    return;
            }

            var lead = TimeSpan.FromSeconds(BellTimeCalibrationPlugin.Config.ArmedLeadSeconds);

            // 左时间 > lead 或已越界(<=0) → 清除本边界 armed 标志（新边界出现自动复位）
            if (remaining <= TimeSpan.Zero || remaining > lead)
            {
                _armed = false;
                _pendingBoundaryKind = null;
                _pendingBoundaryDisplay = null;
                return;
            }

            // 0 < 左时间 <= lead 且尚未布防 → 触发一次监听窗口并置 armed
            if (_armed)
                return;

            _armed = true;

            // 推算边界显示时刻与监听窗口（显示时钟域，基于宿主内核时钟）
            var nowDisplay = _exactTimeService.GetCurrentLocalDateTime();
            var boundaryDisplay = nowDisplay.Add(remaining);
            var windowStart = boundaryDisplay.Add(-lead);
            var windowEnd = boundaryDisplay.Add(
                TimeSpan.FromSeconds(BellTimeCalibrationPlugin.Config.WindowSeconds));

            // 忽略列表（默认 08:00 / 18:30：这两次铃声音色与已采集样本不一致、检测不到有效样本）：
            // 仍置 armed 以免每 tick 重复判定；不暂存边界信息 → 不会触发 BoundaryReached、不会开麦、不会校准。
            if (BellTimeCalibrationPlugin.Config.IsBoundaryIgnored(boundaryDisplay))
            {
                _pendingBoundaryKind = null;
                _pendingBoundaryDisplay = null;
                Logger.Info($"[校时] 边界 {boundaryDisplay:HH:mm} 在忽略列表内，本次不监听、不校准。");
                return;
            }

            // 暂存边界信息：边界越过（状态切换）时随 BoundaryReached 事件抛出
            _pendingBoundaryKind = boundaryKind;
            _pendingBoundaryDisplay = boundaryDisplay;

            Logger.Info(
                $"[校时] 进入监听窗口：边界类型={boundaryKind}，" +
                $"B_display={boundaryDisplay:HH:mm:ss.fff}，剩余={remaining.TotalSeconds:F1}秒，" +
                $"窗口=[{windowStart:HH:mm:ss.fff} ~ {windowEnd:HH:mm:ss.fff}]。");

            // 对外发布事件（M3 起响铃识别模块订阅后开始实际监听）
            BoundaryApproaching?.Invoke(this, new BoundaryApproachingEventArgs(
                boundaryKind, boundaryDisplay, remaining, windowStart, windowEnd));
        }
        catch (Exception ex)
        {
            // 静默容错：任何异常不得抛出到宿主 tick 链
            Logger.Error($"[校时] 调度 tick 处理异常：{ex}");
        }
    }
}

/// <summary>
/// 边界逼近事件参数。
/// 时间字段全部处于“显示时钟域”（即宿主内核时钟 <see cref="IExactTimeService.GetCurrentLocalDateTime"/>
/// 所表示的时钟域）；物理铃墙钟时刻 t_ring 由 M3 的响铃识别产生，
/// M4 推算偏移 delta = B_display - t_ring。
/// </summary>
public class BoundaryApproachingEventArgs : EventArgs
{
    /// <summary>边界类型（中文）：“上课” 或 “下课”。</summary>
    public string BoundaryKind { get; }

    /// <summary>预计边界显示时刻 B_display（显示时钟域）。</summary>
    public DateTime BoundaryDisplayTime { get; }

    /// <summary>触发时刻距离边界的剩余时间。</summary>
    public TimeSpan Remaining { get; }

    /// <summary>监听窗口起点（显示时钟域）= B_display - 布防提前量。</summary>
    public DateTime WindowStart { get; }

    /// <summary>监听窗口终点（显示时钟域）= B_display + 窗口时长。</summary>
    public DateTime WindowEnd { get; }

    /// <summary>构造边界逼近事件参数。</summary>
    /// <param name="boundaryKind">边界类型（中文）：“上课” 或 “下课”。</param>
    /// <param name="boundaryDisplayTime">预计边界显示时刻 B_display（显示时钟域）。</param>
    /// <param name="remaining">触发时刻距离边界的剩余时间。</param>
    /// <param name="windowStart">监听窗口起点（显示时钟域）。</param>
    /// <param name="windowEnd">监听窗口终点（显示时钟域）。</param>
    public BoundaryApproachingEventArgs(
        string boundaryKind,
        DateTime boundaryDisplayTime,
        TimeSpan remaining,
        DateTime windowStart,
        DateTime windowEnd)
    {
        BoundaryKind = boundaryKind;
        BoundaryDisplayTime = boundaryDisplayTime;
        Remaining = remaining;
        WindowStart = windowStart;
        WindowEnd = windowEnd;
    }
}

/// <summary>
/// 边界到达事件参数（M3）：当已布防的课表边界（状态切换点）实际越过时触发，
/// 携带布防时暂存的边界信息。B_display 属宿主“显示时钟域”。
/// </summary>
public class BoundaryReachedEventArgs : EventArgs
{
    /// <summary>边界类型（中文）：“上课” 或 “下课”（布防时暂存值）。</summary>
    public string BoundaryKind { get; }

    /// <summary>该边界的预计显示时刻 B_display（布防时推算并暂存，显示时钟域）。</summary>
    public DateTime BoundaryDisplayTime { get; }

    /// <summary>构造边界到达事件参数。</summary>
    /// <param name="boundaryKind">边界类型（中文）。</param>
    /// <param name="boundaryDisplayTime">预计边界显示时刻 B_display。</param>
    public BoundaryReachedEventArgs(string boundaryKind, DateTime boundaryDisplayTime)
    {
        BoundaryKind = boundaryKind;
        BoundaryDisplayTime = boundaryDisplayTime;
    }
}

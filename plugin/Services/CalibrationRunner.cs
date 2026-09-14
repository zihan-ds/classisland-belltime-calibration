using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BellTimeCalibration.Models;

namespace BellTimeCalibration.Services;

/// <summary>
/// 校准执行器（里程碑 M3+M4，v0.4.0 起单一内核通道）：订阅调度器的
/// <see cref="CalibrationScheduler.BoundaryApproaching"/> 与 <see cref="CalibrationScheduler.BoundaryReached"/>，
/// 在监听窗口内开麦捕获音频、做突发能量响铃检测，记录 t_ring 并按 delta = B_display − t_ring 输出校准记录。
/// 学习模式只记录，绝不应用偏移；自动应用（IsLearningMode=false &amp;&amp; IsAutoApplyEnabled=true）时
/// 仅经内核校时 API 通道（<see cref="KernelOffsetApplier"/>，绝对写入 delta_abs）写偏移，
/// 经 <see cref="CorrectionPolicy"/> 的**残差死区**门控（v0.6.0：残差 = 当前内核偏移 − 本次 delta，
/// 超过死区即应用，不再等「连续 2 次同号」）。
/// v0.4.0 起课表平移（ProfileShift）通道已彻底移除：任何情况下都不再修改课表档案，
/// 宿主内核未开放可写偏移 API 时自动应用停用（仅学习模式记录）。
/// 门控：仅当 Config.IsEnabled 且（IsLearningMode 或 IsAutoApplyEnabled）时开麦。
/// 每个监听窗口终结时向 calibration-history.jsonl 落一条结构化记录（含配置快照与结局），
/// 供离线调参分析（见 <see cref="CalibrationHistory"/>）。
/// </summary>
public class CalibrationRunner : IDisposable
{
    private readonly CalibrationScheduler _scheduler;
    private readonly object _sync = new();

    /// <summary>内核校时应用通道（绝对偏移；宿主内核无对应 API 时为 null/不可用）。</summary>
    private readonly IOffsetApplier? _kernelApplier;

    /// <summary>当前选中的应用通道：仅当内核可用时为内核通道，否则 null（自动应用停用、只记录）。</summary>
    private readonly IOffsetApplier? _active;

    /// <summary>校正量门控策略（跨窗口累计同号 run；串行使用，见类注释）。</summary>
    private readonly CorrectionPolicy _policy;

    /// <summary>偏移拟合器（v0.7.0）：多次测量最小二乘拟合广播铃钟的规律性走时偏差。</summary>
    private readonly DriftFitter _fitter = new();

    /// <summary>当前进行中的监听窗口；null = 空闲（上一窗口未结束时新窗口直接跳过）。</summary>
    private ActiveWindow? _window;

    /// <summary>
    /// 构造执行器并订阅调度器事件。宿主应用启动完成后由 Plugin 创建一次并持有，防止被 GC。
    /// </summary>
    /// <param name="scheduler">铃声监听调度器。</param>
    /// <param name="kernelApplier">内核校时应用通道（可为 null；不可用时自动应用停用）。</param>
    public CalibrationRunner(CalibrationScheduler scheduler, IOffsetApplier? kernelApplier)
    {
        _scheduler = scheduler;
        _kernelApplier = kernelApplier;

        // v0.4.0 起唯一应用通道为内核校时 API：可用则选中，否则无通道（绝不回退到改课表）
        _active = kernelApplier?.IsAvailable == true ? kernelApplier : null;

        _policy = new CorrectionPolicy(BellTimeCalibrationPlugin.Config.DeadZoneSeconds);

        if (_active != null)
        {
            Logger.Info(
                $"[校时] 应用通道选定：{_active.Name}（IsAvailable={_active.IsAvailable}）。{_active.StatusMessage}");
        }
        else
        {
            Logger.Warn(
                "[校时] 无可用内核校时 API 通道（宿主内核未开放 TimeOffset/OffsetSeconds 可写属性），" +
                "自动应用停用，仅学习模式记录；绝不回退到课表平移。");
        }

        _scheduler.BoundaryApproaching += OnBoundaryApproaching;
        _scheduler.BoundaryReached += OnBoundaryReached;
    }

    /// <summary>退订调度器事件。可安全重复调用。</summary>
    public void Dispose()
    {
        _scheduler.BoundaryApproaching -= OnBoundaryApproaching;
        _scheduler.BoundaryReached -= OnBoundaryReached;
    }

    /// <summary>当前监听窗口状态（仅在工作线程/UI 线程经 _sync 访问）。</summary>
    private sealed class ActiveWindow
    {
        public required string Kind { get; init; }

        /// <summary>课表边界 B_display（**显示时钟域** = 墙钟 + 内核 TimeOffsetSeconds）。</summary>
        public required DateTime BDisplay { get; init; }

        /// <summary>
        /// 本窗口开启时冻结的内核时间偏移（秒），用于把 B_display 换算回裸墙钟域。
        /// 窗口内冻结（而不是每次现读）：窗口跨越时若自动应用刚写过偏移，逐处现读会让同一次测量
        /// 前后半段落在不同钟域，产生亚秒级跳变。
        /// </summary>
        public double KernelOffsetSeconds { get; init; }

        /// <summary>窗口开启的墙钟时刻（UTC，BoundaryApproaching 触发时）。</summary>
        public DateTime StartWallUtc { get; init; }

        /// <summary>边界实际越过（BoundaryReached）的墙钟时刻（UTC）；null = 尚未越过。</summary>
        public DateTime? ReachedWallUtc { get; set; }

        /// <summary>
        /// B_display → 裸墙钟（本地）。音频时间戳（<see cref="CaptureAudio.WallTimeAt"/> 与
        /// <see cref="RingBurst.OnsetUtc"/>）都取自 <see cref="DateTime.UtcNow"/>，属裸墙钟域；
        /// B_display 却含内核偏移（内核取时间 = 墙钟 + 偏移）。两者相减前必须换算到同一域，
        /// 否则偏移每 1 s 就吃掉 1 s 的搜索余量（v0.9.1 修复：实机 15:40 曾出现
        /// 「模板命中但起响点偏差 15.18s 超出搜索半窗 ±12s」——正是 15.44 − 0.8 的错位）。
        /// </summary>
        public DateTime BWallLocal => BDisplay.AddSeconds(-KernelOffsetSeconds);
    }

    /// <summary>
    /// 边界逼近（调度器 tick 线程）：门控通过且无窗口在进行时，记录窗口信息并
    /// 异步启动一次麦克风捕获。捕获结束后在后台线程完成结果处理。
    /// </summary>
    private void OnBoundaryApproaching(object? sender, BoundaryApproachingEventArgs e)
    {
        try
        {
            var config = BellTimeCalibrationPlugin.Config;
            if (!config.IsEnabled || !(config.IsLearningMode || config.IsAutoApplyEnabled))
                return;

            lock (_sync)
            {
                if (_window != null)
                {
                    Logger.Info(
                        $"[校时] 边界（{e.BoundaryKind} {e.BoundaryDisplayTime:HH:mm:ss.fff}）逼近时上一监听窗口仍在进行，本次跳过。");

                    // 结构化历史：防重入跳过同样落一条（无捕获，EndedBy=n/a）
                    var skipped = NewRecord(
                        e.BoundaryKind,
                        e.BoundaryDisplayTime.ToString(CalibrationHistory.ClockFormat),
                        startWallUtc: null,
                        BellTimeCalibrationPlugin.Config);
                    skipped.Outcome = "skipped-busy";
                    skipped.EndedBy = "n/a";
                    CalibrationHistory.Append(skipped);
                    return;
                }

                _window = new ActiveWindow
                {
                    Kind = e.BoundaryKind,
                    BDisplay = e.BoundaryDisplayTime,
                    KernelOffsetSeconds = _active?.CurrentOffsetSeconds ?? 0,
                    StartWallUtc = DateTime.UtcNow
                };
            }

            var detector = new RingDetector(BellTimeCalibrationPlugin.Config.DetectionSensitivity);
            // fire-and-forget：内部全程 try/catch，无异常逃逸；不得阻塞调度器 tick 线程
            _ = RunWindowAsync(detector);
        }
        catch (Exception ex)
        {
            Logger.Error($"[校时] BoundaryApproaching 处理异常：{ex}");
        }
    }

    /// <summary>
    /// 边界到达（调度器 tick 线程）：与当前窗口匹配（边界类型与 B_display 相同）时记录到达墙钟时刻，
    /// 捕获循环据此在到达后继续监听 WindowSeconds 秒。
    /// </summary>
    private void OnBoundaryReached(object? sender, BoundaryReachedEventArgs e)
    {
        try
        {
            lock (_sync)
            {
                var w = _window;
                if (w != null
                    && w.Kind == e.BoundaryKind
                    && w.BDisplay == e.BoundaryDisplayTime
                    && w.ReachedWallUtc == null)
                {
                    w.ReachedWallUtc = DateTime.UtcNow;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[校时] BoundaryReached 处理异常：{ex}");
        }
    }

    /// <summary>
    /// 捕获循环体（后台线程）：执行捕获 → 关闭窗口 → 依结果记录人类日志与结构化历史。
    /// 每个终结路径恰好向 <see cref="CalibrationHistory"/> 落一条记录（含配置快照与结局），
    /// 无突发/初始化失败/异常同样落一条。
    /// </summary>
    private async Task RunWindowAsync(RingDetector detector)
    {
        // 本窗口实例：finally 兜底只清理“仍是本窗口”的 _window，绝不误伤随后新建的窗口
        ActiveWindow? window = null;
        CalibrationHistoryRecord? rec = null;
        var appended = false;
        try
        {
            var config = BellTimeCalibrationPlugin.Config;

            // 捕获前先取窗口快照（锁内）；捕获期间 ShouldContinue 仍以 _window 为准
            lock (_sync)
            {
                window = _window;
            }

            if (window == null)
                return; // 理论竞态：窗口已被关闭，无信息可记录（该窗口已由其自身路径记录）

            // 结构化记录骨架：配置快照在捕获开始前读取（≈窗口开启时刻）
            rec = NewRecord(
                window.Kind,
                window.BDisplay.ToString(CalibrationHistory.ClockFormat),
                window.StartWallUtc,
                config);

            var hardTimeout = TimeSpan.FromSeconds(
                config.ArmedLeadSeconds + config.WindowSeconds + 15);

            // v0.9.0：启用模板匹配时保留窗口音频（仅内存），供模板匹配定位起响点
            var keepAudio = config.EnableTemplateMatch && TemplateLibrary.Templates.Count > 0;
            var result = await MicCaptureService.CaptureAsync(detector, ShouldContinue, hardTimeout, keepAudio);

            // 捕获结束：关闭窗口（允许下一边界布防）；实例安全，避免误关随后新建的窗口
            lock (_sync)
            {
                if (_window == window)
                    _window = null;
            }

            // 路径 a：捕获初始化失败（无默认设备/设备被占用/引擎异常等）
            if (result == null)
            {
                rec.Outcome = "init-failed";
                rec.EndedBy = "n/a";
                CalibrationHistory.Append(rec);
                appended = true;

                Logger.Warn(
                    $"[校时] 边界（{window.Kind} {window.BDisplay:HH:mm:ss.fff}）麦克风捕获初始化失败，本次监听作废（详见上方错误日志）。");
                return;
            }

            // 调试音频转存（默认关闭）：开启时把本窗口音频写成 WAV，供离线核对实机铃声、重建模板
            if (config.DebugDumpAudio && result.Audio is { Length: > 0 })
                DumpWindowAudio(result.Audio, window);

            // ── 路径 a2：整窗静默（v0.10.4）──
            // 麦克风偶发未进音时，音频是纯数字静默；此时任何「比值」类判据都会变成噪声除噪声，
            // 凑出假的起响沿（实机 2026-09-14 09:40：噪声底 −80 dBFS、峰值 −114.5 dBFS，
            // 却被判为 detected、还写进了历史）。这类窗口必须在入口就作废，绝不让它进入任何判据或统计。
            // 判据用「本窗峰值 RMS」这一绝对量，与铃声实测电平（−6.8~−34.4 dBFS）之间留 ≥16 dB 余量。
            if (result.Audio is { Length: > 0 } silentAudio)
            {
                double peakSum = 0;
                for (var i = 0; i < silentAudio.Length; i++)
                    peakSum += (double)silentAudio.Samples[i] * silentAudio.Samples[i];
                var windowPeakRms = Math.Sqrt(peakSum / silentAudio.Length);

                if (windowPeakRms < OnsetGate.AbsoluteMinRms)
                {
                    rec.Outcome = "silent-window";
                    rec.EndedBy = "n/a";
                    rec.CaptureSec = Math.Round(result.CaptureDuration.TotalSeconds, 3);
                    rec.NoiseFloorDb = result.NoiseFloorDb;
                    rec.PeakDb = result.MaxRmsDb;
                    CalibrationHistory.Append(rec);
                    appended = true;

                    Logger.Warn(
                        $"[校时] 整窗静默（本窗峰值 {20 * Math.Log10(Math.Max(windowPeakRms, 1e-12)):F1} dBFS，" +
                        $"低于下限 {20 * Math.Log10(OnsetGate.AbsoluteMinRms):F1} dBFS）→ 判为麦克风未进音，" +
                        $"边界（{window.Kind} {window.BDisplay:HH:mm:ss.fff}）本次作废，不参与任何判据与统计。");
                    return;
                }
            }

            // ── 候选静置过滤 + 有效铃选择（v0.3.0 检测硬化）──
            // 原始全量候选（含开麦静置期伪影）写入结构化历史，便于离线复核判定过程；
            // 有效铃判定只在静置过滤后的候选上进行。
            if (result.Candidates.Count > 0)
            {
                var rawCandidatesLocal = new List<string>(result.Candidates.Count);
                foreach (var c in result.Candidates)
                {
                    rawCandidatesLocal.Add(c.ToLocalTime().ToString(CalibrationHistory.LocalFormat));
                }

                rec.Candidates = rawCandidatesLocal;
            }

            var postSettle = RingCandidateSelector.ExcludeSettlePeriod(result.Bursts, window.StartWallUtc);

            // 搜索半窗取配置项 ToleranceSeconds（需覆盖校铃钟漂移幅度，默认 12s）
            var tolerance = TimeSpan.FromSeconds(config.ToleranceSeconds);

            // ── 钟域统一（v0.9.1）──
            // 音频时间戳与候选起响点全部来自 DateTime.UtcNow（裸墙钟域），而 B_display 含内核偏移。
            // 下面所有与音频比较的量（搜索区间、模板偏差判定、delta）一律使用 B_wall = B_display − 偏移；
            // 只有「历史/日志里对人类展示的边界时刻」和忽略列表匹配仍用 B_display。
            var boundaryWallLocal = window.BWallLocal;

            // ── 主路径（v0.10.0，路线 2）：起响沿闸门 ──
            // 在「边界 ±OnsetGate.GateSeconds」内找起响比值最大处，一步完成两件事：
            // ① 排除提前 6~15 s 的那次强起响（实测主铃稳定落在边界 ±0.6 s 内）；
            // ② 排除渐强噪声（实测伪匹配绝对电平甚至比真铃更响，但起响比值只有 1~3×）。
            // 不依赖波形对齐 —— 实测同一「下课铃」在各边界的波形互相关仅 0.08~0.64，
            // 精确模板匹配跨边界 0 命中（详见 plugin/tools/README.md 验收表与 bell-recordings/对照记录.md）。
            OnsetGate.OnsetHit? gateHit = null;
            string gateNote = "";
            if (result.Audio != null && result.Audio.Length > 0)
            {
                gateHit = OnsetGate.Find(result.Audio, boundaryWallLocal, out gateNote);
                Logger.Info(gateHit != null
                    ? $"[校时] 起响沿闸门命中：{gateNote}"
                    : $"[校时] 起响沿闸门未命中：{gateNote}");
            }

            // ── 辅助路径：模板匹配（闸门未命中时才尝试，命中则省一次全区间扫描）──
            // 模板匹配保留但降级：它在个别窗口能给出毫秒级起响点（9/11 21:30 下课铃 NCC 0.968），
            // 但跨边界不稳定，故不作主判据。
            DateTime? templateOnsetUtc = null;
            string templateNote = "";
            if (gateHit == null && keepAudio && result.Audio != null)
            {
                var audio = result.Audio;
                var settleSample = (int)(RingCandidateSelector.SettleSkipSeconds * CaptureAudio.SampleRate);
                var boundaryUtc = boundaryWallLocal.ToUniversalTime();
                var fromSample = Math.Max(settleSample, audio.SampleIndexAt(boundaryUtc - tolerance));
                var toSample = Math.Min(audio.Length, audio.SampleIndexAt(boundaryUtc + tolerance));

                var match = fromSample < toSample
                    ? RingCandidateSelector.SelectByTemplate(
                        TemplateLibrary.Templates, audio, fromSample, toSample,
                        boundaryWallLocal, tolerance, config.MatchNccMin, config.MatchSpectralMin,
                        out templateNote)
                    : null;
                if (fromSample >= toSample)
                    templateNote = "搜索区间无效（音频缓冲不足）";

                if (match != null)
                {
                    templateOnsetUtc = audio.WallTimeAt(match.Value.OnsetSampleIndex);
                    rec.MatchedTemplate = match.Value.Label;
                    rec.MatchNcc = Math.Round(match.Value.Ncc, 4);
                    rec.MatchSpectral = Math.Round(match.Value.SpectralSim, 4);
                    Logger.Info($"[校时] 模板匹配命中（{templateNote}）。");
                }
                else
                {
                    Logger.Info($"[校时] 模板匹配未命中（{templateNote}）。");
                }
            }

            // 路径 b：静置过滤后无任何候选段，且闸门与模板都没命中 → 无突发能量
            if (postSettle.Count == 0 && templateOnsetUtc == null && gateHit == null)
            {
                rec.Outcome = "no-burst";
                rec.EndedBy = result.EndedBy;
                rec.CaptureSec = Math.Round(result.CaptureDuration.TotalSeconds, 3);
                rec.NoiseFloorDb = result.NoiseFloorDb;
                rec.PeakDb = result.MaxRmsDb;
                CalibrationHistory.Append(rec);
                appended = true;

                var artifactNote = result.Candidates.Count > 0
                    ? $"（原始候选 {result.Candidates.Count} 个均落在开麦静置 {RingCandidateSelector.SettleSkipSeconds:F1}s 内，已忽略）"
                    : "";
                Logger.Info(
                    $"[校时] 窗口内未检测到突发能量（噪声底 {result.NoiseFloorDb:F1} dBFS），" +
                    $"边界（{window.Kind} B_display={window.BDisplay:HH:mm:ss.fff}）本次作废。{artifactNote}");
                return;
            }

            // 路径 c：闸门与模板都没找到铃声 → 本次不作校准，**绝不回退启发式**
            // v0.10.2：启发式选铃（最长/最响持续段）已实测确认不可靠，彻底移除：
            // 08:10 → e=−7.8 s、14:50 → −6.2 s、11:20 → +8.1 s、10:30 → +4.9 s（全部是选错事件）。
            // 用户要求「较大误差时坚决不用脏值和已被确认不准的办法」——
            // 本插件的输出要么是可信测量（起响沿闸门 / 模板匹配，二者都带独立的真伪判据），
            // 要么是「无可靠测量」，不存在中间态。日志与结构化历史同样只记录可信结果。
            if (gateHit == null && templateOnsetUtc == null)
            {
                rec.Outcome = keepAudio ? "no-trusted-match" : "no-ring";
                rec.EndedBy = result.EndedBy;
                rec.CaptureSec = Math.Round(result.CaptureDuration.TotalSeconds, 3);
                rec.NoiseFloorDb = result.NoiseFloorDb;
                rec.PeakDb = result.MaxRmsDb;
                // TRing/DeltaSec/ShiftSec/Gate 保持 null：无可信测量即不作任何推算与应用
                CalibrationHistory.Append(rec);
                appended = true;

                var templatePart = keepAudio ? $"；模板：{templateNote}" : "";
                Logger.Info(
                    $"[校时] 无可信测量（{rec.Outcome}）：闸门：{gateNote}{templatePart}；" +
                    "本次作废，不应用（已移除启发式兜底：其实测误差可达 −7.8~+8.1 s，属已确认不准的办法）。");
                return;
            }

            // 路径 d：检测到可信测量 —— t_ring 优先级：起响沿闸门 > 模板匹配
            // （两者都不会走到这里为 null，故无需空值容忍）
            var tRingUtc = gateHit?.OnsetUtc ?? templateOnsetUtc!.Value;
            var tRingLocal = tRingUtc.ToLocalTime();
            // delta 与 t_ring 同域（裸墙钟）相减：delta = B_wall − t_ring，正值 = 铃早于边界
            var delta = boundaryWallLocal - tRingLocal;
            var windowDuration = (DateTime.UtcNow - window.StartWallUtc).TotalSeconds;
            var sourceNote = gateHit != null
                ? $"起响沿闸门（{gateNote}）"
                : $"模板匹配命中（{templateNote}）";

            Logger.Info(
                $"[校时] 检测到响铃：边界类型={window.Kind}，" +
                $"课表边界 B_display={window.BDisplay:HH:mm:ss.fff}，" +
                $"物理响铃 t_ring={tRingLocal:HH:mm:ss.fff}，" +
                $"delta={(double)delta.TotalSeconds:F3} 秒，" +
                $"({sourceNote}，起响点偏差 {Math.Abs(delta.TotalSeconds):F3} 秒，" +
                $"搜索半窗 ±{config.ToleranceSeconds:F0} 秒)，" +
                $"监听时长={windowDuration:F1} 秒，" +
                $"噪声底={result.NoiseFloorDb:F1} dBFS，峰值={result.MaxRmsDb:F1} dBFS，" +
                $"灵敏度={config.DetectionSensitivity:F1}×");

            // ── 结构化历史：检测字段（Candidates 已在上方写入原始全量）──
            rec.Outcome = "detected";
            rec.EndedBy = result.EndedBy;
            rec.CaptureSec = Math.Round(result.CaptureDuration.TotalSeconds, 3);
            rec.NoiseFloorDb = result.NoiseFloorDb;
            rec.PeakDb = result.MaxRmsDb;
            rec.TRing = tRingLocal.ToString(CalibrationHistory.LocalFormat);
            rec.DeltaSec = Math.Round(delta.TotalSeconds, 3);

            // ReachedWall 跨线程读写经 _sync（UI tick 线程写入）；有候选且有越界时补时序字段
            DateTime? reachedUtc;
            lock (_sync)
            {
                reachedUtc = window.ReachedWallUtc;
            }

            if (reachedUtc != null)
            {
                rec.ReachedWall = reachedUtc.Value.ToLocalTime().ToString(CalibrationHistory.LocalFormat);
                rec.LeadActualSec = Math.Round((reachedUtc.Value - window.StartWallUtc).TotalSeconds, 3);
                var offset = (tRingUtc - reachedUtc.Value).TotalSeconds;
                rec.OffsetFromReachedSec = Math.Round(offset, 3);
                rec.ShiftSec = Math.Round(offset, 3);
            }

            // 应用管线（v0.4.0 起仅内核通道；内部把 Gate/GateNote/Channel 写进 rec）
            // v0.6.1+：把「实测切换墙钟」与「实测铃响起响点」一并交给应用管线，用真实误差 e = t_ring − t_switch 做判据。
            // v0.10.2：走到这里的测量**必然来自起响沿闸门或模板匹配**（都带独立真伪判据），
            // 启发式兜底已移除，因此应用管线不再需要「来源可信度」参数。
            TryApplyCalibration(delta, tRingUtc, reachedUtc, config, rec);

            CalibrationHistory.Append(rec);
            appended = true;
        }
        catch (Exception ex)
        {
            // 路径 d：外层异常兜底——同样落一条（rec 未落过才补落）
            if (!appended && rec != null)
            {
                rec.Outcome = "error";
                rec.EndedBy = "n/a";
                CalibrationHistory.Append(rec);
            }

            Logger.Error($"[校时] 监听窗口处理异常：{ex}");
        }
        finally
        {
            // 兜底：确保本窗口最终被清理，同时不误伤后续新建的窗口。
            // - window != null：本窗口已在正常路径清理过；仅当 _window 仍是本窗口时才再清
            //   （若期间新窗口已创建则保留，避免误杀）。
            // - window == null：异常发生在清理段之前，_window 只能是本窗口自己
            //   （新窗口在旧窗口清理前无法通过防重入门控创建），直接清空即可。
            lock (_sync)
            {
                if (window == null)
                    _window = null;
                else if (_window == window)
                    _window = null;
            }
        }
    }

    /// <summary>
    /// 大误差复核阈值（秒，v0.10.2）：即便测量来自可信来源（闸门/模板），
    /// 若本次算出的「所需偏移」与当前偏移之差超过该值，也**先拒绝写入并告警**。
    ///
    /// 依据：收敛状态下实测误差的观测范围是 −1.97~+1.23 s；出现更大的值通常意味着
    /// 校铃钟被人工大规模校准过、或系统时钟发生跳变 —— 两种情况都值得人工看一眼，
    /// 而不是让插件静静地把偏移改掉几秒。用户要求：**遇到较大误差时坚决不用脏值**。
    ///
    /// 注意这**不是**抑制正常的首次收敛：闸门/模板都是可信来源，其 |e| 落在 ±2 s 内时照常写入。
    /// </summary>
    public const double LargeErrorLimitSeconds = 3.0;

    /// <summary>
    /// 应用管线（捕获完成的后台线程，每个窗口最多执行一次；v0.4.0 起仅内核通道）：
    /// v0.6.1 口径 = 以**实测误差** e = t_ring（铃响墙钟）− t_switch（宿主真正切换的墙钟）为判据，
    /// 二者同为墙钟，任何未知的偏移来源（内核额外偏移/手工调整/时钟偏差）都自动抵消；
    /// |e| ≥ 死区即校正，校正量为**相对更新** 当前偏移 − e（不依赖 delta 与偏移的换算假设）。
    /// 未观测到切换时刻（BoundaryReached 丢失）时退化为 v0.6.0 的绝对口径 delta_abs。
    /// 各返回路径把 Gate/GateNote/Channel 写进结构化历史记录 <paramref name="rec"/>。
    /// v0.10.2：走到这里的测量必然来自起响沿闸门或模板匹配（启发式兜底已移除），
    /// 故不再需要「来源可信度」参数；另加 <see cref="LargeErrorLimitSeconds"/> 复核。
    /// </summary>
    private void TryApplyCalibration(
        TimeSpan deltaAbs,
        DateTime tRingUtc,
        DateTime? switchWallUtc,
        BellCalibrationSettings config,
        CalibrationHistoryRecord rec)
    {
        try
        {
            if (config.IsLearningMode)
            {
                rec.Gate = "skipped";
                rec.GateNote = "learning-mode";
                Logger.Info("[校时] 学习模式：仅记录，本次不应用偏移。");
                return;
            }

            if (!config.IsAutoApplyEnabled)
            {
                rec.Gate = "skipped";
                rec.GateNote = "auto-apply-off";
                Logger.Info("[校时] 未启用自动应用：本次仅记录。");
                return;
            }

            if (_active == null)
            {
                rec.Gate = "skipped";
                rec.GateNote = "no-channel";
                Logger.Warn("[校时] 无可用内核校时 API 通道，本次仅记录（绝不改课表）。");
                return;
            }

            // 唯一通道：写入「应用设置 → 时钟 → 时间偏移」
            rec.Channel = "settings";

            // 死区每次窗口按当前配置刷新（用户改设置后无需重启即生效）
            _policy.DeadZoneSeconds = config.DeadZoneSeconds;

            var currentOffset = _active.CurrentOffsetSeconds ?? 0;

            // 本次「让误差归零所需的绝对偏移」：实测误差 e 直接给出 —— 所需偏移 = 当前偏移 − e
            TimeSpan required;
            string basis;

            if (switchWallUtc != null)
            {
                // 实测误差：正 = 铃晚于切换（切换偏早），负 = 铃早于切换（切换偏晚）
                var e = tRingUtc - switchWallUtc.Value;
                required = TimeSpan.FromSeconds(currentOffset - e.TotalSeconds);
                basis = $"实测误差 e={e.TotalSeconds:F3}s（铃−切换）→ 本次所需偏移 {required.TotalSeconds:F3}s";
            }
            else
            {
                // 退化：未观测到切换 → 绝对口径（delta_abs 即所需总偏移）
                required = deltaAbs;
                basis = $"未观测到切换，按绝对口径 → 本次所需偏移 {required.TotalSeconds:F3}s";
            }

            // 偏移估计（v0.11.0）：写「近期样本中位数」；趋势外推仅在门槛满足时启用（见 DriftFitter 类注释）
            _fitter.Add(tRingUtc.ToLocalTime(), required);
            var predictedSeconds = _fitter.PredictSeconds(DateTime.Now) ?? required.TotalSeconds;
            var target = TimeSpan.FromSeconds(predictedSeconds);
            var diff = target - TimeSpan.FromSeconds(currentOffset);
            var shouldApply = _policy.ShouldApply(diff);

            Logger.Info(
                $"[校时] 拟合判定：{basis}；当前偏移 {currentOffset:F3}s；{_fitter.LastNote}；" +
                $"预测当前所需偏移 {target.TotalSeconds:F3}s（与当前差 {diff.TotalSeconds:F3}s，" +
                $"样本离散度 {_fitter.MaxDeviationSeconds:F3}s）；{_policy.LastNote}");

            if (!shouldApply)
            {
                rec.Gate = "skipped";
                rec.GateNote = $"{_policy.LastNote}；样本 {_fitter.SampleCount} 个";
                return;
            }

            // 大误差复核（v0.10.2）：即便来源可信，一次要改 3 s 以上也值得人工看一眼再动手。
            // 用户要求「遇到较大误差时坚决不用脏值」——这里拒绝写入并把原因写进历史与日志。
            var changeSeconds = Math.Abs(diff.TotalSeconds);
            if (changeSeconds > LargeErrorLimitSeconds)
            {
                rec.Gate = "skipped";
                rec.GateNote = $"large-error（需改 {diff.TotalSeconds:F3}s ＞ ±{LargeErrorLimitSeconds:F1}s，已拒写）";
                Logger.Warn(
                    $"[校时] 大误差复核：本次要把偏移从 {currentOffset:F3}s 改到 {target.TotalSeconds:F3}s" +
                    $"（差 {diff.TotalSeconds:F3}s，超过 ±{LargeErrorLimitSeconds:F1}s），**拒绝写入**。" +
                    "常见原因：校铃钟被人工大规模校准、系统时钟跳变、或信号源切换。请人工确认后再决定是否放行。");
                return;
            }

            rec.Gate = "applied";
            rec.GateNote = $"{_policy.LastNote}；{_fitter.LastNote}";
            _active.Apply(target);
        }
        catch (Exception ex)
        {
            rec.Gate = "skipped";
            rec.GateNote = "exception";
            Logger.Error($"[校时] 应用管线异常：{ex}");
        }
    }

    /// <summary>
    /// 调试音频转存：把本窗口音频写成 WAV（&lt;插件配置目录&gt;\Dumps\）；仅在配置开启时调用。
    /// </summary>
    private static void DumpWindowAudio(CaptureAudio audio, ActiveWindow window)
    {
        try
        {
            var folder = Logger.ConfigFolder;
            if (string.IsNullOrEmpty(folder))
                return;

            var name = $"{window.StartWallUtc.ToLocalTime():yyyyMMdd-HHmmss}-{window.Kind}-B{window.BDisplay:HHmmss}.wav";
            var path = System.IO.Path.Combine(folder, "Dumps", name);
            if (audio.SaveWav(path, out var note))
                Logger.Info($"[校时] 调试音频转存：{path}（{note}）");
            else
                Logger.Warn($"[校时] 调试音频转存失败：{note}");
        }
        catch (Exception ex)
        {
            Logger.Warn($"[校时] 调试音频转存异常：{ex.Message}");
        }
    }

    /// <summary>
    /// 构造结构化历史记录骨架：协议版本、写入时刻、边界信息、窗口开启墙钟与配置快照。
    /// Outcome/EndedBy 等结局字段由各终结路径补填。
    /// </summary>
    /// <param name="kind">边界类型（中文）。</param>
    /// <param name="bDisplayClock">B_display 钟面时刻（"HH:mm:ss.fff"；可为 null）。</param>
    /// <param name="startWallUtc">窗口开启墙钟（UTC；可为 null，如防重入跳过时无窗口）。</param>
    /// <param name="config">配置快照来源。</param>
    private static CalibrationHistoryRecord NewRecord(
        string kind,
        string? bDisplayClock,
        DateTime? startWallUtc,
        BellCalibrationSettings config)
    {
        return new CalibrationHistoryRecord
        {
            Ts = DateTime.Now.ToString(CalibrationHistory.LocalFormat),
            Kind = kind,
            BDisplay = bDisplayClock,
            StartWall = startWallUtc?.ToLocalTime().ToString(CalibrationHistory.LocalFormat) ?? "",
            LeadSec = config.ArmedLeadSeconds,
            WindowSec = config.WindowSeconds,
            ToleranceSec = config.ToleranceSeconds,
            Sensitivity = config.DetectionSensitivity,
            DeadZoneSec = config.DeadZoneSeconds,
            Learn = config.IsLearningMode,
            AutoApply = config.IsAutoApplyEnabled,
            ConfigEnabled = config.IsEnabled
        };
    }

    /// <summary>
    /// 捕获轮询条件（约每 200ms 由捕获线程调用）：
    /// 窗口已关闭 → 停止；边界未到达 → 继续；边界到达后继续监听 Config.WindowSeconds 秒。
    /// </summary>
    private bool ShouldContinue()
    {
        lock (_sync)
        {
            var w = _window;
            if (w == null)
                return false;
            if (w.ReachedWallUtc == null)
                return true;
            return (DateTime.UtcNow - w.ReachedWallUtc.Value).TotalSeconds
                   < BellTimeCalibrationPlugin.Config.WindowSeconds;
        }
    }
}

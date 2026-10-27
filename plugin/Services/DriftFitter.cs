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
/// <item><description><b>离群点不参与、但不清空样本段</b>（v1.1.0）：单个样本与稳健中位数相差
/// &gt; <see cref="OutlierRejectSeconds"/> 秒时判为离群，<b>不入列</b>并置 <see cref="LastSampleWasOutlier"/>
/// （由调用方决定拒写）；连续两次「彼此一致、但都远离旧段」的测量才认定基准真跳变、重开样本段。
/// <para>为什么改：v1.0.5 用 <c>JumpResetSeconds=2.5</c> 一超差就 <c>_samples.Clear()</c>，
/// 10-14 实测 10 次写入里 8 次退回「样本 1 个」的单点直写（中位数退化 = 没有稳健性），
/// 而其中多数是干净测量被前面的坏值钉住。</para></description></item>
/// <item><description><b>趋势外推默认关闭</b>，仅在「样本数 ≥ <see cref="TrendMinSamples"/>、
/// 时间跨度 ≥ <see cref="TrendMinSpanHours"/> 小时、且拟合残差 &lt; <see cref="TrendMaxResidualSeconds"/> 秒」
/// 三项同时成立时启用；斜率仍钳制在 ±<see cref="MaxSlopeSecondsPerHour"/>。
/// 门槛的含义：只有噪声被平均到足以看出真实走时之后，才允许追趋势。</description></item>
/// </list>
/// 不做线程同步的说明已作废（v1.0.4）：手动拟合（设置页按钮）会在 UI 线程上
/// <see cref="Seed"/> 一份新序列，而捕获线程可能正在 <see cref="Add"/>，
/// 因此所有公开成员与 <see cref="Recompute"/> 都由 <see cref="_sync"/> 串行化 ——
/// 「边采集边点按钮」绝不允许读出半更新的中间态。
/// </summary>
public sealed class DriftFitter
{
    /// <summary>参与估计的最大样本数（滚动窗口，超出丢最旧）。</summary>
    public const int MaxSamples = 40;

    /// <summary>首选样本窗口（小时）：只取最近这段时间内的样本。</summary>
    public const double WindowHours = 6.0;

    /// <summary>样本窗口内至少要有这么多个样本，否则放宽到全部样本。</summary>
    public const int MinSamplesForWindow = 3;

    /// <summary>
    /// 离群判定阈值（秒，v1.1.0 取代 <c>JumpResetSeconds</c>）：样本与当前稳健中位数之差超过该值时
    /// 判为离群 —— 不入列、并置 <see cref="LastSampleWasOutlier"/>，但**不清空样本段**。
    /// 取 2.0 s 的依据：判据（起响沿闸门）实测散布 ±1.5 s，2.0 s 以外已经不是「同一次测量」的抖动范围。
    /// </summary>
    public const double OutlierRejectSeconds = 2.0;

    /// <summary>认定基准跳变（人工校准/换信号源）所需的**连续**离群次数（v1.1.0）。</summary>
    public const int SegmentRestartAgreements = 2;

    /// <summary>上述连续两次离群样本彼此之间允许的最大差（秒）：它们要指向同一个新基准才算数。</summary>
    public const double SegmentRestartAgreementSeconds = 1.0;

    /// <summary>
    /// 双峰判定门槛（秒，v1.1.2.0）：样本分簇后两簇中心相距超过该值，判为「两个声源/两个基准同时在列」。
    ///
    /// 为什么需要（2026-10-26 全天实测）：稳健中位数在**双峰**样本上会给出一个既非真值、
    /// 也不对应任何实际状态的中间值，而 <see cref="OutlierRejectSeconds"/> 离群门以这个中间值为中心，
    /// 于是同时**拒真纳假**。当天 14:40 的实例：4 个参与样本 −4.072 / −3.220 / +7.285 / +7.912，
    /// 中位数 = (−3.220 + 7.285)/2 = **2.033**；正确的 6.052 距中心 4.019 s 被判离群拒写，
    /// 错误的 0.583 距中心 1.450 s 被接受，偏移遂从 7.60 被拖到 4.60，此后 13 次连续拒写真值、
    /// 段永不重开（两条错值彼此差 &gt; <see cref="SegmentRestartAgreementSeconds"/>，凑不成跳变确认）。
    ///
    /// 4.0 s 的依据：单声源时全天「所需偏移」跨度实测 0.6~1.8 s（10-07/10-22/10-23/10-24），
    /// 双声源时达 12~16 s；取 4.0 s 既远离正常散布，又能在两簇只差 5~9 s 时及时拦住。
    /// </summary>
    public const double BimodalGapSeconds = 4.0;

    /// <summary>
    /// 判定「双峰」所需的每簇最少样本数（v1.1.2.0）：只有 1 个样本的孤立簇不算，避免一个脏值就把整段冻结。
    /// </summary>
    public const int BimodalMinPerCluster = 2;

    /// <summary>
    /// 基准跳变确认是否必须至少有一条模板命中的测量（v1.1.2.0）。
    ///
    /// 为什么需要（2026-10-26 上午实测）：09:40 下课测得 +7.971、10:10 上课测得 +7.912，
    /// 两条**都是闸门选错了事件**得到的错值，但因为彼此只差 0.059 s（＜
    /// <see cref="SegmentRestartAgreementSeconds"/> 1.0 s）而互相印证，触发「基准跳变确认」重开样本段，
    /// 于是把偏移从 −3.10 直接改到 −0.10、再改到 +2.90。错值互相印证是最危险的一种伪一致：
    /// 它们共享同一个系统性错误（同一个声源选错），一致并不增加可信度。
    ///
    /// 模板命中是这里唯一「独立于事件选取」的证据源 —— 它回答「这段波形就是我们的铃」，
    /// 而不是「窗内哪一下最像起响」。要求至少一条来自模板，等于要求跳变确认不能只靠闸门自证。
    /// 代价：模板命中率约一半，真实的大幅人工校准可能需要多等几个窗口才被确认；
    /// 这比「两条错值把偏移拽飞」可接受得多（用户口径：遇到较大误差时坚决不用脏值）。
    /// </summary>
    public const bool SegmentRestartRequiresTemplate = true;

    /// <summary>启用趋势外推所需的最少样本数（v1.1.0：12 → 6，让「当天就能启用」成为常态）。</summary>
    public const int TrendMinSamples = 6;

    /// <summary>启用趋势外推所需的最小时间跨度（小时，v1.1.0：6 → 4）。</summary>
    public const double TrendMinSpanHours = 4.0;

    /// <summary>启用趋势外推允许的最大拟合残差（秒，v1.1.0：0.3 → 0.6）：残差大于该值说明散布仍以噪声为主，不追趋势。</summary>
    public const double TrendMaxResidualSeconds = 0.6;

    /// <summary>斜率上限（秒/小时）：钳制以抑制噪声样本导致的虚假走时速率。</summary>
    public const double MaxSlopeSecondsPerHour = 2.0;

    /// <summary>趋势启用时的最大外推时长（小时，v1.1.0：2 → 24）：只做温和外推，钳制与外推挂钩，
    /// 把「过夜后第一窗」也纳入（旧值 2 h 让跨夜预测直接退回中位数，白丢趋势）。</summary>
    public const double MaxExtrapolationHours = 24.0;

    /// <summary>批次导入时的默认时间间隔（秒）：历史文件只存时刻与所需偏移，
    /// 导入时按此间隔递增，避免「同一时刻多个样本」把时间跨度算成 0（仅影响趋势门槛判定）。</summary>
    private const double ImportedSampleSpacingSeconds = 1.0;

    private readonly List<(DateTime At, double OffsetSec)> _samples = new();

    /// <summary>
    /// 待确认的「新基准」候选（v1.1.0）：第一条离群样本记在这里，若下一条测量与它一致
    /// （差值 ≤ <see cref="SegmentRestartAgreementSeconds"/>）则认定基准跳变、以这两条重开样本段；
    /// 否则被后续正常测量冲掉。v1.1.2.0 起带 <c>TemplateConfirmed</c> 标记（见
    /// <see cref="SegmentRestartRequiresTemplate"/>）。
    /// </summary>
    private (DateTime At, double Sec, bool TemplateConfirmed)? _pending;

    /// <summary>
    /// 串行化所有样本读写（v1.0.4）：手动拟合走 UI 线程，校准走捕获线程，
    /// 两者可能同时碰到这份序列。锁只保护本类内部状态，不跨类调用，不会造成死锁。
    /// </summary>
    private readonly object _sync = new();

    /// <summary>
    /// 用历史样本预置拟合器（v1.0.1，供进程重启后恢复**当天**序列）。
    /// 与逐条 <see cref="Add"/> 的区别：**不做跳变检测**——历史样本本就同属一个基准，
    /// 逐条跑跳变判定会把正常的时间漂移误判成「人工校准」而清空序列。
    ///
    /// v1.0.5：**先清空已有样本**。「预置」在语义上就是「用这批历史替换当前序列」，
    /// 而原来的实现是追加 —— 手动拟合后 <c>ReloadTodaySamples()</c> 会把同一批当天样本再灌一遍，
    /// 早期样本被计两次、40 条上限更早挤掉真样本（2026-10-07 实测：15:40 的 11 个样本
    /// 在 16:20 变成 23 个 = 11 + 11 + 1）。
    /// </summary>
    /// <param name="history">历史样本（按时间升序）。</param>
    /// <returns>实际导入的样本数。</returns>
    public int Seed(IEnumerable<(DateTime At, double RequiredSec)> history)
    {
        lock (_sync)
        {
            _samples.Clear();
            _pending = null;
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
                // 锁内直读字段：属性 getter 会再取同一把锁（可重入，但没必要）
                var noteBefore = _lastNote;
                Recompute();
                LastNote = $"已从当天历史恢复 {count} 个样本；{_lastNote ?? noteBefore}";
            }

            return count;
        }
    }

    /// <summary>当前参与估计的样本数。</summary>
    public int SampleCount
    {
        get { lock (_sync) { return _samples.Count; } }
    }

    private double? _medianSeconds;
    /// <summary>近期样本的中位数（秒）；无样本为 null。</summary>
    public double? MedianSeconds
    {
        get { lock (_sync) { return _medianSeconds; } }
        private set => _medianSeconds = value;
    }

    private double? _maxDeviationSeconds;
    /// <summary>近期样本相对中位数的最大偏差（秒）；无样本为 null。用于判断散度。</summary>
    public double? MaxDeviationSeconds
    {
        get { lock (_sync) { return _maxDeviationSeconds; } }
        private set => _maxDeviationSeconds = value;
    }

    private bool _trendEnabled;
    /// <summary>趋势是否已启用（样本数、跨度、残差三项门槛同时满足）。</summary>
    public bool TrendEnabled
    {
        get { lock (_sync) { return _trendEnabled; } }
        private set => _trendEnabled = value;
    }

    private bool _isBimodal;
    /// <summary>
    /// 当前样本是否呈双峰（v1.1.2.0，见 <see cref="BimodalGapSeconds"/>）。
    /// 为 true 时中位数落在两簇之间的「空档」里、不代表任何真实状态，
    /// 调用方**必须拒写**（既不写偏移，也不把本次测量当脏值——问题在样本集，不在本次测量）。
    /// </summary>
    public bool IsBimodal
    {
        get { lock (_sync) { return _isBimodal; } }
        private set => _isBimodal = value;
    }

    private string? _bimodalNote;
    /// <summary>双峰判定的说明（中文，供日志）；非双峰时为 null。</summary>
    public string? BimodalNote
    {
        get { lock (_sync) { return _bimodalNote; } }
        private set => _bimodalNote = value;
    }

    private double? _slopeSecondsPerHour;
    /// <summary>拟合出的走时速率（秒/小时）；仅在 <see cref="TrendEnabled"/> 时有意义。</summary>
    public double? SlopeSecondsPerHour
    {
        get { lock (_sync) { return _slopeSecondsPerHour; } }
        private set => _slopeSecondsPerHour = value;
    }

    private bool _lastSampleWasOutlier;
    /// <summary>
    /// 最近一次 <see cref="Add"/> 的样本是否被判为离群（未入列）。调用方据此**拒写**本次偏移
    /// （v1.1.0：脏值的拦截点从「目标与当前之差」搬到「测量与本批拟合之差」）。
    /// </summary>
    public bool LastSampleWasOutlier
    {
        get { lock (_sync) { return _lastSampleWasOutlier; } }
        private set => _lastSampleWasOutlier = value;
    }

    private string? _lastNote;
    /// <summary>最近一次操作的说明（中文，供日志）。</summary>
    public string? LastNote
    {
        get { lock (_sync) { return _lastNote; } }
        private set => _lastNote = value;
    }

    private double? _lastResidualSeconds;
    /// <summary>最近一个样本相对当前中位数的残差（秒）；无样本时为 null。</summary>
    public double? LastResidualSeconds
    {
        get { lock (_sync) { return _lastResidualSeconds; } }
        private set => _lastResidualSeconds = value;
    }

    /// <summary>
    /// 追加一次测量样本。
    /// </summary>
    /// <param name="atLocal">样本时刻（本地墙钟，通常取铃响时刻）。</param>
    /// <param name="requiredOffset">让当前误差归零所需的绝对偏移（秒）。</param>
    /// <param name="templateConfirmed">
    /// 本次测量是否由**模板命中**确认（v1.1.2.0，见 <see cref="SegmentRestartRequiresTemplate"/>）。
    /// 只有模板命中的样本才有资格参与「基准跳变」确认。
    /// </param>
    public void Add(DateTime atLocal, TimeSpan requiredOffset, bool templateConfirmed = false)
    {
        lock (_sync)
        {
            var sec = requiredOffset.TotalSeconds;
            LastSampleWasOutlier = false;

            // v1.1.0：离群样本「不入列」而不是「清空样本段」——旧行为一次坏测量就把整段历史抹掉，
            // 于是此后每次写入都退化成单点直写（中位数的稳健性全没了）。
            var priorMedian = _medianSeconds;
            var farFromSegment = priorMedian.HasValue
                                 && _samples.Count >= MinSamplesForWindow
                                 && Math.Abs(sec - priorMedian.Value) > OutlierRejectSeconds;

            if (farFromSegment)
            {
                var pending = _pending;
                var agreesWithPending = pending.HasValue
                                        && (atLocal - pending.Value.At).TotalHours <= WindowHours
                                        && Math.Abs(sec - pending.Value.Sec) <= SegmentRestartAgreementSeconds;

                // v1.1.2.0：基准跳变确认必须至少有一条来自模板命中的测量（见常量注释）。
                var templateEvidence = templateConfirmed || (pending?.TemplateConfirmed ?? false);

                if (!agreesWithPending || (SegmentRestartRequiresTemplate && !templateEvidence))
                {
                    _pending = (atLocal, sec, templateConfirmed);
                    LastSampleWasOutlier = true;
                    Recompute();
                    var why = !agreesWithPending
                        ? $"待下一次测量确认是否为新基准（已累计 1/{SegmentRestartAgreements} 次）"
                        : "两次测量彼此一致但都不是模板命中，不足以判基准跳变 → 继续挂起";
                    AppendNote($"离群拒用：{sec:F3}s 与稳健中位数 {priorMedian!.Value:F3}s 相差 " +
                               $"{sec - priorMedian.Value:F3}s（＞ {OutlierRejectSeconds:F1}s）→ 本次不参与拟合，{why}");
                    return;
                }

                // 连续两次一致、都远离旧段、且至少一条由模板确认 → 认定基准真跳变，用这两条新样本重开样本段
                _samples.Clear();
                _samples.Add((pending!.Value.At, pending.Value.Sec));
                _samples.Add((atLocal, sec));
                _pending = null;
                Recompute();
                AppendNote($"基准跳变确认：连续 {SegmentRestartAgreements} 次测量（{pending.Value.Sec:F3}s / {sec:F3}s）" +
                           $"一致且都远离旧基准（旧中位数 {priorMedian!.Value:F3}s）→ 已重开样本段");
                return;
            }

            _pending = null;   // 与当前段一致的测量会冲掉待确认候选
            _samples.Add((atLocal, sec));
            if (_samples.Count > MaxSamples)
            {
                _samples.RemoveAt(0);
            }

            Recompute();
        }
    }

    /// <summary>把一句话并到 <see cref="LastNote"/> 末尾（保留 <see cref="Recompute"/> 写下的统计说明）。</summary>
    private void AppendNote(string sentence)
    {
        var tail = _lastNote;
        _lastNote = string.IsNullOrEmpty(tail) ? sentence : $"{sentence}｜{tail}";
    }

    /// <summary>
    /// 预测某时刻所需的绝对偏移（秒）；无样本返回 null。
    /// 默认返回**近期样本中位数**；仅当趋势门槛全部满足时，才在中位数基准上做温和外推。
    /// </summary>
    /// <param name="atLocal">目标时刻（本地墙钟）。</param>
    public double? PredictSeconds(DateTime atLocal)
    {
        lock (_sync)
        {
            if (_samples.Count == 0)
                return null;

            if (_samples.Count == 1)
                return _samples[0].OffsetSec;

            if (!_trendEnabled || _slopeSecondsPerHour == null || _medianSeconds == null)
                return _medianSeconds;

            // 趋势启用：以中位数为基准做温和外推（受外推时长护栏约束）
            var hours = (atLocal - _samples[^1].At).TotalHours;
            var cappedHours = Math.Clamp(hours, -MaxExtrapolationHours, MaxExtrapolationHours);
            return _medianSeconds + _slopeSecondsPerHour.Value * cappedHours;
        }
    }

    /// <summary>重算中位数、散度与（满足门槛时的）趋势。调用方必须已持有 <see cref="_sync"/>。</summary>
    private void Recompute()
    {
        RecomputeCore();
        if (IsBimodal && BimodalNote != null)
            AppendNote(BimodalNote);
    }

    /// <summary><see cref="Recompute"/> 的主体；双峰说明由外层追加到 <see cref="LastNote"/> 末尾。</summary>
    private void RecomputeCore()
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
            IsBimodal = false;
            BimodalNote = null;
            LastNote = $"样本 1 个（{_samples[0].OffsetSec:F3}s）：先按单次测量写入，待更多样本后转中位数";
            return;
        }

        var median = ComputeMedian(windowed.Select(s => s.OffsetSec).ToList());
        if (median == null)
            return;

        MedianSeconds = median.Value;
        MaxDeviationSeconds = windowed.Max(s => Math.Abs(s.OffsetSec - median.Value));
        LastResidualSeconds = Math.Round(_samples[^1].OffsetSec - median.Value, 3);

        // 双峰检测（v1.1.2.0）：见 BimodalGapSeconds 的注释。
        // 判据 = 排序后找**最大间隔**，若该间隔 ≥ 门槛且两侧各有 ≥ BimodalMinPerCluster 个样本，判双峰。
        // 这样只依赖「样本分成两堆」这个事实，不需要聚类算法，也不会被单个脏值触发（脏值那簇只有 1 个）。
        DetectBimodal(windowed);

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
        var clampNote = Math.Abs(rawSlope - _slopeSecondsPerHour!.Value) > 1e-9 ? $"（原始斜率 {rawSlope:F2} 已钳制）" : "";
        LastNote = $"中位数 {median.Value:F3}s + 趋势 {_slopeSecondsPerHour.Value:F3} 秒/小时{clampNote}" +
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

    /// <summary>
    /// 双峰检测（v1.1.2.0）：排序后找最大间隔，若该间隔 ≥ <see cref="BimodalGapSeconds"/>
    /// 且两侧各有 ≥ <see cref="BimodalMinPerCluster"/> 个样本，判为双峰并写说明。
    /// 调用方必须已持有 <see cref="_sync"/>（由 <see cref="Recompute"/> 调用）。
    /// </summary>
    private void DetectBimodal(List<(DateTime At, double OffsetSec)> windowed)
    {
        IsBimodal = false;
        BimodalNote = null;

        if (windowed.Count < BimodalMinPerCluster * 2)
            return;

        var sorted = windowed.Select(s => s.OffsetSec).OrderBy(v => v).ToList();
        var gapIdx = -1;
        var gap = 0.0;
        for (var i = 1; i < sorted.Count; i++)
        {
            var d = sorted[i] - sorted[i - 1];
            if (d > gap)
            {
                gap = d;
                gapIdx = i;
            }
        }

        if (gapIdx < BimodalMinPerCluster || sorted.Count - gapIdx < BimodalMinPerCluster)
            return;
        if (gap < BimodalGapSeconds)
            return;

        var lowCluster = sorted.Take(gapIdx).ToList();
        var highCluster = sorted.Skip(gapIdx).ToList();
        var lowMedian = ComputeMedian(lowCluster) ?? 0;
        var highMedian = ComputeMedian(highCluster) ?? 0;
        IsBimodal = true;
        BimodalNote = $"样本呈双峰：低簇 {lowCluster.Count} 个（中位 {lowMedian:F3}s，{lowCluster.Min():F3}~{lowCluster.Max():F3}）" +
                      $" / 高簇 {highCluster.Count} 个（中位 {highMedian:F3}s，{highCluster.Min():F3}~{highCluster.Max():F3}），" +
                      $"间隔 {gap:F3}s ＞ {BimodalGapSeconds:F1}s → 中位数落在两簇之间、不代表任何真实状态，本次不写偏移";
    }
}

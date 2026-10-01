using System;
using System.Collections.Generic;
using System.Linq;

namespace BellTimeCalibration.Services;

/// <summary>
/// 起响沿闸门选择器（v0.10.0，路线 2 的核心判据）。
///
/// 背景（2026-09-12 实机 10 个边界 dump 的结论）：
/// <list type="bullet">
/// <item><description><b>精确波形模板不适用</b>：同一「下课铃」在各边界的波形互相关只有 0.08~0.64，
/// 实录段当模板也只命中自己的 dump（跨边界 0 命中）。每次播出的精确波形都不同。</description></item>
/// <item><description><b>每个边界附近有两次事件</b>：主铃稳定落在课表边界 ±0.6 s 内，
/// 另有一次提前 6~15 s 的强起响（比值可达 346×）。历史上那些 −9.6 / +8.1 / −7.8 s 的巨大误差，
/// 都是把提前那次选走了。</description></item>
/// <item><description><b>「比值」比「响度」更能识别铃声</b>：铃声是突然起响，
/// 环境声（人声、桌椅、走廊）是渐强的。实测伪匹配的绝对电平甚至比真铃更响，
/// 但其「起响后峰值 ÷ 起响前基线」的比值远低于真铃（下课铃 14~346×、上课铃 4~7×；
/// 连续噪声里的伪匹配只有 1~3×）。</description></item>
/// </list>
///
/// 因此判据取：**在边界 ±<see cref="GateSeconds"/> 秒内，找起响比值最大的位置**。
/// 一步同时完成「排除提前事件」与「排除渐强噪声」，且不依赖任何波形对齐。
/// </summary>
public static class OnsetGate
{
    /// <summary>闸门半窗（秒）：只在课表边界 ±该值内找起响点。实测主铃落在 ±0.6 s 内，留足余量。</summary>
    public const double GateSeconds = 2.0;

    /// <summary>起响比值下限（倍）：起响后 0.5 s 峰值 RMS ÷ 起响前 0.3 s 基线 RMS。</summary>
    public const double RatioMin = 4.0;

    /// <summary>
    /// 强候选组容差（dB，v1.0.4）：只在与窗内最强候选相差不超过该值的候选里，再按比值挑最终起响点。
    ///
    /// 为什么要加这道门（2026-09-18 实机 111 个 dump 的复盘）：
    /// 旧判据单看「起响比值最大」，而比值 = 起响后 0.5 s 峰值 RMS ÷ 起响前 0.3 s 基线 RMS —— 只要起响前那 0.3 s
    /// 恰好落在噪声间隙里，一个**弱**瞬态就能凑出很高的比值，把真正更响的那次起响挤掉。实测两例：
    /// <list type="bullet">
    /// <item><description>20260918 11:40 窗口（用户举报）：选中点峰值 −17.5 dBFS，同窗更强事件约 −10.5 dBFS，<b>差约 7 dB</b>；</description></item>
    /// <item><description>20260918 09:40 窗口：选中点峰值 −28.2 dBFS（比值 32.2×），同窗更强事件 −21.2 dBFS（比值 10.0×），<b>差 7.0 dB</b>。</description></item>
    /// </list>
    /// 反过来，111 个窗口里新旧两种选法多数几乎一致（中位差 0.05 s），所以这里只是**收敛**判据、不做替换：
    /// 先框定「与最强事件同一量级」的候选组，再用比值挑最像铃声的那一下。
    /// </summary>
    public const double DominanceDb = 6.0;

    /// <summary>
    /// 起响处绝对电平下限（v0.10.4）：与 <see cref="RingDetector.AbsoluteMinRms"/> 同口径（约 −50 dBFS）。
    /// 只靠比值会被「静音窗口的噪声除噪声」骗过（实测噪声底 −80 dBFS、峰值 −114.5 dBFS 仍报出 6.2× 命中）。
    /// 真铃声起响峰值实测 −6.8~−13.9 dBFS，与下限留 36 dB 余量。
    /// </summary>
    public const double AbsoluteMinRms = RingDetector.AbsoluteMinRms;

    /// <summary>用于算比值的基线与峰值窗长（秒）。</summary>
    private const double BaselineSeconds = 0.3;
    private const double PeakSeconds = 0.5;

    /// <summary>扫描步长（秒）：10 ms，与包络跳距一致。</summary>
    private const double StepSeconds = BellTemplate.HopSeconds;

    /// <summary>一次命中结果。</summary>
    /// <param name="OnsetSampleIndex">起响点（<paramref name="capture"/> 内的采样索引）。</param>
    /// <param name="OnsetUtc">起响点墙钟（UTC）。</param>
    /// <param name="Ratio">起响比值（倍）。</param>
    /// <param name="Note">说明（中文，供日志）。</param>
    /// <param name="AnchorWallLocal">本窗口音频锚点墙钟（本地）= 首个音频块的回调时刻（v1.0.4，供离线复算对齐）。</param>
    /// <param name="GateFromSeconds">闸门区间起点在本窗口音频内的秒数（v1.0.4）。</param>
    /// <param name="GateToSeconds">闸门区间终点在本窗口音频内的秒数（v1.0.4）。</param>
    /// <param name="PeakRms">选中起响点后 0.5 s 的峰值 RMS（v1.0.4 增补；供「双窗口复核」比较两侧证据强弱）。</param>
    /// <param name="CandidateCount">扫描到的候选数（v1.0.4：供人类日志按字段拼短摘要，不必回抄整段判据文本）。</param>
    /// <param name="QualifiedCount">通过两道门槛的候选数（v1.0.4）。</param>
    /// <param name="StrongCount">强候选组大小（v1.0.4）。</param>
    public readonly record struct OnsetHit(
        int OnsetSampleIndex,
        DateTime OnsetUtc,
        double Ratio,
        string Note,
        DateTime AnchorWallLocal = default,
        double GateFromSeconds = 0,
        double GateToSeconds = 0,
        double PeakRms = 0,
        int CandidateCount = 0,
        int QualifiedCount = 0,
        int StrongCount = 0);

    /// <summary>
    /// 在「边界 ±<see cref="GateSeconds"/> 秒」内找起响比值最大的位置。
    /// </summary>
    /// <param name="audio">窗口音频（仅内存）。</param>
    /// <param name="boundaryWallLocal">课表边界（**裸墙钟域**本地时刻；显示域需先减去内核偏移）。</param>
    /// <param name="note">说明（中文，供日志；未命中时给出原因与最佳候选）。</param>
    /// <returns>命中返回起响点；未找到合格起响沿返回 null。</returns>
    public static OnsetHit? Find(CaptureAudio audio, DateTime boundaryWallLocal, out string note)
    {
        var rate = CaptureAudio.SampleRate;
        var baseSamples = (int)(BaselineSeconds * rate);
        var peakSamples = (int)(PeakSeconds * rate);
        var stepSamples = Math.Max(1, (int)(StepSeconds * rate));

        var gateFrom = (int)((boundaryWallLocal - TimeSpan.FromSeconds(GateSeconds) - audio.AnchorWallLocal).TotalSeconds * rate);
        var gateTo = (int)((boundaryWallLocal + TimeSpan.FromSeconds(GateSeconds) - audio.AnchorWallLocal).TotalSeconds * rate);
        var settle = (int)(RingCandidateSelector.SettleSkipSeconds * rate);

        gateFrom = Math.Max(gateFrom, settle);
        gateTo = Math.Min(gateTo, audio.Length - peakSamples);

        if (gateTo <= gateFrom)
        {
            note = $"闸门区间无效（边界 ±{GateSeconds:F1}s 落在音频之外或静置期内）";
            return null;
        }

        // 先做预算，避免在长区间里反复重算（每个候选只 O(1) 递推）
        var window = new double[audio.Length + 1];
        var samples = audio.Samples;
        window[0] = 0;
        for (var i = 0; i < audio.Length; i++)
            window[i + 1] = window[i] + (double)samples[i] * samples[i];

        double Rms(int from, int to)
        {
            var n = to - from;
            if (n <= 0)
                return 0;
            return Math.Sqrt((window[to] - window[from]) / n);
        }

        var bestIdx = -1;
        var bestRatio = 0.0;
        var bestPeak = 0.0;
        var scanBest = 0.0;
        var scanPeakMax = 0.0;

        // ── 两趟扫描（v1.0.4）──
        // 第一趟只收集候选：每个候选的基线/峰值仍由前缀和 O(1) 递推，性能与旧实现相同。
        var candidates = new List<(int Idx, double Ratio, double Peak)>();
        var silentBaselineCandidates = 0;   // 基线落在数字静默里的候选数（诊断用）
        for (var idx = gateFrom; idx <= gateTo; idx += stepSamples)
        {
            var baseFrom = idx - baseSamples;
            if (baseFrom < 0)
                continue;

            var baseline = Rms(baseFrom, idx);
            var peak = Rms(idx, Math.Min(audio.Length, idx + peakSamples));

            // ── 基线有效性门槛（v1.0.4）──
            // 比值 = 峰值 ÷ 基线；基线若是**数字静默**（麦克风整段未进音后恢复供音），除数≈0，
            // 比值会炸成千万级，把「麦克风恢复出声的那一瞬间」选成铃声。
            // 实机证据（2026-09-19 上午）：8 个窗口各含 10~21 s 全零静默，闸门报出
            // 「起响比值 748579× ~ 96702200×」，delta 集中在 −1.68/−1.98/−1.99 s（恢复点的机器特征），
            // 而真铃的起响比值实测只有 4~346×。这类候选的比值**没有物理意义**，直接排除。
            // 阈值取 RingDetector.NoiseFloorMin（1e-4 ≈ −80 dBFS，已存在的常量，不新增口径）。
            if (baseline < RingDetector.NoiseFloorMin)
            {
                silentBaselineCandidates++;
                candidates.Add((idx, 0, peak));   // 比值记 0：保留计数与峰值用于诊断，但不参与选择
                if (peak > scanPeakMax)
                    scanPeakMax = peak;
                continue;
            }

            var ratio = peak / baseline;
            if (ratio > scanBest)
                scanBest = ratio;
            if (peak > scanPeakMax)
                scanPeakMax = peak;

            candidates.Add((idx, ratio, peak));
        }

        // 绝对电平下限（v0.10.4）与比值下限（v0.10.0）两道门槛语义不变：不合格的候选一律不参与选择。
        var qualified = candidates
            .Where(c => c.Peak >= AbsoluteMinRms && c.Ratio >= RatioMin)
            .ToList();

        // 第二趟：先框定「强候选组」，再在组内按比值取最优（见 DominanceDb 注释）。
        var strongCount = 0;
        var strongPeakMax = 0.0;
        if (qualified.Count > 0)
        {
            strongPeakMax = qualified.Max(c => c.Peak);
            var strongFloor = strongPeakMax * Math.Pow(10, -DominanceDb / 20.0);
            foreach (var c in qualified)
            {
                if (c.Peak < strongFloor)
                    continue;
                strongCount++;
                if (c.Ratio > bestRatio)
                {
                    bestRatio = c.Ratio;
                    bestIdx = c.Idx;
                    bestPeak = c.Peak;
                }
            }
        }

        // ── 绝对电平下限（v0.10.4）──
        // 只比值不够：麦克风整窗静默时，**噪声除噪声**也能凑出 ≥4× 的假起响沿。
        // 实机证据（2026-09-14 09:40 边界）：日志记录「噪声底 −80.0 dBFS、峰值 −114.5 dBFS」
        // ——峰值比噪声底还低 34 dB 且等于数字静默，闸门却报了「命中 6.2×」。若自动应用开着，
        // 这一次就会写出一个完全错误的偏移。真铃声的起响峰值实测在 −6.8~−13.9 dBFS，
        // 与下限之间留 36 dB 余量，不会误杀。
        if (bestIdx < 0 && candidates.Count > 0 && candidates.Max(c => c.Peak) < AbsoluteMinRms)
        {
            note = $"边界 ±{GateSeconds:F1}s 内起响沿电平过低（峰值 {ToDb(candidates.Max(c => c.Peak)):F1} dBFS，" +
                   $"下限 {ToDb(AbsoluteMinRms):F1} dBFS）→ 判为静音/麦克风未进音，拒绝本次测量";
            return null;
        }

        if (bestIdx < 0 || bestRatio < RatioMin)
        {
            // v1.0.4：未命中说明改为**紧凑串**（原先一次 130+ 字，逐窗口写进日志很占地方）。
            // 但「基线静默候选」这个关键词必须保留：gate-guard 断言就靠它统计被门槛拦下的窗口。
            // 命中时的说明（下面那段）保持完整——它同时是结构化历史 GateNote 的内容。
            note = $"未命中（最佳比值 {scanBest:F1}×／下限 {RatioMin:F1}×，区间峰值 {ToDb(scanPeakMax):F1} dBFS" +
                   (silentBaselineCandidates > 0 ? $"，已排除 {silentBaselineCandidates} 个基线静默候选" : "") + "）";
            return null;
        }

        var onsetUtc = audio.WallTimeAt(bestIdx);
        if (onsetUtc == null)
        {
            note = "起响点墙钟换算失败";
            return null;
        }

        var onsetLocal = onsetUtc.Value.ToLocalTime();
        var offsetSec = (onsetLocal - boundaryWallLocal).TotalSeconds;

        // 候选摘要（v1.0.4）：把「最强候选是谁、强候选组多大、最终选了谁」一并写进日志。
        // 必要性：dump 文件名用的是布防时刻，而本函数用的是音频锚点（首个块回调时刻），两者实测能差 1~3 s；
        // 没有这行摘要，离线复算的位置与实机判据对不上（这次排查为此反复得出相反结论）。
        var anchorLocal = audio.AnchorWallLocal;
        var anchorDelta = anchorLocal == DateTime.MinValue
            ? double.NaN
            : (anchorLocal - boundaryWallLocal).TotalSeconds;
        note = $"起响沿命中：边界 {(offsetSec >= 0 ? "+" : "")}{offsetSec:F2}s，起响比值 {bestRatio:F1}×" +
               $"（峰值 {ToDb(bestPeak):F1} dBFS）；" +
               $"候选 {candidates.Count} 个 / 合格 {qualified.Count} 个 / 强候选组 {strongCount} 个" +
               $"（组内峰值上限 {ToDb(strongPeakMax):F1} dBFS，容差 {DominanceDb:F1} dB）" +
               (silentBaselineCandidates > 0 ? $"；已排除 {silentBaselineCandidates} 个基线静默候选" : "") + "；" +
               $"锚点=边界{(double.IsNaN(anchorDelta) ? "n/a" : $"{anchorDelta:+0.000;-0.000;0.000}s")}";

        return new OnsetHit(
            bestIdx, onsetUtc.Value, bestRatio, note,
            anchorLocal,
            gateFrom / (double)rate,
            gateTo / (double)rate,
            PeakRms: bestPeak,
            CandidateCount: candidates.Count,
            QualifiedCount: qualified.Count,
            StrongCount: strongCount);
    }

    /// <summary>
    /// 双窗口复核的一致性容差（秒，v1.0.4）：两侧命中位置相差不超过该值即视为「同一次起响」。
    /// 两侧扫描用的是同一套判据与步长（10 ms），只是窗口中心不同，因此同一次起响通常落在同一格。
    /// </summary>
    public const double AgreeSeconds = 1.0;

    /// <summary>双窗口复核结论（v1.0.4）。</summary>
    public enum WindowVerdict
    {
        /// <summary>两个窗口都没有合格起响沿。</summary>
        None,

        /// <summary>只有「开关窗口」命中 —— 现状路径，采用该命中。</summary>
        SwitchOnly,

        /// <summary>只有「课表窗口」命中 —— 只作旁证写日志，不作测量源（测量源仍走模板匹配/无可信测量）。</summary>
        ScheduleOnly,

        /// <summary>两侧指向同一次起响 —— 采用开关窗口命中（与旧行为完全一致）。</summary>
        Agree,

        /// <summary>两侧不一致，但开关窗口明显更强 —— 仍采用开关窗口命中（保住「铃确实比课表晚几秒」的合法测量）。</summary>
        PreferSwitch,

        /// <summary>两侧不一致且课表窗口并不更弱 —— 本次判为冲突，**不写入、不落样本**。</summary>
        Conflict
    }

    /// <summary>
    /// 双窗口复核（v1.0.4）：把「开关窗口」的命中与「课表窗口」的命中放在一起裁决。
    ///
    /// 为什么需要（2026-09-20 上午实机 4 个下课窗口 + 录音复算）：
    /// 闸门窗口的中心是 <c>B_display − 偏移</c>，也就是**内核真正切换的位置**，而它本身正是要被校准的量。
    /// 于是「所需偏移 = 课表边界 − 铃响」恒落在「当前偏移 ±2 s」内：偏移一旦偏出 2 s（如校铃钟被人工
    /// 校正过、或上一轮把脏样本写进了偏移），闸门就只能看见开关附近的**杂音**，而结果看起来像一次
    /// 正常的小修正（实测 08:50/09:40/10:50/11:40 四个窗口各写入 +0.3~+1.3 s，把偏移从 6.34 推到 8.13）。
    /// 同一段录音里真正落在课表边界上的那一下要强 14~18 dB，但它在「开关 +6.9 s」处，被 ±2 s 窗挡在外面。
    /// 因此这里再扫一遍**以标称边界为中心**的同一判据窗口（该中心与当前偏移无关），作为独立旁证：
    /// 两侧一致 → 照旧；两侧分歧且开关窗口并不明显更强 → 判冲突、不写入、交人工（绝不写脏值）。
    /// </summary>
    /// <param name="switchHit">开关窗口命中（现状中心 = B_display − 偏移）。</param>
    /// <param name="scheduleHit">课表窗口命中（中心 = 标称边界，与偏移无关）。</param>
    /// <param name="verdict">复核结论。</param>
    /// <param name="note">中文说明（供日志）。</param>
    /// <returns><see cref="WindowVerdict.SwitchOnly"/>/<see cref="WindowVerdict.Agree"/>/<see cref="WindowVerdict.PreferSwitch"/>
    /// 返回应采用的开关窗口命中；其余结论返回 null（无测量）。</returns>
    public static OnsetHit? Reconcile(
        OnsetHit? switchHit,
        OnsetHit? scheduleHit,
        out WindowVerdict verdict,
        out string note)
    {
        if (switchHit == null && scheduleHit == null)
        {
            verdict = WindowVerdict.None;
            note = "两窗口均无合格起响沿";
            return null;
        }

        if (switchHit == null)
        {
            verdict = WindowVerdict.ScheduleOnly;
            note = $"仅课表窗口命中（峰值 {ToDb(scheduleHit!.Value.PeakRms):F1} dBFS）——只作旁证，不作测量源";
            return null;
        }

        if (scheduleHit == null)
        {
            verdict = WindowVerdict.SwitchOnly;
            note = "仅开关窗口命中（课表窗口无合格起响沿）→ 采用开关窗口";
            return switchHit;
        }

        var switchOnset = switchHit.Value.OnsetUtc;
        var scheduleOnset = scheduleHit.Value.OnsetUtc;
        var deltaSeconds = (switchOnset - scheduleOnset).TotalSeconds;
        var switchDb = ToDb(switchHit.Value.PeakRms);
        var scheduleDb = ToDb(scheduleHit.Value.PeakRms);
        var gapDb = switchDb - scheduleDb;

        if (Math.Abs(deltaSeconds) <= AgreeSeconds)
        {
            verdict = WindowVerdict.Agree;
            note = $"两窗口一致（Δt={deltaSeconds:+0.00;-0.00;0.00}s）→ 采用开关窗口命中";
            return switchHit;
        }

        if (gapDb > DominanceDb)
        {
            verdict = WindowVerdict.PreferSwitch;
            note = $"两窗口相差 {Math.Abs(deltaSeconds):F2}s，开关窗口强 {gapDb:F1} dB（＞{DominanceDb:F1} dB）" +
                   "→ 采用开关窗口命中（判定铃确实远离课表边界）";
            return switchHit;
        }

        verdict = WindowVerdict.Conflict;
        note = $"两窗口相差 {Math.Abs(deltaSeconds):F2}s，而开关窗口并未强出 {DominanceDb:F1} dB" +
               $"（开关 {switchDb:F1} / 课表 {scheduleDb:F1} dBFS，差 {gapDb:F1} dB）" +
               "→ 判定冲突：本次不写入、不落样本，请人工确认铃况";
        return null;
    }

    private static double ToDb(double linear) => 20 * Math.Log10(Math.Max(linear, 1e-12));
}

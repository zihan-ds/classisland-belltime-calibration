using System;
using System.Collections.Generic;

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
    public readonly record struct OnsetHit(int OnsetSampleIndex, DateTime OnsetUtc, double Ratio, string Note);

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
        for (var idx = gateFrom; idx <= gateTo; idx += stepSamples)
        {
            var baseFrom = idx - baseSamples;
            if (baseFrom < 0)
                continue;

            var baseline = Rms(baseFrom, idx);
            var peak = Rms(idx, Math.Min(audio.Length, idx + peakSamples));
            var ratio = peak / Math.Max(baseline, 1e-9);
            if (ratio > scanBest)
                scanBest = ratio;
            if (peak > scanPeakMax)
                scanPeakMax = peak;

            if (ratio > bestRatio)
            {
                bestRatio = ratio;
                bestIdx = idx;
                bestPeak = peak;
            }
        }

        // ── 绝对电平下限（v0.10.4）──
        // 只比值不够：麦克风整窗静默时，**噪声除噪声**也能凑出 ≥4× 的假起响沿。
        // 实机证据（2026-09-14 09:40 边界）：日志记录「噪声底 −80.0 dBFS、峰值 −114.5 dBFS」
        // ——峰值比噪声底还低 34 dB 且等于数字静默，闸门却报了「命中 6.2×」。若自动应用开着，
        // 这一次就会写出一个完全错误的偏移。真铃声的起响峰值实测在 −6.8~−13.9 dBFS，
        // 与下限之间留 36 dB 余量，不会误杀。
        if (bestIdx >= 0 && bestPeak < AbsoluteMinRms)
        {
            note = $"边界 ±{GateSeconds:F1}s 内起响沿电平过低（峰值 {ToDb(bestPeak):F1} dBFS，" +
                   $"下限 {ToDb(AbsoluteMinRms):F1} dBFS）→ 判为静音/麦克风未进音，拒绝本次测量";
            return null;
        }

        if (bestIdx < 0 || bestRatio < RatioMin)
        {
            note = $"边界 ±{GateSeconds:F1}s 内无合格起响沿（最佳起响比值 {scanBest:F1}×，下限 {RatioMin:F1}×" +
                   $"；该区间峰值 {ToDb(scanPeakMax):F1} dBFS）";
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
        note = $"起响沿命中：边界 {(offsetSec >= 0 ? "+" : "")}{offsetSec:F2}s，起响比值 {bestRatio:F1}×" +
               $"（峰值 {ToDb(bestPeak):F1} dBFS）";
        return new OnsetHit(bestIdx, onsetUtc.Value, bestRatio, note);
    }

    private static double ToDb(double linear) => 20 * Math.Log10(Math.Max(linear, 1e-12));
}

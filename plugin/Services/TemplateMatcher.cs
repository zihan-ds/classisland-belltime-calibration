using System;
using System.Linq;

namespace BellTimeCalibration.Services;

/// <summary>
/// 一次模板匹配结果。
/// </summary>
/// <param name="TemplateId">命中的模板标识。</param>
/// <param name="Label">模板中文标签。</param>
/// <param name="Ncc">精匹配的归一化互相关系数（-1..1，越高越像）。</param>
/// <param name="SpectralSim">音色余弦相似度（-1..1）。</param>
/// <param name="Accepted">是否同时达到 NCC 与音色阈值。</param>
/// <param name="OnsetSampleIndex">匹配到的起响点（捕获缓冲内的采样索引）。</param>
/// <param name="OnsetSecondsFromRangeStart">起响点相对搜索区间起点的秒数（便于日志与换算）。</param>
/// <param name="Note">说明（中文，供日志）。</param>
public readonly record struct TemplateMatch(
    string TemplateId,
    string Label,
    double Ncc,
    double SpectralSim,
    bool Accepted,
    int OnsetSampleIndex,
    double OnsetSecondsFromRangeStart,
    string Note);

/// <summary>
/// 模板匹配器（v0.9.0，纯逻辑、可独立自测）。
/// 四步走（粗 → 精 → 音色确认 → 攻击沿校验），全程只用内存中的捕获样本：
/// <list type="number">
/// <item><description><b>粗定位</b>：10 ms 步长能量包络上的归一化互相关，扫过整个搜索区间（尺度大、代价极低）；</description></item>
/// <item><description><b>精定位</b>：在粗定位 ±30 ms 内，对 1/12 抽取信号做 0.25 ms 步长互相关，再回到 48 kHz 原信号上 ±12 个采样细化 → 毫秒级起响点；</description></item>
/// <item><description><b>音色确认</b>：匹配点起 0.5 s 的 32 点对数频段强度向量与模板向量做余弦相似度，排除「时长/包络像但音色不像」的干扰声；</description></item>
/// <item><description><b>攻击沿校验</b>（v0.9.2）：起响处峰值 RMS 必须显著高于起响前基线，排除「连续噪声里挑到局部最大」的伪匹配（实机 21:40 窗口出现过 NCC 0.804 的假命中）。</description></item>
/// </list>
/// 不做线程同步：调用方串行使用（同一时刻仅一个监听窗口）。
/// </summary>
public static class TemplateMatcher
{
    /// <summary>精匹配抽取倍数（48 kHz ÷ 12 = 4 kHz → 0.25 ms 步长）。</summary>
    private const int Decimation = 12;

    /// <summary>粗定位后精搜索的半径（毫秒）。</summary>
    private const double FineSearchRadiusMs = 30;

    /// <summary>包络 NCC 的计算步长（采样，10 ms）。</summary>
    private const int HopSamples = BellTemplate.EnvelopeHopSamples;

    /// <summary>
    /// 攻击沿校验（v0.9.2）的基线窗长（秒）：取样点之前这一段作为「安静基线」。
    /// </summary>
    private const double AttackBaselineSeconds = 0.3;

    /// <summary>
    /// 攻击沿校验的峰值窗长（秒）：取样点之后这一段取峰，代表铃声起响强度。
    /// </summary>
    private const double AttackPeakSeconds = 0.5;

    /// <summary>
    /// 攻击沿校验的强度比阈值（倍）：峰值 RMS 必须 ≥ 基线 RMS × 该值（4 倍 ≈ 12 dB）。
    /// 必要性（2026-09-11 实机）：21:40 那个窗口噪声底 −23 dBFS，整窗连续无静默，
    /// 模板在连续噪声里挑到一个局部最大（NCC 0.804）→ t_ring 偏晚 0.35~1.1 s，把拟合带偏。
    /// 真铃声是「突然起响」，攻击沿必然陡峭；连续噪声里的伪匹配做不到。
    /// </summary>
    private const double AttackRatioMin = 4.0;

    /// <summary>
    /// 在捕获缓冲的 [_fromSample, _toSample) 区间内搜索模板的最佳对齐。
    /// </summary>
    /// <param name="template">铃声模板。</param>
    /// <param name="capture">捕获缓冲（48 kHz 单声道）。</param>
    /// <param name="captureLength">缓冲内有效样本数。</param>
    /// <param name="fromSample">搜索起点（采样索引，通常取静置期之后）。</param>
    /// <param name="toSample">搜索终点（采样索引，模板起点不会越过此处）。</param>
    /// <param name="nccMin">NCC 阈值。</param>
    /// <param name="spectralMin">音色相似度阈值。</param>
    public static TemplateMatch Match(
        BellTemplate template,
        float[] capture,
        int captureLength,
        int fromSample,
        int toSample,
        double nccMin,
        double spectralMin)
    {
        var tplLen = template.Samples.Length;
        fromSample = Math.Max(0, fromSample);
        toSample = Math.Min(captureLength - tplLen, toSample);

        if (tplLen <= 0 || toSample <= fromSample)
        {
            return new TemplateMatch(template.Id, template.Label, 0, 0, false, -1, double.NaN,
                $"搜索区间不足（模板 {tplLen / (double)WavReader.TargetSampleRate:F2}s）");
        }

        // ── 1) 粗定位：包络互相关 ──
        var coarseStart = CoarseAlign(template, capture, captureLength, fromSample, toSample);
        if (coarseStart < 0)
        {
            return new TemplateMatch(template.Id, template.Label, 0, 0, false, -1, double.NaN, "粗定位失败");
        }

        // ── 2) 精定位：抽取信号 0.25 ms 步长 + 原信号细化 ──
        var radius = (int)(FineSearchRadiusMs / 1000.0 * WavReader.TargetSampleRate);
        var fineStart = Math.Clamp(coarseStart, fromSample, toSample);
        var fineFrom = Math.Max(fromSample, fineStart - radius);
        var fineTo = Math.Min(toSample, fineStart + radius);
        var (refined, ncc) = FineAlign(template, capture, captureLength, fineFrom, fineTo);

        // ── 2b) 攻击沿优先重采（v0.9.3）──
        // v0.9.2 是「先取最高 NCC，再校验攻击沿」，于是真正有攻击沿的匹配点根本没被考虑过：
        // 实机 2026-09-12 08:50 窗口，NCC 0.864 落在无攻击沿的噪声段（起响处比值 1.0×）→ 整块被拒，
        // 而真铃（起响处比值 21×）在它后面却从未被检查。现在改为「只在有攻击沿的位置里取最高 NCC」。
        if (!CheckAttack(capture, captureLength, refined).Passed)
        {
            var alt = FindAttackOnsetNear(template, capture, captureLength, fromSample, toSample, refined);
            if (alt.SampleIndex >= 0)
            {
                refined = alt.SampleIndex;
                ncc = alt.Ncc;
            }
        }

        // ── 3) 音色确认 ──
        var specLen = Math.Min(tplLen, (int)(BellTemplate.SpectrumWindowSeconds * WavReader.TargetSampleRate));
        var captureSpectrum = BellTemplate.ComputeSpectrum(capture, refined, specLen);
        var spectral = BellTemplate.CosineSimilarity(captureSpectrum, template.Spectrum);

        // ── 4) 攻击沿校验（v0.9.2）──
        var attack = CheckAttack(capture, captureLength, refined);

        var accepted = ncc >= nccMin && spectral >= spectralMin && attack.Passed;
        var note = $"{template.Label}：NCC {ncc:F3}（阈值 {nccMin:F2}），音色 {spectral:F3}（阈值 {spectralMin:F2}）" +
                   (attack.Passed ? "" : "，攻击沿不足（起响处峰值未超过前段基线 4 倍，疑为连续噪声中的伪匹配）") +
                   $"{(accepted ? " → 命中" : " → 未达阈值")}";

        return new TemplateMatch(
            template.Id,
            template.Label,
            ncc,
            spectral,
            accepted,
            refined,
            (refined - fromSample) / (double)WavReader.TargetSampleRate,
            note);
    }

    /// <summary>
    /// 攻击沿优先重采（v0.9.3）：当最高 NCC 位置没有攻击沿时，在搜索区间内**只看有攻击沿的位置**，
    /// 用 10 ms 步长的抽取信号快速算 NCC，返回其中 NCC 最高者（找不到返回 −1）。
    /// 这是「铃声一定是突然起响」这一物理事实的直接编码：连续噪声里的伪匹配过不了攻击沿，
    /// 真铃位置即使 NCC 略低也会被选中。
    /// </summary>
    /// <param name="template">模板。</param>
    /// <param name="capture">捕获缓冲。</param>
    /// <param name="captureLength">缓冲有效样本数。</param>
    /// <param name="fromSample">搜索起点。</param>
    /// <param name="toSample">搜索终点（模板起点的上界）。</param>
    /// <param name="currentOnset">当前（无攻击沿的）匹配点，用于跳过重复计算。</param>
    private static (int SampleIndex, double Ncc) FindAttackOnsetNear(
        BellTemplate template, float[] capture, int captureLength, int fromSample, int toSample, int currentOnset)
    {
        var tplDec = Decimate(template.Samples, 0, template.Samples.Length);
        if (tplDec.Length == 0)
            return (-1, 0);

        var bestIndex = -1;
        var bestNcc = double.NegativeInfinity;

        // 以 10 ms 为步长扫过搜索区间（与包络跳距一致），逐个位置先做攻击沿校验（便宜），再算 NCC（贵）
        for (var idx = fromSample; idx <= toSample; idx += HopSamples)
        {
            if (idx == currentOnset)
                continue;

            if (!CheckAttack(capture, captureLength, idx).Passed)
                continue;

            var need = (tplDec.Length * Decimation) + Decimation;
            if (idx + need > captureLength)
                break;

            var capDec = Decimate(capture, idx, need);
            if (capDec.Length < tplDec.Length)
                continue;

            var ncc = NccAt(capDec, 0, tplDec);
            if (ncc > bestNcc)
            {
                bestNcc = ncc;
                bestIndex = idx;
            }
        }

        return (bestIndex, bestNcc);
    }

    /// <summary>包络级粗定位：返回最佳对齐的采样起点（失败 -1）。</summary>
    private static int CoarseAlign(BellTemplate template, float[] capture, int captureLength, int fromSample, int toSample)
    {
        var tplEnv = template.Envelope;
        if (tplEnv.Length == 0)
            return -1;

        var envFrom = Math.Max(0, fromSample / HopSamples);
        var envTo = Math.Min((captureLength / HopSamples) - 1, toSample / HopSamples + tplEnv.Length);
        if (envTo - envFrom < tplEnv.Length)
            return -1;

        var capEnv = BellTemplate.ComputeEnvelope(
            capture,
            envFrom * HopSamples,
            Math.Min(captureLength - envFrom * HopSamples, (envTo - envFrom + 1) * HopSamples));
        if (capEnv.Length < tplEnv.Length)
            return -1;

        var tplMean = tplEnv.Average();
        var tplCentered = tplEnv.Select(v => v - tplMean).ToArray();
        var tplNorm = Math.Sqrt(tplCentered.Sum(v => v * v));
        if (tplNorm <= 1e-12)
            return -1;

        var bestOffset = -1;
        var bestScore = double.NegativeInfinity;
        var maxOffset = capEnv.Length - tplEnv.Length;
        for (var off = 0; off <= maxOffset; off++)
        {
            double sum = 0, sumSq = 0, dot = 0;
            for (var i = 0; i < tplEnv.Length; i++)
            {
                var v = capEnv[off + i];
                sum += v;
                sumSq += v * v;
                dot += v * tplCentered[i];
            }

            var mean = sum / tplEnv.Length;
            var varSum = sumSq - tplEnv.Length * mean * mean;
            if (varSum <= 1e-12)
                continue;

            var score = dot / (Math.Sqrt(varSum) * tplNorm);
            if (score > bestScore)
            {
                bestScore = score;
                bestOffset = off;
            }
        }

        return bestOffset < 0 ? -1 : (envFrom + bestOffset) * HopSamples;
    }

    /// <summary>抽取信号 0.25 ms 步长精定位 + 原信号 ±Decimation 采样细化；返回（采样索引, NCC）。</summary>
    private static (int SampleIndex, double Ncc) FineAlign(
        BellTemplate template, float[] capture, int captureLength, int from, int to)
    {
        var tplDec = Decimate(template.Samples, 0, template.Samples.Length);
        if (tplDec.Length == 0)
            return (from, 0);

        var need = (tplDec.Length * Decimation) + (to - from) + Decimation;
        var capFrom = Math.Max(0, from - Decimation);
        var capLen = Math.Min(captureLength - capFrom, Math.Max(need, tplDec.Length * Decimation + Decimation));
        if (capLen <= tplDec.Length * Decimation)
            return (from, 0);

        var capDec = Decimate(capture, capFrom, capLen);

        var (bestDecIndex, bestScore) = (0, double.NegativeInfinity);
        var maxOff = capDec.Length - tplDec.Length;
        for (var off = 0; off <= maxOff; off++)
        {
            var score = NccAt(capDec, off, tplDec);
            if (score > bestScore)
            {
                bestScore = score;
                bestDecIndex = off;
            }
        }

        // 回到原信号细化：抽取索引 → 原采样索引（capFrom + bestDecIndex*Decimation），±Decimation 内 1 采样步长
        var coarse = capFrom + bestDecIndex * Decimation;
        var refineFrom = Math.Max(0, Math.Min(coarse - Decimation, captureLength - template.Samples.Length));
        var refineTo = Math.Max(refineFrom, Math.Min(coarse + Decimation, captureLength - template.Samples.Length));

        var (bestIndex, bestFine) = (refineFrom, double.NegativeInfinity);
        for (var idx = refineFrom; idx <= refineTo; idx++)
        {
            var score = NccAt(capture, idx, template.Samples, Math.Min(template.Samples.Length, captureLength - idx));
            if (score > bestFine)
            {
                bestFine = score;
                bestIndex = idx;
            }
        }

        return (bestIndex, bestFine);
    }

    /// <summary>
    /// 攻击沿校验（v0.9.2）：匹配点前后各取一段短时 RMS，要求「起响后峰值 ≥ 起响前基线 × <see cref="AttackRatioMin"/>」。
    /// 目的：在噪声底很高、整窗连续无静默的窗口里，模板匹配会在连续噪声中挑到局部最大（NCC 仍可能 0.8）
    /// 而给出错误的起响点；真铃声是突然起响，攻击沿必然陡峭，噪声伪匹配没有这个台阶。
    /// </summary>
    /// <param name="capture">捕获缓冲。</param>
    /// <param name="captureLength">缓冲有效样本数。</param>
    /// <param name="onsetSample">模板匹配出的起响点采样索引。</param>
    private static (bool Passed, double BaselineDb, double PeakDb) CheckAttack(
        float[] capture, int captureLength, int onsetSample)
    {
        var baseLen = (int)(AttackBaselineSeconds * WavReader.TargetSampleRate);
        var peakLen = (int)(AttackPeakSeconds * WavReader.TargetSampleRate);
        var baseFrom = Math.Max(0, onsetSample - baseLen);
        var peakTo = Math.Min(captureLength, onsetSample + peakLen);
        if (peakTo - onsetSample < peakLen / 4 || onsetSample - baseFrom <= 0)
        {
            // 缓冲边界不足（首尾各几毫秒）→ 无法判定，按通过处理，避免误杀
            return (true, double.NegativeInfinity, double.NegativeInfinity);
        }

        var baseline = Rms(capture, baseFrom, onsetSample);
        var peak = Rms(capture, onsetSample, peakTo);
        var ratio = peak / Math.Max(baseline, 1e-9);
        return (ratio >= AttackRatioMin, ToDb(baseline), ToDb(peak));
    }

    private static double Rms(float[] samples, int from, int to)
    {
        double sum = 0;
        var n = to - from;
        if (n <= 0)
            return 0;
        for (var i = from; i < to; i++)
            sum += (double)samples[i] * samples[i];
        return Math.Sqrt(sum / n);
    }

    private static double ToDb(double linear) => 20 * Math.Log10(Math.Max(linear, 1e-12));

    /// <summary>在 offset 处计算归一化互相关（向量 a 的一段 vs 整个 b）。</summary>
    private static double NccAt(float[] a, int offset, float[] b, int length = -1)
    {
        length = length <= 0 ? Math.Min(b.Length, a.Length - offset) : Math.Min(length, Math.Min(b.Length, a.Length - offset));
        if (length <= 1)
            return 0;

        double sumA = 0, sumA2 = 0, sumB = 0, sumB2 = 0, dot = 0;
        for (var i = 0; i < length; i++)
        {
            double va = a[offset + i];
            double vb = b[i];
            sumA += va;
            sumA2 += va * va;
            sumB += vb;
            sumB2 += vb * vb;
            dot += va * vb;
        }

        var covar = dot - sumA * sumB / length;
        var varA = sumA2 - sumA * sumA / length;
        var varB = sumB2 - sumB * sumB / length;
        var denom = Math.Sqrt(Math.Max(varA, 1e-18) * Math.Max(varB, 1e-18));
        return covar / denom;
    }

    /// <summary>按 <see cref="Decimation"/> 做滑动平均抽取。</summary>
    private static float[] Decimate(float[] src, int offset, int length)
    {
        var outLen = length / Decimation;
        var dst = new float[outLen];
        for (var i = 0; i < outLen; i++)
        {
            double sum = 0;
            var o = offset + i * Decimation;
            for (var k = 0; k < Decimation; k++)
                sum += src[o + k];
            dst[i] = (float)(sum / Decimation);
        }

        return dst;
    }
}

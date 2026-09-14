using System;
using System.Collections.Generic;
using System.Linq;

namespace BellTimeCalibration.Services;

/// <summary>
/// 铃声模板（v0.9.0）：从「已截取的模板 WAV」或「长录音样本 + 截取区间」构建，
/// 派生两个匹配特征：<b>10 ms 能量包络</b>（用于粗定位）与 <b>32 个对数分布频点的强度向量</b>（音色确认）。
/// 全程只读文件、只占内存，不写任何文件。
/// </summary>
public sealed class BellTemplate
{
    /// <summary>包络跳距（采样）：10 ms @48 kHz。</summary>
    public const int EnvelopeHopSamples = 480;

    /// <summary>音色特征窗长（秒）：取匹配点起的这一段做 Goertzel 强度向量。</summary>
    public const double SpectrumWindowSeconds = 0.5;

    /// <summary>音色特征频点数（对数分布 100 Hz – 8 kHz）。</summary>
    public const int SpectrumPoints = 32;

    /// <summary>自动截取时要求的最短持续时长（秒）。</summary>
    public const double AutoTrimMinSeconds = 1.5;

    /// <summary>模板默认时长（秒）。</summary>
    public const double DefaultTemplateSeconds = 2.5;

    /// <summary>事件边界相对峰值的默认落差（dB），见 <see cref="FindBellEvent"/>。</summary>
    public const double EventDropDb = 12;

    /// <summary>事件内相邻连续段的合并间隔（秒）：铃声内部起伏不判为两次事件。</summary>
    public const double EventMergeGapSeconds = 0.35;

    /// <summary>判定为「一次铃声事件」的最短时长（秒）：0.1~0.2 s 的环境瞬态（椅子/关门）据此排除。</summary>
    public const double MinEventSeconds = 0.45;

    /// <summary>自动截取时起点最多前移到多久之前的同强度事件（秒）：用于覆盖一次铃声的首下击打。</summary>
    public const double MaxEventLeadSeconds = 3.0;

    /// <summary>包络跳距的秒数（10 ms）。</summary>
    public const double HopSeconds = EnvelopeHopSamples / (double)WavReader.TargetSampleRate;

    /// <summary>模板标识（"class"/"break"/文件名）。</summary>
    public string Id { get; }

    /// <summary>中文标签（上课铃/下课铃），用于日志。</summary>
    public string Label { get; }

    /// <summary>48 kHz 单声道样本（已去直流、峰值归一化到 1.0）。</summary>
    public float[] Samples { get; }

    /// <summary>10 ms 步长的能量包络（已去均值、单位方差归一化前的原始能量）。</summary>
    public double[] Envelope { get; }

    /// <summary>音色特征：32 点强度的单位向量。</summary>
    public double[] Spectrum { get; }

    /// <summary>模板时长（秒）。</summary>
    public double DurationSeconds => Samples.Length / (double)WavReader.TargetSampleRate;

    private BellTemplate(string id, string label, float[] samples)
    {
        Id = id;
        Label = label;
        Samples = samples;
        Envelope = ComputeEnvelope(samples, 0, samples.Length);
        var windowLen = Math.Min(samples.Length, (int)(SpectrumWindowSeconds * WavReader.TargetSampleRate));
        Spectrum = ComputeSpectrum(samples, 0, windowLen);
    }

    /// <summary>
    /// 从「已截取的模板 WAV」加载。
    /// </summary>
    /// <param name="path">WAV 路径（建议 48 kHz 单声道，2~3 秒）。</param>
    /// <param name="id">模板标识。</param>
    /// <param name="label">中文标签。</param>
    /// <param name="note">说明（中文，供日志）。</param>
    public static BellTemplate? LoadFromTemplateFile(string path, string id, string label, out string note)
    {
        var samples = WavReader.ReadMono48k(path, out var readNote);
        if (samples == null || samples.Length == 0)
        {
            note = $"读取模板失败（{path}）：{readNote}";
            return null;
        }

        note = $"模板 {label} ← {System.IO.Path.GetFileName(path)}（{readNote}）";
        return new BellTemplate(id, label, Normalize(samples));
    }

    /// <summary>
    /// 从「长录音样本」加载：按 <paramref name="trimSpec"/>（"起始秒-结束秒"）截取；
    /// trimSpec 为空时自动取「最响且持续 ≥ <see cref="AutoTrimMinSeconds"/> 秒」的段，并截取
    /// <see cref="DefaultTemplateSeconds"/> 秒。
    /// </summary>
    /// <param name="path">录音文件（WAV）。</param>
    /// <param name="id">模板标识。</param>
    /// <param name="label">中文标签。</param>
    /// <param name="trimSpec">截取区间，如 "12.0-14.5"；空=自动。</param>
    /// <param name="note">说明（中文，供日志）。</param>
    public static BellTemplate? LoadFromRecording(string path, string id, string label, string? trimSpec, out string note)
    {
        var samples = WavReader.ReadMono48k(path, out var readNote);
        if (samples == null || samples.Length == 0)
        {
            note = $"读取样本失败（{path}）：{readNote}";
            return null;
        }

        int start, count;
        if (TryParseTrim(trimSpec, out var startSec, out var endSec))
        {
            start = Math.Max(0, (int)(startSec * WavReader.TargetSampleRate));
            var end = Math.Min(samples.Length, (int)(endSec * WavReader.TargetSampleRate));
            count = Math.Max(0, end - start);
        }
        else
        {
            start = AutoFindLoudestSegment(samples);
            count = Math.Min(samples.Length - start, (int)(DefaultTemplateSeconds * WavReader.TargetSampleRate));
        }

        if (count < (int)(AutoTrimMinSeconds * WavReader.TargetSampleRate) / 2)
        {
            note = $"截取区间过短或定位失败（{path}）";
            return null;
        }

        var clip = new float[count];
        Array.Copy(samples, start, clip, 0, count);

        note = $"模板 {label} ← {System.IO.Path.GetFileName(path)} 的 {start / (double)WavReader.TargetSampleRate:F2}s–" +
               $"{(start + count) / (double)WavReader.TargetSampleRate:F2}s（{(trimSpec is null or "" ? "自动截取" : trimSpec)}；{readNote}）";
        return new BellTemplate(id, label, Normalize(clip));
    }

    /// <summary>解析 "起始-结束" 秒区间；空/非法返回 false。</summary>
    public static bool TryParseTrim(string? spec, out double startSeconds, out double endSeconds)
    {
        startSeconds = endSeconds = 0;
        if (string.IsNullOrWhiteSpace(spec))
            return false;

        var parts = spec.Split(new[] { '-', '~', '～', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2
            || !double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out startSeconds)
            || !double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out endSeconds))
        {
            return false;
        }

        return endSeconds > startSeconds && startSeconds >= 0;
    }

    /// <summary>
    /// 自动找「一次响亮铃声事件」的起点采样索引，供长录音截取模板。
    /// v0.9.1 判据（三段式，取代旧版「最长的超阈值连续段」）：
    /// <list type="number">
    /// <item><description>阈值 = 包络峰值 − <see cref="EventDropDb"/> dB，取全部超阈值连续段；</description></item>
    /// <item><description>把间隔 ≤<see cref="EventMergeGapSeconds"/> 的段合并为一次<b>事件</b>（铃声内部有起伏，不合并会把一次铃拆成碎段）；</description></item>
    /// <item><description>在时长 ≥<see cref="MinEventSeconds"/> 的事件里取<b>峰值最高</b>者；无合格事件则退回全部事件中峰值最高者。</description></item>
    /// </list>
    /// 为什么不能只看「最长」或「最响」：实测（2026-09-11）本机麦样本里，录音尾部 30–37 s 的几声
    /// 环境瞬态（椅子/关门级）在 10 ms 尺度上比真正的铃声还高 6~7 dB，但它们只持续 0.1~0.2 s；
    /// 而旧判据「最长」在削掉数字静默的样本上会选中长达数十秒的环境尾巴。
    /// 合并 + 最短时长把二者一起挡掉。对旧手机样本仍选出 13.13 s 处那次事件（与现役模板同源），行为不退化。
    /// </summary>
    public static int AutoFindLoudestSegment(float[] samples)
    {
        var ev = FindBellEvent(samples);
        return ev.StartSample;
    }

    /// <summary>
    /// 定位一次铃声事件（v0.9.1，供自动截取与离线工具告警用）：
    /// 阈值取「包络峰值 − <paramref name="eventDropDb"/> dB」（相对判据——本机麦降噪样本静默段低至 −87 dB、
    /// 旧手机样本甚至 −240 dB 下溢，绝对阈值必然失准），合并间隔 ≤<see cref="EventMergeGapSeconds"/> 的连续段，
    /// 在时长 ≥<see cref="MinEventSeconds"/> 的事件中取峰值最高者。
    /// </summary>
    /// <param name="samples">48 kHz 单声道样本。</param>
    /// <param name="eventDropDb">事件边界相对峰值的落差（dB）。</param>
    public static BellEvent FindBellEvent(float[] samples, double eventDropDb = EventDropDb)
    {
        var env = ComputeEnvelope(samples, 0, samples.Length);
        if (env.Length == 0)
            return new BellEvent(0, 0, double.NegativeInfinity, 0, "样本为空");

        var peak = env.Max();
        var threshold = peak * Math.Pow(10, -eventDropDb / 20.0);
        var mergeHops = Math.Max(1, (int)(EventMergeGapSeconds / HopSeconds));
        var minHops = Math.Max(1, (int)(MinEventSeconds / HopSeconds));

        // ① 全部超阈值连续段
        var runs = new List<(int Start, int End, double Peak)>();
        var runStart = -1;
        var runPeak = 0.0;
        for (var i = 0; i < env.Length; i++)
        {
            if (env[i] >= threshold)
            {
                if (runStart < 0)
                {
                    runStart = i;
                    runPeak = env[i];
                }
                else if (env[i] > runPeak)
                {
                    runPeak = env[i];
                }
            }
            else if (runStart >= 0)
            {
                runs.Add((runStart, i, runPeak));
                runStart = -1;
            }
        }

        if (runStart >= 0)
            runs.Add((runStart, env.Length, runPeak));

        if (runs.Count == 0)
        {
            // 没有任何超阈值段（整段近乎恒定）→ 退回最响点
            var peakIndex = Array.IndexOf(env, peak);
            var fallback = peakIndex * EnvelopeHopSamples;
            return new BellEvent(fallback, fallback, ToDb(peak), fallback, "未找到超阈值段，取最响点");
        }

        // ② 合并为「事件」
        var events = new List<(int Start, int End, double Peak)>();
        foreach (var r in runs)
        {
            if (events.Count == 0)
            {
                events.Add(r);
                continue;
            }

            var last = events[^1];
            if (r.Start - last.End <= mergeHops)
                events[^1] = (last.Start, Math.Max(last.End, r.End), Math.Max(last.Peak, r.Peak));
            else
                events.Add(r);
        }

        // ③ 在合格事件中取峰值最高者（无合格事件则放宽到全部事件）
        var qualified = events.Where(e => e.End - e.Start >= minHops).ToList();
        var pool = qualified.Count > 0 ? qualified : events;
        var bestIndex = 0;
        for (var i = 1; i < pool.Count; i++)
        {
            if (pool[i].Peak > pool[bestIndex].Peak
                || (pool[i].Peak == pool[bestIndex].Peak
                    && pool[i].End - pool[i].Start > pool[bestIndex].End - pool[bestIndex].Start))
            {
                bestIndex = i;
            }
        }

        var best = pool[bestIndex];

        // 一次铃声常由多次击打构成：若整串击打里还有「更早、但峰值不低于最高峰 3 dB」的事件，
        // 就把起点前移到它，让自动截取的 2.5 s 覆盖整串击打的第一下（而不是从中间某一下开始）。
        var strongPeak = best.Peak * Math.Pow(10, -3 / 20.0);
        var leadLimit = (int)(MaxEventLeadSeconds / HopSeconds);
        var startHop = best.Start;
        for (var i = 0; i < events.Count; i++)
        {
            if (events[i].Start >= best.Start)
                break;

            if (events[i].Peak >= strongPeak && best.Start - events[i].Start <= leadLimit)
                startHop = events[i].Start;
        }

        var endHopLimit = best.End;
        best = (startHop, endHopLimit, best.Peak);

        var start = best.Start * EnvelopeHopSamples;
        var end = Math.Min(samples.Length, best.End * EnvelopeHopSamples);

        var peakHop = best.Start;
        for (var i = best.Start; i < best.End; i++)
        {
            if (env[i] > env[peakHop])
                peakHop = i;
        }        var peakDb = ToDb(best.Peak);
        var note = $"{start / (double)WavReader.TargetSampleRate:F2}s–{end / (double)WavReader.TargetSampleRate:F2}s" +
                   $"（时长 {(end - start) / (double)WavReader.TargetSampleRate:F2}s，峰值 {peakDb:F1} dBFS" +
                   $"{(qualified.Count > 0 ? "" : "；无 ≥" + MinEventSeconds.ToString("F1") + "s 的候选事件，已放宽")}）";
        return new BellEvent(start, end, peakDb, peakHop * EnvelopeHopSamples, note);
    }

    private static double ToDb(double linear) => 20 * Math.Log10(Math.Max(linear, 1e-12));


    /// <summary>去直流 + 峰值归一化。</summary>
    public static float[] Normalize(float[] samples)
    {
        if (samples.Length == 0)
            return samples;

        double mean = 0;
        foreach (var s in samples)
            mean += s;
        mean /= samples.Length;

        var peak = 0.0;
        var tmp = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            tmp[i] = (float)(samples[i] - mean);
            peak = Math.Max(peak, Math.Abs(tmp[i]));
        }

        if (peak > 1e-9)
        {
            for (var i = 0; i < tmp.Length; i++)
                tmp[i] = (float)(tmp[i] / peak);
        }

        return tmp;
    }

    /// <summary>10 ms 步长的 RMS 能量包络。</summary>
    public static double[] ComputeEnvelope(float[] samples, int offset, int length)
    {
        var hopCount = Math.Max(0, (length - EnvelopeHopSamples) / EnvelopeHopSamples + 1);
        var env = new double[hopCount];
        for (var h = 0; h < hopCount; h++)
        {
            var o = offset + h * EnvelopeHopSamples;
            double sum = 0;
            for (var i = 0; i < EnvelopeHopSamples; i++)
            {
                var v = samples[o + i];
                sum += v * v;
            }

            env[h] = Math.Sqrt(sum / EnvelopeHopSamples);
        }

        return env;
    }

    /// <summary>
    /// 32 点对数分布频点的强度向量（单位向量）；用于音色确认。
    /// v0.9.0 起在**对数域（dB）去均值**后再归一化：模板来自手机录音、实机用电脑麦克风，
    /// 绝对增益与频响差异会让线性强度向量相似度骤降（实机仅 0.55~0.75），而 dB 域去均值只比较频段
    /// 相对形状，对增益/设备差异稳健得多。
    /// </summary>
    public static double[] ComputeSpectrum(float[] samples, int offset, int length)
    {
        var vector = new double[SpectrumPoints];
        if (length <= 0)
            return vector;

        var low = 100.0;
        var high = 8000.0;
        var ratio = Math.Pow(high / low, 1.0 / (SpectrumPoints - 1));
        for (var p = 0; p < SpectrumPoints; p++)
        {
            var freq = low * Math.Pow(ratio, p);
            var magnitude = Goertzel(samples, offset, length, freq, WavReader.TargetSampleRate);
            vector[p] = 20 * Math.Log10(Math.Max(magnitude, 1e-9));
        }

        var mean = vector.Average();
        for (var i = 0; i < vector.Length; i++)
            vector[i] -= mean;

        return Normalize(vector);
    }

    /// <summary>Goertzel 单频强度。</summary>
    private static double Goertzel(float[] x, int offset, int length, double freq, int sampleRate)
    {
        var w = 2 * Math.PI * freq / sampleRate;
        var c = 2 * Math.Cos(w);
        double s1 = 0, s2 = 0;
        for (var i = 0; i < length; i++)
        {
            var s0 = x[offset + i] + c * s1 - s2;
            s2 = s1;
            s1 = s0;
        }

        return Math.Sqrt(Math.Max(0, s1 * s1 + s2 * s2 - c * s1 * s2)) / length;
    }

    /// <summary>向量归一化（零向量原样返回）。</summary>
    public static double[] Normalize(double[] v)
    {
        var norm = Math.Sqrt(v.Sum(x => x * x));
        if (norm <= 1e-12)
            return v;

        for (var i = 0; i < v.Length; i++)
            v[i] /= norm;
        return v;
    }

    /// <summary>余弦相似度（输入应为单位向量）。</summary>
    public static double CosineSimilarity(double[] a, double[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        double dot = 0;
        for (var i = 0; i < n; i++)
            dot += a[i] * b[i];
        return Math.Clamp(dot, -1, 1);
    }
}

/// <summary>
/// 一段铃声事件（v0.9.1，见 <see cref="BellTemplate.FindBellEvent"/>）。
/// </summary>
/// <param name="StartSample">事件起点（采样索引，含）。</param>
/// <param name="EndSample">事件终点（采样索引，不含）。</param>
/// <param name="PeakDb">事件峰值电平（dBFS）。</param>
/// <param name="PeakSample">峰值所在采样索引。</param>
/// <param name="Note">说明（中文，供日志）。</param>
public readonly record struct BellEvent(
    int StartSample,
    int EndSample,
    double PeakDb,
    int PeakSample,
    string Note)
{
    /// <summary>事件时长（秒）。</summary>
    public double DurationSeconds => (EndSample - StartSample) / (double)WavReader.TargetSampleRate;
}

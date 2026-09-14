using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BellTimeCalibration.Services;

namespace BellTimeCalibration.Tools;

/// <summary>
/// 模板工具（离线，开发用）：
/// <list type="bullet">
/// <item><description><c>extract &lt;样本.wav&gt; &lt;输出.wav&gt; [起始秒-结束秒] [时长秒]</c>：从长录音截取铃声模板（区间留空=自动取最响持续段）。</description></item>
/// <item><description><c>selftest &lt;模板.wav&gt; &lt;录音.wav&gt; [nccMin] [specMin]</c>：用生产匹配器在录音上逐点探测，输出命中位置与分离度（正/负样本 NCC 对比）。</description></item>
/// </list>
/// 仅开发期使用，不随插件打包。
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine(Usage);
            return 1;
        }

        return args[0].ToLowerInvariant() switch
        {
            "extract" => Extract(args),
            "selftest" => SelfTest(args),
            "scan" => Scan(args),
            "match" => Match(args),
            "probe" => Probe(args),
            "xcorr" => XCorr(args),
            _ => Unknown()
        };
    }

    private const string Usage = """
        用法：
          TemplateTool extract  <样本.wav> <输出模板.wav> [起始秒-结束秒] [时长秒]
          TemplateTool selftest <模板.wav> <录音.wav> [nccMin=0.6] [specMin=0.45] [探针半径秒=0.2]
          TemplateTool scan     <录音.wav> [边界相对峰值dB=12]
          TemplateTool match    <模板.wav> <录音.wav> [起始秒] [结束秒] [nccMin=0.6] [specMin=0.45]
          TemplateTool probe    <录音.wav> [起始秒=0] [结束秒=全部] [步长秒=0.2]
          TemplateTool xcorr    <A.wav> <B.wav>   # 纯波形最大归一化互相关（无任何门槛，供段与段互为对照）
        """;

    private static int Unknown()
    {
        Console.WriteLine(Usage);
        return 1;
    }

    /// <summary>
    /// 两段音频的纯波形相似度：在两者重叠范围内滑动，取归一化互相关的最大值与对应位移。
    /// 与 <c>selftest</c>/<c>match</c> 的区别：**不施加任何阈值、音色或攻击沿判据**，只回答
    /// 「这两段波形有多像」。用途是把「实机录到的铃声段」两两互为对照、找出同一种铃的波形变体 ——
    /// 用带门槛的匹配器做互比时，读数会被门槛与重采逻辑污染（同一对段可读到 0.04 或 0.19）。
    /// </summary>
    private static int XCorr(string[] args)
    {
        if (args.Length < 3)
        {
            Console.WriteLine(Usage);
            return 1;
        }

        var a = WavReader.ReadMono48k(args[1], out var noteA);
        if (a == null) { Console.WriteLine($"读取 A 失败：{noteA}"); return 2; }
        var b = WavReader.ReadMono48k(args[2], out var noteB);
        if (b == null) { Console.WriteLine($"读取 B 失败：{noteB}"); return 2; }

        // 以短者为模板，在长者上滑动；1/12 抽取（4 kHz）足够比对 10 ms 级结构，且快 12 倍
        var (tpl, rec) = a.Length <= b.Length ? (a, b) : (b, a);
        const int dec = 12;
        var tplLen = tpl.Length / dec;
        var recLen = rec.Length / dec;
        if (tplLen < 4 || recLen < tplLen)
        {
            Console.WriteLine("片段过短，无法比对。");
            return 3;
        }

        var tplDec = new double[tplLen];
        var recDec = new double[recLen];
        for (var i = 0; i < tplLen; i++)
        {
            double s = 0;
            for (var k = 0; k < dec; k++) s += tpl[i * dec + k];
            tplDec[i] = s / dec;
        }
        for (var i = 0; i < recLen; i++)
        {
            double s = 0;
            for (var k = 0; k < dec; k++) s += rec[i * dec + k];
            recDec[i] = s / dec;
        }

        var bestNcc = double.NegativeInfinity;
        var bestLag = 0;
        var tplMean = tplDec.Average();
        var tplC = tplDec.Select(v => v - tplMean).ToArray();
        var tplNorm = Math.Sqrt(tplC.Sum(v => v * v));

        for (var lag = 0; lag + tplLen <= recLen; lag++)
        {
            double sum = 0, sumSq = 0, dot = 0;
            for (var i = 0; i < tplLen; i++)
            {
                var v = recDec[lag + i];
                sum += v;
                sumSq += v * v;
                dot += v * tplC[i];
            }
            var mean = sum / tplLen;
            var varSum = sumSq - tplLen * mean * mean;
            if (varSum <= 1e-12) continue;
            var score = dot / (Math.Sqrt(varSum) * tplNorm);
            if (score > bestNcc) { bestNcc = score; bestLag = lag; }
        }

        Console.WriteLine($"A：{Path.GetFileName(args[1])}（{noteA}）");
        Console.WriteLine($"B：{Path.GetFileName(args[2])}（{noteB}）");
        Console.WriteLine($"最大波形互相关 NCC = {bestNcc:F3}" +
                          $"（位移 {bestLag * dec / (double)WavReader.TargetSampleRate:F3}s，" +
                          $"{(a.Length <= b.Length ? "A 在 B 内" : "B 在 A 内")}）");
        return bestNcc >= 0.6 ? 0 : 3;
    }

    /// <summary>
    /// 逐点打印短时 RMS 与「攻击沿比值」（峰值 0.5 s ÷ 基线 0.3 s）：定位真铃声到底在哪几秒、
    /// 以及为什么某处被判为「无攻击沿」。诊断匹配失败时的第一手段。
    /// </summary>
    private static int Probe(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine(Usage);
            return 1;
        }

        var rec = WavReader.ReadMono48k(args[1], out var recNote);
        if (rec == null)
        {
            Console.WriteLine($"录音加载失败：{recNote}");
            return 2;
        }

        var total = rec.Length / (double)WavReader.TargetSampleRate;
        var fromSec = args.Length > 2 && double.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 0;
        var toSec = args.Length > 3 && double.TryParse(args[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t : total;
        var step = args.Length > 4 && double.TryParse(args[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 0.2;

        Console.WriteLine($"录音：{recNote}");
        Console.WriteLine("  时间(s)   短时RMS      攻击比值");
        for (var sec = fromSec; sec < Math.Min(toSec, total); sec += step)
        {
            var center = (int)(sec * WavReader.TargetSampleRate);
            var rms = WindowRms(rec, Math.Max(0, center - (int)(step * WavReader.TargetSampleRate)), center);
            var baseRms = WindowRms(rec, Math.Max(0, center - (int)(0.3 * WavReader.TargetSampleRate)), center);
            var peakRms = WindowRms(rec, center, Math.Min(rec.Length, center + (int)(0.5 * WavReader.TargetSampleRate)));
            var ratio = peakRms / Math.Max(baseRms, 1e-9);
            Console.WriteLine($"{sec,9:F1}  {20 * Math.Log10(Math.Max(rms, 1e-12)),8:F1} dB  {ratio,9:F1}x {(ratio >= 4 ? "攻击沿" : "")}");
        }

        return 0;
    }

    private static double WindowRms(float[] samples, int from, int to)
    {
        if (to <= from)
            return 0;
        double sum = 0;
        for (var i = from; i < to; i++)
            sum += (double)samples[i] * samples[i];
        return Math.Sqrt(sum / (to - from));
    }

    /// <summary>
    /// 在指定区间内做一次生产级匹配（等价于插件在窗口内的一次搜索），打印对齐点与判定。
    /// 与 selftest 的区别：不做 0.5 s 步长探针网格，直接搜索整个给定区间，所以报出的对齐点就是插件会用的那个。
    /// </summary>
    private static int Match(string[] args)
    {
        if (args.Length < 3)
        {
            Console.WriteLine(Usage);
            return 1;
        }

        var tpl = BellTemplate.LoadFromTemplateFile(args[1], "t", Path.GetFileNameWithoutExtension(args[1]), out var tplNote);
        if (tpl == null)
        {
            Console.WriteLine($"模板加载失败：{tplNote}");
            return 2;
        }

        var rec = WavReader.ReadMono48k(args[2], out var recNote);
        if (rec == null)
        {
            Console.WriteLine($"录音加载失败：{recNote}");
            return 2;
        }

        var total = rec.Length / (double)WavReader.TargetSampleRate;
        var fromSec = args.Length > 3 && double.TryParse(args[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 0;
        var toSec = args.Length > 4 && double.TryParse(args[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t : total;
        var nccMin = args.Length > 5 && double.TryParse(args[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0.60;
        var specMin = args.Length > 6 && double.TryParse(args[6], NumberStyles.Float, CultureInfo.InvariantCulture, out var sp) ? sp : 0.45;

        var fromSample = Math.Max(0, (int)(fromSec * WavReader.TargetSampleRate));
        var toSample = Math.Min(rec.Length - tpl.Samples.Length, (int)(toSec * WavReader.TargetSampleRate));
        if (toSample <= fromSample)
        {
            Console.WriteLine($"区间无效：{fromSec:F2}s–{toSec:F2}s（录音 {total:F2}s）");
            return 3;
        }

        var m = TemplateMatcher.Match(tpl, rec, rec.Length, fromSample, toSample, nccMin, specMin);
        Console.WriteLine($"模板：{tplNote}");
        Console.WriteLine($"录音：{recNote}");
        Console.WriteLine($"搜索区间：{fromSec:F2}s–{toSec:F2}s（阈值 NCC ≥{nccMin:F2}、音色 ≥{specMin:F2}）");
        Console.WriteLine(m.Accepted
            ? $"命中：对齐点 {m.OnsetSampleIndex / (double)WavReader.TargetSampleRate:F3}s，NCC={m.Ncc:F3} 音色={m.SpectralSim:F3}"
            : $"未命中：对齐点 {(m.OnsetSampleIndex < 0 ? "n/a" : (m.OnsetSampleIndex / (double)WavReader.TargetSampleRate).ToString("F3") + "s")}，{m.Note}");
        return m.Accepted ? 0 : 3;
    }

    /// <summary>
    /// 打印录音的铃声事件定位（起止/时长/峰值）与自动截取落点：调参时先用它确认「铃声到底在哪几秒」。
    /// </summary>
    private static int Scan(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine(Usage);
            return 1;
        }

        var dropDb = args.Length > 2 && double.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d
            : BellTemplate.EventDropDb;

        var rec = WavReader.ReadMono48k(args[1], out var recNote);
        if (rec == null)
        {
            Console.WriteLine($"录音加载失败：{recNote}");
            return 2;
        }

        var ev = BellTemplate.FindBellEvent(rec, dropDb);
        var autoStart = BellTemplate.AutoFindLoudestSegment(rec);

        Console.WriteLine($"录音：{recNote}");
        Console.WriteLine($"事件（相对峰值 −{dropDb:F0} dB 截断）：{ev.Note}");
        Console.WriteLine($"自动截取落点：{autoStart / (double)WavReader.TargetSampleRate:F2}s" +
                          $"（起截 {BellTemplate.DefaultTemplateSeconds:F1}s）");
        if (ev.DurationSeconds < BellTemplate.MinEventSeconds)
        {
            Console.WriteLine($"警告：未找到 ≥{BellTemplate.MinEventSeconds:F2}s 的铃声事件，录音里可能没有铃声（或全是瞬态）。");
            return 3;
        }

        return 0;
    }

    /// <summary>从长录音截取模板并写成 48 kHz 单声道 PCM16 WAV。</summary>
    private static int Extract(string[] args)
    {
        if (args.Length < 3)
        {
            Console.WriteLine(Usage);
            return 1;
        }

        var samplePath = args[1];
        var outPath = args[2];
        var trim = args.Length > 3 ? args[3] : "";
        var seconds = args.Length > 4 && double.TryParse(args[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var s)
            ? s
            : BellTemplate.DefaultTemplateSeconds;

        var tpl = BellTemplate.LoadFromRecording(samplePath, "tmp", Path.GetFileNameWithoutExtension(samplePath), trim, out var note);
        if (tpl == null)
        {
            Console.WriteLine($"提取失败：{note}");
            return 2;
        }

        // 自动截取（未给区间）时打印事件定位，便于人工确认截到的是铃声而不是环境噪声
        if (BellTemplate.TryParseTrim(trim, out _, out _) == false)
        {
            var src = WavReader.ReadMono48k(samplePath, out _);
            if (src != null)
            {
                var ev = BellTemplate.FindBellEvent(src);
                Console.WriteLine($"自动定位事件：{ev.Note}");
                if (ev.DurationSeconds < BellTemplate.MinEventSeconds)
                {
                    Console.WriteLine($"警告：未找到 ≥{BellTemplate.MinEventSeconds:F2}s 的铃声事件，" +
                                      "自动截取结果可能落在环境瞬态上，请显式给出区间。");
                }
            }
        }

        // 若需要指定时长，则按需截断（LoadFromRecording 默认 2.5s）
        var wanted = (int)(seconds * WavReader.TargetSampleRate);
        var samples = tpl.Samples.Length > wanted && wanted > 0
            ? tpl.Samples[..wanted]
            : tpl.Samples;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        WriteWav(outPath, samples);
        Console.WriteLine($"已写出模板：{outPath}");
        Console.WriteLine($"  来源：{note}");
        Console.WriteLine($"  时长：{samples.Length / (double)WavReader.TargetSampleRate:F2}s，峰值归一化，48kHz 单声道 PCM16");
        return 0;
    }

    /// <summary>用生产匹配器在录音上探测若干位置，输出命中与分离度。</summary>
    private static int SelfTest(string[] args)
    {
        if (args.Length < 3)
        {
            Console.WriteLine(Usage);
            return 1;
        }

        var nccMin = args.Length > 3 && double.TryParse(args[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0.60;
        var specMin = args.Length > 4 && double.TryParse(args[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var sp) ? sp : 0.45;
        // v0.9.1：探针半径（秒）。默认 ±0.2s 只为「定位」——跨不过铃声攻击沿，NCC 会被系统性低估
        // （实测：dump 里同一位置，±0.2s 探针给 0.670，插件全区间搜索给 0.968）。
        // 想读「插件会遇到的那个 NCC」就把半径放大到覆盖整段（如 15），此时 0.5s 步长下第一个探针即全区间最优。
        var radiusSec = args.Length > 5 && double.TryParse(args[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var rr) ? rr : 0.2;

        var tpl = BellTemplate.LoadFromTemplateFile(args[1], "t", Path.GetFileNameWithoutExtension(args[1]), out var tplNote);
        if (tpl == null)
        {
            Console.WriteLine($"模板加载失败：{tplNote}");
            return 2;
        }

        var rec = WavReader.ReadMono48k(args[2], out var recNote);
        if (rec == null)
        {
            Console.WriteLine($"录音加载失败：{recNote}");
            return 2;
        }

        Console.WriteLine($"模板：{tplNote}");
        Console.WriteLine($"录音：{recNote}");
        Console.WriteLine($"阈值：NCC ≥ {nccMin:F2}，音色 ≥ {specMin:F2}；探针半径 ±{radiusSec:F2}s");
        Console.WriteLine();

        // 全区间扫描：把录音切成若干 0.4 s 探针窗口，逐点调用生产匹配器
        var radius = (int)(radiusSec * WavReader.TargetSampleRate);
        var probes = new List<(double ProbeSec, double OnsetSec, double Ncc, double Spec, bool Accepted)>();
        for (var sec = 0.0; sec + tpl.DurationSeconds < rec.Length / (double)WavReader.TargetSampleRate; sec += 0.5)
        {
            var center = (int)(sec * WavReader.TargetSampleRate);
            var from = Math.Max(0, center - radius);
            var to = Math.Min(rec.Length - tpl.Samples.Length, center + radius);
            if (to <= from)
                continue;

            var m = TemplateMatcher.Match(tpl, rec, rec.Length, from, to, nccMin, specMin);
            probes.Add((sec, m.OnsetSampleIndex / (double)WavReader.TargetSampleRate, m.Ncc, m.SpectralSim, m.Accepted));
        }

        // v0.9.1 修复：录音不短于模板时长时一个探针都排不出来，旧实现在此抛 InvalidOperationException
        if (probes.Count == 0)
        {
            Console.WriteLine(
                $"无法探测：录音时长 {rec.Length / (double)WavReader.TargetSampleRate:F2}s 不足以容纳模板 " +
                $"（{tpl.DurationSeconds:F2}s）+ 滑动步长。请给出更长的录音，或对两段模板改用更长的样本文件比较。");
            return 3;
        }

        var best = probes.MaxBy(p => p.Ncc);
        var accepted = probes.Where(p => p.Accepted).ToList();
        var bestAccepted = accepted.Count > 0 ? accepted.MaxBy(p => p.Ncc) : (0, 0, 0, 0, false);
        Console.WriteLine("探针(秒)  对齐点(秒)   NCC     音色     命中");
        foreach (var p in probes)
        {
            var mark = Math.Abs(p.ProbeSec - best.ProbeSec) < 0.001 ? " ←最高NCC" : "";
            Console.WriteLine($"{p.ProbeSec,8:F2}  {p.OnsetSec,10:F2}  {p.Ncc,6:F3}  {p.Spec,6:F3}   {(p.Accepted ? "是" : "否")}{mark}");
        }

        var rejected = probes.Where(p => !p.Accepted && Math.Abs(p.ProbeSec - best.ProbeSec) > 0.001).ToList();
        Console.WriteLine();
        Console.WriteLine($"最高 NCC：对齐点 {best.OnsetSec:F2}s NCC={best.Ncc:F3} 音色={best.Spec:F3}" +
                          $"（{(best.Accepted ? "通过全部判据" : "未通过判据，见下")}）");
        if (accepted.Count > 0)
        {
            Console.WriteLine($"最佳命中：对齐点 {bestAccepted.OnsetSec:F2}s NCC={bestAccepted.Ncc:F3} 音色={bestAccepted.Spec:F3}");
        }
        else
        {
            Console.WriteLine("最佳命中：无（全部探针均未通过 NCC/音色/攻击沿判据）");
        }

        Console.WriteLine($"命中数 {accepted.Count} / 探针数 {probes.Count}；" +
                          $"非最佳位置的平均 NCC={rejected.DefaultIfEmpty((0, 0, 0, 0, false)).Average(p => p.Ncc):F3}");
        var separation = best.Ncc - (rejected.Count > 0 ? rejected.Max(p => p.Ncc) : 0);
        Console.WriteLine($"分离度（最高 NCC − 其他位置最高 NCC）= {separation:F3}");
        if (accepted.Count > 0)
        {
            // 半径较大时每个探针都会收敛到同一个对齐点，按「对齐点」去重后再打印，否则重复几十行
            var byOnset = accepted.GroupBy(p => Math.Round(p.OnsetSec, 2))
                                  .Select(g => (Onset: g.Key, Best: g.MaxBy(p => p.Ncc)))
                                  .OrderBy(g => g.Onset);
            Console.WriteLine("命中位置：" + string.Join("、",
                byOnset.Select(g => $"{g.Onset:F2}s(NCC {g.Best.Ncc:F3}/音色 {g.Best.Spec:F3})")));
        }

        return best.Accepted ? 0 : 3;
    }

    /// <summary>写 48 kHz 单声道 PCM16 WAV。</summary>
    private static void WriteWav(string path, float[] samples)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);
        var dataBytes = samples.Length * 2;
        bw.Write("RIFF"u8.ToArray());
        bw.Write(36 + dataBytes);
        bw.Write("WAVE"u8.ToArray());
        bw.Write("fmt "u8.ToArray());
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)1);
        bw.Write(WavReader.TargetSampleRate);
        bw.Write(WavReader.TargetSampleRate * 2);
        bw.Write((short)2);
        bw.Write((short)16);
        bw.Write("data"u8.ToArray());
        bw.Write(dataBytes);
        foreach (var s in samples)
        {
            var v = (short)Math.Clamp(s * 32767.0, short.MinValue, short.MaxValue);
            bw.Write(v);
        }
    }
}

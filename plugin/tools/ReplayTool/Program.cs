using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BellTimeCalibration.Services;

namespace BellTimeCalibration.Tools;

/// <summary>
/// 回放工具：对已采集的实机 dump 跑一遍**与插件完全相同**的判定链，把每一步的数值打出来。
/// 用途：离线复核「这个边界插件会选到哪一点、为什么」——模板匹配（含攻击沿）与启发式选铃都在这里被真实调用，
/// 而不是靠外部脚本近似。
///
/// 用法：
///   ReplayTool &lt;dump.wav&gt; &lt;模板目录&gt; &lt;边界在dump内秒数&gt; [内核偏移秒=0.8] [搜索半窗秒=12]
///
/// 「边界在 dump 内秒数」= B_display − dump 起点墙钟；dump 起点由文件名（yyyyMMdd-HHmmss）给出，
/// 日志里的 B_display 与之相减即得（见 HANDOFF 第 8 节的换算方法）。
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.WriteLine("用法：");
            Console.WriteLine("  ReplayTool <dump.wav> <模板目录> <边界在dump内秒数> [内核偏移秒=-5.1] [搜索半窗秒=12]");
            Console.WriteLine("  ReplayTool fitter <样本序列文件> [死区秒=0.3]   # 离线验证偏移估计算法（每行：yyyy-MM-dd HH:mm:ss,所需偏移秒）");
            return 1;
        }

        if (string.Equals(args[0], "fitter", StringComparison.OrdinalIgnoreCase))
            return Fitter(args);

        var dumpPath = args[0];
        var tplDir = args[1];
        if (!double.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var boundaryInDumpSec))
        {
            Console.WriteLine("边界秒数无法解析。");
            return 1;
        }

        var kernelOffset = args.Length > 3 && double.TryParse(args[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var ko) ? ko : 0.8;
        var tolerance = args.Length > 4 && double.TryParse(args[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var tol) ? tol : 12.0;

        var samples = WavReader.ReadMono48k(dumpPath, out var readNote);
        if (samples == null)
        {
            Console.WriteLine($"读取失败：{readNote}");
            return 2;
        }

        Console.WriteLine($"dump：{Path.GetFileName(dumpPath)}（{readNote}）");
        Console.WriteLine($"边界在 dump 内：{boundaryInDumpSec:F3}s（内核偏移 {kernelOffset:F3}s，搜索半窗 ±{tolerance:F1}s）");
        Console.WriteLine();

        // ── 模板加载（与 TemplateLibrary 相同的规则：目录内全部 *.wav） ──
        var templates = Directory.EnumerateFiles(tplDir, "*.wav")
            .Select(f => BellTemplate.LoadFromTemplateFile(f, Path.GetFileNameWithoutExtension(f), Path.GetFileNameWithoutExtension(f), out _))
            .Where(t => t != null)
            .Select(t => t!)
            .ToList();
        Console.WriteLine($"模板 {templates.Count} 个：{string.Join("、", templates.Select(t => $"{t.Label} {t.DurationSeconds:F2}s"))}");

        // ── 模拟 CaptureAudio 的墙钟锚点：dump 起点 = 边界前 (边界在dump内) 秒 ──
        var audio = new CaptureAudio();
        var dumpStartUtc = DateTime.UtcNow.Date; // 占位，仅用于构造锚点；下面用相对量计算
        _ = dumpStartUtc;
        // CaptureAudio 只暴露 Write(block, wallUtc)，因此一次性写入整段并给出起点墙钟
        var anchorWallUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc).AddSeconds(-boundaryInDumpSec);
        audio.Write(samples, anchorWallUtc);

        // 边界（裸墙钟域，本地）：B_wall = dump起点本地 + 边界在dump内
        var dumpStartLocal = anchorWallUtc.ToLocalTime();
        var boundaryWallLocal = dumpStartLocal.AddSeconds(boundaryInDumpSec);

        var tolerate = TimeSpan.FromSeconds(tolerance);

        // ── 0) 起响沿闸门（v0.10.0 主路径）：与 CalibrationRunner 相同口径 ──
        var gate = OnsetGate.Find(audio, boundaryWallLocal, out var gateNote);
        if (gate != null)
        {
            var gateInDump = (gate.Value.OnsetUtc - anchorWallUtc).TotalSeconds;
            var gateDelta = (boundaryWallLocal - gate.Value.OnsetUtc.ToLocalTime()).TotalSeconds;
            Console.WriteLine($"起响沿闸门：命中（dump 内 {gateInDump:F3}s，delta={gateDelta:F3}s，比值 {gate.Value.Ratio:F1}×）");
            Console.WriteLine($"  明细：{gateNote}");
        }
        else
        {
            Console.WriteLine($"起响沿闸门：未命中 —— {gateNote}");
        }

        // ── 1) 模板匹配（与 CalibrationRunner 相同：搜索区间 = 静置期后 ∧ 边界 ±半窗） ──
        var boundaryUtc = boundaryWallLocal.ToUniversalTime();
        var settleSample = (int)(RingCandidateSelector.SettleSkipSeconds * CaptureAudio.SampleRate);
        var fromSample = Math.Max(settleSample, audio.SampleIndexAt(boundaryUtc - tolerate));
        var toSample = Math.Min(audio.Length, audio.SampleIndexAt(boundaryUtc + tolerate));
        Console.WriteLine($"搜索区间：[{fromSample / (double)CaptureAudio.SampleRate:F2}s ~ {toSample / (double)CaptureAudio.SampleRate:F2}s]（静置 {RingCandidateSelector.SettleSkipSeconds:F1}s 之后）");

        var note = "";
        var match = fromSample < toSample
            ? RingCandidateSelector.SelectByTemplate(templates, audio, fromSample, toSample, boundaryWallLocal, tolerate, 0.60, 0.45, out note)
            : null;
        Console.WriteLine($"模板匹配：{(match != null ? $"命中 {match.Value.Label} @ {match.Value.OnsetSampleIndex / (double)CaptureAudio.SampleRate:F3}s NCC={match.Value.Ncc:F3} 音色={match.Value.SpectralSim:F3}" : "未命中")}");
        Console.WriteLine($"  明细：{note}");
        Console.WriteLine();

        // ── 2) 启发式选铃（对整个捕获区做突发检测，再按与插件相同的口径选段） ──
        var detector = new RingDetector(2.0);
        const int blockFrames = 4800; // 100 ms
        for (var offset = 0; offset + blockFrames <= samples.Length; offset += blockFrames)
        {
            var block = new float[blockFrames];
            Array.Copy(samples, offset, block, 0, blockFrames);
            var blockWallUtc = anchorWallUtc.AddSeconds(offset / (double)CaptureAudio.SampleRate);
            detector.Feed(block, blockWallUtc);
        }

        detector.Flush();
        var postSettle = RingCandidateSelector.ExcludeSettlePeriod(detector.Bursts, anchorWallUtc);
        Console.WriteLine(
            $"启发式候选（仅供参考）：原始 {detector.Bursts.Count} 段 / 静置后 {postSettle.Count} 段" +
            $"（噪声底 {detector.NoiseFloorDb:F1} dBFS 峰值 {detector.MaxRmsDb:F1} dBFS）");

        // ── 2) 启发式选铃：**已移除**（v0.10.2）──
        // 实测其误差可达 −7.8~+8.1 s（选错事件），属「已确认不准的办法」，插件不再使用。
        // 这里只报告结论，不给估算值，避免离线复核时又被那个数带偏。
        var measuredByGate = gate != null;
        var measuredByTemplate = match != null;
        Console.WriteLine(measuredByGate
            ? "结论：本窗口由【起响沿闸门】给出可信测量。"
            : measuredByTemplate
                ? "结论：本窗口由【模板匹配】给出可信测量。"
                : "结论：**本窗口无可信测量** —— 闸门与模板均未命中；插件不会写入偏移（启发式兜底已移除）。");

        // ── 3) 攻击沿逐点（10 ms 步长，找真实起响点） ──
        Console.WriteLine();
        Console.WriteLine("有攻击沿的位置（比值 ≥4×，0.5 s 步长）：");
        for (var sec = 0.0; sec < samples.Length / (double)CaptureAudio.SampleRate - 0.5; sec += 0.5)
        {
            var center = (int)(sec * CaptureAudio.SampleRate);
            var baseRms = Rms(samples, Math.Max(0, center - (int)(0.3 * CaptureAudio.SampleRate)), center);
            var peakRms = Rms(samples, center, Math.Min(samples.Length, center + (int)(0.5 * CaptureAudio.SampleRate)));
            var ratio = peakRms / Math.Max(baseRms, 1e-9);
            if (ratio >= 4.0)
                Console.WriteLine($"  {sec,7:F1}s  比值 {ratio,6:F1}x");
        }

        return 0;
    }

    private static double Rms(float[] s, int from, int to)
    {
        if (to <= from)
            return 0;
        double sum = 0;
        for (var i = from; i < to; i++)
            sum += (double)s[i] * s[i];
        return Math.Sqrt(sum / (to - from));
    }

    /// <summary>
    /// 离线验证偏移估计算法（<see cref="DriftFitter"/>）：读入「时刻,所需偏移」样本序列，
    /// 逐步喂给生产类并打印每一步的估计值与「是否写入」。用途是在不碰实机的前提下，
    /// 用真实数据序列对比不同估计策略的稳定性（v0.11.0 由趋势外推改中位数即以此验证）。
    /// </summary>
    private static int Fitter(string[] args)
    {
        if (args.Length < 2 || !File.Exists(args[1]))
        {
            Console.WriteLine("找不到样本序列文件。");
            return 2;
        }

        var deadZone = args.Length > 2 && double.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var dz) ? dz : 0.3;

        var samples = new List<(DateTime At, double Required)>();
        foreach (var line in File.ReadAllLines(args[1]))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith("#"))
                continue;
            var parts = t.Split(',');
            if (parts.Length < 2)
                continue;
            if (DateTime.TryParse(parts[0].Trim(), CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var at)
                && double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var req))
            {
                samples.Add((at, req));
            }
        }

        if (samples.Count == 0)
        {
            Console.WriteLine("样本序列为空。");
            return 3;
        }

        Console.WriteLine($"样本 {samples.Count} 个；死区 ±{deadZone:F2}s");
        Console.WriteLine("  时刻                  所需偏移    估计值    与当前差   动作");
        var fitter = new DriftFitter();
        var currentOffset = samples[0].Required; // 起点假定为首次测量值（与实机无关，只看收敛性）
        foreach (var s in samples)
        {
            fitter.Add(s.At, TimeSpan.FromSeconds(s.Required));
            var est = fitter.PredictSeconds(s.At) ?? s.Required;
            var diff = est - currentOffset;
            var action = Math.Abs(diff) >= deadZone ? $"写入 → {est:F3}s" : "带内不写";
            if (Math.Abs(diff) >= deadZone)
                currentOffset = est;

            Console.WriteLine($"  {s.At:yyyy-MM-dd HH:mm:ss}  {s.Required,9:F3}s  {est,8:F3}s  {diff,8:F3}s   {action}");
            Console.WriteLine($"      └ {fitter.LastNote}");
        }

        Console.WriteLine();
        Console.WriteLine($"最终偏移 {currentOffset:F3}s；样本离散度 {fitter.MaxDeviationSeconds:F3}s；趋势启用={fitter.TrendEnabled}");
        return 0;
    }
}

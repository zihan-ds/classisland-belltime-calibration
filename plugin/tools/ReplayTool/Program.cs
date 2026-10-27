using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BellTimeCalibration.Services;

namespace BellTimeCalibration.Tools;

/// <summary>
/// 回放工具：对已采集的 dump 跑一遍**与插件完全相同**的判定链，把每一步的数值打出来。
/// 用途：离线复核「这个边界插件会选到哪一点、为什么」——闸门与模板匹配（含攻击沿）都在这里被真实调用，
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
        // 子命令先分派：它们不需要 dump 的三个位置参数
        if (args.Length > 0 && string.Equals(args[0], "fitter", StringComparison.OrdinalIgnoreCase))
            return Fitter(args);

        if (args.Length > 0 && string.Equals(args[0], "notify-test", StringComparison.OrdinalIgnoreCase))
            return NotifyTest();

        if (args.Length > 0 && string.Equals(args[0], "manual-fit", StringComparison.OrdinalIgnoreCase))
            return ManualFit(args);

        // 闸门候选诊断：单个 dump（gate-scan <dump> <边界秒>）或整个 Dumps 目录（gate-scan <目录>）
        if (args.Length > 0 && string.Equals(args[0], "gate-scan", StringComparison.OrdinalIgnoreCase))
            return GateScan(args);

        // 基线静默门槛的回归断言（gate-guard <Dumps 目录>）
        if (args.Length > 0 && string.Equals(args[0], "gate-guard", StringComparison.OrdinalIgnoreCase))
            return GateGuardTest(args);

        // 样本管理数据层的回归断言（sample-manage-test）
        if (args.Length > 0 && string.Equals(args[0], "sample-manage-test", StringComparison.OrdinalIgnoreCase))
            return SampleManageTest();

        // 「清除无用录音」的回归断言（dumps-clear-test）
        if (args.Length > 0 && string.Equals(args[0], "dumps-clear-test", StringComparison.OrdinalIgnoreCase))
            return DumpsClearTest();

        // 双窗口复核诊断（gate-reconcile <dump.wav> [当时偏移秒]）
        if (args.Length > 0 && string.Equals(args[0], "gate-reconcile", StringComparison.OrdinalIgnoreCase))
            return GateReconcile(args);

        // 双窗口复核的回归断言（gate-reconcile-test <Dumps 目录>）
        if (args.Length > 0 && string.Equals(args[0], "gate-reconcile-test", StringComparison.OrdinalIgnoreCase))
            return GateReconcileTest(args);

        // v1.1.2.0 四项修复的回归断言（bell-match-test <Dumps 目录> <模板目录>）
        if (args.Length > 0 && string.Equals(args[0], "bell-match-test", StringComparison.OrdinalIgnoreCase))
            return BellMatchTest(args);

        // 退化口径换算的回归断言（absolute-required-test）
        if (args.Length > 0 && string.Equals(args[0], "absolute-required-test", StringComparison.OrdinalIgnoreCase))
            return AbsoluteRequiredTest();

        // v1.1.0 写入策略与估计器的回归断言（estimate-test）
        if (args.Length > 0 && string.Equals(args[0], "estimate-test", StringComparison.OrdinalIgnoreCase))
            return EstimateTest();

        // 整段包络（envelope <dump.wav> [步长秒]）：回答「这段时间里到底有什么声音」
        if (args.Length > 0 && string.Equals(args[0], "envelope", StringComparison.OrdinalIgnoreCase))
            return Envelope(args);

        if (args.Length < 3)
        {
            Console.WriteLine("用法：");
            Console.WriteLine("  ReplayTool <dump.wav> <模板目录> <边界在dump内秒数> [内核偏移秒=-5.1] [搜索半窗秒=12]");
            Console.WriteLine("  ReplayTool fitter <样本序列文件> [死区秒=0.3]   # 离线验证偏移估计算法（每行：yyyy-MM-dd HH:mm:ss,所需偏移秒）");
            Console.WriteLine("  ReplayTool notify-test                        # 断言「人工审核提醒」的开关与阈值判定");
            Console.WriteLine("  ReplayTool sample-manage-test                 # 断言样本管理（人工标记 + 当时偏移列 + 采用偏移预览/两位小数）的数据层行为");
            Console.WriteLine("  ReplayTool dumps-clear-test                   # 断言「清除无用录音」只删陈年 wav（近期/断言夹具/非 wav/子目录一律不动）");
            Console.WriteLine("  ReplayTool manual-fit <offset-samples.jsonl> [yyyy-MM-dd]  # 跑一遍手动拟合（当天有效样本 → 偏移）");
            Console.WriteLine("  ReplayTool gate-scan <dump.wav> <边界在dump内秒数>   # 打印闸门候选表（比值/峰值）+ 两种选法对比");
            Console.WriteLine("  ReplayTool gate-scan <Dumps 目录>                    # 对目录内全部 dump（文件名含边界时刻）批量跑");
            Console.WriteLine("  ReplayTool gate-guard <Dumps 目录>                   # 断言「基线静默候选被排除」（含整段数字静默的样本）");
            Console.WriteLine("  ReplayTool envelope <dump.wav> [步长秒=0.1]          # 整段 RMS 包络（铃到底响在哪几段）");
            Console.WriteLine("  ReplayTool gate-reconcile <dump.wav> [当时偏移秒]    # 打印开关窗口/课表窗口两套判据 + 复核结论");
            Console.WriteLine("  ReplayTool gate-reconcile-test <Dumps 目录>          # 断言双窗口复核（含开关窗口与课表窗口结论相反的样本）");
            Console.WriteLine("  ReplayTool absolute-required-test                    # 断言退化口径换算（残差 + 当前偏移 = 绝对所需偏移）");
            Console.WriteLine("  ReplayTool estimate-test                             # 断言 v1.1.0：离群不入列/连续两次重开段/单次 3s 步进");
            return 1;
        }

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

        // ── 0) 模板匹配（v1.1.0：生产里先跑模板、拿命中位置当闸门锚点，这里同口径）──
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

        DateTime? anchorLocal = match != null
            ? audio.WallTimeAt(match.Value.OnsetSampleIndex)?.ToLocalTime()
            : null;

        // ── 1) 起响沿闸门：老行为与 v1.1.0 锚定行为**并排**打印（离线对照就是这两行的差）──
        void PrintGate(string title, OnsetGate.OnsetHit? hit, string gateNote)
        {
            if (hit != null)
            {
                var inDump = (hit.Value.OnsetUtc - anchorWallUtc).TotalSeconds;
                var delta = (boundaryWallLocal - hit.Value.OnsetUtc.ToLocalTime()).TotalSeconds;
                Console.WriteLine($"{title}：命中（dump 内 {inDump:F3}s，delta={delta:F3}s，比值 {hit.Value.Ratio:F1}×）");
                Console.WriteLine($"  明细：{gateNote}");
            }
            else
            {
                Console.WriteLine($"{title}：未命中 —— {gateNote}");
            }
        }

        var gateOld = OnsetGate.Find(audio, boundaryWallLocal, out var gateOldNote);
        PrintGate($"起响沿闸门（老行为：边界 ±{OnsetGate.GateSeconds:F1}s）", gateOld, gateOldNote);

        var gate = gateOld;
        if (anchorLocal != null)
        {
            gate = OnsetGate.Find(audio, boundaryWallLocal, anchorLocal, out var gateNote);
            PrintGate($"起响沿闸门（v1.1.0 锚定：模板命中 ±{OnsetGate.AnchorApertureSeconds:F1}s）", gate, gateNote);
        }
        else
        {
            Console.WriteLine("起响沿闸门（v1.1.0 锚定）：无锚点（模板未命中）→ 与老行为完全一致");
        }
        Console.WriteLine();

        var measuredByGate = gate != null;
        var measuredByTemplate = match != null;
        Console.WriteLine(measuredByGate
            ? "结论：本窗口由【起响沿闸门】给出可信测量。"
            : measuredByTemplate
                ? "结论：本窗口由【模板匹配】给出可信测量。"
                : "结论：**本窗口无可信测量** —— 闸门与模板均未命中；插件不会写入偏移。");

        // ── 2) 攻击沿逐点（10 ms 步长，找真实起响点） ──
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

    /// <summary>
    /// 整段 RMS 包络（只读诊断）：把「这段时间里到底有什么声音」按固定步长打出来。
    ///
    /// 动机（2026-10-07 晚）：人工把偏移改成 +7.05 s 之后，闸门与模板都答不出「铃到底在哪」——
    /// 闸门只会说「±2 s 窗内没有合格起响沿」，模板只会说 NCC≈0.01。判断铃的位置只能先看原始包络：
    /// 响的是哪几段、比环境底高多少。文件名同时给出布防起点与标称边界，两列时间并排打，
    /// 行尾标注「开关窗口中心（文件内 20.0s）」与「课表窗口中心（标称边界）」。
    ///
    /// 用法：ReplayTool envelope &lt;dump.wav&gt; [步长秒=0.1]
    /// </summary>
    private static int Envelope(string[] args)
    {
        if (args.Length < 2 || !File.Exists(args[1]))
        {
            Console.WriteLine("用法：ReplayTool envelope <dump.wav> [步长秒=0.1]");
            return 1;
        }

        var step = args.Length > 2 && double.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var st) && st > 0
            ? st
            : 0.1;

        var samples = WavReader.ReadMono48k(args[1], out var readNote);
        if (samples == null)
        {
            Console.WriteLine($"读取失败：{readNote}");
            return 2;
        }

        var name = Path.GetFileNameWithoutExtension(args[1]);
        var m = System.Text.RegularExpressions.Regex.Match(name, @"^\d{8}-(\d{2})(\d{2})(\d{2})-(\S+)-B(\d{2})(\d{2})(\d{2})$");
        var hasTimes = m.Success;
        var startWall = hasTimes
            ? new TimeSpan(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value))
            : TimeSpan.Zero;
        var nominalInDump = hasTimes
            ? new TimeSpan(int.Parse(m.Groups[5].Value), int.Parse(m.Groups[6].Value), int.Parse(m.Groups[7].Value))
                .Subtract(startWall).TotalSeconds
            : double.NaN;
        if (hasTimes && nominalInDump < 0)
            nominalInDump += 24 * 3600;

        Console.WriteLine($"dump：{Path.GetFileName(args[1])}（{readNote}）");
        if (hasTimes)
            Console.WriteLine($"起点 {startWall:hh\\:mm\\:ss}（布防时刻）→ 标称边界在文件内 {nominalInDump:F3}s；"
                              + "开关窗口中心恒在文件内 20.000s（布防提前量 20 s）");
        Console.WriteLine($"步长 {step:F3}s　格式：文件内秒　[墙钟]　dBFS");

        var total = samples.Length / (double)CaptureAudio.SampleRate;
        var win = Math.Max(1, (int)(step * CaptureAudio.SampleRate));
        for (var t = 0.0; t < total; t += step)
        {
            var from = (int)Math.Round(t * CaptureAudio.SampleRate);
            var to = Math.Min(samples.Length, from + win);
            var rms = Rms(samples, from, to);
            var db = 20 * Math.Log10(Math.Max(rms, 1e-12));
            var bars = (int)Math.Clamp((db + 60.0) / 60.0 * 40.0, 0, 40);

            var tag = "";
            if (hasTimes)
            {
                if (Math.Abs(t - 20.0) < step / 2)
                    tag = "  ← 开关窗口中心";
                if (Math.Abs(t - nominalInDump) < step / 2)
                    tag = "  ← 课表窗口中心（标称边界）";
            }

            var wallCol = hasTimes ? $" {startWall.Add(TimeSpan.FromSeconds(t)):hh\\:mm\\:ss\\.ff}" : "";
            Console.WriteLine($"  {t,7:F2}s{wallCol}  {db,7:F1}  {new string('#', bars)}{tag}");
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
    /// 闸门候选诊断（只读）：把「边界 ± 闸门半窗」内每个候选的起响比值与峰值原样打出来，
    /// 并**并排对比旧判据（取比值最大）与新判据（强候选组内取比值最大）**。
    ///
    /// 动机：实机 2026-09-18 11:40 窗口的环境底只有 −33 dBFS、4.8~19.5 s 每秒都有事件（持续噪声）。
    /// 旧判据选中了弱事件（约 −17.5 dBFS），跳过了同窗更强的事件（约 −10.5 dBFS），于是 t_ring 被记错。
    /// 要改判据就必须先把候选表与两种选法的分歧全部看清，并对 111 个历史窗口做回归。
    ///
    /// 用法：
    ///   ReplayTool gate-scan &lt;dump.wav&gt; [锚点在文件名起点后秒数]
    ///   ReplayTool gate-scan &lt;Dumps 目录&gt; [锚点偏移秒数]
    /// 锚点默认 0（= 假定文件名里的起点就是音频锚点）。实机日志给出的锚点与文件名能差 1~3 s，
    /// 需要精确复算时用该参数对齐。
    /// </summary>
    private static int GateScan(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("用法：ReplayTool gate-scan <dump.wav> [锚点偏移秒数]  |  ReplayTool gate-scan <Dumps 目录> [锚点偏移秒数]");
            return 1;
        }

        var target = args[1];
        var anchorOffset = args.Length > 2 && double.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var ao)
            ? ao
            : 0.0;

        if (Directory.Exists(target))
        {
            var files = Directory.GetFiles(target, "*.wav").OrderBy(f => f).ToList();
            Console.WriteLine($"目录：{target}（{files.Count} 个 dump），锚点偏移 {anchorOffset:F3}s");
            var stats = new List<GateScanRow>();
            foreach (var f in files)
            {
                var row = ScanOne(f, anchorOffset);
                if (row != null)
                    stats.Add(row);
            }

            Console.WriteLine();
            Console.WriteLine("════════ 汇总（旧判据 → 新判据）════════");
            var comparable = stats.Where(s => s.OldHit || s.NewHit).ToList();
            var changed = comparable.Where(s => s.Changed).ToList();
            var improved = changed.Where(s => s.NewPeak > s.OldPeak + 0.5).ToList();
            var worsened = changed.Where(s => s.NewPeak < s.OldPeak - 0.5).ToList();
            var newMissOldHit = stats.Where(s => s.OldHit && !s.NewHit).ToList();
            var newHitOldMiss = stats.Where(s => !s.OldHit && s.NewHit).ToList();

            Console.WriteLine($"  可比窗口 {comparable.Count} / 共 {stats.Count}");
            Console.WriteLine($"  选点改变 {changed.Count} 个：其中峰值提升(>0.5dB) {improved.Count} 个，峰值下降(>0.5dB) {worsened.Count} 个");
            Console.WriteLine($"  旧命中→新未命中：{newMissOldHit.Count} 个 {(newMissOldHit.Count > 0 ? "★ 回归风险" : "")}");
            Console.WriteLine($"  旧未命中→新命中：{newHitOldMiss.Count} 个");
            if (changed.Count > 0)
            {
                Console.WriteLine("  逐条分歧：");
                foreach (var s in changed.OrderByDescending(x => Math.Abs(x.NewPeak - x.OldPeak)))
                    Console.WriteLine($"    {s.Name,-42} Δt={s.DeltaSeconds,+6:F2}s  峰值 {s.OldPeak,6:F1} → {s.NewPeak,6:F1} dBFS" +
                                      $"（{(s.NewPeak > s.OldPeak ? "更响" : "更弱")}）");
            }
            return 0;
        }

        if (!File.Exists(target))
        {
            Console.WriteLine($"找不到文件：{target}");
            return 2;
        }

        ScanOne(target, anchorOffset);
        return 0;
    }

    /// <summary>
    /// 基线静默门槛的回归断言（v1.0.4）。
    ///
    /// 背景（2026-09-19 实机）：麦克风在多个窗口里整段数字静默（采样恒为 0），
    /// 闸门把「麦克风恢复供音的那一瞬间」当成铃声——因为分母（起响前 0.3 s 基线）≈0，
    /// 比值炸成 748579× ~ 96702200×，而真铃的起响比值实测只有 4~346×。
    /// v1.0.4 起：基线 RMS &lt; RingDetector.NoiseFloorMin 的候选一律排除（比值不可信）。
    ///
    /// 断言方式：对目录内每个 dump 跑生产闸门，检查判据说明里是否出现「基线静默候选」字样，
    /// 并据此统计「被门槛拦下的窗口数」。任何命中窗口的比值若仍 &gt; 1000×，说明门槛漏了。
    /// </summary>
    private static int GateGuardTest(string[] args)
    {
        if (args.Length < 2 || !Directory.Exists(args[1]))
        {
            Console.WriteLine("用法：ReplayTool gate-guard <Dumps 目录>");
            return 1;
        }

        var files = Directory.GetFiles(args[1], "*.wav").OrderBy(f => f).ToList();
        Console.WriteLine($"基线静默门槛断言：{files.Count} 个 dump");

        var guarded = 0;          // 门槛实际排除过静默候选的窗口
        var hits = 0;             // 闸门命中数
        var suspicious = 0;       // 命中且比值仍然离谱（门槛漏网）
        var maxRatio = 0.0;
        var shown = 0;

        foreach (var f in files)
        {
            var name = Path.GetFileNameWithoutExtension(f);
            var m = System.Text.RegularExpressions.Regex.Match(name, @"^\d{8}-(\d{2})(\d{2})(\d{2})-(\S+)-B(\d{2})(\d{2})(\d{2})$");
            if (!m.Success)
                continue;

            var start = new TimeSpan(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
            var boundary = new TimeSpan(int.Parse(m.Groups[5].Value), int.Parse(m.Groups[6].Value), int.Parse(m.Groups[7].Value));
            var boundaryInDump = (boundary - start).TotalSeconds;
            if (boundaryInDump < 0)
                boundaryInDump += 24 * 3600;

            var samples = WavReader.ReadMono48k(f, out _);
            if (samples == null)
                continue;

            var audio = new CaptureAudio();
            var fileStartLocal = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Local);
            audio.Write(samples, fileStartLocal.ToUniversalTime());
            var boundaryWallLocal = fileStartLocal.AddSeconds(boundaryInDump);

            var hit = OnsetGate.Find(audio, boundaryWallLocal, out var note);
            if (note.Contains("基线静默候选", StringComparison.Ordinal))
            {
                guarded++;
                if (shown++ < 12)
                    Console.WriteLine($"  [已拦截] {name}：{note}");
            }

            if (hit != null)
            {
                hits++;
                maxRatio = Math.Max(maxRatio, hit.Value.Ratio);
                if (hit.Value.Ratio > 1000)
                {
                    suspicious++;
                    Console.WriteLine($"  [失败] {name}：命中比值 {hit.Value.Ratio:F1}× 仍超过 1000×（基线静默门槛漏网）");
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"  命中 {hits} 个；门槛拦截静默候选的窗口 {guarded} 个；命中中比值 >1000× 的 {suspicious} 个；命中最大比值 {maxRatio:F1}×");
        if (suspicious == 0)
        {
            Console.WriteLine("  断言通过：没有任何命中依赖「除以数字静默」的比值。");
            return 0;
        }

        Console.WriteLine("  断言失败。");
        return 6;
    }

    /// <summary>
    /// v1.1.2.0 四项修复的回归断言（bell-match-test &lt;Dumps 目录&gt; &lt;模板目录&gt;）。
    ///
    /// 用**实机 dump** 验证（不是构造数据），并且走**生产口径**：
    /// 先模板匹配，命中就把它的位置当锚点交给闸门（<c>CalibrationRunner</c> 正是这么调用的）。
    /// <list type="number">
    /// <item><description>① 强匹配免攻击沿：NCC ≥ 0.85 且音色 ≥ 0.80 的模板命中不得因攻击沿被拒。
    /// 2026-10-26 全天实测：改前 20 个窗口只有 9 个能过攻击沿（其余 NCC 掉到 0.25~0.53 报未命中），
    /// 改后今天 12 个模板窗口全部命中。</description></item>
    /// <item><description>② 锚定闸门不跑到别处：给了锚点后，闸门选中点必须落在锚点 ±SegmentLookbackSeconds
    /// 之内（回溯到段起点会让位置比光圈再往前提几秒，这是设计内的；跑到窗内另一个事件才是错）。</description></item>
    /// <item><description>③ 结果收敛：**同一天**模板命中的窗口里，「所需偏移」= −(闸门位置 − 边界) 应聚成一簇，
    /// 最大偏离中位 ≤ 3.5s。这是用户关心的那个数：2026-10-26 改前当天跨度 15.91s，改后 12 个模板窗口
    /// 全部落在 6.6~9.6s（跨度 3.0s）。跨天不能一起算——不同日期的真实偏移本来就不同。</description></item>
    /// </list>
    /// ③ 双峰拒写 / ④ 跳变需模板确认在 estimate-test 里断言（纯逻辑，不需要音频）。
    /// </summary>
    private static int BellMatchTest(string[] args)
    {
        if (args.Length < 3 || !Directory.Exists(args[1]) || !Directory.Exists(args[2]))
        {
            Console.WriteLine("用法：ReplayTool bell-match-test <Dumps 目录> <模板目录>");
            return 1;
        }

        var templates = new List<BellTemplate>();
        foreach (var f in Directory.GetFiles(args[2], "*.wav").OrderBy(f => f))
        {
            var isClass = Path.GetFileNameWithoutExtension(f).Contains("上课");
            var tpl = BellTemplate.LoadFromTemplateFile(f, isClass ? "class" : "break", isClass ? "上课铃" : "下课铃", out var loadNote);
            if (tpl != null)
                templates.Add(tpl);
            else
                Console.WriteLine($"  [跳过] {loadNote}");
        }

        if (templates.Count == 0)
        {
            Console.WriteLine("  没有可用模板。");
            return 1;
        }

        var files = Directory.GetFiles(args[1], "*.wav").OrderBy(f => f).ToList();
        Console.WriteLine($"v1.1.2.0 四项修复断言：{files.Count} 个 dump，{templates.Count} 个模板");

        var strongHits = 0;          // ① 强匹配生效次数
        var strongRejected = 0;      // ① 强匹配被攻击沿误拒（必须为 0）
        var tplWindows = 0;          // 模板命中的窗口数
        var anchoredAway = 0;        // ② 锚定闸门跑到别的事件上（必须为 0）
        var byDay = new Dictionary<string, List<double>>();   // ③ 每天各自的「所需偏移」
        var failures = 0;

        foreach (var f in files)
        {
            var name = Path.GetFileNameWithoutExtension(f);
            var m = System.Text.RegularExpressions.Regex.Match(name, @"^\d{8}-(\d{2})(\d{2})(\d{2})-(\S+)-B(\d{2})(\d{2})(\d{2})$");
            if (!m.Success)
                continue;

            var start = new TimeSpan(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
            var boundary = new TimeSpan(int.Parse(m.Groups[5].Value), int.Parse(m.Groups[6].Value), int.Parse(m.Groups[7].Value));
            var boundaryInDump = (boundary - start).TotalSeconds;
            if (boundaryInDump < 0)
                boundaryInDump += 24 * 3600;

            var samples = WavReader.ReadMono48k(f, out _);
            if (samples == null)
                continue;

            var audio = new CaptureAudio();
            var fileStartLocal = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Local);
            audio.Write(samples, fileStartLocal.ToUniversalTime());
            var boundaryWallLocal = fileStartLocal.AddSeconds(boundaryInDump);

            var rate = CaptureAudio.SampleRate;
            var fromSample = (int)Math.Max(0, (boundaryInDump - 12) * rate);
            var toSample = (int)Math.Min(samples.Length, (boundaryInDump + 12) * rate);

            // ── 生产口径的模板匹配（与 RingCandidateSelector 同阈值）──
            TemplateMatch? best = null;
            foreach (var tpl in templates)
            {
                var mm = TemplateMatcher.Match(tpl, audio.Samples, audio.Length, fromSample, toSample, 0.60, 0.45);
                if (mm.Ncc >= 0.85 && mm.SpectralSim >= 0.80)
                {
                    strongHits++;
                    if (!mm.Accepted)
                    {
                        strongRejected++;
                        Console.WriteLine($"  [失败] {name} {tpl.Label}：NCC {mm.Ncc:F3} 音色 {mm.SpectralSim:F3} 应免攻击沿却未命中");
                    }
                }

                if (mm.Accepted && mm.OnsetSampleIndex >= 0 && (best == null || mm.Ncc > best.Value.Ncc))
                    best = mm;
            }

            if (best == null)
                continue;

            var anchorWall = audio.WallTimeAt(best.Value.OnsetSampleIndex);
            if (anchorWall == null)
                continue;

            tplWindows++;
            var day = name.Substring(0, 8);
            if (!byDay.TryGetValue(day, out var dayList))
                byDay[day] = dayList = new List<double>();

            // ── 生产口径的闸门：带锚点 ──
            var gate = OnsetGate.Find(audio, boundaryWallLocal, anchorWall.Value.ToLocalTime(), out _);
            if (gate == null)
            {
                // 锚定光圈内没有合格起响沿：生产链路上会退化成「用模板位置」，不算跑偏
                dayList.Add(-(anchorWall.Value.ToLocalTime() - boundaryWallLocal).TotalSeconds);
                continue;
            }

            var gateSec = (gate.Value.OnsetUtc.ToLocalTime() - boundaryWallLocal).TotalSeconds;
            var anchorSec = (anchorWall.Value.ToLocalTime() - boundaryWallLocal).TotalSeconds;
            // 回溯（SegmentLookbackSeconds）会把位置从锚点往前推，这是设计内的；
            // 但推到「比光圈还早一个回溯窗」以外，就说明它抓的是窗内另一个事件。
            var limit = OnsetGate.AnchorApertureSeconds + OnsetGate.SegmentLookbackSeconds;
            if (Math.Abs(gateSec - anchorSec) > limit)
            {
                anchoredAway++;
                Console.WriteLine($"  [失败] {name}：锚定闸门抓到别的事件（锚 {anchorSec:+0.00}s → 选中 {gateSec:+0.00}s，" +
                                  $"上限 ±{limit:F1}s）");
            }

            dayList.Add(-gateSec);
        }

        // ── ③ 相邻窗口的测量差（摇摆的直接度量）──
        // 不用「全天中位 ±X」：一天里真实偏移本身会变（人工校准、基准跳变），全天中位不代表任何时刻的真值。
        // 摇摆的定义是**相邻两个窗口给出互相矛盾的结果** —— 它们只隔 10~40 分钟，钟不可能真的走掉几秒。
        const double jumpSeconds = 2.5;
        var jumps = new List<(string Day, double Delta, string A, string B)>();
        foreach (var (day, list) in byDay.OrderBy(kv => kv.Key))
        {
            for (var i = 1; i < list.Count; i++)
            {
                var d = Math.Abs(list[i] - list[i - 1]);
                if (d > jumpSeconds)
                    jumps.Add((day, d, $"{list[i - 1]:F2}", $"{list[i]:F2}"));
            }
        }

        var pairs = byDay.Values.Sum(l => Math.Max(0, l.Count - 1));
        var jumpRatio = pairs == 0 ? 0.0 : (double)jumps.Count / pairs;

        Console.WriteLine();
        Console.WriteLine($"  ① 强匹配（NCC ≥ 0.85 且音色 ≥ 0.80）：{strongHits} 次，其中被攻击沿误拒 {strongRejected} 次");
        Console.WriteLine($"  ② 模板命中 {tplWindows} 个窗口，锚定闸门抓到别的事件 {anchoredAway} 个");
        Console.WriteLine($"  ③ 相邻窗口差 ＞ {jumpSeconds:F1}s 的 {jumps.Count}/{pairs} 对（{jumpRatio:P0}）");
        foreach (var j in jumps.Take(12))
            Console.WriteLine($"      {j.Day}：{j.A} → {j.B}（差 {j.Delta:F2}s）");

        if (strongRejected > 0)
            failures++;
        if (anchoredAway > 0)
            failures++;
        // 门槛依据：2026-10-26 改前 20 个窗口的相邻差里有 9 对 ＞ 2.5s（45%），是当天偏移在
        // −4.07~+11.84 之间来回跳的直接体现；改后 12 个模板窗口只剩 1 对（08:10 那个固有难例）。
        // 取 20%：真回归时（攻击沿重新毙掉真铃、闸门退回盲扫）这个比例会立刻回到 40% 以上。
        if (jumpRatio > 0.20)
        {
            failures++;
            Console.WriteLine($"  [失败] 相邻窗口差 ＞ {jumpSeconds:F1}s 的比例 {jumpRatio:P0} ＞ 20%：测量仍在摇摆");
        }

        if (failures == 0)
        {
            Console.WriteLine("  断言通过：强匹配免攻击沿生效、锚定闸门不跑偏、所需偏移收敛成一簇。");
            return 0;
        }

        Console.WriteLine($"  断言失败 {failures} 项。");
        return 7;
    }


    ///
    /// 窗口中心怎么来的：dump 文件名给的是**布防时刻**，而布防发生在显示域边界前 20 s，
    /// 显示域 = 真墙钟 + 偏移 ⇒ 开关（内核真正切换）在文件内 +20 s 处，标称课表边界再往后 +偏移。
    /// 偏移由文件名反推：偏移 = (边界 − 起点) − 20。
    /// </summary>
    private static int GateReconcile(string[] args)
    {
        if (args.Length < 2 || !File.Exists(args[1]))
        {
            Console.WriteLine("用法：ReplayTool gate-reconcile <dump.wav> [当时偏移秒]");
            return 1;
        }

        var r = ScanBothWindows(args[1], args.Length > 2 && double.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var ov) ? ov : (double?)null);
        if (r == null)
        {
            Console.WriteLine("文件名不含「窗口起点 / 边界时刻」，无法反推偏移。");
            return 1;
        }

        var (verdict, switchHit, scheduleHit, note, offset, boundaryInDump) = r.Value;
        Console.WriteLine($"dump：{Path.GetFileName(args[1])}（边界在文件内 {boundaryInDump:F3}s，反推当时偏移 {offset:F3}s）");
        Console.WriteLine($"  开关窗口（中心 = 边界 − 偏移 = 文件内 +20.0s）：{switchHit?.Note ?? "未命中"}");
        Console.WriteLine($"  课表窗口（中心 = 标称边界 = 文件内 {20 + offset:F3}s）：{scheduleHit?.Note ?? "未命中"}");
        if (switchHit != null)
            Console.WriteLine($"  开关窗口所需偏移 = {-(OnsetInFile(switchHit.Value) - boundaryInDump):F3}s" +
                              $"（= 边界 − 铃响；铃响在文件内 {OnsetInFile(switchHit.Value):F3}s）");
        if (scheduleHit != null)
            Console.WriteLine($"  课表窗口所需偏移 = {-(OnsetInFile(scheduleHit.Value) - boundaryInDump):F3}s" +
                              $"（铃响在文件内 {OnsetInFile(scheduleHit.Value):F3}s）");
        Console.WriteLine($"  复核结论：{verdict} —— {note}");
        return 0;
    }

    /// <summary>
    /// 双窗口复核的回归断言（v1.0.4 增补）：用真实 dump 锁住 2026-09-20 上午那次「闸门锁到杂音」的行为。
    ///
    /// 断言用例与依据：
    /// <list type="number">
    /// <item><description>2026-09-20 上午 4 个下课窗口：开关窗口测到的是开关附近的弱杂音（峰值 −18~−28 dBFS），
    /// 课表窗口测到的才是真铃（强 2~14 dB）—— 修复前这 4 次被当成小修正写入，把偏移从 6.34 推到 8.13。
    /// **断言：4 个窗口全部判为 Conflict（不写入）**。</description></item>
    /// <item><description>2026-09-18 09:40 下课：模板匹配在边界 +9.01s 命中下课铃（NCC 0.924）——铃确实比课表晚 9 秒。
    /// **断言：判为 PreferSwitch（保住合法的大偏移测量）**。</description></item>
    /// <item><description>2026-09-20 14:40 / 17:40 下课（偏移已收敛）：两窗口指向同一次起响。
    /// **断言：判为 Agree（行为与修复前完全一致）**。</description></item>
    /// <item><description>2026-09-17 09:00 上课：课表窗口无合格起响沿。
    /// **断言：判为 SwitchOnly（行为与修复前完全一致）**。</description></item>
    /// <item><description>2026-10-07 20:10 上课 / 21:30 下课（v1.0.5 新增）：偏移被人为改成 +7.05 s 之后的真实处境
    /// —— 两窗口抓到的是同一段约 15 s 多击钟声的**不同两击**（相差 5.66 s / 3.74 s，峰值只差 0.7 / 0.1 dB）。
    /// **断言：判为 PreferSwitch（取更早的开关窗口），旧版则分别是不留样本的 no-trusted-match 与 match-conflict**。</description></item>
    /// <item><description>2026-10-07 20:00 下课（v1.0.5 新增）：布防晚 3.4 s，开麦时铃的第一击已经在响，
    /// 当窗偏移还是布防时冻结的 −16.34 s（用户把偏移改成 +7.05 s 发生在开麦之后）。开关窗口空、只有课表窗口命中。
    /// **断言：判为 ScheduleOnly —— 只作旁证；v1.0.4 这类窗口连一条样本都不落（正是「人工校准后样本全废」的现场）**。</description></item>
    /// </list>
    /// </summary>
    private static int GateReconcileTest(string[] args)
    {
        if (args.Length < 2 || !Directory.Exists(args[1]))
        {
            Console.WriteLine("用法：ReplayTool gate-reconcile-test <Dumps 目录>");
            return 1;
        }

        // 第 4 列 OffsetSeconds = 当时内核偏移：null 表示按文件名反推（布防恰好提前 20.0 s 时的常规情形）；
        // 显式传值用于布防晚于 20.0 s 的窗口（2026-10-07 晚间就是如此，文件名反推会偏 ~1 s）。
        var cases = new (string File, OnsetGate.WindowVerdict Expected, string Why, double? OffsetSeconds)[]
        {
            ("20260920-084933-下课-B085000.wav", OnsetGate.WindowVerdict.Agree, "v1.0.5 长铃起点回溯后两窗一致：课表窗口从钟声中段那一击（文件内 27.55s）回溯到这段声音的起点 22.23s，与开关窗口命中 21.89s 只差 0.34s。回溯前判 Conflict（开关 −27.3 dBFS 比课表 −13.8 dBFS 弱 13.5 dB、两窗相差 5.66s）——写值与课表窗口口径只差 0.34s", null),
            ("20260920-093933-下课-B094000.wav", OnsetGate.WindowVerdict.Conflict, "上午下课：课表窗口强 2.1 dB", null),
            ("20260920-104932-下课-B105000.wav", OnsetGate.WindowVerdict.Conflict, "上午下课：课表窗口强 3.3 dB", null),
            ("20260920-113931-下课-B114000.wav", OnsetGate.WindowVerdict.Conflict, "上午下课：课表窗口强 4.8 dB", null),
            ("20260918-093948-下课-B094000.wav", OnsetGate.WindowVerdict.PreferSwitch, "铃确实晚 9 秒，模板 NCC 0.924 佐证", null),
            ("20260920-143941-下课-B144000.wav", OnsetGate.WindowVerdict.Agree, "两窗口同一起响", null),
            ("20260920-173941-下课-B174000.wav", OnsetGate.WindowVerdict.Agree, "两窗口同一起响", null),
            ("20260917-085946-上课-B090000.wav", OnsetGate.WindowVerdict.SwitchOnly, "课表窗口无合格起响沿", null),

            // v1.0.5 新增：2026-10-07 晚间三个真实窗口（当时偏移由用户人工改为 +7.05 s）
            ("20261007-200932-上课-B201000.wav", OnsetGate.WindowVerdict.PreferSwitch,
                "v1.0.5：开关窗口靠「高声压起响」入选（2.1× 但 -16.3 dBFS），两窗相差 5.66s、峰值仅差 0.7 dB = 同一段 15s 长铃的先后两击；v1.0.4 判 no-trusted-match、不留样本", 7.05),
            ("20261007-212932-下课-B213000.wav", OnsetGate.WindowVerdict.Agree,
                "v1.0.5：长铃起点回溯把两窗口都挪到钟声第一击上，Δt=0.00s → 两窗一致；未回溯时判 PreferSwitch（开关 -22.9 / 课表 -23.0 dBFS 相差 3.74s、峰值差 0.1 dB，同一段 15s 长铃的先后两击），v1.0.4 判 match-conflict、不写不留样本", 7.05),
            ("20261007-195956-下课-B200000.wav", OnsetGate.WindowVerdict.ScheduleOnly,
                "开麦时铃的第一击已在响（布防晚 3.4s，当窗偏移仍是布防时冻结的 -16.34s；用户把偏移改成 +7.05 是在开麦之后）：开关窗口空、只有课表窗口命中 → 只作旁证 —— v1.0.4 判 no-trusted-match 且一条样本都不落，v1.0.5 起落一条旁证样本", -16.34),

            // v1.0.5 补（对称支配）：偏移被人工改乱后的真实窗口 —— 闸门半窗还是 2 s 时，开关窗口只剩 2 个合格候选
            // （20 ms 单点瞬态）、课表窗口强 16.9 dB，于是 v1.0.5 先判 match-conflict（不写不落样本）、
            // 补对称支配后改用课表窗口坐标。2026-10-12 半窗加宽到 8 s 后，开关窗口也能看到真正的钟声 → 两窗一致，
            // 本用例随之改判 Agree；对称支配那条路径由下面的合成用例继续覆盖，不再依赖这个录音。
            ("20261012-085938-上课-B090000.wav", OnsetGate.WindowVerdict.Agree,
                "半窗 8 s（2026-10-12 起）后开关窗口也能看见真铃 → 两窗一致；半窗 2 s 时代这里判 PreferSchedule", 1.730),
        };

        // 注意：带真实录音的用例里，有 11 条的录音在 2026-10-12 被「清除无用录音」误删（连回收站一起清空）——
        // 它们现在只会打印「跳过」，其期望值仍是 ±2 s 半窗时代的记录；若将来重录同名文件，需重新核对期望。

        var failed = 0;
        var skipped = 0;

        // 漂移断言：用例表里的每个录音都必须列在 DumpCleaner.ProtectedFiles 里 —— 设置页「清除无用录音」
        // 永远不删名单里的文件。谁加了用例却忘了加名字，跑一次本断言就会红（这正是录音被误删的那次事故）。
        foreach (var c in cases)
        {
            if (Array.IndexOf(DumpCleaner.ProtectedFiles, c.File) < 0)
            {
                Console.WriteLine($"  [失败] 用例 {c.File} 不在 DumpCleaner.ProtectedFiles 里：设置页「清除无用录音」会把它删掉");
                failed++;
            }
        }

        foreach (var c in cases)
        {
            var path = Path.Combine(args[1], c.File);
            if (!File.Exists(path))
            {
                // 2026-10-12：这些录音被「清除无用录音」误删后永久丢失（仓库不分发音频、回收站也已清空），
                // 已无法复核 → 记为「跳过」而不是失败，免得一个永远修不好的红项把真回归埋掉。
                Console.WriteLine($"  [跳过] 缺少 dump：{c.File}（录音已丢失：2026-10-12 误删事故；仓库不分发音频）");
                skipped++;
                continue;
            }

            var r = ScanBothWindows(path, c.OffsetSeconds);
            if (r == null)
            {
                Console.WriteLine($"  [失败] {c.File}：文件名无法解析");
                failed++;
                continue;
            }

            if (r.Value.Verdict == c.Expected)
            {
                Console.WriteLine($"  [通过] {c.File} → {r.Value.Verdict}（{c.Why}）");
            }
            else
            {
                Console.WriteLine($"  [失败] {c.File}：期望 {c.Expected}，实际 {r.Value.Verdict} —— {r.Value.Note}");
                failed++;
            }
        }

        // v1.0.5 补（对称支配）：合成命中用例 —— 不依赖录音，直接卡 Reconcile 的分支边界。
        // 支配判据必须**对称**：开关更强 → 用开关；课表更强 → 用课表；只有中间带（2~6 dB）才判冲突。
        var t0 = new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Local);
        var synth = new (string Why, double SwitchDb, double ScheduleDb, double DeltaSec,
                         OnsetGate.WindowVerdict Expected, bool ExpectScheduleReturned)[]
        {
            ("开关强 7.0 dB、相差 3.0s → 用开关窗口", -23.0, -30.0, 3.0, OnsetGate.WindowVerdict.PreferSwitch, false),
            ("课表强 7.0 dB、相差 3.0s → 用课表窗口（对称支配）", -30.0, -23.0, 3.0, OnsetGate.WindowVerdict.PreferSchedule, true),
            ("课表强 6.2 dB（刚过支配门）→ 用课表窗口", -30.0, -23.8, 3.0, OnsetGate.WindowVerdict.PreferSchedule, true),
            ("课表强 5.8 dB（差一点）→ 仍判冲突", -30.0, -24.2, 3.0, OnsetGate.WindowVerdict.Conflict, false),
            ("课表强 4.8 dB（2026-09-20 那批的量级）→ 仍判冲突", -30.0, -25.2, 3.0, OnsetGate.WindowVerdict.Conflict, false),
            ("课表强 12.0 dB 但只差 0.5s → 一致优先，用开关窗口", -32.0, -20.0, 0.5, OnsetGate.WindowVerdict.Agree, false),
        };
        foreach (var s in synth)
        {
            var switchHit = SynthHit(t0, s.SwitchDb);
            var scheduleHit = SynthHit(t0.AddSeconds(s.DeltaSec), s.ScheduleDb);
            var chosen = OnsetGate.Reconcile(switchHit, scheduleHit, out var synVerdict, out var synNote);
            var returnedSchedule = chosen != null && chosen.Value.OnsetUtc == scheduleHit.OnsetUtc;
            if (synVerdict == s.Expected && returnedSchedule == s.ExpectScheduleReturned)
            {
                Console.WriteLine($"  [通过] 合成：{s.Why} → {synVerdict}");
            }
            else
            {
                Console.WriteLine(
                    $"  [失败] 合成：{s.Why}：期望 {s.Expected}／返回{(s.ExpectScheduleReturned ? "课表" : "开关")}，" +
                    $"实际 {synVerdict}／返回{(returnedSchedule ? "课表" : "开关")} —— {synNote}");
                failed++;
            }
        }

        Console.WriteLine();
        if (failed == 0)
        {
            Console.WriteLine($"  双窗口复核断言全部通过（含「开关窗口与课表窗口结论相反」与「长铃起点回溯后改判一致」两类窗口，"
                              + "以及「支配判据对称：开关更强用开关、课表更强用课表、中间带才判冲突」的合成用例）"
                              + (skipped > 0 ? $"；跳过 {skipped} 条（录音已丢失，见上）。" : "；无跳过项。"));
            return 0;
        }

        Console.WriteLine($"  断言失败 {failed} 项。");
        return 7;
    }

    /// <summary>合成一次闸门命中（v1.0.5 补）：只给墙钟与峰值 dBFS，用于复核判据的分支断言，不依赖录音。</summary>
    private static OnsetGate.OnsetHit SynthHit(DateTime onsetLocal, double peakDb)
        => new(0, onsetLocal.ToUniversalTime(), 5.0, "合成用例", PeakRms: Math.Pow(10, peakDb / 20.0));

    /// <summary>
    /// 退化口径换算的回归断言（v1.0.4 增补）：未观测到切换时，绝对所需偏移 = 残差 + 当前偏移。
    /// 4 条用例取自实机样本（记录值、当时偏移、由「边界 − 铃响」独立算出的真值）。
    /// </summary>
    private static int AbsoluteRequiredTest()
    {
        var cases = new (string Label, double Recorded, double OffsetThen, double Truth)[]
        {
            ("2026-09-16 18:50 下课", -0.79, -4.22, -5.01),
            ("2026-09-17 17:10 下课", -0.91, -6.68, -7.59),
            ("2026-09-17 18:50 下课", 0.29, -7.51, -7.22),
            ("2026-09-20 18:50 下课", 0.86, -1.374, -0.51),
        };

        var failed = 0;
        foreach (var c in cases)
        {
            var fixedSeconds = CorrectionPolicy.RequiredFromAbsolute(
                TimeSpan.FromSeconds(c.Recorded), c.OffsetThen).TotalSeconds;
            var oldError = Math.Abs(c.Recorded - c.Truth);
            if (Math.Abs(fixedSeconds - c.Truth) <= 0.05)
            {
                Console.WriteLine($"  [通过] {c.Label}：记录 {c.Recorded:F2}s + 偏移 {c.OffsetThen:F2}s = {fixedSeconds:F3}s" +
                                  $"（真值 {c.Truth:F2}s；旧口径偏 {oldError:F2}s）");
            }
            else
            {
                Console.WriteLine($"  [失败] {c.Label}：换算得 {fixedSeconds:F3}s，真值 {c.Truth:F2}s（差 {Math.Abs(fixedSeconds - c.Truth):F3}s）");
                failed++;
            }
        }

        Console.WriteLine();
        if (failed == 0)
        {
            Console.WriteLine("  退化口径断言全部通过（残差 + 当前偏移 = 所需绝对偏移）。");
            return 0;
        }

        Console.WriteLine($"  断言失败 {failed} 项。");
        return 8;
    }

    /// <summary>
    /// v1.1.0 写入策略与估计器的回归断言（estimate-test）：
    /// ① 单次写入钳制（<see cref="CorrectionPolicy.StepTowards"/>）与「分几步能走完 20 s」；
    /// ② 离群样本**不入列但不清空样本段**（v1.0.5 的 JumpReset 会把整段历史抹掉）；
    /// ③ 连续两次一致的新基准才重开样本段；
    /// ④ 趋势门槛（6 样本 / 4 h / 残差 0.6 s）在该启用时启用、该拒绝时拒绝。
    /// 断言的是**生产代码本身**，不是另抄一份公式。
    /// </summary>
    private static int EstimateTest()
    {
        var failed = 0;
        void Check(bool ok, string what)
        {
            Console.WriteLine($"  [{(ok ? "通过" : "失败")}] {what}");
            if (!ok)
                failed++;
        }

        Console.WriteLine("① 单次写入钳制（目标差多少都走，一次最多 ±上限）");
        var (fwd, fwdStepped) = CorrectionPolicy.StepTowards(0, 25, 3);
        Check(Math.Abs(fwd - 3) < 1e-9 && fwdStepped, $"偏移 0 → 目标 +25s：本次写 {fwd:F3}s（步进={fwdStepped}）");
        var (bwd, bwdStepped) = CorrectionPolicy.StepTowards(0, -25, 3);
        Check(Math.Abs(bwd + 3) < 1e-9 && bwdStepped, $"偏移 0 → 目标 −25s：本次写 {bwd:F3}s（方向必须跟着目标走）");
        var (exact, exactStepped) = CorrectionPolicy.StepTowards(0, 3, 3);
        Check(Math.Abs(exact - 3) < 1e-9 && !exactStepped, $"差值恰等于上限：一次到位 {exact:F3}s（不算步进）");
        var (inline, inlineStepped) = CorrectionPolicy.StepTowards(-3.3, -4.6, 3);
        Check(Math.Abs(inline + 4.6) < 1e-9 && !inlineStepped, $"差值在限内：一次到位 {inline:F3}s");
        var (free, freeStepped) = CorrectionPolicy.StepTowards(0, 50, 0);
        Check(Math.Abs(free - 50) < 1e-9 && !freeStepped, $"上限 ≤ 0（配置关闭）：不限幅，一次写 {free:F3}s");

        // 「偏移量可能超过 20 s」：从 0 走到 −20，每窗一步，几窗走完？
        var steps = 0;
        var current = 0.0;
        while (steps < 20 && Math.Abs(current - (-20)) > 1e-9)
        {
            var (next, _) = CorrectionPolicy.StepTowards(current, -20, 3);
            current = next;
            steps++;
        }
        Check(Math.Abs(current + 20) < 1e-9 && steps == 7, $"0 → −20s 需要 {steps} 个窗口走完（每窗 3 s，期望 7）");

        Console.WriteLine();
        Console.WriteLine("② 离群不入列、但不清空样本段（v1.0.5 的 JumpReset 会抹掉整段历史）");
        var baseAt = new DateTime(2026, 10, 15, 8, 0, 0);
        var fitter = new DriftFitter();
        fitter.Add(baseAt, TimeSpan.FromSeconds(-3.4));
        fitter.Add(baseAt.AddHours(1), TimeSpan.FromSeconds(-3.5));
        fitter.Add(baseAt.AddHours(2), TimeSpan.FromSeconds(-3.3));
        Check(fitter.SampleCount == 3 && fitter.MedianSeconds is { } m0 && Math.Abs(m0 + 3.4) < 0.05,
            $"建立基准：{fitter.SampleCount} 个样本，中位数 {fitter.MedianSeconds:F3}s");

        fitter.Add(baseAt.AddHours(3), TimeSpan.FromSeconds(-12.0));
        Check(fitter.LastSampleWasOutlier, "★ 与中位数差 8.6s（＞ 2.0s）的一次测量被判离群（调用方据此拒写）");
        Check(fitter.SampleCount == 3, $"★ 样本段**没有被清空**，仍是 {fitter.SampleCount} 个（中位数保持稳健）");
        Check(fitter.MedianSeconds is { } m1 && Math.Abs(m1 + 3.4) < 0.05, $"★ 中位数未被那条坏值拽动：{fitter.MedianSeconds:F3}s");

        fitter.Add(baseAt.AddHours(4), TimeSpan.FromSeconds(-3.45));
        Check(!fitter.LastSampleWasOutlier && fitter.SampleCount == 4, $"下一条正常测量入列：{fitter.SampleCount} 个样本");
        Check(!fitter.LastSampleWasOutlier, "正常测量不会误判离群");

        Console.WriteLine();
        Console.WriteLine("③ 连续两次一致的新基准 → 重开样本段（人工校准 / 换信号源）");
        // v1.1.2.0：重开段还要求至少一条来自模板命中（见 DriftFitter.SegmentRestartRequiresTemplate）。
        // 先走「两条都不是模板命中」这条路：彼此一致也不许重开（2026-10-26 09:40/10:10 两条错值
        // 只差 0.059s 就互相印证、把偏移从 −3.10 拽到 −0.10，正是这里要拦的情形）。
        fitter.Add(baseAt.AddHours(5), TimeSpan.FromSeconds(20.0));
        Check(fitter.LastSampleWasOutlier && fitter.SampleCount == 4, $"第一条新基准先当离群挂起：{fitter.SampleCount} 个样本仍为旧段");
        fitter.Add(baseAt.AddHours(6), TimeSpan.FromSeconds(20.2));
        Check(fitter.SampleCount == 4 && fitter.LastSampleWasOutlier,
            $"★ 两条都不是模板命中 → 即使彼此只差 0.2s 也不重开样本段（仍 {fitter.SampleCount} 个）");

        // 再来一条与挂起值一致的**模板命中**测量 → 允许重开
        fitter.Add(baseAt.AddHours(7), TimeSpan.FromSeconds(20.1), templateConfirmed: true);
        Check(fitter.SampleCount == 2, $"★ 模板命中确认后重开样本段，{fitter.SampleCount} 个样本");
        Check(fitter.MedianSeconds is { } m2 && Math.Abs(m2 - 20.1) < 0.05, $"新段中位数 {fitter.MedianSeconds:F3}s（期望 20.1）");

        Console.WriteLine();
        Console.WriteLine("③b 双峰检测：两簇中心相距 ≥ 4.0s 时中位数不代表任何真实状态（v1.1.2.0）");
        // 2026-10-26 14:40 实机实例：4 个参与样本 −4.072 / −3.220 / +7.285 / +7.912，
        // 中位数 = 2.033，正确的 6.052 距中心 4.019s 被判离群拒写、错误的 0.583 距中心 1.450s 被接受。
        // 这种「两簇同时在列」只可能来自 Seed（当天 jsonl 恢复，不做离群过滤）或人工勾选——
        // 逐条 Add 的路径上，第二条那簇早就被离群门拦在门外了。
        var bimodal = new DriftFitter();
        bimodal.Seed(new[]
        {
            (baseAt, -4.072),
            (baseAt.AddHours(1), -3.220),
            (baseAt.AddHours(2), 7.285),
            (baseAt.AddHours(3), 7.912),
        });
        Check(bimodal.IsBimodal, $"★ 两簇（−4.07/−3.22 与 +7.29/+7.91）→ 判双峰");
        Check(bimodal.MedianSeconds is { } mb && Math.Abs(mb - 2.033) < 0.01,
            $"中位数落在两簇空档里：{bimodal.MedianSeconds:F3}s（这正是「不代表任何真实状态」的那个值）");
        Check(bimodal.BimodalNote?.Contains("双峰") == true, "双峰说明可读（供日志与设置页）");

        // 单声源散布 1.8s 不许误判双峰
        var spread = new DriftFitter();
        for (var i = 0; i < 6; i++)
            spread.Add(baseAt.AddHours(i), TimeSpan.FromSeconds(-3.0 + 0.36 * i));
        Check(!spread.IsBimodal, $"单声源散布 1.8s → 不判双峰（{spread.MaxDeviationSeconds:F3}s）");

        // 一个孤立脏值也不许判双峰（那簇只有 1 个样本）
        var lone = new DriftFitter();
        for (var i = 0; i < 4; i++)
            lone.Add(baseAt.AddHours(i), TimeSpan.FromSeconds(-3.0 + 0.05 * i));
        lone.Add(baseAt.AddHours(5), TimeSpan.FromSeconds(30.0));
        Check(!lone.IsBimodal, $"孤立脏值（1 个样本的簇）→ 不判双峰，仍按离群处理（{lone.SampleCount} 个样本）");

        Console.WriteLine();
        Console.WriteLine("④ 趋势门槛：6 样本 / 跨度 4h / 残差 0.6s");
        var trend = new DriftFitter();
        for (var i = 0; i < 6; i++)
            trend.Add(baseAt.AddHours(i), TimeSpan.FromSeconds(-5.0 + 0.1 * i));
        Check(trend.TrendEnabled, $"★ 6 个样本跨 5h、残差≈0 → 趋势启用（斜率 {trend.SlopeSecondsPerHour:F3} s/h）");
        Check(trend.PredictSeconds(baseAt.AddHours(6)) is { } p1 && Math.Abs(p1 - (-4.75 + 0.1)) < 0.05,
            $"趋势外推 1h 后：{trend.PredictSeconds(baseAt.AddHours(6)):F3}s（中位数 −4.75s + 斜率 0.1）");

        var noisy = new DriftFitter();
        for (var i = 0; i < 6; i++)
            noisy.Add(baseAt.AddHours(i), TimeSpan.FromSeconds(i % 2 == 0 ? 0.0 : 1.5));
        Check(!noisy.TrendEnabled && noisy.SampleCount == 6,
            $"★ 散布 ±1.5s（残差 > 0.6s）时拒绝追趋势（{noisy.SampleCount} 个样本，TrendEnabled={noisy.TrendEnabled}）");

        var few = new DriftFitter();
        for (var i = 0; i < 5; i++)
            few.Add(baseAt.AddHours(i), TimeSpan.FromSeconds(-5.0 + 0.1 * i));
        Check(!few.TrendEnabled, $"只有 5 个样本 → 不启用趋势（门槛 {DriftFitter.TrendMinSamples} 个）");

        Console.WriteLine();
        if (failed == 0)
        {
            Console.WriteLine("  写入策略与估计器断言全部通过。");
            return 0;
        }

        Console.WriteLine($"  断言失败 {failed} 项。");
        return 8;
    }

    /// <summary>
    /// 按生产口径对单个 dump 跑「开关窗口 + 课表窗口」两次闸门并复核。
    /// 返回 null 表示文件名无法解析（不含窗口起点/边界时刻）。
    /// </summary>
    private static (OnsetGate.WindowVerdict Verdict, OnsetGate.OnsetHit? Switch, OnsetGate.OnsetHit? Schedule,
        string Note, double Offset, double BoundaryInDump)? ScanBothWindows(string path, double? overrideOffset)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var m = System.Text.RegularExpressions.Regex.Match(name, @"^\d{8}-(\d{2})(\d{2})(\d{2})-(\S+)-B(\d{2})(\d{2})(\d{2})$");
        if (!m.Success)
            return null;

        var start = new TimeSpan(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
        var boundary = new TimeSpan(int.Parse(m.Groups[5].Value), int.Parse(m.Groups[6].Value), int.Parse(m.Groups[7].Value));
        var boundaryInDump = (boundary - start).TotalSeconds;
        if (boundaryInDump < 0)
            boundaryInDump += 24 * 3600;

        // 文件名起点 = 布防时刻（显示域边界前 20 s）⇒ 偏移 = (边界 − 起点) − 20
        var offset = overrideOffset ?? boundaryInDump - 20.0;

        var samples = WavReader.ReadMono48k(path, out _);
        if (samples == null)
            return null;

        var audio = new CaptureAudio();
        var fileStartLocal = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Local);
        audio.Write(samples, fileStartLocal.ToUniversalTime());

        // 开关（= B_wall）在文件内 +20 s；课表边界再往后 +偏移
        var switchCenter = fileStartLocal.AddSeconds(20.0);
        var scheduleCenter = switchCenter.AddSeconds(offset);

        var switchHit = OnsetGate.Find(audio, switchCenter, out _);
        var scheduleHit = OnsetGate.Find(audio, scheduleCenter, out _);
        var chosen = OnsetGate.Reconcile(switchHit, scheduleHit, out var verdict, out var note);
        _ = chosen;

        return (verdict, switchHit, scheduleHit, note, offset, boundaryInDump);
    }

    /// <summary>命中点在 dump 文件内的秒数（音频锚点即文件起点；命中时刻是 UTC，锚点是本地）。</summary>
    private static double OnsetInFile(OnsetGate.OnsetHit hit)
        => (hit.OnsetUtc.ToLocalTime() - hit.AnchorWallLocal).TotalSeconds;

    /// <summary>
    /// 样本管理数据层的回归断言（v1.0.4 增补，纯离线、不碰实机目录）。
    ///
    /// 断言对象：<see cref="OffsetSampleStore"/> 的人工复核标记链路。语义（用户要求）：
    /// **勾选 = 参与手动拟合，取消勾选 = 不参与；人工设为有效的一律参与，无论当初是否被写入过。**
    /// 默认勾选状态 = 判据原判（Applied）。造一份临时样本文件与一份临时旁挂文件，验证：
    /// <list type="number">
    /// <item><description>默认：只有 Applied 的样本参与拟合（= 旧口径，未被人改过时行为不变）；</description></item>
    /// <item><description>**把一条「未写入」的样本人工设为有效 → 它参与拟合**（本次要求的核心用例）；</description></item>
    /// <item><description>把一条「已写入」的样本人工取消 → 它不再参与，其余不受影响；</description></item>
    /// <item><description>撤销人工结论后回到判据原判；</description></item>
    /// <item><description>旁挂文件不存在 / 含乱行时不影响样本读取。</description></item>
    /// <item><description>设置页「当时偏移」列取的是记录里的 <c>CurrentSec</c>（那次测量依据的偏移、拟合的输入侧），
    /// 不是拟合算出来的 <c>RequiredSec</c>——用例里 4 条的 CurrentSec 全相同而 RequiredSec 各不相同，
    /// 字段一旦用错就会被钉住（v1.1.0 修订）。</description></item>
    /// </list>
    /// 任一条不符即返回非零。
    /// </summary>
    private static int SampleManageTest()
    {
        var root = Path.Combine(Path.GetTempPath(), "belltime-sample-manage-test");
        if (Directory.Exists(root))
            Directory.Delete(root, true);
        Directory.CreateDirectory(Path.Combine(root, "Logs"));

        var failed = 0;
        void Check(bool ok, string what)
        {
            Console.WriteLine($"  [{(ok ? "通过" : "失败")}] {what}");
            if (!ok)
                failed++;
        }

        try
        {
            OffsetSampleStore.Initialize(root);

            // 造 4 条当天样本，覆盖全部默认勾选情形：
            //   ① 判据写入过                       → 默认参与
            //   ② 死区带内未写入（差值 0.02 ≤ 死区）→ 默认参与（本次要求）
            //   ③ 大误差拒写（差值 5.0 > 死区）     → 默认不参与
            //   ④ 死区带内未写入，但人工已标不参与  → 不参与（人工优先）
            var now = DateTime.Now;
            // 落盘只到毫秒：先把时刻截到毫秒，否则回读后与内存值不相等（下面按时刻定位会找不到）
            static DateTime Ms(DateTime t) => new DateTime(t.Ticks - t.Ticks % TimeSpan.TicksPerMillisecond, t.Kind);
            var appliedAt = Ms(now.AddHours(-3));
            var deadZoneAt = Ms(now.AddHours(-2));
            var rejectedAt = Ms(now.AddHours(-1));
            var overriddenAt = Ms(now);

            OffsetSampleStore.Append(appliedAt, 6.90, 6.50, "下课", appliedAt.ToString("HH:mm:ss.fff"), true);
            OffsetSampleStore.Append(deadZoneAt, 6.52, 6.50, "下课", deadZoneAt.ToString("HH:mm:ss.fff"), false);
            OffsetSampleStore.Append(rejectedAt, 1.50, 6.50, "上课", rejectedAt.ToString("HH:mm:ss.fff"), false);
            OffsetSampleStore.Append(overriddenAt, 6.51, 6.50, "下课", overriddenAt.ToString("HH:mm:ss.fff"), false);
            OffsetSampleStore.SetValidity(overriddenAt.ToString("yyyy-MM-dd HH:mm:ss.fff"), false);

            var loaded = OffsetSampleStore.LoadToday(now, out var note);
            Check(loaded.Count == 4, $"载入当天样本 4 条（实际 {loaded.Count}）：{note}");
            Check(loaded.Count(x => x.IsEligible) == 2, $"默认参与拟合 = 2 条（写入过 + 死区带内），实际 {loaded.Count(x => x.IsEligible)}");
            Check(loaded.All(x => !string.IsNullOrEmpty(x.Kind)), "Kind 字段已解析（供界面显示）");
            Check(loaded.All(x => !string.IsNullOrEmpty(x.BoundaryDisplay)), "B 字段已解析（供界面显示）");

            var applied1 = loaded.First(x => x.At == appliedAt);
            var deadZone1 = loaded.First(x => x.At == deadZoneAt);
            var rejected1 = loaded.First(x => x.At == rejectedAt);
            var overridden1 = loaded.First(x => x.At == overriddenAt);

            Check(applied1.IsEligible, "① 判据写入过 → 默认参与");
            Check(deadZone1.IsEligible && deadZone1.InDeadZone, "★ ② 死区带内未写入 → 默认参与（本次要求）");
            Check(!rejected1.IsEligible && !rejected1.InDeadZone, "③ 大误差拒写 → 默认不参与");
            Check(!overridden1.IsEligible, "④ 人工标为不参与 → 不参与（人工优先于默认）");

            // ── 样本管理「当时偏移」列的口径（v1.1.0 修订）──
            // 这一列要显示的是样本记录里那次测量**依据的偏移量**（CurrentSec，拟合的输入侧），
            // 不是拟合算出来的「所需偏移」（RequiredSec）。上面 4 条的 CurrentSec 都落成 6.50、
            // RequiredSec 却是 6.90/6.52/1.50/6.51 —— 只要混用就会立刻被这几条钉住。
            Check(loaded.All(x => Math.Abs(x.CurrentSec - 6.50) < 0.0005),
                "★ 「当时偏移」列的取值 = 记录里的 CurrentSec（4 条都是 6.500）");
            Check(loaded.All(x => Math.Abs(x.CurrentSec - x.RequiredSec) > 0.0005),
                "★ 「当时偏移」与「所需偏移」在这批样本里确实不同（用错字段必被这组数据暴露）");
            Check(OffsetSampleStore.InputOffsetLabel(applied1) == "+6.50 s",
                $"★ 列文字格式 = 带符号两位小数 + 空格 s（实际「{OffsetSampleStore.InputOffsetLabel(applied1)}」）");
            Check(OffsetSampleStore.InputOffsetLabel(loaded.First(x => x.CurrentSec == 6.50 && x.RequiredSec == 1.50)) == "+6.50 s",
                "★ 大误差拒写那条（所需 1.500 ≠ 当时 6.500）显示的仍是当时偏移");

            // ── 「采用偏移」列与两位小数写入（v1.1.1.0）──
            // 预览必须复用 Analyze：当前参与 ①6.90 + ②6.52 → 6.71；强制纳入 ③1.50 后
            // 三条中位数 6.52。只取单行 RequiredSec 或手写中位数都会被这组数钉住。
            var previewApplied = ManualFitService.AnalyzeWithForcedSample(loaded, 0);
            Check(previewApplied != null && Math.Abs(previewApplied.OffsetSeconds - 6.71) < 0.0005,
                $"★ 已勾选行的采用偏移 = 当前拟合结果 6.71（实际 {previewApplied?.OffsetSeconds:F2}）");
            var previewRejected = ManualFitService.AnalyzeWithForcedSample(loaded, 2);
            Check(previewRejected != null && previewRejected.ValidCount == 3
                  && Math.Abs(previewRejected.OffsetSeconds - 6.52) < 0.0005,
                $"★ 未勾选行强制纳入：参与 3 条、拟合 6.52（实际 {previewRejected?.ValidCount}/{previewRejected?.OffsetSeconds:F2}）");
            Check(ManualFitService.AnalyzeWithForcedSample(loaded, -1) == null
                  && ManualFitService.AnalyzeWithForcedSample(loaded, loaded.Count) == null,
                "采用偏移预览：越界索引返回 null");

            var roundingFit = ManualFitService.Analyze(new[]
            {
                new OffsetSample(now.AddMinutes(-10), 6.901, false),
                new OffsetSample(now, 6.526, false),
            });
            Check(roundingFit != null && Math.Abs(roundingFit.OffsetSeconds - 6.71) < 0.0005,
                $"★ 手动拟合写入值取两位小数（6.901/6.526 → 6.71，实际 {roundingFit?.OffsetSeconds:F4}）");
            Check(Math.Abs(CorrectionPolicy.RoundOffsetSeconds(4.17585) - 4.18) < 1e-9
                  && Math.Abs(CorrectionPolicy.RoundOffsetSeconds(-3.2294) + 3.23) < 1e-9,
                "写入舍入：4.17585 → 4.18，-3.2294 → -3.23");
            Check(OffsetSampleStore.OffsetLabel(4.17585) == "+4.18 s"
                  && OffsetSampleStore.OffsetLabel(-3.2294) == "-3.23 s",
                $"★ 采用偏移列文字：两位小数、带符号（实际「{OffsetSampleStore.OffsetLabel(4.17585)}」「{OffsetSampleStore.OffsetLabel(-3.2294)}」）");

            // 死区阈值真的被使用：把死区调到比差值更小，② 就应变成不参与
            var savedDeadZone = OffsetSampleStore.DeadZoneSeconds;
            OffsetSampleStore.DeadZoneSeconds = 0.01;
            var narrowed = OffsetSampleStore.LoadToday(now, out _);
            Check(!narrowed.First(x => x.At == deadZoneAt).IsEligible,
                "死区阈值调小后，② 不再被判为死区带内（证明阈值真的被使用）");
            OffsetSampleStore.DeadZoneSeconds = savedDeadZone;

            // 拟合结果随默认口径变化：3 条未被人为改过的默认参与（①②④中④被人工否掉 → 实际 2 条）
            var fit = ManualFitService.Analyze(loaded);
            Check(fit != null && fit.ValidCount == 2, $"手动拟合参与样本 {fit?.ValidCount}（期望 2）");
            Check(fit != null && fit.TotalCount == 4, $"总样本数 {fit?.TotalCount}（期望 4）");

            // ── 人工设为有效：把「大误差拒写」那条勾上 → 它必须参与 ──
            Check(OffsetSampleStore.SetValidity(rejected1.Ts, true), "把大误差拒写那条人工设为有效");
            var afterOptIn = OffsetSampleStore.LoadToday(now, out _);
            var optedIn = afterOptIn.First(x => x.Ts == rejected1.Ts);
            Check(optedIn.IsEligible, "★ 人工设为有效的样本（大误差拒写）现在参与拟合");
            Check(!optedIn.Applied, "（它的 Applied 仍为 false —— 参与与否只看人工标记）");
            Check(afterOptIn.Count(x => x.IsEligible) == 3, $"参与拟合升为 3 条，实际 {afterOptIn.Count(x => x.IsEligible)}");

            var fitAfterOptIn = ManualFitService.Analyze(afterOptIn);
            Check(fitAfterOptIn != null && fitAfterOptIn.ValidCount == 3,
                $"手动拟合参与样本数 {fitAfterOptIn?.ValidCount}（期望 3）");

            // ── 反向：把「写入过」那条人工取消 → 不再参与 ──
            Check(OffsetSampleStore.SetValidity(applied1.Ts, false), "把写入过的那条人工取消勾选");
            var afterOptOut = OffsetSampleStore.LoadToday(now, out _);
            Check(!afterOptOut.First(x => x.Ts == applied1.Ts).IsEligible, "人工取消后不再参与拟合");
            Check(afterOptOut.First(x => x.Ts == applied1.Ts).Applied, "（它的 Applied 仍为 true）");
            Check(afterOptOut.Count(x => x.IsEligible) == 2, $"参与拟合回到 2 条，实际 {afterOptOut.Count(x => x.IsEligible)}");

            // ── 旁挂文件含乱行：不影响样本读取 ──
            File.AppendAllText(OffsetSampleStore.ExclusionsPath!, "{ 这不是合法 JSON" + Environment.NewLine);
            var afterGarbage = OffsetSampleStore.LoadToday(now, out var note2);
            Check(afterGarbage.Count == 4, $"旁挂文件含乱行时样本仍可读（{afterGarbage.Count} 条）：{note2}");
            Check(afterGarbage.Count(x => x.IsEligible) == 2, "乱行不影响参与拟合的判定");

            // ── 跨天恢复（v1.1.0）：布防时按时间窗恢复样本，而不是只看当天 ──
            // 起因：10-15 的 08:10 窗口是当天第一条样本（−6.456s 脏值），序列为空 → 它自己成了中位数，
            // 被当成预测值步进写入（偏移 1.438 → −1.562）。带上前一晚的样本，这种脏值立刻能判成离群。
            var clock = DateTime.Today.AddHours(9);
            var yesterdayEvening = clock.Date.AddDays(-1).AddHours(22);
            OffsetSampleStore.Append(yesterdayEvening, 1.40, 1.44, "下课", yesterdayEvening.ToString("HH:mm:ss.fff"), true);

            var todayOnly = OffsetSampleStore.LoadToday(clock, out _);
            Check(todayOnly.All(x => x.At.Date == clock.Date), "LoadToday 仍然只给当天样本");
            var last12h = OffsetSampleStore.LoadSince(clock.AddHours(-12), out _);
            Check(last12h.Any(x => x.At == yesterdayEvening), "★ 12h 窗口把前一晚的样本也恢复进来（早晨第一个窗口不再是孤证）");
            Check(last12h.Count == todayOnly.Count + 1, $"12h 窗口 = 当天 {todayOnly.Count} 条 + 前一晚 1 条（实际 {last12h.Count}）");
            var last1h = OffsetSampleStore.LoadSince(clock.AddHours(-1), out _);
            Check(!last1h.Any(x => x.At == yesterdayEvening), "窗口收窄到 1h 后前一晚样本不再进入");

            // ── 恢复序列的摄取口径（v1.1.0.0）：判据已判脏值拒写的样本不得回灌估计器 ──
            // 起因（2026-10-16 实机）：首窗恢复把前一晚两条 outlier（−6.395 / −6.427）当普通测量喂进去，
            // 恢复中位数被拖到 −3.359s（只留干净样本应为 −0.11s）—— 判据白天明确拒写的脏值，
            // 隔夜又从恢复窗口流了回来（原先只排了旁证样本）。
            OffsetSampleStore.Append(clock.AddHours(-3), -6.395, -1.414, "上课",
                clock.AddHours(-3).ToString("HH:mm:ss.fff"), false, SampleReason.Outlier);
            OffsetSampleStore.Append(clock.AddHours(-2), -6.427, -1.414, "上课",
                clock.AddHours(-2).ToString("HH:mm:ss.fff"), false, SampleReason.LargeError);
            OffsetSampleStore.Append(clock.AddHours(-4), 0.150, 0.254, "下课",
                clock.AddHours(-4).ToString("HH:mm:ss.fff"), false, SampleReason.SideSchedule);
            var beforeIngest = OffsetSampleStore.LoadToday(clock, out _);
            var reject = beforeIngest.First(x => x.Reason == SampleReason.Outlier);
            var legacyReject = beforeIngest.First(x => x.Reason == SampleReason.LargeError);
            var side = beforeIngest.First(x => x.Reason == SampleReason.SideSchedule);
            Check(reject.IsRejected && legacyReject.IsRejected, "★ 已拒写样本（outlier / large-error）标记为「不该回灌估计器」");
            Check(!side.IsRejected && side.IsSideEvidence, "旁证样本与之分开标记（不混为一类）");
            Check(!OffsetSampleStore.IsSeedableForRuntime(reject)
                  && !OffsetSampleStore.IsSeedableForRuntime(legacyReject)
                  && !OffsetSampleStore.IsSeedableForRuntime(side),
                "★ IsSeedableForRuntime：已拒写与旁证都不进运行时估计器");
            Check(OffsetSampleStore.IsSeedableForRuntime(beforeIngest.First(x => x.Applied)),
                "★ 判据写入过的干净测量照常回灌");
            Check(beforeIngest.Any(x => x.Reason == SampleReason.Unknown)
                  && OffsetSampleStore.IsSeedableForRuntime(beforeIngest.First(x => x.Reason == SampleReason.Unknown)),
                "旧数据（无 Reason 字段、原因未记）仍按普通测量处理，不被新规则误伤");

            // 数值验证：同一批样本，排除已拒写后的中位数必须明显回正（10-16 实机那组数）
            var withRejects = new[] { -6.395, -6.427, 0.098, -0.323 };
            var cleanOnly = new[] { 0.098, -0.323 };
            static double Median(double[] v)
            {
                var s = v.OrderBy(x => x).ToArray();
                var m = s.Length / 2;
                return s.Length % 2 == 1 ? s[m] : (s[m - 1] + s[m]) / 2.0;
            }
            Check(Math.Abs(Median(withRejects) + 3.359) < 0.01,
                $"回灌脏值会把中位数拖到 {Median(withRejects):F3}s（实机日志正是 -3.359s）");
            Check(Math.Abs(Median(cleanOnly) + 0.112) < 0.01,
                $"★ 排除已拒写后中位数为 {Median(cleanOnly):F3}s（首窗本可一条到位）");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [失败] 异常：{ex}");
            failed++;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine();
        if (failed == 0)
        {
            Console.WriteLine("样本管理数据层断言全部通过。");
            return 0;
        }

        Console.WriteLine($"有 {failed} 项断言失败。");
        return 7;
    }

    /// <summary>
    /// 「清除无用录音」（设置页按钮）的数据层回归断言：直接断言生产代码 <see cref="DumpCleaner"/>，
    /// 而不是另抄一份删除逻辑。覆盖：
    /// <list type="bullet">
    /// <item><description>清点：只数 <c>Dumps\</c> 下一层的 <c>*.wav</c>，体积相加正确；</description></item>
    /// <item><description>清除：只删「陈年且不在保护名单里」的 wav，返回数量 / 释放体积 / 失败数 / 保留数；</description></item>
    /// <item><description>**保留判据**：<see cref="DumpCleaner.RetentionDays"/> 天以内的、名字在
    /// <see cref="DumpCleaner.ProtectedFiles"/> 里的（离线断言夹具）、名字认不出时刻的一律不删；</description></item>
    /// <item><description>**安全边界**：铃声模板、非 wav 文件、子目录里的 wav 一个都不能少（这是不可撤销的删除）；</description></item>
    /// <item><description>幂等：再清一次是空操作，不报错；</description></item>
    /// <item><description>配置目录为空 / 不存在时不抛异常。</description></item>
    /// </list>
    /// 任一条不符即返回非零。
    /// </summary>
    private static int DumpsClearTest()
    {
        var root = Path.Combine(Path.GetTempPath(), "belltime-dumps-clear-test");
        if (Directory.Exists(root))
            Directory.Delete(root, true);

        var dumps = Path.Combine(root, "Dumps");
        var templates = Path.Combine(root, "Templates");
        var nested = Path.Combine(dumps, "sub");
        Directory.CreateDirectory(dumps);
        Directory.CreateDirectory(templates);
        Directory.CreateDirectory(nested);

        var failed = 0;
        void Check(bool ok, string what)
        {
            Console.WriteLine($"  [{(ok ? "通过" : "失败")}] {what}");
            if (!ok)
                failed++;
        }

        try
        {
            // 基准时刻写死：断言不随运行日期漂移
            var now = new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Local);

            // 两个陈年调试录音（1000 + 3000 字节）= 唯一该删的
            var a = Path.Combine(dumps, "20260101-080000-上课-B080000.wav");
            var b = Path.Combine(dumps, "20260101-120000-下课-B120000.wav");
            File.WriteAllBytes(a, new byte[1000]);
            File.WriteAllBytes(b, new byte[3000]);

            // 三份「绝不该删」的：近 7 天的、名字在保护名单里的（哪怕很旧）、名字认不出时刻的
            var recent = Path.Combine(dumps, "20261012-100000-上课-B100000.wav");
            File.WriteAllBytes(recent, new byte[2000]);
            var protectedOld = Path.Combine(dumps, "20260917-085946-上课-B090000.wav");
            File.WriteAllBytes(protectedOld, new byte[4000]);
            var weird = Path.Combine(dumps, "随手丢进来的录音.wav");
            File.WriteAllBytes(weird, new byte[6000]);

            // 三类结构上就不该被碰的东西
            var keepTemplate = Path.Combine(templates, "上课铃.wav");
            File.WriteAllBytes(keepTemplate, new byte[240044]);
            var keepNote = Path.Combine(dumps, "转存说明.txt");
            File.WriteAllText(keepNote, "非 wav");
            var keepNested = Path.Combine(nested, "不该被递归删掉.wav");
            File.WriteAllBytes(keepNested, new byte[500]);

            Check(Array.IndexOf(DumpCleaner.ProtectedFiles, Path.GetFileName(protectedOld)) >= 0,
                "保护名单里确实有这个用例夹具名（名单与用例表对得上）");

            var counted = DumpCleaner.Inspect(root, now);
            Check(counted.Deletable == 2, $"清点：可清除 {counted.Deletable} 个（期望 2）");
            Check(counted.Bytes == 4000, $"清点：可清除体积 {counted.Bytes} 字节（期望 4000）");
            Check(counted.Kept == 3, $"清点：保留 {counted.Kept} 个（期望 3：近期 / 保护名单 / 认不出名字）");

            var cleared = DumpCleaner.Clear(root, now);
            Check(cleared.Deleted == 2, $"删除 {cleared.Deleted} 个（期望 2）");
            Check(cleared.FreedBytes == 4000, $"释放 {cleared.FreedBytes} 字节（期望 4000）");
            Check(cleared.Failed == 0, $"失败 {cleared.Failed} 个（期望 0）");
            Check(cleared.Kept == 3, $"保留 {cleared.Kept} 个（期望 3）");
            Check(!File.Exists(a) && !File.Exists(b), "★ 两个陈年调试录音已删除");

            Check(File.Exists(recent), $"★ {DumpCleaner.RetentionDays} 天以内的录音未被删除");
            Check(File.Exists(protectedOld), "★ 保护名单里的录音未被删除（离线断言夹具）");
            Check(File.Exists(weird), "★ 名字认不出时刻的录音未被删除（不猜着删）");
            Check(File.Exists(keepTemplate), "★ 铃声模板（Templates\\上课铃.wav）未被删除");
            Check(File.Exists(keepNote), "★ 非 wav 文件未被删除");
            Check(File.Exists(keepNested), "★ 子目录里的 wav 未被删除（不递归）");
            Check(Directory.Exists(dumps), "Dumps 目录本身保留（下次转存还能写）");

            var again = DumpCleaner.Clear(root, now);
            Check(again is { Deleted: 0, FreedBytes: 0, Failed: 0, Kept: 3 }, "★ 再清一次是空操作（幂等）");

            var missing = DumpCleaner.Inspect(Path.Combine(root, "不存在的目录"), now);
            Check(missing is { Deletable: 0, Bytes: 0, Kept: 0 }, "配置目录不存在 → 清点 0 个（不抛异常）");
            Check(DumpCleaner.Inspect("", now) is { Deletable: 0, Bytes: 0, Kept: 0 }, "配置目录为空串 → 清点 0 个（不抛异常）");
            Check(DumpCleaner.Clear(null, now) is { Deleted: 0, FreedBytes: 0, Failed: 0, Kept: 0 }, "配置目录为 null → 删除 0 个（不抛异常）");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [失败] 异常：{ex}");
            failed++;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine();
        if (failed == 0)
        {
            Console.WriteLine("「清除无用录音」断言全部通过。");
            return 0;
        }

        Console.WriteLine($"有 {failed} 项断言失败。");
        return 7;
    }

    /// <summary>一个窗口的闸门对比结果（供汇总统计）。</summary>
    private sealed class GateScanRow
    {
        public string Name = "";
        public bool OldHit;
        public bool NewHit;
        public double OldPeak;
        public double NewPeak;
        public double DeltaSeconds;
        public bool Changed => OldHit && NewHit && Math.Abs(DeltaSeconds) > 0.005;
    }

    /// <summary>
    /// 扫描一个 dump：打印候选表，并并排给出「旧判据（比值最大）」与「新判据（强候选组内比值最大）」的结果。
    /// </summary>
    /// <param name="path">dump 路径。</param>
    /// <param name="anchorOffsetSeconds">文件名起点与真实音频锚点之间的偏移（秒）。</param>
    private static GateScanRow? ScanOne(string path, double anchorOffsetSeconds = 0.0)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var m = System.Text.RegularExpressions.Regex.Match(name, @"^\d{8}-(\d{2})(\d{2})(\d{2})-(\S+)-B(\d{2})(\d{2})(\d{2})$");
        if (!m.Success)
        {
            Console.WriteLine($"跳过（文件名不含窗口起点/边界时刻）：{name}");
            return null;
        }

        var start = new TimeSpan(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
        var boundary = new TimeSpan(int.Parse(m.Groups[5].Value), int.Parse(m.Groups[6].Value), int.Parse(m.Groups[7].Value));
        var boundaryInDump = (boundary - start).TotalSeconds;
        if (boundaryInDump < 0)
            boundaryInDump += 24 * 3600;

        var samples = WavReader.ReadMono48k(path, out var readNote);
        if (samples == null)
        {
            Console.WriteLine($"读取失败：{name}：{readNote}");
            return null;
        }

        var rate = CaptureAudio.SampleRate;
        var audio = new CaptureAudio();
        // 锚点 = 文件起点（t=0）对应的墙钟；文件名给的是布防时刻，真实锚点可能晚 1~3 s，用 anchorOffsetSeconds 对齐
        var fileStartLocal = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Local);
        var anchorLocal = fileStartLocal.AddSeconds(anchorOffsetSeconds);
        audio.Write(samples, anchorLocal.ToUniversalTime());

        // 边界在「音频坐标」里的墙钟 = 文件起点 + 边界在文件内的秒数
        var boundaryWallLocal = fileStartLocal.AddSeconds(boundaryInDump);

        var baseSamples = (int)(0.3 * rate);
        var peakSamples = (int)(0.5 * rate);
        var stepSamples = Math.Max(1, (int)(BellTemplate.HopSeconds * rate));
        var gateFrom = (int)((boundaryWallLocal - TimeSpan.FromSeconds(OnsetGate.GateSeconds) - audio.AnchorWallLocal).TotalSeconds * rate);
        var gateTo = (int)((boundaryWallLocal + TimeSpan.FromSeconds(OnsetGate.GateSeconds) - audio.AnchorWallLocal).TotalSeconds * rate);
        gateFrom = Math.Max(gateFrom, (int)(RingCandidateSelector.SettleSkipSeconds * rate));
        gateTo = Math.Min(gateTo, audio.Length - peakSamples);

        var win = new double[audio.Length + 1];
        for (var i = 0; i < audio.Length; i++)
            win[i + 1] = win[i] + (double)audio.Samples[i] * audio.Samples[i];
        double Rms(int from, int to) => to <= from ? 0 : Math.Sqrt((win[to] - win[from]) / (to - from));

        Console.WriteLine();
        Console.WriteLine($"=== {name} ===");
        Console.WriteLine($"    窗口起点 {start:hh\\:mm\\:ss}，标称边界 {boundary:hh\\:mm\\:ss}，" +
                          $"边界在文件内 {boundaryInDump:F3}s，锚点偏移 {anchorOffsetSeconds:F3}s；" +
                          $"闸门区间 [{gateFrom / (double)rate:F2}s ~ {gateTo / (double)rate:F2}s]（±{OnsetGate.GateSeconds:F1}s）");

        var rows = new List<(double AtSec, double Ratio, double PeakRms, double BaseRms)>();
        for (var idx = gateFrom; idx <= gateTo; idx += stepSamples)
        {
            var baseFrom = idx - baseSamples;
            if (baseFrom < 0)
                continue;
            var baseline = Rms(baseFrom, idx);
            var peak = Rms(idx, Math.Min(audio.Length, idx + peakSamples));
            rows.Add((idx / (double)rate, peak / Math.Max(baseline, 1e-9), peak, baseline));
        }

        var qualified = rows.Where(r => r.PeakRms >= OnsetGate.AbsoluteMinRms && r.Ratio >= OnsetGate.RatioMin).ToList();
        Console.WriteLine($"    候选 {rows.Count} 个 / 合格 {qualified.Count} 个" +
                          $"（比值≥{OnsetGate.RatioMin:F1}× 且峰值≥{20 * Math.Log10(OnsetGate.AbsoluteMinRms):F1} dBFS）；" +
                          $"全区间峰值 {20 * Math.Log10(rows.Count == 0 ? 1e-12 : rows.Max(r => r.PeakRms)):F1} dBFS");

        var row = new GateScanRow { Name = name };
        if (qualified.Count > 0)
        {
            // 旧判据：合格候选里比值最大
            var oldPick = qualified.OrderByDescending(r => r.Ratio).First();
            // 新判据：强候选组（峰值 ≥ 组内上限 − DominanceDb）里比值最大
            var peakMax = qualified.Max(r => r.PeakRms);
            var floor = peakMax * Math.Pow(10, -OnsetGate.DominanceDb / 20.0);
            var strong = qualified.Where(r => r.PeakRms >= floor).ToList();
            var newPick = strong.OrderByDescending(r => r.Ratio).First();

            Console.WriteLine($"      合格候选按比值前 5 / 按峰值前 3（强候选组 {strong.Count} 个，容差 {OnsetGate.DominanceDb:F1} dB）：");
            foreach (var r in qualified.OrderByDescending(r => r.Ratio).Take(5))
                Console.WriteLine($"        {r.AtSec,8:F2}s  比值 {r.Ratio,7:F1}×  峰值 {20 * Math.Log10(r.PeakRms),6:F1} dBFS" +
                                  $"  {(r.PeakRms >= floor ? "∈强组" : "  ") }");
            foreach (var r in qualified.OrderByDescending(r => r.PeakRms).Take(3))
                Console.WriteLine($"        {r.AtSec,8:F2}s  比值 {r.Ratio,7:F1}×  峰值 {20 * Math.Log10(r.PeakRms),6:F1} dBFS  [峰值前三]");

            row.OldHit = true;
            row.NewHit = true;
            row.OldPeak = 20 * Math.Log10(oldPick.PeakRms);
            row.NewPeak = 20 * Math.Log10(newPick.PeakRms);
            row.DeltaSeconds = newPick.AtSec - oldPick.AtSec;

            Console.WriteLine($"    → 旧判据（比值最大）：{oldPick.AtSec,7:F2}s  比值 {oldPick.Ratio,6:F1}×  峰值 {row.OldPeak,6:F1} dBFS");
            Console.WriteLine($"    → 新判据（强组内比值最大）：{newPick.AtSec,7:F2}s  比值 {newPick.Ratio,6:F1}×  峰值 {row.NewPeak,6:F1} dBFS" +
                              $"    Δt={row.DeltaSeconds:+0.00;-0.00;0.00}s  Δ峰值={row.NewPeak - row.OldPeak:+0.0;-0.0;0.0} dB");
        }
        else
        {
            Console.WriteLine("    → 无合格候选（新旧判据都不会命中）");
        }

        // 用生产代码再跑一遍，确认新判据的实际输出（防止上面复算与生产口径漂移）
        var hit = OnsetGate.Find(audio, boundaryWallLocal, out var gateNote);
        Console.WriteLine($"    [生产口径] {(hit == null ? "未命中" : "命中")}：{gateNote}");
        if (hit == null)
            row.NewHit = false;

        return row;
    }

    /// <summary>
    /// 离线断言「大误差人工复核提醒」的判定（<see cref="NotificationReviewDecision"/>）。
    /// 这是复核提醒唯一的可离线验证部分：判定是纯函数，弹窗本身只能在实机看到。
    /// 用例覆盖「人工审核提醒」开关语义、阈值边界（严格大于）、绝对值处理，
    /// 以及「无冷却」这一行为（同一个大误差连续出现必须每次都提醒）。
    /// 任一条不符即返回非零，可直接用于回归。
    /// </summary>
    private static int NotifyTest()
    {
        var failed = 0;

        // (说明, 改动量, 阈值, 开关, 期望是否弹)
        var cases = new (string Name, double Change, double Limit, bool Enabled, bool Expect)[]
        {
            ("开关关（改动 3.5s 超阈值）→ 不弹", 3.5, 3.0, false, false),
            ("开关关（改动 50s 超阈值）→ 不弹", 50, 3.0, false, false),
            ("开关开，改动 0.8s 未超阈值 3.0s → 不弹", 0.8, 3.0, true, false),
            ("开关开，改动 3.5s 超过阈值 3.0s → 弹", 3.5, 3.0, true, true),
            ("开关开，改动 2.0s 恰好等于阈值 2.0s（严格大于语义）→ 不弹", 2.0, 2.0, true, false),
            ("开关开，改动 -5.8s（取绝对值）→ 弹", -5.8, 3.0, true, true),
            ("开关开，改动 7.5s（与基准值的差）→ 弹", 7.5, 3.0, true, true),
        };

        Console.WriteLine("提醒判定离线断言（无冷却；由「人工审核提醒」开关决定是否弹）：");
        foreach (var c in cases)
        {
            var (shouldNotify, why) = NotificationReviewDecision.Evaluate(c.Change, c.Limit, c.Enabled);
            var ok = shouldNotify == c.Expect;
            if (!ok)
                failed++;
            Console.WriteLine($"  [{(ok ? "通过" : "失败")}] {c.Name} → 弹={shouldNotify}（期望 {c.Expect}）");
            Console.WriteLine($"         └ {why}");
        }

        // 无冷却回归：同一个大误差连续判定多次，必须每次都说「弹」
        var repeats = 3;
        for (var i = 0; i < repeats; i++)
        {
            var (shouldNotify, _) = NotificationReviewDecision.Evaluate(4.2, 3.0, true);
            if (!shouldNotify)
            {
                failed++;
                Console.WriteLine($"  [失败] 无冷却回归：第 {i + 1} 次相同大误差应仍然弹提醒");
            }
        }
        if (failed == 0)
            Console.WriteLine($"  [通过] 无冷却回归：连续 {repeats} 次相同大误差都判为弹提醒");

        // 双窗口冲突提醒（v1.0.4 增补）：只受「人工审核提醒」开关约束，与阈值无关，
        // 也**没有冷却**（弹窗本身只能在实机看到，这里断言生产代码里的判定函数）。
        var conflictCases = new (string Name, bool Enabled, bool Expect)[]
        {
            ("冲突提醒：开关开 → 弹", true, true),
            ("冲突提醒：开关关 → 只记日志、不弹", false, false),
        };
        foreach (var c in conflictCases)
        {
            var (shouldNotify, why) = NotificationReviewDecision.EvaluateConflict(c.Enabled);
            var ok = shouldNotify == c.Expect;
            if (!ok)
                failed++;
            Console.WriteLine($"  [{(ok ? "通过" : "失败")}] {c.Name} → 弹={shouldNotify}（期望 {c.Expect}）");
            Console.WriteLine($"         └ {why}");
        }

        Console.WriteLine();
        if (failed == 0)
        {
            Console.WriteLine($"全部 {cases.Length} 条用例 + 无冷却回归 + {conflictCases.Length} 条冲突提醒用例通过。");
            return 0;
        }

        Console.WriteLine($"有 {failed} 项断言失败。");
        return 4;
    }

    /// <summary>
    /// 离线跑一遍「手动拟合」：读 <c>offset-samples.jsonl</c>（或任何同格式样本文件），
    /// 用**生产代码**（<see cref="OffsetSampleStore"/> 的判定口径 + <see cref="ManualFitService"/>）
    /// 选出参与拟合的样本并算出要写入的偏移，打印全过程，并对样本过滤做断言。
    ///
    /// 参与口径（与生产一致，v1.0.4）：**勾选 = 参与；默认勾选状态 = 判据原判（Applied）**，
    /// 人工结论读同目录的旁挂文件 <c>sample-exclusions.jsonl</c>。
    /// 因此本命令会**只读**地把旁挂文件也读进来（不写任何东西）。
    ///
    /// 用法：<c>ReplayTool manual-fit &lt;offset-samples.jsonl&gt; [筛选日期 yyyy-MM-dd]</c>（默认今天）
    /// </summary>
    private static int ManualFit(string[] args)
    {
        if (args.Length < 2 || !File.Exists(args[1]))
        {
            Console.WriteLine("找不到样本文件。");
            return 2;
        }

        var day = DateTime.Now.Date;
        if (args.Length > 2 && DateTime.TryParse(args[2], CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            day = parsed.Date;

        // 让生产存储指向该样本文件所在目录，从而读到同目录的旁挂文件（只读；不写盘）
        var dir = Path.GetDirectoryName(Path.GetFullPath(args[1]));
        if (!string.IsNullOrEmpty(dir))
            OffsetSampleStore.Initialize(Directory.GetParent(dir)?.FullName ?? dir);
        var userChoices = OffsetSampleStore.LoadExclusions();

        var samples = ReadSamplesFrom(path: args[1], day: day, userChoices: userChoices);
        if (samples == null)
            return 3;

        Console.WriteLine($"样本文件：{args[1]}");
        Console.WriteLine($"旁挂人工结论：{(userChoices.Count == 0 ? "无（全部按判据原判）" : $"{userChoices.Count} 条")}");
        Console.WriteLine($"筛选日期：{day:yyyy-MM-dd}；当天样本 {samples.Count} 个" +
                          $"（参与拟合 {samples.Count(s => s.IsEligible)} 个，" +
                          $"其中死区带内 {samples.Count(s => s.InDeadZone)} 个默认参与、" +
                          $"拒写 {samples.Count(s => !s.Applied && !s.InDeadZone && !s.IsSideEvidence)} 个默认不参与" +
                          (samples.Any(s => s.IsSideEvidence)
                              ? $"、旁证 {samples.Count(s => s.IsSideEvidence)} 个默认不参与"
                              : "") + "）");
        Console.WriteLine();

        foreach (var s in samples)
        {
            var mark = s.IsEligible ? "[参与]" : "[不参与]";
            var origin = OffsetSampleStore.JudgeLabel(s);
            var changed = s.UserValid == OffsetSampleStore.DefaultValid(s) ? "" : "（人工调整）";
            Console.WriteLine($"  {mark} {s.At:HH:mm:ss.fff}  当时偏移 {OffsetSampleStore.InputOffsetLabel(s),9}" +
                              $"  所需偏移 {s.RequiredSec,9:F4}s  {origin}{changed}");
        }
        Console.WriteLine();

        var failed = 0;

        // 参考提示：把「判据写入过、却被人工作废」以及「判据未写入、却被人工作废/参与」的条目说清楚
        var valid = samples.Where(s => s.IsEligible).ToList();
        foreach (var s in samples.Where(x => !x.IsEligible))
        {
            Console.WriteLine($"  [提示] 不参与拟合：{s.At:HH:mm:ss}（当时偏移 {OffsetSampleStore.InputOffsetLabel(s)}，" +
                              $"所需偏移 {s.RequiredSec:F4}s，判据写入={(s.Applied ? "是" : "否")}）");
        }

        var result = ManualFitService.Analyze(samples);
        if (result == null)
        {
            Console.WriteLine("当天没有参与拟合的样本 → 手动拟合不会写入任何东西（按钮会提示原因）。");
            return 0;
        }

        Console.WriteLine($"拟合结果：写入 {result.OffsetSeconds:F2}s");
        Console.WriteLine($"  参与拟合样本 {result.ValidCount}/{result.TotalCount}；中位数 {result.MedianSeconds:F3}s；" +
                          $"最大偏差 {result.MaxDeviationSeconds:F3}s；趋势启用={result.TrendEnabled}");
        Console.WriteLine($"  {result.Note}");
        Console.WriteLine();
        Console.WriteLine($"设置页结果文字：{ManualFitService.Describe(result, previousOffsetSeconds: null)}");

        // 断言：结果必须落在参与拟合样本的取值范围内（中位数/外推不可能越界太多）
        var min = valid.Min(v => v.RequiredSec);
        var max = valid.Max(v => v.RequiredSec);
        var slack = 1.0; // 趋势外推允许略微越界
        if (result.OffsetSeconds < min - slack || result.OffsetSeconds > max + slack)
        {
            failed++;
            Console.WriteLine($"  [失败] 拟合值 {result.OffsetSeconds:F3}s 越出有效样本范围 [{min:F3}, {max:F3}]±{slack:F1}s");
        }
        else
        {
            Console.WriteLine($"  [通过] 拟合值落在有效样本范围 [{min:F3}, {max:F3}]±{slack:F1}s 内");
        }

        // 断言：排除掉脏样本后，结果不应该被脏值拖走（用含脏样本的口径对比）
        var fitterAll = new DriftFitter();
        fitterAll.Seed(samples.Select(s => (s.At, s.RequiredSec)));
        var offsetAll = fitterAll.PredictSeconds(DateTime.Now);
        Console.WriteLine($"  对照：若把被排除的样本也喂进去，会得到 {offsetAll:F3}s（相差 " +
                          $"{Math.Abs((offsetAll ?? 0) - result.OffsetSeconds):F3}s）");

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "断言全部通过。" : $"有 {failed} 项断言失败。");
        return failed == 0 ? 0 : 5;
    }

    /// <summary>
    /// 解析样本文件里指定日期的样本（与生产 <see cref="OffsetSampleStore.LoadToday"/> 同一套字段解析规则），
    /// 并按旁挂人工结论给出 <see cref="OffsetSample.UserValid"/>：有结论以人工为准，否则按判据原判（Applied）。
    /// </summary>
    /// <param name="path">样本文件路径。</param>
    /// <param name="day">筛选日期。</param>
    /// <param name="userChoices">旁挂人工结论（Ts → 是否参与），可为 null。</param>
    private static List<OffsetSample>? ReadSamplesFrom(string path, DateTime day,
        IReadOnlyDictionary<string, bool>? userChoices = null)
    {
        try
        {
            var result = new List<OffsetSample>();
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0)
                    continue;

                var atText = ExtractQuoted(line, "Ts");
                var required = ExtractNumber(line, "RequiredSec");
                if (atText == null || required == null)
                    continue;
                if (!DateTime.TryParse(atText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
                    continue;
                if (at.Date != day)
                    continue;

                var applied = ExtractBool(line, "Applied") ?? true;
                var current = ExtractNumber(line, "CurrentSec") ?? 0;
                // 与生产同一口径：死区带内（未写入但差值落在死区里）默认也参与手动拟合
                var inDeadZone = !applied && Math.Abs(required.Value - current) <= OffsetSampleStore.DeadZoneSeconds;
                var userValid = userChoices != null && userChoices.TryGetValue(atText, out var choice)
                    ? choice
                    : applied || inDeadZone;

                result.Add(new OffsetSample(
                    at, required.Value, applied,
                    ExtractQuoted(line, "Kind") ?? "",
                    ExtractQuoted(line, "B") ?? "",
                    atText,
                    userValid,
                    current,
                    inDeadZone));
            }

            return result.OrderBy(x => x.At).ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"读取样本文件失败：{ex.Message}");
            return null;
        }
    }

    private static string? ExtractQuoted(string line, string name)
    {
        var key = $"\"{name}\":\"";
        var i = line.IndexOf(key, StringComparison.Ordinal);
        if (i < 0)
            return null;
        i += key.Length;
        var j = line.IndexOf('"', i);
        return j < 0 ? null : line.Substring(i, j - i);
    }

    private static double? ExtractNumber(string line, string name)
    {
        var key = $"\"{name}\":";
        var i = line.IndexOf(key, StringComparison.Ordinal);
        if (i < 0)
            return null;
        i += key.Length;
        var j = i;
        while (j < line.Length && (char.IsDigit(line[j]) || line[j] is '-' or '+' or '.' or 'e' or 'E'))
            j++;
        return double.TryParse(line.Substring(i, j - i), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v
            : null;
    }

    private static bool? ExtractBool(string line, string name)
    {
        var key = $"\"{name}\":";
        var i = line.IndexOf(key, StringComparison.Ordinal);
        if (i < 0)
            return null;
        i += key.Length;
        if (string.CompareOrdinal(line, i, "true", 0, 4) == 0)
            return true;
        if (string.CompareOrdinal(line, i, "false", 0, 5) == 0)
            return false;
        return null;
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

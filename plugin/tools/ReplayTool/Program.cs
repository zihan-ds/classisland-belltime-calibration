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

        // 双窗口复核诊断（gate-reconcile <dump.wav> [当时偏移秒]）
        if (args.Length > 0 && string.Equals(args[0], "gate-reconcile", StringComparison.OrdinalIgnoreCase))
            return GateReconcile(args);

        // 双窗口复核的回归断言（gate-reconcile-test <Dumps 目录>）
        if (args.Length > 0 && string.Equals(args[0], "gate-reconcile-test", StringComparison.OrdinalIgnoreCase))
            return GateReconcileTest(args);

        // 退化口径换算的回归断言（absolute-required-test）
        if (args.Length > 0 && string.Equals(args[0], "absolute-required-test", StringComparison.OrdinalIgnoreCase))
            return AbsoluteRequiredTest();

        if (args.Length < 3)
        {
            Console.WriteLine("用法：");
            Console.WriteLine("  ReplayTool <dump.wav> <模板目录> <边界在dump内秒数> [内核偏移秒=-5.1] [搜索半窗秒=12]");
            Console.WriteLine("  ReplayTool fitter <样本序列文件> [死区秒=0.3]   # 离线验证偏移估计算法（每行：yyyy-MM-dd HH:mm:ss,所需偏移秒）");
            Console.WriteLine("  ReplayTool notify-test                        # 断言「人工审核提醒」的开关与阈值判定");
            Console.WriteLine("  ReplayTool sample-manage-test                 # 断言样本管理（人工作废标记）的数据层行为");
            Console.WriteLine("  ReplayTool manual-fit <offset-samples.jsonl> [yyyy-MM-dd]  # 跑一遍手动拟合（当天有效样本 → 偏移）");
            Console.WriteLine("  ReplayTool gate-scan <dump.wav> <边界在dump内秒数>   # 打印闸门候选表（比值/峰值）+ 两种选法对比");
            Console.WriteLine("  ReplayTool gate-scan <Dumps 目录>                    # 对目录内全部 dump（文件名含边界时刻）批量跑");
            Console.WriteLine("  ReplayTool gate-guard <Dumps 目录>                   # 断言「基线静默候选被排除」（含 2026-09-19 静默样本）");
            Console.WriteLine("  ReplayTool gate-reconcile <dump.wav> [当时偏移秒]    # 打印开关窗口/课表窗口两套判据 + 复核结论");
            Console.WriteLine("  ReplayTool gate-reconcile-test <Dumps 目录>          # 断言双窗口复核（含 2026-09-20 上午 4 个杂音窗口）");
            Console.WriteLine("  ReplayTool absolute-required-test                    # 断言退化口径换算（残差 + 当前偏移 = 绝对所需偏移）");
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
    /// 基线静默门槛的回归断言（v1.0.3）。
    ///
    /// 背景（2026-09-19 实机）：麦克风在多个窗口里整段数字静默（采样恒为 0），
    /// 闸门把「麦克风恢复供音的那一瞬间」当成铃声——因为分母（起响前 0.3 s 基线）≈0，
    /// 比值炸成 748579× ~ 96702200×，而真铃的起响比值实测只有 4~346×。
    /// v1.0.3 起：基线 RMS &lt; RingDetector.NoiseFloorMin 的候选一律排除（比值不可信）。
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
    /// 双窗口复核（v1.0.3 增补）：对单个 dump 按生产口径跑「开关窗口」与「课表窗口」两次闸门，并给出复核结论。
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
    /// 双窗口复核的回归断言（v1.0.3 增补）：用真实 dump 锁住 2026-09-20 上午那次「闸门锁到杂音」的行为。
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
    /// </list>
    /// </summary>
    private static int GateReconcileTest(string[] args)
    {
        if (args.Length < 2 || !Directory.Exists(args[1]))
        {
            Console.WriteLine("用法：ReplayTool gate-reconcile-test <Dumps 目录>");
            return 1;
        }

        var cases = new (string File, OnsetGate.WindowVerdict Expected, string Why)[]
        {
            ("20260920-084933-下课-B085000.wav", OnsetGate.WindowVerdict.Conflict, "上午下课：课表窗口强 14.0 dB（真铃在课表边界上）"),
            ("20260920-093933-下课-B094000.wav", OnsetGate.WindowVerdict.Conflict, "上午下课：课表窗口强 2.1 dB"),
            ("20260920-104932-下课-B105000.wav", OnsetGate.WindowVerdict.Conflict, "上午下课：课表窗口强 3.3 dB"),
            ("20260920-113931-下课-B114000.wav", OnsetGate.WindowVerdict.Conflict, "上午下课：课表窗口强 4.8 dB"),
            ("20260918-093948-下课-B094000.wav", OnsetGate.WindowVerdict.PreferSwitch, "铃确实晚 9 秒，模板 NCC 0.924 佐证"),
            ("20260920-143941-下课-B144000.wav", OnsetGate.WindowVerdict.Agree, "两窗口同一起响"),
            ("20260920-173941-下课-B174000.wav", OnsetGate.WindowVerdict.Agree, "两窗口同一起响"),
            ("20260917-085946-上课-B090000.wav", OnsetGate.WindowVerdict.SwitchOnly, "课表窗口无合格起响沿"),
        };

        var failed = 0;
        foreach (var c in cases)
        {
            var path = Path.Combine(args[1], c.File);
            if (!File.Exists(path))
            {
                Console.WriteLine($"  [失败] 缺少 dump：{c.File}（本断言依赖实机录音，仓库不分发音频）");
                failed++;
                continue;
            }

            var r = ScanBothWindows(path, null);
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

        Console.WriteLine();
        if (failed == 0)
        {
            Console.WriteLine("  双窗口复核断言全部通过（含 2026-09-20 上午 4 个杂音窗口被判冲突）。");
            return 0;
        }

        Console.WriteLine($"  断言失败 {failed} 项。");
        return 7;
    }

    /// <summary>
    /// 退化口径换算的回归断言（v1.0.3 增补）：未观测到切换时，绝对所需偏移 = 残差 + 当前偏移。
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
    /// 样本管理数据层的回归断言（v1.0.3 增补，纯离线、不碰实机目录）。
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
            ("开关开，改动 -5.8s（实机 2026-09-17 17:10 那次，取绝对值）→ 弹", -5.8, 3.0, true, true),
            ("开关开，改动 7.5s（实机 18:50 那次 +0.29 与基准 -7.2 的差）→ 弹", 7.5, 3.0, true, true),
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

        // 双窗口冲突提醒（v1.0.3 增补）：只受「人工审核提醒」开关约束，与阈值无关，
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
    /// 参与口径（与生产一致，v1.0.3）：**勾选 = 参与；默认勾选状态 = 判据原判（Applied）**，
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
                          $"大误差拒写 {samples.Count(s => !s.Applied && !s.InDeadZone)} 个默认不参与）");
        Console.WriteLine();

        foreach (var s in samples)
        {
            var mark = s.IsEligible ? "[参与]" : "[不参与]";
            var origin = s.Applied ? "写入过" : s.InDeadZone ? "死区带内" : "大误差拒写";
            var changed = s.UserValid == (s.Applied || s.InDeadZone) ? "" : "（人工调整）";
            Console.WriteLine($"  {mark} {s.At:HH:mm:ss.fff}  所需偏移 {s.RequiredSec,9:F4}s  {origin}{changed}");
        }
        Console.WriteLine();

        var failed = 0;

        // 参考提示：把「判据写入过、却被人工作废」以及「判据未写入、却被人工作废/参与」的条目说清楚
        var valid = samples.Where(s => s.IsEligible).ToList();
        foreach (var s in samples.Where(x => !x.IsEligible))
        {
            Console.WriteLine($"  [提示] 不参与拟合：{s.At:HH:mm:ss}（{s.RequiredSec:F4}s，判据写入={(s.Applied ? "是" : "否")}）");
        }

        var result = ManualFitService.Analyze(samples);
        if (result == null)
        {
            Console.WriteLine("当天没有参与拟合的样本 → 手动拟合不会写入任何东西（按钮会提示原因）。");
            return 0;
        }

        Console.WriteLine($"拟合结果：写入 {result.OffsetSeconds:F3}s");
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

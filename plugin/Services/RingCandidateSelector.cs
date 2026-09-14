using System;
using System.Collections.Generic;
using System.Linq;

namespace BellTimeCalibration.Services;

/// <summary>
/// 有效铃选择器（v0.8.0：改为按「持续段 + 响度」选铃，取<b>起响点</b>为 t_ring）。
/// 背景与判据：
/// <list type="bullet">
/// <item><description>学校铃声是<b>持续数秒</b>的响亮声音；教室人声、桌椅、脚步等是瞬时声（时长通常 &lt;1s）——
/// 因此「持续时长 ≥ <see cref="MinBellSeconds"/>」是区分铃声与环境声的强判据。</description></item>
/// <item><description>同一窗口内可能有多段持续声，取<b>最响</b>者（校铃是本窗口最响的持续事件），峰值相同时取更靠近标称边界者。</description></item>
/// <item><description>返回的 t_ring 是段的<b>起响点</b>，不是「离标称时刻最近的那一点」——旧实现取后者，
/// 实测偏晚 2s 以上（2026-09-10 20:10：旧结果 20:10:00.495，用户实测真值 20:09:58.175）。</description></item>
/// <item><description>搜索窗口取配置项 <c>ToleranceSeconds</c>（v0.8.0 起语义 = 以标称边界为中心的搜索半窗），
/// 必须覆盖校铃钟的漂移幅度（可达 ~10s），否则漂移一大就再也搜不到铃。</description></item>
/// </list>
/// 校铃钟与 PC 墙钟同步假设：B_display 为课表显示域边界时刻（Kind=Local），候选（UTC）转本地后与其相减取偏差。
/// </summary>
public static class RingCandidateSelector
{
    /// <summary>
    /// 开麦静置秒数：忽略捕获开始后前 1.5 秒内起响的段（开麦瞬态环境伪影，2026-09-06 实机曾恒定误记为铃）。
    /// </summary>
    public const double SettleSkipSeconds = 1.5;

    /// <summary>判定为「持续铃声」的最短时长（秒）：人声/桌椅等瞬时声远短于此。</summary>
    public const double MinBellSeconds = 1.0;

    /// <summary>
    /// 相邻段的合并间隔（秒，v0.8.1）：间隔小于该值的多段视为**同一次铃声**（电铃/连击铃每一下都会短暂低于阈值，
    /// 不合并就会被拆成一堆 &lt;1s 的短段而被时长门槛全部滤掉——2026-09-10 21:40 实机即因此选错）。
    /// 合并后段长 = 首末段跨度，起响点 = 第一段的起响点，峰值 = 各段峰值最大值。
    /// </summary>
    public const double MergeGapSeconds = 0.5;

    /// <summary>
    /// 模板匹配选铃（v0.9.0 主路径）：对每个模板在整个可用区间做「粗定位 → 精定位 → 音色确认」，
    /// 只接受同时达标的匹配，且要求其起响点落在边界搜索窗口内；多个模板命中时取 NCC 最高者。
    /// </summary>
    /// <param name="templates">已加载的模板集合。</param>
    /// <param name="audio">窗口音频缓冲（仅内存）。</param>
    /// <param name="fromSample">搜索起点（采样索引，通常为静置期之后）。</param>
    /// <param name="toSample">搜索终点（采样索引）。</param>
    /// <param name="boundaryDisplayLocal">B_display（本地墙钟域）。</param>
    /// <param name="searchWindow">起响点允许的偏差半窗（±秒）。</param>
    /// <param name="nccMin">NCC 阈值。</param>
    /// <param name="spectralMin">音色相似度阈值。</param>
    /// <param name="note">判定说明（中文，供日志，含各模板得分）。</param>
    /// <returns>命中的最佳匹配；无命中返回 null。</returns>
    public static TemplateMatch? SelectByTemplate(
        IReadOnlyList<BellTemplate> templates,
        CaptureAudio audio,
        int fromSample,
        int toSample,
        DateTime boundaryDisplayLocal,
        TimeSpan searchWindow,
        double nccMin,
        double spectralMin,
        out string note)
    {
        note = "无模板";
        if (templates.Count == 0)
            return null;

        var details = new List<string>(templates.Count);
        TemplateMatch? best = null;
        var bestNcc = double.NegativeInfinity;

        foreach (var tpl in templates)
        {
            var m = TemplateMatcher.Match(tpl, audio.Samples, audio.Length, fromSample, toSample, nccMin, spectralMin);
            if (!m.Accepted || m.OnsetSampleIndex < 0)
            {
                details.Add(m.Note);
                continue;
            }

            var onsetWall = audio.WallTimeAt(m.OnsetSampleIndex);
            if (onsetWall == null)
            {
                details.Add($"{m.Label}：起响点墙钟换算失败");
                continue;
            }

            var deviation = onsetWall.Value.ToLocalTime() - boundaryDisplayLocal;
            if (Math.Abs(deviation.TotalSeconds) > searchWindow.TotalSeconds)
            {
                details.Add($"{m.Label}：命中但起响点偏差 {deviation.TotalSeconds:F2}s 超出搜索半窗 ±{searchWindow.TotalSeconds:F0}s");
                continue;
            }

            details.Add($"{m.Note}；起响点偏差 {deviation.TotalSeconds:+0.000;-0.000}s");
            if (m.Ncc > bestNcc)
            {
                bestNcc = m.Ncc;
                best = m;
            }
        }

        note = string.Join(" | ", details);
        return best;
    }

    /// <summary>
    /// 同簇合并：把间隔小于 <see cref="MergeGapSeconds"/> 的相邻段合并为一段，返回按起响点升序的新列表。
    /// </summary>
    /// <param name="burstsUtc">原始段（UTC，升序）。</param>
    public static List<RingBurst> MergeClusters(IReadOnlyList<RingBurst> burstsUtc)
    {
        var result = new List<RingBurst>();
        foreach (var b in burstsUtc)
        {
            if (result.Count == 0)
            {
                result.Add(b);
                continue;
            }

            var last = result[^1];
            var lastEnd = last.OnsetUtc + TimeSpan.FromSeconds(last.DurationSeconds);
            var gap = (b.OnsetUtc - lastEnd).TotalSeconds;
            if (gap <= MergeGapSeconds)
            {
                var newEnd = b.OnsetUtc + TimeSpan.FromSeconds(b.DurationSeconds);
                // 峰值归属的那一击要一并记住：起响点必须取它，而不是簇内最早那一击（见 RingBurst.PeakOnsetUtc）
                var peakOnset = b.PeakRms > last.PeakRms ? b.OnsetUtc : last.PeakOnsetUtc;
                var newPeak = Math.Max(last.PeakRms, b.PeakRms);
                result[^1] = last with
                {
                    DurationSeconds = Math.Max((newEnd - last.OnsetUtc).TotalSeconds, last.DurationSeconds),
                    PeakRms = newPeak,
                    PeakOnsetUtc = peakOnset
                };
            }
            else
            {
                result.Add(b);
            }
        }

        return result;
    }

    /// <summary>
    /// 开麦静置过滤：返回捕获开始 <paramref name="windowStartWallUtc"/>（UTC）起
    /// <see cref="SettleSkipSeconds"/> 秒之后起响的全部段（保持升序）；期间内的段视为开麦瞬态伪影丢弃。
    /// </summary>
    /// <param name="burstsUtc">候选段（UTC，升序，来自 RingDetector.Bursts）。</param>
    /// <param name="windowStartWallUtc">窗口（开麦）起始墙钟时刻（UTC）。</param>
    public static List<RingBurst> ExcludeSettlePeriod(IReadOnlyList<RingBurst> burstsUtc, DateTime windowStartWallUtc)
    {
        var cutoff = windowStartWallUtc + TimeSpan.FromSeconds(SettleSkipSeconds);
        return burstsUtc.Where(b => b.OnsetUtc >= cutoff).ToList();
    }


    // ─────────────────────────────────────────────────────────────────────
    // 已移除（v0.10.2）：SelectBell —— 启发式选铃
    //
    // 旧实现：在搜索窗口内挑「持续 ≥ MinBellSeconds 秒且峰值最响」的段，取其起响点为 t_ring。
    // 移除原因（2026-09-12 实机 15 个边界 dump 实测）：该判据在本场景下不可靠，误差可达
    //   −7.8 s（08:10）、−6.2 s（14:50）、+8.1 s（11:20）、+4.9 s（10:30）——
    // 因为它只能回答「哪个段最响」，而无法区分「主铃」与「提前 6~15 s 的另一段强起响」，
    // 也无法识别渐强噪声（实测伪匹配的绝对电平甚至比真铃更响）。
    //
    // 取代它的判据是 OnsetGate（起响沿闸门）：边界 ±2 s 内取「起响后峰值 ÷ 起响前基线」最大处，
    // 一步同时排除提前事件与渐强噪声，且不依赖波形对齐。
    //
    // 如需回退到旧行为，请从 git 历史或 HANDOFF.md 第 5 节的 0.9.x 版本记录取回，
    // 不要在本文件里重新实现 —— 它已被两次实机错误证伪。
    // ─────────────────────────────────────────────────────────────────────
}

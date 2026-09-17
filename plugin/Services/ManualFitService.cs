using System;
using System.Collections.Generic;
using System.Linq;

namespace BellTimeCalibration.Services;

/// <summary>
/// 一次手动拟合的结果（全部为基元类型，便于离线工具直接打印与断言）。
/// </summary>
/// <param name="OffsetSeconds">拟合出的「所需绝对偏移」（秒）——这就是要写入设置项的值。</param>
/// <param name="ValidCount">参与拟合的有效样本数（<c>Applied = true</c>）。</param>
/// <param name="TotalCount">当天样本总数（含被排除的）。</param>
/// <param name="MedianSeconds">有效样本的中位数（秒）。</param>
/// <param name="MaxDeviationSeconds">有效样本相对中位数的最大偏差（秒）。</param>
/// <param name="TrendEnabled">趋势外推是否启用（三项门槛是否同时满足）。</param>
/// <param name="Note">估计器给出的说明（中文，含样本数/跨度/残差）。</param>
public sealed record ManualFitResult(
    double OffsetSeconds,
    int ValidCount,
    int TotalCount,
    double? MedianSeconds,
    double? MaxDeviationSeconds,
    bool TrendEnabled,
    string Note);

/// <summary>
/// 手动拟合（v1.0.2，设置页「手动拟合并应用」按钮背后的纯逻辑）。
///
/// 做什么：读当天已落盘的样本 → 只保留**有效样本** → 用与运行时**完全相同**的估计器
/// （<see cref="DriftFitter"/>：近 <see cref="DriftFitter.WindowHours"/> 小时中位数，
/// 趋势外推仅在样本数/跨度/残差三项门槛同时满足时启用）算一次所需偏移，交给调用方写入。
///
/// 为什么叫「有效样本」而不是「全部样本」：<see cref="OffsetSample.Applied"/> 为 false 的样本
/// 有两种来源，都不是干净测量 —— 死区带内（偏移没动的存量，不代表当前基准）与大误差拒写
/// （含未观测到切换的退化绝对口径；实机 2026-09-17 就留下过 −0.91 / +0.29 两条，
/// 与当天真实基准 −7 s 相差近 7 秒且会被签名成「正常测量」）。用它们拟合等于把脏值写进设置项，
/// 正是「坚决不用脏值」要避免的事。这里不另造启发式（MAD/残差阈值之类），只用样本自带的标记。
///
/// 为什么用 <see cref="DriftFitter.Seed"/> 而不是逐条 <see cref="DriftFitter.Add"/>：
/// Add 带 2.5 s 跳变检测，序列里只要夹进一条脏样本就会清空整段重开，
/// 会让「一次手动拟合」变成「只用最后一条样本」。
/// </summary>
public static class ManualFitService
{
    /// <summary>
    /// 按当天样本算一次所需偏移。
    /// </summary>
    /// <param name="todaySamples">当天样本（<see cref="OffsetSampleStore.LoadToday"/> 的返回值）。</param>
    /// <returns>拟合结果；当天没有有效样本时返回 null。</returns>
    public static ManualFitResult? Analyze(IEnumerable<OffsetSample> todaySamples)
    {
        var all = todaySamples?.ToList() ?? new List<OffsetSample>();
        var valid = all.Where(s => s.Applied)
            .OrderBy(s => s.At)
            .ToList();

        if (valid.Count == 0)
            return null;

        var fitter = new DriftFitter();
        fitter.Seed(valid.Select(s => (s.At, s.RequiredSec)));

        var offset = fitter.PredictSeconds(DateTime.Now);
        if (offset == null)
            return null;

        return new ManualFitResult(
            offset.Value,
            valid.Count,
            all.Count,
            fitter.MedianSeconds,
            fitter.MaxDeviationSeconds,
            fitter.TrendEnabled,
            fitter.LastNote ?? "");
    }

    /// <summary>
    /// 把拟合结果整理成一行可读说明（设置页结果文字与日志共用，保证两处措辞一致）。
    /// </summary>
    /// <param name="result">拟合结果。</param>
    /// <param name="previousOffsetSeconds">写入前的偏移（秒）；未知时为 null。</param>
    public static string Describe(ManualFitResult result, double? previousOffsetSeconds)
    {
        var change = previousOffsetSeconds == null
            ? ""
            : $"（由 {previousOffsetSeconds.Value:F3} 改为 {result.OffsetSeconds:F3}，" +
              $"差 {result.OffsetSeconds - previousOffsetSeconds.Value:+0.000;-0.000;0.000}）";
        return $"已写入 {result.OffsetSeconds:F3} 秒{change}；" +
               $"有效样本 {result.ValidCount}/{result.TotalCount}；{result.Note}";
    }
}

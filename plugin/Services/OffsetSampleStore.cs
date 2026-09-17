using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace BellTimeCalibration.Services;

/// <summary>
/// 一条已落盘的偏移样本。除「时刻 + 所需偏移」外还带 <see cref="Applied"/>：
/// 它是判断样本是否「干净」的唯一依据——手动拟合（设置页按钮）只取 <c>Applied = true</c> 的样本，
/// 因为 false 的两种来源都不是干净测量：死区带内（偏移没动的存量，不代表当前基准）
/// 与大误差拒写（含未观测到切换的退化绝对口径，实测出现过 −0.91 / +0.29 这类脏值）。
/// </summary>
/// <param name="At">样本时刻（本地墙钟，取铃响起响点）。</param>
/// <param name="RequiredSec">让误差归零所需的绝对偏移（秒）。</param>
/// <param name="Applied">本次是否实际写入了偏移。</param>
public readonly record struct OffsetSample(DateTime At, double RequiredSec, bool Applied);

/// <summary>
/// 偏移样本序列的持久化（v1.0.2）：把 <see cref="DriftFitter"/> 的测量样本按天落盘，
/// 使 <b>进程重启不再清空当天的样本</b>，并留下可离线分析的记录。
///
/// 背景：估计器是内存态，重启后从零累积——那意味着重启后的前几个边界会重新「单次测量直接写入」，
/// 期间偏移会有 1~2 次小幅跳动（2026-09-15 10:05 重启实测：样本序列从「4 个」退回「1 个」）。
///
/// 存储：`&lt;插件配置目录&gt;\Logs\offset-samples.jsonl`——每个监听窗口最多追加一行：
/// <code>
/// {"Ts":"2026-09-15 08:10:21","RequiredSec":-1.685,"CurrentSec":-1.344,"Kind":"上课","B":"08:10:00.001","Applied":true}
/// </code>
/// 只有拿到**可信测量**的窗口才落盘；静默窗口、无可信测量的窗口不写。
/// 与 `calibration-history.jsonl` 的分工：后者逐窗口记录判定全过程，本文件只留「参与偏移估计的样本」，
/// 便于直接画偏移随时间的走势。
///
/// 加载规则：**只取当天**的样本（跨天的基准可能已被人为校准改变），按时间升序返回。
/// </summary>
public static class OffsetSampleStore
{
    private static readonly object Sync = new();
    private static string? _path;

    /// <summary>当前样本文件路径（未初始化时为 null）。</summary>
    public static string? Path => _path;

    /// <summary>本次会话内已落盘的样本数。</summary>
    public static int WrittenThisSession { get; private set; }

    /// <summary>初始化存储（与日志同目录，随插件启动调用一次）。</summary>
    /// <param name="pluginConfigFolder">插件配置目录。</param>
    public static void Initialize(string pluginConfigFolder)
    {
        try
        {
            var dir = System.IO.Path.Combine(pluginConfigFolder, "Logs");
            Directory.CreateDirectory(dir);
            _path = System.IO.Path.Combine(dir, "offset-samples.jsonl");
            WrittenThisSession = 0;
        }
        catch (Exception ex)
        {
            _path = null;
            Logger.Warn($"[校时] 偏移样本存储初始化失败（本次运行不落盘）：{ex.Message}");
        }
    }

    /// <summary>
    /// 追加一个样本（同步写盘，保证进程异常退出也不丢当天序列）。
    /// </summary>
    /// <param name="atLocal">样本时刻（本地墙钟，取铃响起响点）。</param>
    /// <param name="requiredSeconds">让误差归零所需的绝对偏移（秒）。</param>
    /// <param name="currentSeconds">本次测量时的当前偏移（秒），供分析「需求随时间如何变化」。</param>
    /// <param name="kind">边界类型（上课/下课）。</param>
    /// <param name="bDisplay">该边界的课表显示时刻。</param>
    /// <param name="applied">本次是否实际写入了偏移。</param>
    public static void Append(DateTime atLocal, double requiredSeconds, double currentSeconds,
        string kind, string bDisplay, bool applied)
    {
        var path = _path;
        if (path == null)
            return;

        try
        {
            // 手写 JSON：字段固定、无需引入序列化依赖，也不受宿主 JSON 配置的影响
            var line = string.Format(CultureInfo.InvariantCulture,
                "{{\"Ts\":\"{0:yyyy-MM-dd HH:mm:ss.fff}\",\"RequiredSec\":{1:F4},\"CurrentSec\":{2:F4}," +
                "\"Kind\":\"{3}\",\"B\":\"{4}\",\"Applied\":{5}}}",
                atLocal, requiredSeconds, currentSeconds, kind, bDisplay, applied ? "true" : "false");

            lock (Sync)
            {
                File.AppendAllText(path, line + Environment.NewLine);
            }

            WrittenThisSession++;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[校时] 偏移样本落盘失败（不影响校准）：{ex.Message}");
        }
    }

    /// <summary>
    /// 载入**当天**的样本（按时间升序）。文件不存在、为空或解析失败时返回空列表。
    /// </summary>
    /// <param name="today">用于筛选日期（本地）。</param>
    /// <param name="note">说明（中文，供日志）。</param>
    public static List<OffsetSample> LoadToday(DateTime today, out string note)
    {
        var result = new List<OffsetSample>();
        var path = _path;
        if (path == null || !File.Exists(path))
        {
            note = "尚无样本文件";
            return result;
        }

        try
        {
            var skippedOtherDays = 0;
            var unparsed = 0;
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0)
                    continue;

                var at = ExtractQuoted(line, "Ts");
                var required = ExtractNumber(line, "RequiredSec");
                if (at == null || required == null)
                {
                    unparsed++;
                    continue;
                }

                if (at.Value.Date != today.Date)
                {
                    skippedOtherDays++;
                    continue;
                }

                // Applied 字段缺失（早期版本写下的行）按 true 处理：那时只有可信测量才落盘
                result.Add(new OffsetSample(at.Value, required.Value, ExtractBool(line, "Applied") ?? true));
            }

            result = result.OrderBy(x => x.At).ToList();
            note = $"载入当天样本 {result.Count} 个" +
                   $"（跳过其它日期 {skippedOtherDays} 个{(unparsed > 0 ? $"，无法解析 {unparsed} 行" : "")}）";
            return result;
        }
        catch (Exception ex)
        {
            note = $"读取样本文件失败：{ex.Message}";
            return new List<OffsetSample>();
        }
    }

    /// <summary>取出 <c>"name":true</c> 中的布尔值（字段缺失返回 null）。</summary>
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

    /// <summary>取出 <c>"name":"..."</c> 中的字符串并解析为本地时间。</summary>
    private static DateTime? ExtractQuoted(string line, string name)
    {
        var key = $"\"{name}\":\"";
        var i = line.IndexOf(key, StringComparison.Ordinal);
        if (i < 0)
            return null;
        i += key.Length;
        var j = line.IndexOf('"', i);
        if (j < 0)
            return null;

        var text = line.Substring(i, j - i);
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : null;
    }

    /// <summary>取出 <c>"name":123.45</c> 中的数值。</summary>
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
}

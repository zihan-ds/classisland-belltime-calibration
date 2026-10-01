using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace BellTimeCalibration.Services;

/// <summary>
/// 一条已落盘的偏移样本。除「时刻 + 所需偏移」外还带：
/// <list type="bullet">
/// <item><description><see cref="Applied"/>：本次是否真的写入了偏移。
/// 它与 <see cref="InDeadZone"/> 一起决定界面上的**默认勾选状态**，不直接决定能否参与拟合。</description></item>
/// <item><description><see cref="InDeadZone"/>：本次测量落在死区带内（|所需偏移 − 当时偏移| ≤ 死区），
/// 判据因此没动偏移。**这类测量同样是干净测量**，只是当时"已经够准所以不写"，
/// 所以默认也参与手动拟合（v1.0.4 增补，用户要求）。</description></item>
/// <item><description><see cref="UserValid"/>：**是否参与手动拟合**（v1.0.4 增补，人工可改）。
/// 默认 = <see cref="Applied"/> 或 <see cref="InDeadZone"/>；设置页「样本管理」里可逐条改，
/// 人工结论优先。落盘在旁挂文件 <c>sample-exclusions.jsonl</c>（只记「与默认不同」的那些），
/// 不重写测量流水。</description></item>
/// </list>
/// </summary>
/// <param name="At">样本时刻（本地墙钟，取铃响起响点）。</param>
/// <param name="RequiredSec">让误差归零所需的绝对偏移（秒）。</param>
/// <param name="Applied">本次是否实际写入了偏移。</param>
/// <param name="Kind">边界类型（上课 / 下课），供界面显示。</param>
/// <param name="BoundaryDisplay">课表边界显示时刻（HH:mm:ss.fff），供界面显示。</param>
/// <param name="Ts">落盘行里的原始时刻文本（yyyy-MM-dd HH:mm:ss.fff），人工标记以它为键精确匹配。</param>
/// <param name="UserValid">是否参与手动拟合。默认 = 写入过 或 落在死区带内；人工改过则以人工为准。</param>
/// <param name="CurrentSec">本次测量时的当前偏移（秒），用于判定是否落在死区带内。</param>
/// <param name="InDeadZone">本次是否落在死区带内（|RequiredSec − CurrentSec| ≤ 死区）。</param>
public readonly record struct OffsetSample(
    DateTime At,
    double RequiredSec,
    bool Applied,
    string Kind = "",
    string BoundaryDisplay = "",
    string Ts = "",
    bool UserValid = true,
    double CurrentSec = 0,
    bool InDeadZone = false)
{
    /// <summary>
    /// 是否参与手动拟合（「有效样本」的唯一口径）= <see cref="UserValid"/>。
    ///
    /// **人工设为有效的样本一律参与，无论它当初是否被写入过**（用户要求）：
    /// 勾选即参与，取消勾选即不参与；<see cref="Applied"/> 与 <see cref="InDeadZone"/>
    /// 只影响界面的默认勾选状态。这意味着使用者可以主动把「大误差拒写」的测量拉回来参与拟合——
    /// 前提是他自己判断那条测量可信。
    /// </summary>
    public bool IsEligible => UserValid;
}

/// <summary>
/// 偏移样本序列的持久化（v1.0.1）：把 <see cref="DriftFitter"/> 的测量样本按天落盘，
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

    /// <summary>人工复核结论的旁挂文件路径（未初始化时为 null）。</summary>
    private static string? _exclusionsPath;

    /// <summary>当前样本文件路径（未初始化时为 null）。</summary>
    public static string? Path => _path;

    /// <summary>
    /// 当天样本的紧凑摘要（v1.0.4：日志瘦身）。原先「载入当天样本 N 个（参与…；跳过其它日期 81 个）」
    /// 这类说明每窗口/每次打开设置页都往日志里写一遍，一行 130+ 字；改成这个短格式，并由调用方去重。
    /// </summary>
    public static string Summarize(IReadOnlyList<OffsetSample> samples)
    {
        int eligible = 0, deadZone = 0, rejected = 0, overridden = 0;
        foreach (var s in samples)
        {
            if (s.IsEligible)
                eligible++;
            if (s.InDeadZone)
                deadZone++;
            else if (!s.Applied)
                rejected++;
            if (s.UserValid != (s.Applied || s.InDeadZone))
                overridden++;
        }

        return $"当天 {samples.Count}（参与 {eligible}｜死区 {deadZone}｜拒写 {rejected}" +
               (overridden > 0 ? $"｜人工 {overridden}" : "") + "）";
    }

    /// <summary>人工复核结论文件路径（未初始化时为 null）。</summary>
    public static string? ExclusionsPath => _exclusionsPath;

    /// <summary>本次会话内已落盘的样本数。</summary>
    public static int WrittenThisSession { get; private set; }

    /// <summary>
    /// 判定「是否落在死区带内」用的死区（秒）。插件启动时从配置写入（<c>Config.DeadZoneSeconds</c>）；
    /// 离线工具不设时用与配置默认一致的 0.3。
    ///
    /// 为什么不直接读 <c>BellTimeCalibrationPlugin.Config</c>：离线工具（ReplayTool）只链接
    /// <see cref="OffsetSampleStore"/> 等无宿主依赖的文件、**不链接插件入口类**，直接引用会编译不过。
    /// </summary>
    public static double DeadZoneSeconds { get; set; } = 0.3;

    /// <summary>初始化存储（与日志同目录，随插件启动调用一次）。</summary>
    /// <param name="pluginConfigFolder">插件配置目录。</param>
    public static void Initialize(string pluginConfigFolder)
    {
        try
        {
            var dir = System.IO.Path.Combine(pluginConfigFolder, "Logs");
            Directory.CreateDirectory(dir);
            _path = System.IO.Path.Combine(dir, "offset-samples.jsonl");
            _exclusionsPath = System.IO.Path.Combine(dir, "sample-exclusions.jsonl");
            WrittenThisSession = 0;
        }
        catch (Exception ex)
        {
            _path = null;
            _exclusionsPath = null;
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
    /// 每一条都会带上人工复核结论 <see cref="OffsetSample.UserValid"/>（读旁挂文件，见 <see cref="LoadExclusions"/>）。
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
            var exclusions = LoadExclusions();
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

                var ts = ExtractQuotedText(line, "Ts") ?? at.Value.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                // Applied 字段缺失（早期版本写下的行）按 true 处理：那时只有可信测量才落盘
                var applied = ExtractBool(line, "Applied") ?? true;
                var current = ExtractNumber(line, "CurrentSec") ?? 0;

                // 「死区带内」= 没写入偏移，但所需偏移与当时偏移的差落在死区里 —— 说明当时"已经够准所以不写"，
                // 与「大误差拒写」完全不同（后者差值 7~9 秒）。这类测量默认也参与手动拟合（用户要求）。
                var inDeadZone = !applied && Math.Abs(required.Value - current) <= DeadZoneSeconds;
                var defaultValue = applied || inDeadZone;

                result.Add(new OffsetSample(
                    at.Value,
                    required.Value,
                    applied,
                    ExtractQuotedText(line, "Kind") ?? "",
                    ExtractQuotedText(line, "B") ?? "",
                    ts,
                    // 默认 = 写入过 或 死区带内；人工改过（旁挂文件里有该 ts 的记录）则以人工为准。
                    // 这样「人工设为有效」的样本无论当初是否写入过，都参与手动拟合。
                    exclusions.TryGetValue(ts, out var userChoice) ? userChoice : defaultValue,
                    current,
                    inDeadZone));
            }

            result = result.OrderBy(x => x.At).ToList();
            var eligible = result.Count(x => x.IsEligible);
            var userOverridden = result.Count(x => x.UserValid != (x.Applied || x.InDeadZone));
            var deadZoneOnes = result.Count(x => x.InDeadZone);
            var rejected = result.Count(x => !x.Applied && !x.InDeadZone);
            note = $"载入当天样本 {result.Count} 个（参与手动拟合 {eligible} 个" +
                   $"{(deadZoneOnes > 0 ? $"，其中死区带内 {deadZoneOnes} 个默认参与" : "")}" +
                   $"{(rejected > 0 ? $"，大误差拒写 {rejected} 个默认不参与" : "")}" +
                   $"{(userOverridden > 0 ? $"，人工调整过 {userOverridden} 个" : "")}；" +
                   $"跳过其它日期 {skippedOtherDays} 个{(unparsed > 0 ? $"，无法解析 {unparsed} 行" : "")}）";
            return result;
        }
        catch (Exception ex)
        {
            note = $"读取样本文件失败：{ex.Message}";
            return new List<OffsetSample>();
        }
    }

    /// <summary>
    /// 读取人工复核结论（每条被人工改过的样本 → 是否参与手动拟合）。文件不存在/为空时返回空表。
    /// 存储格式（每行一条，只记人工结论，不改动测量流水）：
    /// <code>{"Ts":"2026-09-20 09:39:52.832","Invalid":true}</code>
    /// 语义：<c>Invalid=true</c> → 人工置为**不参与**；<c>Invalid=false</c> → 人工置为**参与**
    /// （后者正对应「人工设为有效的样本，即使当初没被写入过也参与拟合」）。同一条以最后一行为准。
    /// </summary>
    public static Dictionary<string, bool> LoadExclusions()
    {
        var map = new Dictionary<string, bool>(StringComparer.Ordinal);
        var path = _exclusionsPath;
        if (path == null || !File.Exists(path))
            return map;

        try
        {
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0)
                    continue;
                var ts = ExtractQuotedText(line, "Ts");
                if (string.IsNullOrEmpty(ts))
                    continue;
                // Invalid=true → 人工判定「不参与」；Invalid=false → 人工判定「参与」（历史行按最后一行为准）
                var invalid = ExtractBool(line, "Invalid") ?? true;
                map[ts] = !invalid;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[校时] 读取人工复核结论失败（按判据原判处理）：{ex.Message}");
        }

        return map;
    }

    /// <summary>
    /// 写入一条人工复核结论（设置页「样本管理」勾选框调用）。同步写盘；失败只记日志。
    /// 采用**只追加**：同一条样本后来的结论覆盖先前的（读取时以最后一行为准），
    /// 因此不会因为写盘过程中的异常而丢掉已有结论。
    /// </summary>
    /// <param name="sampleTs">样本时刻文本（与落盘行完全一致，作为键）。</param>
    /// <param name="valid">true = 参与手动拟合，false = 不参与。</param>
    /// <returns>是否写入成功。</returns>
    public static bool SetValidity(string sampleTs, bool valid)
    {
        var path = _exclusionsPath;
        if (path == null || string.IsNullOrWhiteSpace(sampleTs))
            return false;

        try
        {
            // Invalid=true 表示「不参与」；valid=true 时写 false，读取端据此判定人工要求参与
            var line = string.Format(CultureInfo.InvariantCulture,
                "{{\"Ts\":\"{0}\",\"Invalid\":{1}}}", sampleTs, valid ? "false" : "true");
            lock (Sync)
            {
                File.AppendAllText(path, line + Environment.NewLine);
            }

            Logger.Info($"[校时] 样本管理：{sampleTs} 人工设为{(valid ? "参与" : "不参与")}手动拟合。");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[校时] 写入人工复核结论失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>取出 <c>"name":"文本"</c> 中的原始字符串（不解码为时间）。</summary>
    private static string? ExtractQuotedText(string line, string name)
    {
        var key = $"\"{name}\":\"";
        var i = line.IndexOf(key, StringComparison.Ordinal);
        if (i < 0)
            return null;
        i += key.Length;
        var j = line.IndexOf('"', i);
        return j < 0 ? null : line.Substring(i, j - i);
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

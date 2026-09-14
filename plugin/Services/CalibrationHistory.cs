using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace BellTimeCalibration.Services;

/// <summary>
/// 结构化校准历史（M6）：每个监听窗口终结时向
/// <c>&lt;插件配置目录&gt;\Logs\calibration-history.jsonl</c> 落一条紧凑 JSONL 记录，
/// 包含离线调参分析所需的全部信息：配置快照（灵敏度/容差/布防提前量/窗口/死区）、
/// 窗口时序（开始/越界墙钟）、捕获结局（初始化失败/无突发/检测到）、全部突发候选时刻、
/// 门控与应用结果。
/// 隐私：记录只含墙钟时刻、RMS 统计（dBFS）、候选时刻与门控文本，绝不含任何音频数据。
/// 线程安全：Append 内部加锁；文件写入失败仅 Logger.Error 兜底，绝不外抛异常。
/// 未调用 <see cref="Initialize"/> 时 Append 静默忽略（幂等安全，不创建文件）。
/// </summary>
public static class CalibrationHistory
{
    /// <summary>本地时间格式（毫秒），统一手工格式化避免时区/文化歧义。</summary>
    public const string LocalFormat = "yyyy-MM-dd HH:mm:ss.fff";

    /// <summary>时间字段（钟面）格式。</summary>
    public const string ClockFormat = "HH:mm:ss.fff";

    private static readonly object LockObj = new();
    private static string? _filePath;

    /// <summary>目标文件路径；未初始化时为 null。</summary>
    public static string? FilePath => _filePath;

    /// <summary>
    /// 初始化历史文件（目标 &lt;folder&gt;\Logs\calibration-history.jsonl，Logs 不存在则创建）。
    /// 应在插件 Initialize 时于 Logger.Initialize 之后调用一次；可重复调用（以最后一次为准）。
    /// </summary>
    /// <param name="folder">插件配置目录（即 Logger 的 PluginConfigFolder）。</param>
    public static void Initialize(string folder)
    {
        try
        {
            var logDir = Path.Combine(folder, "Logs");
            Directory.CreateDirectory(logDir);
            _filePath = Path.Combine(logDir, "calibration-history.jsonl");
        }
        catch (Exception ex)
        {
            Logger.Error($"[校时] 校准历史文件初始化失败（后续记录将被静默忽略）：{ex}");
            _filePath = null;
        }
    }

    /// <summary>
    /// 追加一条记录（线程安全）：序列化为一行无缩进紧凑 JSON 并换行追加（UTF-8 无 BOM）。
    /// 未初始化/写入异常时仅记日志，绝不外抛、绝不进入音频回调路径。
    /// </summary>
    /// <param name="record">记录内容。</param>
    public static void Append(CalibrationHistoryRecord record)
    {
        if (record == null)
            return;

        var path = _filePath;
        if (path == null)
            return;

        try
        {
            var json = BuildJson(record);
            lock (LockObj)
            {
                File.AppendAllText(path, json + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch (Exception ex)
        {
            // 一次性兜底：历史写入失败不得影响主功能
            Logger.Error($"[校时] 校准历史写入失败：{ex}");
        }
    }

    // ── 手写 JSON 构建（刻意不用 System.Text.Json）────────────────────────
    // 原因：插件经 ClassIsland.PluginSdk 传递引用 System.Text.Json 9.0.2（> net8 共享框架的 8.0），
    // 运行期依赖宿主 app 目录恰好携带 9.0.2 才能绑定；这是 M5 SoundFlow 缺 DLL 缺陷的同族问题。
    // 校准历史属诊断日志，绝不能因宿主缺某版本 STJ 而失效，故用最简手写构建器（无任何运行期依赖）。

    /// <summary>把记录序列化为一行紧凑 JSON（字段顺序稳定，与协议键名一致）。</summary>
    private static string BuildJson(CalibrationHistoryRecord r)
    {
        var sb = new StringBuilder(360);
        sb.Append('{');

        JsonProp(sb, "V", r.V);
        JsonProp(sb, "Ts", r.Ts);
        JsonProp(sb, "Kind", r.Kind);
        JsonProp(sb, "BDisplay", r.BDisplay);

        JsonProp(sb, "LeadSec", r.LeadSec);
        JsonProp(sb, "WindowSec", r.WindowSec);
        JsonProp(sb, "ToleranceSec", r.ToleranceSec);
        JsonProp(sb, "Sensitivity", r.Sensitivity);
        JsonProp(sb, "DeadZoneSec", r.DeadZoneSec);
        JsonProp(sb, "Learn", r.Learn);
        JsonProp(sb, "AutoApply", r.AutoApply);
        JsonProp(sb, "ConfigEnabled", r.ConfigEnabled);

        JsonProp(sb, "StartWall", r.StartWall);
        JsonProp(sb, "ReachedWall", r.ReachedWall);
        JsonProp(sb, "LeadActualSec", r.LeadActualSec);

        JsonProp(sb, "Outcome", r.Outcome);
        JsonProp(sb, "EndedBy", r.EndedBy);
        JsonProp(sb, "CaptureSec", r.CaptureSec);
        JsonProp(sb, "NoiseFloorDb", r.NoiseFloorDb);
        JsonProp(sb, "PeakDb", r.PeakDb);

        JsonProp(sb, "Candidates", r.Candidates);
        JsonProp(sb, "OffsetFromReachedSec", r.OffsetFromReachedSec);
        JsonProp(sb, "TRing", r.TRing);
        JsonProp(sb, "DeltaSec", r.DeltaSec);
        JsonProp(sb, "ShiftSec", r.ShiftSec);
        JsonProp(sb, "MatchedTemplate", r.MatchedTemplate);
        JsonProp(sb, "MatchNcc", r.MatchNcc);
        JsonProp(sb, "MatchSpectral", r.MatchSpectral);

        JsonProp(sb, "Gate", r.Gate);
        JsonProp(sb, "GateNote", r.GateNote);
        JsonProp(sb, "Channel", r.Channel);

        // 去掉末尾分隔逗号后收尾
        if (sb[^1] == ',')
            sb.Length--;
        sb.Append('}');
        return sb.ToString();
    }

    private static void JsonProp(StringBuilder sb, string key, string? value)
    {
        if (value == null)
            sb.Append('"').Append(key).Append("\":null");
        else
        {
            sb.Append('"').Append(key).Append("\":\"");
            AppendEscaped(sb, value);
            sb.Append('"');
        }
        sb.Append(',');
    }

    private static void JsonProp(StringBuilder sb, string key, double value)
    {
        sb.Append('"').Append(key).Append("\":");
        sb.Append(value.ToString("R", CultureInfo.InvariantCulture));
        sb.Append(',');
    }

    private static void JsonProp(StringBuilder sb, string key, double? value)
    {
        sb.Append('"').Append(key).Append("\":");
        if (value is { } v)
            sb.Append(v.ToString("R", CultureInfo.InvariantCulture));
        else
            sb.Append("null");
        sb.Append(',');
    }

    private static void JsonProp(StringBuilder sb, string key, bool value)
    {
        sb.Append('"').Append(key).Append("\":");
        sb.Append(value ? "true" : "false");
        sb.Append(',');
    }

    private static void JsonProp(StringBuilder sb, string key, List<string>? value)
    {
        sb.Append('"').Append(key).Append("\":");
        if (value == null)
        {
            sb.Append("null");
        }
        else
        {
            sb.Append('[');
            for (var i = 0; i < value.Count; i++)
            {
                if (i > 0)
                    sb.Append(',');
                sb.Append('"');
                AppendEscaped(sb, value[i]);
                sb.Append('"');
            }
            sb.Append(']');
        }
        sb.Append(',');
    }

    /// <summary>JSON 字符串转义（引号/反斜杠/控制字符 → 转义序列）。</summary>
    private static void AppendEscaped(StringBuilder sb, string s)
    {
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
    }
}

/// <summary>
/// 单条校准历史记录（稳定协议 v1，字段扁平、键名固定）。
/// 所有时间字段为字符串（本地时间手工格式化，见 <see cref="CalibrationHistory.LocalFormat"/>），
/// 避免序列化时区/文化歧义；数值为 double/double?/bool。
/// 建议用对象初始化器填充；Outcome 必填 ∈ {"skipped-busy","init-failed","no-burst","no-ring","detected","error"}：
/// no-burst = 开麦静置过滤后无任何候选（无突发能量）；no-ring = 有候选但无一落入
/// ToleranceSeconds 容差（v0.3.0 起环境突发不得当铃，绝不触发应用管线）。
/// </summary>
public sealed class CalibrationHistoryRecord
{
    // ── 协议版本与身份 ─────────────────────────────────────────────
    /// <summary>协议版本（固定 "1"）。</summary>
    public string V { get; set; } = "1";

    /// <summary>记录写入时刻（本地 ISO "yyyy-MM-dd HH:mm:ss.fff"）。</summary>
    public string Ts { get; set; } = "";

    /// <summary>边界类型（"上课"/"下课"；无窗口时为实际状态或空）。</summary>
    public string Kind { get; set; } = "";

    /// <summary>课表边界显示时刻 B_display（"HH:mm:ss.fff"，显示时钟域；无窗口为 null）。</summary>
    public string? BDisplay { get; set; }

    // ── 配置快照（窗口开启时刻读取）──────────────────────────────
    /// <summary>布防提前秒数。</summary>
    public double LeadSec { get; set; }

    /// <summary>边界后监听秒数。</summary>
    public double WindowSec { get; set; }

    /// <summary>响铃判定容差（±秒）。</summary>
    public double ToleranceSec { get; set; }

    /// <summary>识别灵敏度（噪声底倍数）。</summary>
    public double Sensitivity { get; set; }

    /// <summary>应用死区（秒）。</summary>
    public double DeadZoneSec { get; set; }

    /// <summary>学习模式开关。</summary>
    public bool Learn { get; set; }

    /// <summary>自动应用开关。</summary>
    public bool AutoApply { get; set; }

    /// <summary>总开关（IsEnabled）。</summary>
    public bool ConfigEnabled { get; set; }

    // ── 窗口时序（墙钟域，本地 ISO）──────────────────────────────
    /// <summary>窗口开启墙钟时刻（本地 ISO）。</summary>
    public string StartWall { get; set; } = "";

    /// <summary>边界实际越过（状态切换）墙钟时刻（本地 ISO）；未越界为 null。</summary>
    public string? ReachedWall { get; set; }

    /// <summary>实际布防时长 = ReachedWall − StartWall（秒，3 位小数；未越界为 null）——评估布防提前量是否够。</summary>
    public double? LeadActualSec { get; set; }

    // ── 结局 ──────────────────────────────────────────────────────
    /// <summary>
    /// 窗口结局：skipped-busy / init-failed / no-burst（静置过滤后无候选）/
    /// no-ring（有候选但无一落入容差，v0.3.0）/ detected / error。
    /// </summary>
    public string Outcome { get; set; } = "";

    /// <summary>捕获如何结束：normal / hard-timeout / n/a（未进入捕获）。</summary>
    public string EndedBy { get; set; } = "n/a";

    /// <summary>实际捕获时长（秒）。</summary>
    public double CaptureSec { get; set; }

    /// <summary>捕获结束时噪声底（dBFS）。</summary>
    public double? NoiseFloorDb { get; set; }

    /// <summary>捕获期间峰值（dBFS）。</summary>
    public double? PeakDb { get; set; }

    // ── 检测结果 ──────────────────────────────────────────────────
    /// <summary>
    /// 全部突发候选时刻（本地 ISO 毫秒，升序全量；无候选为 null）。
    /// v0.3.0 起为 RingDetector 原始全量（含开麦静置期伪影），便于离线复核判定；
    /// 有效铃判定使用静置过滤后的最近容差候选（见 TRing）。
    /// </summary>
    public List<string>? Candidates { get; set; }

    /// <summary>首候选相对越界时刻偏移 = Candidates[0] − ReachedWall（秒，墙钟域；有候选且有越界时）。</summary>
    public double? OffsetFromReachedSec { get; set; }

    /// <summary>响铃墙钟时刻 t_ring = 最早候选（本地 ISO）。</summary>
    public string? TRing { get; set; }

    /// <summary>绝对偏移 = B_display − t_ring（秒，显示时钟域，3 位小数）。</summary>
    public double? DeltaSec { get; set; }

    /// <summary>
    /// 增量观测值 = t_ring − ReachedWall（秒，墙钟域，3 位小数；当前与 OffsetFromReachedSec 同值）。
    /// v0.4.0 起仅作观测（课表平移通道已移除，不再作为任何应用的输入）。
    /// </summary>
    public double? ShiftSec { get; set; }

    // ── 模板匹配（v0.9.0，协议 v1 追加字段，向后兼容）──────────────
    /// <summary>命中的模板标签（上课铃/下课铃）；未命中或未启用模板匹配时为 null。</summary>
    public string? MatchedTemplate { get; set; }

    /// <summary>模板匹配的归一化互相关系数（0..1）。</summary>
    public double? MatchNcc { get; set; }

    /// <summary>模板匹配的音色余弦相似度（0..1）。</summary>
    public double? MatchSpectral { get; set; }

    // ── 门控与应用 ────────────────────────────────────────────────
    /// <summary>门控结果：null=未进入应用管线 / pending / skipped / applied。</summary>
    public string? Gate { get; set; }

    /// <summary>门控说明（CorrectionPolicy.LastNote 或跳过原因）。</summary>
    public string? GateNote { get; set; }

    /// <summary>
    /// 应用通道：v0.4.0 起仅 "kernel"（课表平移已移除）；未选定为 null。
    /// 历史记录（v0.4.0 之前）可能含旧值 "profile"，读取方需兼容。
    /// </summary>
    public string? Channel { get; set; }
}

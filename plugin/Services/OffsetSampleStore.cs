using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace BellTimeCalibration.Services;

/// <summary>
/// 落盘样本的「判据位置」（v1.0.5）：这一条是在哪个环节产生的、判据当时为什么这么处置。
/// 写进 <c>offset-samples.jsonl</c> 的 <c>Reason</c> 字段；旧文件没有该字段时按几何规则回退分类
/// （见 <see cref="OffsetSampleStore.LoadToday"/>）。
///
/// 为什么必须有这个字段（2026-10-07 实测）：v1.0.4 用「没写入 + |所需 − 当时| ≤ 死区」反推原因，
/// 于是把 6 条**预测落在死区**的干净测量标成了「大误差拒写」——一条都不是大误差。原因得由
/// 判据自己落盘，不能事后猜。
/// </summary>
public enum SampleReason
{
    /// <summary>旧数据（落盘行没有 Reason 字段）：界面按几何代理显示，可能误标。</summary>
    Unknown = 0,

    /// <summary>判据可信、差值越出死区 → 写入了偏移。</summary>
    Applied,

    /// <summary>预测值与当时偏移的差落在死区内 → 不写偏移（干净测量，默认参与拟合）。</summary>
    DeadZone,

    /// <summary>差值超过大误差阈值 → 拒写、默认不参与（防一次错误测量把偏移拽飞）。</summary>
    LargeError,

    /// <summary>
    /// v1.1.0：测量与稳健拟合差得太远（相对离群）→ 拒写、默认不参与。
    /// 与 <see cref="LargeError"/> 的区别是判据：那个比的是「目标与当前偏移」，这个比的是「测量与本批拟合」。
    /// </summary>
    Outlier,

    /// <summary>
    /// v1.1.0：目标是合法的（非离群），但「目标 − 当前」超过单次写入上限 → 只写上限那么多，下次窗口继续逼近。
    /// <c>Applied = true</c>（确实写了），默认参与拟合。
    /// </summary>
    Stepped,

    /// <summary>只有课表窗口命中（v1.0.5）→ 旁证：不写偏移、默认不参与。</summary>
    SideSchedule,

    /// <summary>双窗口冲突（v1.0.5）→ 旁证：不写偏移、默认不参与。</summary>
    SideConflict,

    /// <summary>
    /// v1.1.2.0：样本集呈**双峰**（两簇中心相距 ≥ <c>DriftFitter.BimodalGapSeconds</c>）→ 本次不写偏移。
    ///
    /// 与 <see cref="Outlier"/> 的区别很重要：离群说的是「**这次测量**不可信」，双峰说的是
    /// 「**这批样本**分成两堆、中位数落在空档里，不代表任何真实状态」。本次测量本身可能是对的，
    /// 但它参与的拟合没有意义，所以照样拒写；默认不参与手动拟合（人工复核后可以勾上）。
    /// </summary>
    Bimodal
}

/// <summary>
/// 一条已落盘的偏移样本。除「时刻 + 所需偏移」外还带：
/// <list type="bullet">
/// <item><description><see cref="Applied"/>：本次是否真的写入了偏移。
/// 它与 <see cref="InDeadZone"/> 一起决定界面上的**默认勾选状态**，不直接决定能否参与拟合。</description></item>
/// <item><description><see cref="InDeadZone"/>：本次测量落在死区带内（|所需偏移 − 当时偏移| ≤ 死区），
/// 判据因此没动偏移。**这类测量同样是干净测量**，只是当时"已经够准所以不写"，
/// 所以默认也参与手动拟合（v1.0.4 增补，用户要求）。</description></item>
/// <item><description><see cref="Reason"/>（v1.0.5）：判据自己落盘的「判据位置」，
/// 取代 v1.0.4 靠 <see cref="Applied"/><see cref="InDeadZone"/> 反推的做法（会误标）。</description></item>
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
/// <param name="Reason">判据位置（v1.0.5）。旧数据为 <see cref="SampleReason.Unknown"/>。</param>
public readonly record struct OffsetSample(
    DateTime At,
    double RequiredSec,
    bool Applied,
    string Kind = "",
    string BoundaryDisplay = "",
    string Ts = "",
    bool UserValid = true,
    double CurrentSec = 0,
    bool InDeadZone = false,
    SampleReason Reason = SampleReason.Unknown)
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

    /// <summary>
    /// 旁证样本（v1.0.5）：判据没拿到「开关窗口」的测量，只留下课表窗口的坐标
    /// （或一次冲突记录）。这类样本默认不参与拟合，只在设置页里供人工复核；
    /// 判断可信就勾上，它会像普通样本一样参与手动拟合。
    /// </summary>
    public bool IsSideEvidence => Reason is SampleReason.SideSchedule or SampleReason.SideConflict;

    /// <summary>
    /// 判据已判为**不可信、拒写**的样本（v1.1.0.0 增补）：离群（<see cref="SampleReason.Outlier"/>）、
    /// 旧版的「大误差拒写」（<see cref="SampleReason.LargeError"/>）、以及 v1.1.2.0 的
    /// 双峰（<see cref="SampleReason.Bimodal"/>，此时不可信的是**整批样本**而非本次测量）。
    ///
    /// 与 <see cref="IsSideEvidence"/> 的区别：旁证是「判据没拿到开关窗口的测量」，这类是「拿到了，但测量本身不可信」。
    /// 两类都**不该回到运行时估计器**，但判据不同，界面上也要分开显示（<c>拒写（旧数据，原因未记）</c> 属于第三类：
    /// 连原因都没记的旧行，无法判定，照旧当普通测量处理）。
    /// </summary>
    public bool IsRejected => Reason is SampleReason.Outlier or SampleReason.LargeError or SampleReason.Bimodal;
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
        int eligible = 0, deadZone = 0, rejected = 0, side = 0, overridden = 0;
        foreach (var s in samples)
        {
            if (s.IsEligible)
                eligible++;
            if (s.IsSideEvidence)
                side++;
            else if (s.InDeadZone)
                deadZone++;
            else if (!s.Applied)
                rejected++;
            if (s.UserValid != DefaultValid(s))
                overridden++;
        }

        return $"当天 {samples.Count}（参与 {eligible}｜死区 {deadZone}｜拒写 {rejected}" +
               (side > 0 ? $"｜旁证 {side}" : "") +
               (overridden > 0 ? $"｜人工 {overridden}" : "") + "）";
    }

    /// <summary>
    /// 该样本是否可以喂给**运行时估计器**（v1.0.5 起排除旁证；v1.1.0.0 起一并排除判据已判脏值拒写的样本）。
    ///
    /// 起因（2026-10-16 实机）：首窗恢复把前一晚两条 `outlier`（−6.395 / −6.427）当成普通测量喂了进去，
    /// 恢复中位数被拖到 −3.359 s（只留干净样本是 −0.11 s）—— 判据白天已经明确拒写的脏值，
    /// 隔夜又从恢复窗口流了回来。旁证当初就是因为同一个理由被排除的，这次把「已拒写」补齐。
    ///
    /// 不影响手动拟合：那边用 <see cref="DefaultValid"/> 与人工勾选（<see cref="OffsetSample.IsEligible"/>）判定，
    /// 使用者判断某条拒写样本可信时仍可勾上让它参与。
    /// </summary>
    public static bool IsSeedableForRuntime(OffsetSample sample) =>
        !sample.IsSideEvidence && !sample.IsRejected;

    /// <summary>
    /// 判据给出的默认勾选状态（界面默认值，也是「人工是否改过」的比较基准）。
    /// 旁证样本（<see cref="OffsetSample.IsSideEvidence"/>）两项都是 false → 默认不参与 ✓。
    /// </summary>
    public static bool DefaultValid(OffsetSample sample) => sample.Applied || sample.InDeadZone;

    /// <summary>
    /// 设置页「判据位置」列与离线工具共用的中文标签（v1.0.5）。
    /// 旧数据没有 <c>Reason</c> 字段，只能按几何代理显示——v1.0.4 就是这么误标的
    /// （把「预测落在死区」标成「大误差拒写」），所以旧数据一律注明「原因未记」。
    /// </summary>
    public static string JudgeLabel(OffsetSample sample) => sample.Reason switch
    {
        SampleReason.Applied => "写入过",
        SampleReason.DeadZone => "死区带内（未写）",
        SampleReason.LargeError => "大误差拒写",
        SampleReason.Outlier => "离群拒写",
        SampleReason.Stepped => "步进写入",
        SampleReason.SideSchedule => "仅课表命中（旁证）",
        SampleReason.SideConflict => "双窗口冲突（旁证）",
        SampleReason.Bimodal => "双峰拒写",
        _ => sample.Applied ? "写入过"
            : sample.InDeadZone ? "死区带内（未写）"
            : "拒写（旧数据，原因未记）"
    };

    /// <summary>
    /// 偏移值 → 界面文字（v1.1.1.0：两位小数）。写入粒度也是两位小数，
    /// 所以「当时偏移」「采用偏移」两列与离线工具统一用这个格式。
    /// </summary>
    public static string OffsetLabel(double seconds) => $"{seconds:+0.00;-0.00;0.00} s";

    /// <summary>
    /// 设置页样本管理「当时偏移」列与离线工具共用的文字（v1.1.0 修订，v1.1.1 改两位小数）。
    ///
    /// 这一列显示的是**测量那一刻生效的偏移值**（<see cref="OffsetSample.CurrentSec"/>）：
    /// 使用者能看到的偏移就是这个量 —— 他手填进「应用设置 → 时钟 → 时间偏移」的是它，
    /// 判据做死区与限幅比较的基准也是它。
    /// 它与「所需偏移」（<see cref="OffsetSample.RequiredSec"/> = 让误差归零所需的绝对偏移，
    /// 也就是喂给估计器的那个测量值）不是一个量；两者之差才是本次的实际改动量。
    /// 界面与离线工具共用本方法，保证两处口径与格式一致。
    /// </summary>
    public static string InputOffsetLabel(OffsetSample sample) => OffsetLabel(sample.CurrentSec);

    /// <summary>判据位置 → 落盘代码（英文短语，稳定可读，写在 JSON 行的 <c>Reason</c> 字段）。</summary>
    private static string ReasonCode(SampleReason reason) => reason switch
    {
        SampleReason.Applied => "applied",
        SampleReason.DeadZone => "dead-zone",
        SampleReason.LargeError => "large-error",
        SampleReason.Outlier => "outlier",
        SampleReason.Stepped => "stepped",
        SampleReason.SideSchedule => "side-schedule",
        SampleReason.SideConflict => "side-conflict",
        SampleReason.Bimodal => "bimodal",
        _ => ""
    };

    /// <summary>落盘代码 → 判据位置（缺失或未知代码一律按旧数据 <see cref="SampleReason.Unknown"/>）。</summary>
    private static SampleReason ReasonFromCode(string? code) => code switch
    {
        "applied" => SampleReason.Applied,
        "dead-zone" => SampleReason.DeadZone,
        "large-error" => SampleReason.LargeError,
        "outlier" => SampleReason.Outlier,
        "stepped" => SampleReason.Stepped,
        "side-schedule" => SampleReason.SideSchedule,
        "side-conflict" => SampleReason.SideConflict,
        "bimodal" => SampleReason.Bimodal,
        _ => SampleReason.Unknown
    };

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
    /// <param name="reason">判据位置（v1.0.5）：这一条为什么这么处置。未知时不落盘代码（等价于旧数据）。</param>
    public static void Append(DateTime atLocal, double requiredSeconds, double currentSeconds,
        string kind, string bDisplay, bool applied, SampleReason reason = SampleReason.Unknown)
    {
        var path = _path;
        if (path == null)
            return;

        try
        {
            // 手写 JSON：字段固定、无需引入序列化依赖，也不受宿主 JSON 配置的影响
            var line = string.Format(CultureInfo.InvariantCulture,
                "{{\"Ts\":\"{0:yyyy-MM-dd HH:mm:ss.fff}\",\"RequiredSec\":{1:F4},\"CurrentSec\":{2:F4}," +
                "\"Kind\":\"{3}\",\"B\":\"{4}\",\"Applied\":{5},\"Reason\":\"{6}\"}}",
                atLocal, requiredSeconds, currentSeconds, kind, bDisplay, applied ? "true" : "false",
                ReasonCode(reason));

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
    /// <summary>读取指定日期当天的样本（设置页「样本管理」用）。</summary>
    public static List<OffsetSample> LoadToday(DateTime today, out string note) => LoadSince(today.Date, out note);

    /// <summary>
    /// 读取 <paramref name="sinceLocal"/> 之后的样本（按时间升序）。
    /// 用于**跨天恢复**拟合序列：只恢复当天会让每天第一个窗口把「样本 1」当成中位数
    /// （10-15 的 08:10 就是这样把一条脏测量 −6.456s 直接写进偏移的）。
    /// </summary>
    public static List<OffsetSample> LoadSince(DateTime sinceLocal, out string note)
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

                if (at.Value < sinceLocal)
                {
                    skippedOtherDays++;
                    continue;
                }

                var ts = ExtractQuotedText(line, "Ts") ?? at.Value.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                // Applied 字段缺失（早期版本写下的行）按 true 处理：那时只有可信测量才落盘
                var applied = ExtractBool(line, "Applied") ?? true;
                var current = ExtractNumber(line, "CurrentSec") ?? 0;
                // v1.0.5：判据位置由判据自己落盘。缺失或未知代码 → 按 v1.0.4 的几何代理反推
                // （老数据只能这么办，会误标，界面标签会注明「原因未记」）。
                var reason = ReasonFromCode(ExtractQuotedText(line, "Reason"));

                // 「死区带内」= 没写入偏移，但所需偏移与当时偏移的差落在死区里 —— 说明当时"已经够准所以不写"，
                // 与「大误差拒写」完全不同（后者差值 7~9 秒）。这类测量默认也参与手动拟合（用户要求）。
                var inDeadZone = reason != SampleReason.Unknown
                    ? reason == SampleReason.DeadZone
                    : !applied && Math.Abs(required.Value - current) <= DeadZoneSeconds;

                result.Add(new OffsetSample(
                    at.Value,
                    required.Value,
                    applied,
                    ExtractQuotedText(line, "Kind") ?? "",
                    ExtractQuotedText(line, "B") ?? "",
                    ts,
                    // 默认 = 写入过 或 死区带内；人工改过（旁挂文件里有该 ts 的记录）则以人工为准。
                    // 这样「人工设为有效」的样本无论当初是否写入过，都参与手动拟合。
                    exclusions.TryGetValue(ts, out var userChoice) ? userChoice : (applied || inDeadZone),
                    current,
                    inDeadZone,
                    reason));
            }

            result = result.OrderBy(x => x.At).ToList();
            var eligible = result.Count(x => x.IsEligible);
            var userOverridden = result.Count(x => x.UserValid != DefaultValid(x));
            var deadZoneOnes = result.Count(x => x.InDeadZone);
            var rejected = result.Count(x => !x.Applied && !x.InDeadZone && !x.IsSideEvidence);
            var sideOnes = result.Count(x => x.IsSideEvidence);
            note = $"载入样本 {result.Count} 个（参与手动拟合 {eligible} 个" +
                   $"{(deadZoneOnes > 0 ? $"，其中死区带内 {deadZoneOnes} 个默认参与" : "")}" +
                   $"{(rejected > 0 ? $"，拒写（离群/旧版大误差）{rejected} 个默认不参与" : "")}" +
                   $"{(sideOnes > 0 ? $"，旁证 {sideOnes} 个默认不参与（可手动勾上）" : "")}" +
                   $"{(userOverridden > 0 ? $"，人工调整过 {userOverridden} 个" : "")}；" +
                   $"跳过更早 {skippedOtherDays} 个{(unparsed > 0 ? $"，无法解析 {unparsed} 行" : "")}）";
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

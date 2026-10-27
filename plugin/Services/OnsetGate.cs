using System;
using System.Collections.Generic;
using System.Linq;

namespace BellTimeCalibration.Services;

/// <summary>
/// 起响沿闸门选择器（v0.10.0，路线 2 的核心判据）。
///
/// 背景（2026-09-12 实机 10 个边界 dump 的结论）：
/// <list type="bullet">
/// <item><description><b>精确波形模板不适用</b>：同一「下课铃」在各边界的波形互相关只有 0.08~0.64，
/// 实录段当模板也只命中自己的 dump（跨边界 0 命中）。每次播出的精确波形都不同。</description></item>
/// <item><description><b>每个边界附近有两次事件</b>：2026-09 那批 dump 里主铃落在课表边界 ±0.6 s 内、
/// 另有一次提前 6~15 s 的强起响（比值可达 346×）。历史上那些 −9.6 / +8.1 / −7.8 s 的巨大误差，
/// 都是把提前那次选走了。<b>2026-10-12 复盘推翻了「±0.6 s」这个前提取值</b>：当天 17 个窗口的包络复算 +
/// 用户手校的 −2.66 s 一致表明本校钟声第一击落在标称边界 **+1.0~+2.5 s**（详见 <see cref="GateSeconds"/>）。</description></item>
/// <item><description><b>「比值」比「响度」更能识别铃声</b>：铃声是突然起响，
/// 环境声（人声、桌椅、走廊）是渐强的。实测伪匹配的绝对电平甚至比真铃更响，
/// 但其「起响后峰值 ÷ 起响前基线」的比值远低于真铃（下课铃 14~346×、上课铃 4~7×；
/// 连续噪声里的伪匹配只有 1~3×）。</description></item>
/// </list>
///
/// 因此判据取：**在边界 ±<see cref="GateSeconds"/> 秒内，找起响比值最大的位置**。
/// 一步同时完成「排除提前事件」与「排除渐强噪声」，且不依赖任何波形对齐。
///
/// v1.0.5 补一步：选中之后**回溯到这段持续声音的起点**（见 <see cref="SegmentLookbackSeconds"/>）。
/// 因为本校铃声是约 15 s 的多击钟声，第一击常在 ±2 s 窗之外，直接用窗内那一击会让坐标系统偏晚 3~4 s。
/// </summary>
public static class OnsetGate
{
    /// <summary>
    /// 闸门半窗（秒）：以窗口中心为原点，向两侧各扫这么多秒找起响点。**必须宽于插件的偏移误差**。
    ///
    /// 取值史：2026-09-12 定为 2.0 s（当时实测主铃落在标称边界 ±0.6 s 内，半窗用来挡「提前 6~15 s 的
    /// 另一次强起响」）。2026-10-12 复盘后改为 8.0 s，因为那个前提在本校不成立，而且窄窗有个死结：
    /// <list type="bullet">
    /// <item><description>当天 17 个窗口的包络复算 + 用户手填的 −2.66 s 一致表明：本校钟声第一击落在
    /// **标称边界 +1.0~+2.5 s**（三个下课窗口实测 +2.3/+2.5/+2.4 s），也就是「所需偏移」本来就是
    /// −1~−2.5 s，而 ±2 s 半窗正好把这个答案的边缘切掉；</description></item>
    /// <item><description>更要命的是**自我锁死**：开窗中心锚在「当前偏移」上（= 标称边界 − 偏移），
    /// 偏移一旦偏出半窗，真铃就落到窗外看不见，闸门只能抓窗内 −40 dB 量级的房间噪声
    /// （2026-10-12 实测选中点 −46.1 / −39.6 / −23.6 dBFS，而同一段录音里的真铃是 −14.8 / −12.4 /
    /// −12.9 dBFS），算出来的「所需偏移」看起来还像一次正常的小修正 —— 偏移因此越漂越远；</description></item>
    /// <item><description>8.0 s 覆盖「大误差闸门 ±3.0 s」+ 实测钟声落点 + 余量。放进来的「提前强起响」
    /// 由 <see cref="DominanceDb"/> 强候选组与响度门槛继续拦；2026-10-12 全量离线复算：15 个有声窗口
    /// 14 个两窗一致、命中点与包络真值相差多在 0.5 s 内，仍有 10:50 那个窗口被一次 160× 的强瞬态抢走
    /// （见 plugin/tools/README.md 的已知风险）。</description></item>
    /// </list>
    /// </summary>
    public const double GateSeconds = 8.0;

    /// <summary>
    /// 锚定半窗（秒，v1.1.0）：给了锚点（模板命中位置）时，扫描区间收窄到「锚 ± 该值」。
    ///
    /// 为什么需要：闸门的「比值竞赛」在吵闹窗口里会被瞬态抢走（2026-10-14 全天多次：09:40 选中 100×、
    /// 10:10 选中 188×，都落在人声/桌椅里，而同一窗口的模板命中位置另有其人）。模板匹配回答的是
    /// 「这段波形就是我们的铃」，把它作为锚，比值竞赛就只剩「这一击的哪个采样是起声沿」这种毫秒级问题。
    ///
    /// 3.0 s 的取值依据：模板命中位置实测落在铃段**内部**而非起声处 —— 2026-10-14 08:10 命中在真起声后
    /// 1.15 s、2026-10-12 20:00 在后 1.9 s（两次都用包络逐点核对过），最长实测偏差 &lt;2 s，留 1 s 余量。
    /// 回溯到段起点仍由 <see cref="SegmentLookbackSeconds"/> 负责（模板不负责找第一击）。
    /// </summary>
    public const double AnchorApertureSeconds = 3.0;

    /// <summary>起响比值下限（倍）：起响后 0.5 s 峰值 RMS ÷ 起响前 0.3 s 基线 RMS。</summary>
    public const double RatioMin = 4.0;

    /// <summary>
    /// 强候选组容差（dB，v1.0.4）：只在与窗内最强候选相差不超过该值的候选里，再按比值挑最终起响点。
    ///
    /// 为什么要加这道门（2026-09-18 实机 111 个 dump 的复盘）：
    /// 旧判据单看「起响比值最大」，而比值 = 起响后 0.5 s 峰值 RMS ÷ 起响前 0.3 s 基线 RMS —— 只要起响前那 0.3 s
    /// 恰好落在噪声间隙里，一个**弱**瞬态就能凑出很高的比值，把真正更响的那次起响挤掉。实测两例：
    /// <list type="bullet">
    /// <item><description>20260918 11:40 窗口（用户举报）：选中点峰值 −17.5 dBFS，同窗更强事件约 −10.5 dBFS，<b>差约 7 dB</b>；</description></item>
    /// <item><description>20260918 09:40 窗口：选中点峰值 −28.2 dBFS（比值 32.2×），同窗更强事件 −21.2 dBFS（比值 10.0×），<b>差 7.0 dB</b>。</description></item>
    /// </list>
    /// 反过来，111 个窗口里新旧两种选法多数几乎一致（中位差 0.05 s），所以这里只是**收敛**判据、不做替换：
    /// 先框定「与最强事件同一量级」的候选组，再用比值挑最像铃声的那一下。
    /// </summary>
    public const double DominanceDb = 6.0;

    /// <summary>
    /// 起响处绝对电平下限（v0.10.4）：与 <see cref="RingDetector.AbsoluteMinRms"/> 同口径（约 −50 dBFS）。
    /// 只靠比值会被「静音窗口的噪声除噪声」骗过（实测噪声底 −80 dBFS、峰值 −114.5 dBFS 仍报出 6.2× 命中）。
    /// 真铃声起响峰值实测 −6.8~−13.9 dBFS，与下限留 36 dB 余量。
    /// </summary>
    public const double AbsoluteMinRms = RingDetector.AbsoluteMinRms;

    /// <summary>
    /// 「高声压起响」入选路径的峰值下限（dBFS，v1.0.5）。
    ///
    /// 为什么需要（2026-10-07 20:10 上课窗口的 dump 复算）：<see cref="RatioMin"/> 是相对门槛，
    /// 房间本身就吵时真铃也达不到 —— 该窗口环境底 −24.6 dBFS，学校铃起响只抬升 8.2 dB（2.1× ＜ 4.0×），
    /// 开关窗口整窗 miss；而 7 s 之外的课表窗口反而抓到**同一段长铃**的后一击（13.3×），
    /// 于是本窗口既没写入也没落样本。真铃起响峰值实测 −6.8~−13.9 dBFS（见
    /// <see cref="AbsoluteMinRms"/> 注释），故取 −20 dBFS 作「近满量程」的下限：足够高到排除
    /// 人声/桌椅/走廊（它们到不了 −20 dBFS），又比真铃低 6 dB 以上留余量。
    /// </summary>
    public const double LoudPeakDb = -20.0;

    /// <summary>
    /// 「高声压起响」入选路径的抬升下限（dB，v1.0.5）：起响后 0.5 s 峰值 ≥ 起响前 0.3 s 基线 + 该值。
    /// 6 dB ≈ 2 倍，是「突然起响」的最低物理要求（铃声是突然起响，环境声是渐强的）。
    /// 本路径只**增加**合格候选，不会让原本合格的候选失效。
    /// </summary>
    public const double MinRiseDb = 6.0;

    /// <summary>
    /// 强候选组内的择优口径（v1.1.2.0）：<c>true</c> = 按**峰值**取最优，<c>false</c> = 按比值取最优（旧行为）。
    ///
    /// 为什么改（2026-10-26 全天 20 个 dump 复算）：比值 = 峰值 ÷ 起响前 0.3 s 基线，它奖励的是
    /// **背景安静**而不是**像铃**。教室本来有人声时真铃的比值只有 1.0~3.7×，而安静间隙里一个小瞬态
    /// 能到 16.7× —— 15:40 上课窗口，真铃（relB −6.25 s，峰值 −16.6 dBFS，比值 3.4×）输给了一个
    /// 更轻的瞬态（relB −0.25 s，峰值 −19.2 dBFS，比值 **16.7×**），于是「所需偏移」从 6.5 变成 0.27。
    /// 当天 20 个窗口里闸门测对 8 个，测错的 12 个中 6 个是「贴边界的小瞬态」、4 个是「提前/过晚的别处事件」。
    ///
    /// 峰值口径为什么更接近真值：校铃是本窗口**最响的持续事件**，这个事实与背景噪声无关；
    /// 而「强候选组」（<see cref="DominanceDb"/>）已经把弱事件排除在外，组内再按峰值取最优，
    /// 等于回答「窗内最响的那一下」。代价是「渐强噪声比真铃更响」这类窗口会选错——
    /// 但那条路由模板锚点 + <see cref="LoudPeakDb"/> 门槛负责，不再依赖比值竞赛。
    /// </summary>
    public const bool PreferPeakOverRatio = true;

    private static readonly double LoudPeakRms = Math.Pow(10, LoudPeakDb / 20.0);
    private static readonly double LoudRiseRatio = Math.Pow(10, MinRiseDb / 20.0);

    /// <summary>
    /// 「长铃起点回溯」的最大回溯量（秒，v1.0.5）：选中起响沿之后再向前找这段持续声音的起点，最多找这么多秒。
    ///
    /// 为什么需要（2026-10-08 三个窗口 + dump 包络复算）：本校铃声是**长约 15 s 的多击钟声**，
    /// 闸门选中的那一击常常不是第一击（多击钟声里某一击的「起响比值」更高）。于是「所需偏移」系统性
    /// 比人工判定（人耳与人工校准说的「铃响了」＝钟声第一击）差 1~4 s：
    /// 2026-10-08 用户手填 4.6 s，而插件同一批窗口给的是 −5.0…−7.9 s（另有旁证样本口径的
    /// 一个偏移误差，见 CalibrationRunner.AppendSideSample）。
    /// 回溯到**这段持续声音的起点**才与「铃响了」对齐。
    /// v1.0.5 时这里曾写着「所以这一条不能靠加宽 <see cref="GateSeconds"/> 实现」—— 2026-10-12 已推翻：
    /// 半窗本身必须宽于偏移误差（否则真铃进不了搜索范围，见 <see cref="GateSeconds"/>），
    /// 而回溯解决的是另一件事（同一段钟声里选中了第几击），两者互补、都要有。
    ///
    /// 取 8.0 s 而不是「预期偏移量 3~5 s」：判定出段要连续 <see cref="SegmentHoldSeconds"/> 秒低于门限，
    /// 窗口必须比「起点到选中击的距离 + 判定所需时长」更宽，否则会遇到「还没判完就到上限」而放弃
    /// （2026-10-08 17:40 窗口实测：取 5.0 s 时刚好差 0.6 s 判不出，退化成保持选中击）。
    /// 回溯是**从选中击逐个 10 ms 往回走、遇到第一段确认的安静就停**，所以不会越过这段铃声的起点
    /// 去够更早的那次独立事件（除非两者之间的间隔短于 <see cref="SegmentHoldSeconds"/>）。
    /// </summary>
    public const double SegmentLookbackSeconds = 8.0;

    /// <summary>
    /// 「仍属同一段持续声音」的允许回落（dB，v1.0.5）：回溯时逐 10 ms 取「后 0.5 s 峰值 RMS」，
    /// 只要不低于选中击峰值以下该值，就认为还在同一段铃声里（钟声的击间起伏实测有 10 dB 量级，
    /// 取 12 dB 刚好跨过击间间隙、又不会把环境底噪算进来）。
    /// </summary>
    public const double SegmentDropDb = 12.0;

    /// <summary>
    /// 判定「出段」所需的持续回落时间（秒，v1.0.5）：低于门限**并且**连续这么多秒都低于门限，
    /// 才算走到这段声音之外；短于该值的回落按**击间间隙**处理，继续向前回溯。
    ///
    /// 为什么必须有这一条（2026-10-08 17:40 dump）：多击钟声的两次击之间会回落到门限以下
    /// （实测间隔 0.5~1 s），只看「第一个低于门限的采样点」会把回溯停在第一个间隙上 ——
    /// 那次只回溯了 1.07 s（真值约 4 s），没解决问题。取 1.2 s：足够跨过击间间隙，
    /// 又短于真人说话/走动的自然停顿所留出的安静段。
    /// </summary>
    public const double SegmentHoldSeconds = 1.2;

    /// <summary>
    /// 「攻击沿归位」的判定短窗（秒，v1.0.5）：回溯给出段起点后，再向**后**（时间递增）走到第一个
    /// 「前向短窗真的已经进声」的位置，把坐标从起声前的静音挪到起声的那一刻。
    ///
    /// 为什么必须有这一条（2026-10-12 21:30 dump 复算）：判据与回溯用的都是「后 0.5 s 峰值 RMS」
    /// （<see cref="PeakSeconds"/>），只要那一下落在前向窗里，该处就算「还在段内」—— 于是回溯会把坐标
    /// 停在**真正起声之前最多一个峰窗**的静音里（实测：坐标落在文件内 20.36 s，包络里起声在 20.75 s，
    /// 早 0.39 s，而「所需偏移 = 课表边界 − 铃响」就把这个提前量原样吃进了偏移）。
    /// 50 ms 短窗在起声处立刻越过门限，提前量降到几十毫秒；门限沿用回溯的
    /// 「选中击峰值 − <see cref="SegmentDropDb"/>」，所以不会把起声前的杂音误认成起声。
    /// </summary>
    public const double AttackWindowSeconds = 0.05;

    /// <summary>用于算比值的基线与峰值窗长（秒）。</summary>
    private const double BaselineSeconds = 0.3;
    private const double PeakSeconds = 0.5;

    /// <summary>扫描步长（秒）：10 ms，与包络跳距一致。</summary>
    private const double StepSeconds = BellTemplate.HopSeconds;

    /// <summary>一次命中结果。</summary>
    /// <param name="OnsetSampleIndex">起响点（<paramref name="capture"/> 内的采样索引）。</param>
    /// <param name="OnsetUtc">起响点墙钟（UTC）。</param>
    /// <param name="Ratio">起响比值（倍）。</param>
    /// <param name="Note">说明（中文，供日志）。</param>
    /// <param name="AnchorWallLocal">本窗口音频锚点墙钟（本地）= 首个音频块的回调时刻（v1.0.4，供离线复算对齐）。</param>
    /// <param name="GateFromSeconds">闸门区间起点在本窗口音频内的秒数（v1.0.4）。</param>
    /// <param name="GateToSeconds">闸门区间终点在本窗口音频内的秒数（v1.0.4）。</param>
    /// <param name="PeakRms">选中起响点后 0.5 s 的峰值 RMS（v1.0.4 增补；供「双窗口复核」比较两侧证据强弱）。</param>
    /// <param name="CandidateCount">扫描到的候选数（v1.0.4：供人类日志按字段拼短摘要，不必回抄整段判据文本）。</param>
    /// <param name="QualifiedCount">通过两道门槛的候选数（v1.0.4）。</param>
    /// <param name="StrongCount">强候选组大小（v1.0.4）。</param>
    public readonly record struct OnsetHit(
        int OnsetSampleIndex,
        DateTime OnsetUtc,
        double Ratio,
        string Note,
        DateTime AnchorWallLocal = default,
        double GateFromSeconds = 0,
        double GateToSeconds = 0,
        double PeakRms = 0,
        int CandidateCount = 0,
        int QualifiedCount = 0,
        int StrongCount = 0);

    /// <summary>
    /// 在「边界 ±<see cref="GateSeconds"/> 秒」内找起响比值最大的位置，再回溯到这段持续声音的起点（v1.0.5）。
    /// </summary>
    /// <param name="audio">窗口音频（仅内存）。</param>
    /// <param name="boundaryWallLocal">课表边界（**裸墙钟域**本地时刻；显示域需先减去内核偏移）。</param>
    /// <param name="note">说明（中文，供日志；未命中时给出原因与最佳候选）。</param>
    /// <returns>命中返回起响点；未找到合格起响沿返回 null。</returns>
    public static OnsetHit? Find(CaptureAudio audio, DateTime boundaryWallLocal, out string note)
        => Find(audio, boundaryWallLocal, null, out note);

    /// <summary>
    /// 同 <see cref="Find(CaptureAudio, DateTime, out string)"/>，但可给一个**锚点**（v1.1.0）：
    /// 给了锚点（模板命中位置）就把扫描区间收窄到「锚 ± <see cref="AnchorApertureSeconds"/>」，
    /// 于是比值竞赛只在铃附近进行，不会被窗内其它瞬态抢走。锚点只负责「圈定是哪一段」，
    /// 起声沿定位（回溯 + 归位）与不给锚点时完全同一套代码。
    /// </summary>
    /// <param name="anchorWallLocal">锚点墙钟（本地）；null = 不给锚点（老行为）。</param>
    public static OnsetHit? Find(CaptureAudio audio, DateTime boundaryWallLocal, DateTime? anchorWallLocal, out string note)
    {
        var rate = CaptureAudio.SampleRate;
        var baseSamples = (int)(BaselineSeconds * rate);
        var peakSamples = (int)(PeakSeconds * rate);
        var stepSamples = Math.Max(1, (int)(StepSeconds * rate));

        // 扫描区间：给锚点就围着锚点，否则围着本窗口中心（老行为）。
        var fromLocal = anchorWallLocal == null
            ? boundaryWallLocal - TimeSpan.FromSeconds(GateSeconds)
            : anchorWallLocal.Value - TimeSpan.FromSeconds(AnchorApertureSeconds);
        var toLocal = anchorWallLocal == null
            ? boundaryWallLocal + TimeSpan.FromSeconds(GateSeconds)
            : anchorWallLocal.Value + TimeSpan.FromSeconds(AnchorApertureSeconds);
        var apertureText = anchorWallLocal == null ? $"边界 ±{GateSeconds:F1}s" : $"锚 ±{AnchorApertureSeconds:F1}s";

        var gateFrom = (int)((fromLocal - audio.AnchorWallLocal).TotalSeconds * rate);
        var gateTo = (int)((toLocal - audio.AnchorWallLocal).TotalSeconds * rate);
        var settle = (int)(RingCandidateSelector.SettleSkipSeconds * rate);

        gateFrom = Math.Max(gateFrom, settle);
        gateTo = Math.Min(gateTo, audio.Length - peakSamples);

        if (gateTo <= gateFrom)
        {
            note = $"闸门区间无效（{apertureText} 落在音频之外或静置期内）";
            return null;
        }

        // 先做预算，避免在长区间里反复重算（每个候选只 O(1) 递推）
        var window = new double[audio.Length + 1];
        var samples = audio.Samples;
        window[0] = 0;
        for (var i = 0; i < audio.Length; i++)
            window[i + 1] = window[i] + (double)samples[i] * samples[i];

        double Rms(int from, int to)
        {
            var n = to - from;
            if (n <= 0)
                return 0;
            return Math.Sqrt((window[to] - window[from]) / n);
        }

        var bestIdx = -1;
        var bestRatio = 0.0;
        var bestPeak = 0.0;
        var scanBest = 0.0;
        var scanPeakMax = 0.0;

        // ── 两趟扫描（v1.0.4）──
        // 第一趟只收集候选：每个候选的基线/峰值仍由前缀和 O(1) 递推，性能与旧实现相同。
        var candidates = new List<(int Idx, double Ratio, double Peak, bool Loud)>();
        var silentBaselineCandidates = 0;   // 基线落在数字静默里的候选数（诊断用）
        for (var idx = gateFrom; idx <= gateTo; idx += stepSamples)
        {
            var baseFrom = idx - baseSamples;
            if (baseFrom < 0)
                continue;

            var baseline = Rms(baseFrom, idx);
            var peak = Rms(idx, Math.Min(audio.Length, idx + peakSamples));

            // ── 基线有效性门槛（v1.0.4）──
            // 比值 = 峰值 ÷ 基线；基线若是**数字静默**（麦克风整段未进音后恢复供音），除数≈0，
            // 比值会炸成千万级，把「麦克风恢复出声的那一瞬间」选成铃声。
            // 实机证据（2026-09-19 上午）：8 个窗口各含 10~21 s 全零静默，闸门报出
            // 「起响比值 748579× ~ 96702200×」，delta 集中在 −1.68/−1.98/−1.99 s（恢复点的机器特征），
            // 而真铃的起响比值实测只有 4~346×。这类候选的比值**没有物理意义**，直接排除。
            // 阈值取 RingDetector.NoiseFloorMin（1e-4 ≈ −80 dBFS，已存在的常量，不新增口径）。
            if (baseline < RingDetector.NoiseFloorMin)
            {
                silentBaselineCandidates++;
                candidates.Add((idx, 0, peak, false));   // 比值记 0：保留计数与峰值用于诊断，但不参与选择
                if (peak > scanPeakMax)
                    scanPeakMax = peak;
                continue;
            }

            var ratio = peak / baseline;
            if (ratio > scanBest)
                scanBest = ratio;
            if (peak > scanPeakMax)
                scanPeakMax = peak;

            // 高声压起响（v1.0.5）：基线有效的前提下，又响又明显抬升的起响沿也算合格候选。
            // 注意这里只在**基线有效**分支里算 —— 「基线数字静默」的候选绝不能走后门进来
            // （比值在那里没有物理意义，正是 v1.0.4 基线门槛要拦的东西）。
            var loud = peak >= LoudPeakRms && peak >= baseline * LoudRiseRatio;
            candidates.Add((idx, ratio, peak, loud));
        }

        // 绝对电平下限（v0.10.4）、比值下限（v0.10.0）与「高声压起响」（v1.0.5）三条门槛：
        // 任一条满足即合格；都不满足的候选一律不参与选择。
        var qualified = candidates
            .Where(c => c.Peak >= AbsoluteMinRms && (c.Ratio >= RatioMin || c.Loud))
            .ToList();

        // 第二趟：先框定「强候选组」，再在组内取最优。
        // 组内口径由 <see cref="PreferPeakOverRatio"/> 决定：v1.1.2.0 起按峰值（窗内最响的那一下），
        // 旧行为按比值（背景最安静的那一下）—— 理由见该常量的注释。
        var strongCount = 0;
        var strongPeakMax = 0.0;
        var bestLoud = false;   // 选中点是否靠「高声压起响」入选（决定最终门槛与日志措辞）
        if (qualified.Count > 0)
        {
            strongPeakMax = qualified.Max(c => c.Peak);
            var strongFloor = strongPeakMax * Math.Pow(10, -DominanceDb / 20.0);
            foreach (var c in qualified)
            {
                if (c.Peak < strongFloor)
                    continue;
                strongCount++;
                var better = PreferPeakOverRatio ? c.Peak > bestPeak : c.Ratio > bestRatio;
                if (better)
                {
                    bestRatio = c.Ratio;
                    bestIdx = c.Idx;
                    bestPeak = c.Peak;
                    bestLoud = c.Loud;
                }
            }
        }

        // ── 绝对电平下限（v0.10.4）──
        // 只比值不够：麦克风整窗静默时，**噪声除噪声**也能凑出 ≥4× 的假起响沿。
        // 实机证据（2026-09-14 09:40 边界）：日志记录「噪声底 −80.0 dBFS、峰值 −114.5 dBFS」
        // ——峰值比噪声底还低 34 dB 且等于数字静默，闸门却报了「命中 6.2×」。若自动应用开着，
        // 这一次就会写出一个完全错误的偏移。真铃声的起响峰值实测在 −6.8~−13.9 dBFS，
        // 与下限之间留 36 dB 余量，不会误杀。
        if (bestIdx < 0 && candidates.Count > 0 && candidates.Max(c => c.Peak) < AbsoluteMinRms)
        {
            note = $"边界 ±{GateSeconds:F1}s 内起响沿电平过低（峰值 {ToDb(candidates.Max(c => c.Peak)):F1} dBFS，" +
                   $"下限 {ToDb(AbsoluteMinRms):F1} dBFS）→ 判为静音/麦克风未进音，拒绝本次测量";
            return null;
        }

        if (bestIdx < 0 || (bestRatio < RatioMin && !bestLoud))
        {
            // v1.0.4：未命中说明改为**紧凑串**（原先一次 130+ 字，逐窗口写进日志很占地方）。
            // 但「基线静默候选」这个关键词必须保留：gate-guard 断言就靠它统计被门槛拦下的窗口。
            // 命中时的说明（下面那段）保持完整——它同时是结构化历史 GateNote 的内容。
            note = $"未命中（区间 {apertureText}，最佳比值 {scanBest:F1}×／下限 {RatioMin:F1}×，区间峰值 {ToDb(scanPeakMax):F1} dBFS" +
                   (silentBaselineCandidates > 0 ? $"，已排除 {silentBaselineCandidates} 个基线静默候选" : "") +
                   (qualified.Count > 0 ? $"，{qualified.Count} 个合格候选都不在强候选组内" : "") + "）";
            return null;
        }

        // ── 长铃起点回溯（v1.0.5 判据修正）──
        // 选中的那一击只是「窗内最像铃声的一下」，而这段钟声可能从别处就开始了。见 SegmentLookbackSeconds
        // 的注释：多击钟声里选中的未必是第一击，直接采用选中击会让「所需偏移」偏 1~4 s。
        // 这里把坐标挪到**这段持续声音的起点**（人耳与人工校准所说的「铃响了」）。
        //
        // 判据与主判据同口径：逐 10 ms 取「后 0.5 s 峰值 RMS」，低于「选中击峰值 − SegmentDropDb」算「出段」，
        // 但必须**连续**低于门限 SegmentHoldSeconds 才算真出段（钟声击间会有 0.5~1 s 的回落，见该常量注释）。
        // 走到回溯上限说明这段声音比回溯窗还长（持续噪声/长环境声，例如 2026-10-08 18:50 那个窗口）——
        // 那时定位不到起点，**保持选中击**（宁可不动，也不能把坐标扔进噪声里）。
        var segmentNote = "";
        var segmentFloor = bestPeak * Math.Pow(10, -SegmentDropDb / 20.0);
        var lookbackLimit = Math.Max(settle, bestIdx - (int)(SegmentLookbackSeconds * rate));
        var holdSamples = (int)(SegmentHoldSeconds * rate);
        var segmentStart = bestIdx;   // 最后一次「确认仍在段内」的位置
        var hitLookbackLimit = false;
        for (var probe = bestIdx - stepSamples; ; probe -= stepSamples)
        {
            if (probe < lookbackLimit)
            {
                hitLookbackLimit = true;
                break;
            }

            if (Rms(probe, Math.Min(audio.Length, probe + peakSamples)) >= segmentFloor)
            {
                segmentStart = probe;
                continue;
            }

            // 已经低于门限：只有「连续回落够久」才算出段，否则视为击间间隙继续向前。
            if (segmentStart - probe >= holdSamples)
                break;
        }

        if (hitLookbackLimit)
        {
            segmentNote = $"；这段声音向前超过 {SegmentLookbackSeconds:F1}s 仍未回落到 {SegmentDropDb:F1} dB 以下，" +
                          "定位不到起点，保持选中击";
        }
        else if (segmentStart < bestIdx)
        {
            segmentNote = $"；已回溯到长铃起点（{(segmentStart - bestIdx) / (double)rate:+0.00;-0.00;0.00}s）";
            bestIdx = segmentStart;
        }

        // ── 攻击沿归位（v1.0.5 判据修正）──
        // 上面的回溯与主判据同口径（后 0.5 s 峰值 RMS），所以它会把段落起点停在**起声之前的静音**里：
        // 只要那一下落在前向窗内，该处就算「还在段内」（见 AttackWindowSeconds 的注释与实测数字）。
        // 这里改用 50 ms 短窗，从段起点向**后**（时间递增）走到第一个真的进声的位置，把坐标挪到起声那一刻。
        // 门限仍是同一个 segmentFloor（选中击峰值 − SegmentDropDb），最多走到 holdSamples；
        // 一个都没找到（渐强声、或坐标本来就在声里）就保持不动、不加说明。
        var attackSamples = Math.Max(1, (int)(AttackWindowSeconds * rate));
        var attackLimit = Math.Min(audio.Length - attackSamples, bestIdx + holdSamples);
        for (var probe = bestIdx; probe <= attackLimit; probe += stepSamples)
        {
            if (Rms(probe, Math.Min(audio.Length, probe + attackSamples)) < segmentFloor)
                continue;

            if (probe > bestIdx)
            {
                segmentNote += $"；已归位到起声（{(probe - bestIdx) / (double)rate:+0.00;-0.00;0.00}s）";
                bestIdx = probe;
            }

            break;
        }

        var onsetUtc = audio.WallTimeAt(bestIdx);
        if (onsetUtc == null)
        {
            note = "起响点墙钟换算失败";
            return null;
        }

        var onsetLocal = onsetUtc.Value.ToLocalTime();
        var offsetSec = (onsetLocal - boundaryWallLocal).TotalSeconds;

        // 候选摘要（v1.0.4）：把「最强候选是谁、强候选组多大、最终选了谁」一并写进日志。
        // 必要性：dump 文件名用的是布防时刻，而本函数用的是音频锚点（首个块回调时刻），两者实测能差 1~3 s；
        // 没有这行摘要，离线复算的位置与实机判据对不上（这次排查为此反复得出相反结论）。
        var anchorLocal = audio.AnchorWallLocal;
        var anchorDelta = anchorLocal == DateTime.MinValue
            ? double.NaN
            : (anchorLocal - boundaryWallLocal).TotalSeconds;
        // 日志不撒谎（v1.0.5）：靠「高声压起响」入选时，必须写明入选路径——否则日志里会出现
        // 「比值 2.1×／下限 4.0×」却报命中的自相矛盾（离线排查时极易被当成 bug）。
        var ratioText = bestRatio >= RatioMin
            ? $"起响比值 {bestRatio:F1}×"
            : $"起响比值 {bestRatio:F1}×（＜下限 {RatioMin:F1}×，按「高声压起响」入选：峰值 {ToDb(bestPeak):F1} dBFS " +
              $"≥ {LoudPeakDb:F1}、抬升 ≥ {MinRiseDb:F1} dB）";
        note = $"起响沿命中（区间 {apertureText}）：边界 {(offsetSec >= 0 ? "+" : "")}{offsetSec:F2}s，{ratioText}" +
               $"（峰值 {ToDb(bestPeak):F1} dBFS）；" +
               $"候选 {candidates.Count} 个 / 合格 {qualified.Count} 个（含高声压起响 {candidates.Count(c => c.Loud)} 个）" +
               $" / 强候选组 {strongCount} 个" +
               $"（组内{(PreferPeakOverRatio ? "按峰值" : "按比值")}择优，峰值上限 {ToDb(strongPeakMax):F1} dBFS，容差 {DominanceDb:F1} dB）" +
               (silentBaselineCandidates > 0 ? $"；已排除 {silentBaselineCandidates} 个基线静默候选" : "") +
               segmentNote + "；" +
               $"锚点=边界{(double.IsNaN(anchorDelta) ? "n/a" : $"{anchorDelta:+0.000;-0.000;0.000}s")}";

        return new OnsetHit(
            bestIdx, onsetUtc.Value, bestRatio, note,
            anchorLocal,
            gateFrom / (double)rate,
            gateTo / (double)rate,
            PeakRms: bestPeak,
            CandidateCount: candidates.Count,
            QualifiedCount: qualified.Count,
            StrongCount: strongCount);
    }

    /// <summary>
    /// 双窗口复核的一致性容差（秒，v1.0.4）：两侧命中位置相差不超过该值即视为「同一次起响」。
    /// 两侧扫描用的是同一套判据与步长（10 ms），只是窗口中心不同，因此同一次起响通常落在同一格。
    /// </summary>
    public const double AgreeSeconds = 1.0;

    /// <summary>
    /// 「同一段长铃的先后两击」判据（dB，v1.0.5）：两窗相隔超过 <see cref="AgreeSeconds"/>、
    /// 峰值又只差不超过该值时，判为**同一段长铃**的先后两击，取更早的开关窗口命中。
    ///
    /// 为什么需要（2026-10-07 晚间 3 个窗口 + dump 包络复算）：本校铃声是**长约 15 s 的多击钟声**，
    /// 钟声持续期间会连续出现第 2、3……击，而课表窗口的中心正是标称边界（离开关约 7 s）：
    /// <list type="bullet">
    /// <item><description>21:30 下课：开关窗口 7.1×/−22.9 dBFS（钟声第 2 击）与课表窗口 4.6×/−23.0 dBFS
    /// （同一段钟声的后一击）相隔 3.74 s、峰值只差 0.1 dB —— 旧判据算作「冲突」，既不写入也不落样本；</description></item>
    /// <item><description>20:10 上课：同理，课表窗口抓到的正是同一段钟声的后一击（13.3×）。</description></item>
    /// </list>
    /// 钟声是衰减型：第一击通常最强，后继击不会更响。所以「开关窗口不弱于课表窗口（差 ≤ 本值）」
    /// 加上「相隔超过一致性容差」只可能是同一段铃声的先后两击；两个**独立**的等强事件很罕见，
    /// 且真出现时本判据会取更早的那次（错一个 Δt，而不是错一个完全无关的坐标）。
    ///
    /// 课表窗口强出超过 <see cref="DominanceDb"/>（6 dB）时判 <see cref="WindowVerdict.PreferSchedule"/>：
    /// 开关窗口的中心锚在**当前偏移**上，偏移一偏，它就整段挪到别处去、只能抓到杂音；课表窗口的中心是
    /// 标称边界（与偏移无关），这个量级的差距只可能是「开关窗口抓错、课表窗口抓对」。强出落在本值（2 dB）
    /// 与 <see cref="DominanceDb"/> 之间的中间带仍判 <see cref="WindowVerdict.Conflict"/> 交人工 ——
    /// 2026-09-20 上午那批「开关窗口锁到杂音」的窗口正是这个带（课表窗口强 2.1~4.8 dB），
    /// 而 2026-10-12 的三条（强出 8.4~16.9 dB）属于前者（偏移整段被人工改乱，开关窗口只剩 2 个合格候选）。
    ///
    /// ponytail: 现有的判别只用峰值差，没有比对「两击之间有没有回落到基线」的音频证据
    /// （钟声本身的击间起伏就有 10 dB 量级，该证据在本校录音上分辨力不足，见 2026-10-07 复算）；
    /// 回退旋钮 = 把本常量调到 0（只保留开关窗口更强或等强的情形）。
    /// </summary>
    public const double SameStrengthDb = 2.0;

    /// <summary>双窗口复核结论（v1.0.4）。</summary>
    public enum WindowVerdict
    {
        /// <summary>两个窗口都没有合格起响沿。</summary>
        None,

        /// <summary>只有「开关窗口」命中 —— 现状路径，采用该命中。</summary>
        SwitchOnly,

        /// <summary>只有「课表窗口」命中 —— 只作旁证（v1.0.5 起会落一条旁证样本，默认不参与），不作测量源。</summary>
        ScheduleOnly,

        /// <summary>两侧指向同一次起响 —— 采用开关窗口命中（与旧行为完全一致）。</summary>
        Agree,

        /// <summary>两侧不一致，但开关窗口明显更强，或只差 ≤ <see cref="SameStrengthDb"/> dB
        /// （判为同一段长铃的先后两击）—— 仍采用开关窗口命中。</summary>
        PreferSwitch,

        /// <summary>两侧不一致且**课表窗口明显更强**（强出 &gt; <see cref="DominanceDb"/> dB）——
        /// 采用课表窗口命中（v1.0.5 补：支配判据对称，见 <see cref="Reconcile"/>）。</summary>
        PreferSchedule,

        /// <summary>两侧不一致且互有强弱（课表窗口强出 <see cref="SameStrengthDb"/>~<see cref="DominanceDb"/> dB）
        /// —— 本次判为冲突，**不写入偏移**（v1.0.5 起落一条旁证样本，默认不参与）。</summary>
        Conflict
    }

    /// <summary>
    /// 双窗口复核（v1.0.4）：把「开关窗口」的命中与「课表窗口」的命中放在一起裁决。
    ///
    /// 为什么需要（2026-09-20 上午实机 4 个下课窗口 + 录音复算）：
    /// 闸门窗口的中心是 <c>B_display − 偏移</c>，也就是**内核真正切换的位置**，而它本身正是要被校准的量。
    /// v1.0.5 前闸门半窗只有 ±2 s，于是「所需偏移 = 课表边界 − 铃响」恒落在「当前偏移 ±2 s」内：偏移一旦
    /// 偏出 2 s（如校铃钟被人工校正过、或上一轮把脏样本写进了偏移），闸门就只能看见开关附近的**杂音**，
    /// 而结果看起来像一次正常的小修正（实测 08:50/09:40/10:50/11:40 四个窗口各写入 +0.3~+1.3 s，把偏移从
    /// 6.34 推到 8.13）；同一段录音里真正落在课表边界上的那一下要强 14~18 dB，但它在「开关 +6.9 s」处，
    /// 被那时的 ±2 s 窗挡在外面。**2026-10-12 起半窗加宽到 <see cref="GateSeconds"/>（8 s）**，
    /// 「真铃落在窗外」这种情形已基本消失（当天 17 个窗口离线复算：15 个有声窗口 14 个两窗一致），
    /// 但双窗口复核仍然保留：半窗再宽也有边界，而课表窗口的中心与当前偏移无关，
    /// 是唯一在偏移长期跑偏时还能看见真铃的锚点。
    /// 因此这里再扫一遍**以标称边界为中心**的同一判据窗口（该中心与当前偏移无关），作为独立旁证：
    /// 两侧一致 → 照旧；开关窗口明显更强（&gt; <see cref="DominanceDb"/>）→ 采用开关窗口（铃确实远离课表边界）；
    /// **课表窗口明显更强（强出 &gt; <see cref="DominanceDb"/>）→ 采用课表窗口**（v1.0.5 补：支配判据对称。
    /// 这个量级的差距说明开关窗口已被偏移锚到杂音上，而课表窗口的坐标与偏移无关，没有理由丢掉这次测量）；
    /// 只有互有强弱、差距落在中间带（<see cref="SameStrengthDb"/>~<see cref="DominanceDb"/> dB）才判冲突、
    /// 不写入、交人工（绝不写脏值）。
    /// </summary>
    /// <param name="switchHit">开关窗口命中（现状中心 = B_display − 偏移）。</param>
    /// <param name="scheduleHit">课表窗口命中（中心 = 标称边界，与偏移无关）。</param>
    /// <param name="verdict">复核结论。</param>
    /// <param name="note">中文说明（供日志）。</param>
    /// <returns><see cref="WindowVerdict.SwitchOnly"/>/<see cref="WindowVerdict.Agree"/>/<see cref="WindowVerdict.PreferSwitch"/>
    /// 返回应采用的开关窗口命中，<see cref="WindowVerdict.PreferSchedule"/> 返回应采用的**课表**窗口命中；
    /// 其余结论返回 null（无测量）。</returns>
    public static OnsetHit? Reconcile(
        OnsetHit? switchHit,
        OnsetHit? scheduleHit,
        out WindowVerdict verdict,
        out string note)
    {
        if (switchHit == null && scheduleHit == null)
        {
            verdict = WindowVerdict.None;
            note = "两窗口均无合格起响沿";
            return null;
        }

        if (switchHit == null)
        {
            verdict = WindowVerdict.ScheduleOnly;
            note = $"仅课表窗口命中（峰值 {ToDb(scheduleHit!.Value.PeakRms):F1} dBFS）——只作旁证，不作测量源" +
                   "（v1.0.5：会落一条旁证样本，默认不参与拟合）";
            return null;
        }

        if (scheduleHit == null)
        {
            verdict = WindowVerdict.SwitchOnly;
            note = "仅开关窗口命中（课表窗口无合格起响沿）→ 采用开关窗口";
            return switchHit;
        }

        var switchOnset = switchHit.Value.OnsetUtc;
        var scheduleOnset = scheduleHit.Value.OnsetUtc;
        var deltaSeconds = (switchOnset - scheduleOnset).TotalSeconds;
        var switchDb = ToDb(switchHit.Value.PeakRms);
        var scheduleDb = ToDb(scheduleHit.Value.PeakRms);
        var gapDb = switchDb - scheduleDb;

        if (Math.Abs(deltaSeconds) <= AgreeSeconds)
        {
            verdict = WindowVerdict.Agree;
            note = $"两窗口一致（Δt={deltaSeconds:+0.00;-0.00;0.00}s）→ 采用开关窗口命中";
            return switchHit;
        }

        if (gapDb > DominanceDb)
        {
            verdict = WindowVerdict.PreferSwitch;
            note = $"两窗口相差 {Math.Abs(deltaSeconds):F2}s，开关窗口强 {gapDb:F1} dB（＞{DominanceDb:F1} dB）" +
                   "→ 采用开关窗口命中（判定铃确实远离课表边界）";
            return switchHit;
        }

        if (gapDb >= -SameStrengthDb)
        {
            verdict = WindowVerdict.PreferSwitch;
            note = $"两窗口相差 {Math.Abs(deltaSeconds):F2}s、峰值仅差 {Math.Abs(gapDb):F1} dB" +
                   $"（开关 {switchDb:F1} / 课表 {scheduleDb:F1} dBFS）→ 判为同一段长铃的先后两击，" +
                   "取更早的开关窗口命中";
            return switchHit;
        }

        if (gapDb < -DominanceDb)
        {
            verdict = WindowVerdict.PreferSchedule;
            note = $"两窗口相差 {Math.Abs(deltaSeconds):F2}s，课表窗口强 {(-gapDb):F1} dB（＞{DominanceDb:F1} dB）" +
                   $"（开关 {switchDb:F1} / 课表 {scheduleDb:F1} dBFS）" +
                   "→ 采用课表窗口命中（开关窗口锚在当前偏移上、已抓错，课表窗口中心与偏移无关）";
            return scheduleHit;
        }

        verdict = WindowVerdict.Conflict;
        note = $"两窗口相差 {Math.Abs(deltaSeconds):F2}s，课表窗口强出 {-gapDb:F1} dB（＞{SameStrengthDb:F1} dB）" +
               $"（开关 {switchDb:F1} / 课表 {scheduleDb:F1} dBFS）" +
               "→ 判定冲突：本次不写入偏移、只落一条旁证样本（默认不参与），请人工确认铃况";
        return null;
    }

    private static double ToDb(double linear) => 20 * Math.Log10(Math.Max(linear, 1e-12));
}

using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BellTimeCalibration.Models;

/// <summary>
/// 铃声自动校时插件的配置（JSON 持久化）。
/// 属性 setter 均触发 <see cref="PropertyChanged"/>，由 Plugin 订阅该事件自动 SaveConfig。
/// </summary>
public class BellCalibrationSettings : INotifyPropertyChanged
{
    // ── 总开关与工作模式 ─────────────────────────────────────────────────
    private bool _isEnabled = true;
    /// <summary>总开关：关闭后不监听铃声、不应用校准结果。</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set { _isEnabled = value; OnPropertyChanged(); }
    }

    private bool _isAutoApplyEnabled;
    /// <summary>是否把校准得到的偏移自动应用到 ClassIsland（需关闭学习模式）。</summary>
    public bool IsAutoApplyEnabled
    {
        get => _isAutoApplyEnabled;
        set { _isAutoApplyEnabled = value; OnPropertyChanged(); }
    }

    private bool _isLearningMode = true;
    /// <summary>学习模式：只记录识别结果，不应用偏移。</summary>
    public bool IsLearningMode
    {
        get => _isLearningMode;
        set { _isLearningMode = value; OnPropertyChanged(); }
    }

    // ── 监听与判定参数 ──────────────────────────────────────────────────
    private double _armedLeadSeconds = 15;
    /// <summary>边界前多少秒开始布防监听。</summary>
    public double ArmedLeadSeconds
    {
        get => _armedLeadSeconds;
        set { _armedLeadSeconds = Math.Max(0, value); OnPropertyChanged(); }
    }

    private double _windowSeconds = 8;
    /// <summary>边界后继续监听多少秒。</summary>
    public double WindowSeconds
    {
        get => _windowSeconds;
        set { _windowSeconds = Math.Max(0, value); OnPropertyChanged(); }
    }

    private double _deadZoneSeconds = 0.3;
    /// <summary>自动应用死区（秒）：|残差| 小于此值不动作（v0.6.0 起判据为残差 = 当前内核偏移 − 本次 delta）。</summary>
    public double DeadZoneSeconds
    {
        get => _deadZoneSeconds;
        set { _deadZoneSeconds = Math.Max(0, value); OnPropertyChanged(); }
    }

    private double _toleranceSeconds = 12;
    /// <summary>铃响搜索半窗（±秒，v0.8.0 起语义）：以课表标称边界为中心搜索铃声起响点的范围，须覆盖校铃钟漂移幅度（可达 ~10s）。</summary>
    public double ToleranceSeconds
    {
        get => _toleranceSeconds;
        set { _toleranceSeconds = Math.Max(0, value); OnPropertyChanged(); }
    }

    private double _detectionSensitivity = 2.0;
    /// <summary>识别灵敏度（语义由后续里程碑定稿，当前为占位值）。</summary>
    public double DetectionSensitivity
    {
        get => _detectionSensitivity;
        set { _detectionSensitivity = Math.Max(0, value); OnPropertyChanged(); }
    }

    private string _ignoredBoundaries = "08:00,18:30";
    /// <summary>
    /// 忽略的边界时刻（HH:mm，逗号/顿号/分号分隔）：这些边界不监听、不校准。
    /// 默认屏蔽 08:00 与 18:30——这两次铃声与已采集的铃声样本音色不一致，检测不到有效样本。
    /// </summary>
    public string IgnoredBoundaries
    {
        get => _ignoredBoundaries;
        set { _ignoredBoundaries = value ?? ""; OnPropertyChanged(); }
    }

    /// <summary>
    /// 判断某边界显示时刻是否落在忽略列表内（按 "HH:mm" 匹配，容忍分隔符与空白）。
    /// </summary>
    /// <param name="boundaryDisplayLocal">边界显示时刻（B_display，本地墙钟域）。</param>
    /// <returns>true = 该边界应被跳过。</returns>
    public bool IsBoundaryIgnored(DateTime boundaryDisplayLocal)
    {
        if (string.IsNullOrWhiteSpace(_ignoredBoundaries))
            return false;

        var target = boundaryDisplayLocal.ToString("HH:mm");
        foreach (var raw in _ignoredBoundaries.Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.Trim() == target)
                return true;
        }

        return false;
    }

    // ── 模板匹配（v0.9.0）────────────────────────────────────────────────

    private bool _enableTemplateMatch = true;
    /// <summary>启用铃声模板匹配（基于提供的铃声样本/模板 WAV 做波形与音色匹配）。关闭时回退启发式选铃。</summary>
    public bool EnableTemplateMatch
    {
        get => _enableTemplateMatch;
        set { _enableTemplateMatch = value; OnPropertyChanged(); }
    }

    private string _templateDirectory = "Templates";
    /// <summary>模板目录（相对插件配置目录）：放置「上课铃.wav / 下课铃.wav」等现成模板文件。</summary>
    public string TemplateDirectory
    {
        get => _templateDirectory;
        set { _templateDirectory = value ?? ""; OnPropertyChanged(); }
    }

    private string _classBellSamplePath = "上课铃样本.wav";
    /// <summary>上课铃长录音样本路径（相对插件配置目录或绝对路径）；留空则改用模板目录内的模板文件。</summary>
    public string ClassBellSamplePath
    {
        get => _classBellSamplePath;
        set { _classBellSamplePath = value ?? ""; OnPropertyChanged(); }
    }

    private string _classBellTrimSeconds = "";
    /// <summary>上课铃样本的截取区间（"起始秒-结束秒"）；留空 = 自动取「最响且持续 ≥1.5s」的段。</summary>
    public string ClassBellTrimSeconds
    {
        get => _classBellTrimSeconds;
        set { _classBellTrimSeconds = value ?? ""; OnPropertyChanged(); }
    }

    private string _breakBellSamplePath = "下课铃样本.wav";
    /// <summary>下课铃长录音样本路径（相对插件配置目录或绝对路径）；留空则改用模板目录内的模板文件。</summary>
    public string BreakBellSamplePath
    {
        get => _breakBellSamplePath;
        set { _breakBellSamplePath = value ?? ""; OnPropertyChanged(); }
    }

    private string _breakBellTrimSeconds = "";
    /// <summary>下课铃样本的截取区间（"起始秒-结束秒"）；留空 = 自动。</summary>
    public string BreakBellTrimSeconds
    {
        get => _breakBellTrimSeconds;
        set { _breakBellTrimSeconds = value ?? ""; OnPropertyChanged(); }
    }

    private double _matchNccMin = 0.60;
    /// <summary>模板匹配的归一化互相关系数阈值（0..1，越高越严格）。</summary>
    public double MatchNccMin
    {
        get => _matchNccMin;
        set { _matchNccMin = Math.Clamp(value, 0.1, 0.99); OnPropertyChanged(); }
    }

    private double _matchSpectralMin = 0.45;
    /// <summary>模板匹配的音色余弦相似度阈值（0..1，越高越严格）。</summary>
    public double MatchSpectralMin
    {
        get => _matchSpectralMin;
        set { _matchSpectralMin = Math.Clamp(value, 0.1, 0.99); OnPropertyChanged(); }
    }

    private bool _debugDumpAudio;
    /// <summary>
    /// 调试音频转存（默认关闭）：开启后每个监听窗口的音频会写成 WAV（&lt;插件配置目录&gt;\Dumps\）供离线分析。
    /// 隐私：默认绝不落盘；仅当用户显式开启时才写出，用于核对实机铃声波形与重建模板。
    /// </summary>
    public bool DebugDumpAudio
    {
        get => _debugDumpAudio;
        set { _debugDumpAudio = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

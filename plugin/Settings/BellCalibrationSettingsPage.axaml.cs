using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using BellTimeCalibration.Services;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;

namespace BellTimeCalibration.Settings;

[FullWidthPage]
[SettingsPageInfo("id.belltime.calibration.settings", "铃声自动校时", "\uE8B7", "\uE8B7")]
public partial class BellCalibrationSettingsPage : SettingsPageBase
{
    /// <summary>音频文件选择器的过滤条件（WAV 为主；插件的 WavReader 只解析 WAV）。</summary>
    private static readonly FilePickerFileType[] AudioFileTypes =
    {
        new("WAV 音频") { Patterns = new[] { "*.wav" } },
        new("全部文件") { Patterns = new[] { "*" } },
    };

    public BellCalibrationSettingsPage()
    {
        InitializeComponent();
        // 全部控件（CheckBox / NumericUpDown）双向绑定插件全局配置，
        // 属性 setter 触发 PropertyChanged → Plugin 订阅后自动保存。
        DataContext = BellTimeCalibrationPlugin.Config;

        ReloadSampleList();
    }

    /// <summary>
    /// 样本管理的一行（v1.0.4 增补）。只做三件事：把样本字段转成界面文本、
    /// 承载「有效」勾选框、把勾选结果写回存储。不含任何判定逻辑。
    /// </summary>
    private sealed class DaySampleRow : System.ComponentModel.INotifyPropertyChanged
    {
        private readonly string _ts;
        private bool _valid;

        public DaySampleRow(OffsetSample sample)
        {
            _ts = string.IsNullOrEmpty(sample.Ts)
                ? sample.At.ToString("yyyy-MM-dd HH:mm:ss.fff")
                : sample.Ts;
            TimeText = sample.At.ToString("HH:mm:ss.fff");
            Kind = string.IsNullOrEmpty(sample.Kind) ? "-" : sample.Kind;
            Boundary = string.IsNullOrEmpty(sample.BoundaryDisplay) ? "-" : sample.BoundaryDisplay;
            RequiredText = $"{sample.RequiredSec:+0.000;-0.000;0.000} s";
            AppliedText = sample.Applied ? "是" : "否";
            // 让「为什么默认勾 / 不勾」一眼可见
            OriginText = sample.Applied ? "写入过"
                : sample.InDeadZone ? "死区带内（未写）"
                : "大误差拒写";
            _valid = sample.UserValid;
        }

        public string TimeText { get; }

        public string Kind { get; }

        public string Boundary { get; }

        public string RequiredText { get; }

        public string AppliedText { get; }

        /// <summary>判据当时把这条样本归到哪一类（写入过 / 死区带内未写 / 大误差拒写）。</summary>
        public string OriginText { get; }

        /// <summary>本行样本是否可用于拟合（人工可改）。改动立即写盘。</summary>
        public bool Valid
        {
            get => _valid;
            set
            {
                if (_valid == value)
                    return;
                _valid = value;
                var ok = OffsetSampleStore.SetValidity(_ts, value);
                if (!ok)
                {
                    // 写盘失败：把界面改回原值，避免显示与落盘不一致
                    _valid = !value;
                    Logger.Warn($"[校时] 样本管理：{_ts} 标记写入失败，界面已回退。");
                }

                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Valid)));
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>
    /// 重新载入当天的样本列表（设置页打开时、以及点「重新载入」时调用）。
    /// 全部异常就地吞掉并记日志：样本管理只是复核工具，不能影响设置页其余部分。
    /// </summary>
    private void ReloadSampleList()
    {
        try
        {
            var today = OffsetSampleStore.LoadToday(DateTime.Now, out var note);
            var rows = new System.Collections.Generic.List<DaySampleRow>(today.Count);
            foreach (var s in today)
                rows.Add(new DaySampleRow(s));

            var list = this.FindControl<ItemsControl>("SampleList");
            if (list != null)
                list.ItemsSource = rows;

            var empty = this.FindControl<TextBlock>("SampleEmptyText");
            if (empty != null)
                empty.IsVisible = rows.Count == 0;

            var summary = this.FindControl<TextBlock>("SampleSummaryText");
            var eligible = today.Count(s => s.IsEligible);
            var overridden = today.Count(s => s.UserValid != (s.Applied || s.InDeadZone));
            var deadZoneOnes = today.Count(s => s.InDeadZone);
            var rejected = today.Count(s => !s.Applied && !s.InDeadZone);
            if (summary != null)
            {
                summary.Text = $"当天 {today.Count} 条样本：参与手动拟合 {eligible} 条（勾选即参与）" +
                               $"{(deadZoneOnes > 0 ? $"，其中死区带内 {deadZoneOnes} 条默认参与" : "")}" +
                               $"{(rejected > 0 ? $"，大误差拒写 {rejected} 条默认不参与（可手动勾上）" : "")}" +
                               $"{(overridden > 0 ? $"，人工调整过 {overridden} 条" : "")}。{note}";
            }

            // v1.0.4 日志瘦身：原先每次打开/重新载入设置页都写一行 130+ 字的说明（一天 30+ 行）；
            // 现在写短摘要，且内容未变就不重复写（界面上的详细说明不受影响）。
            Logger.InfoIfChanged("sample-manage",
                $"[校时] 样本管理 {OffsetSampleStore.Summarize(today)}");
        }
        catch (Exception ex)
        {
            Logger.Warn($"[校时] 样本管理：载入当天样本失败：{ex.Message}");
            var summary = this.FindControl<TextBlock>("SampleSummaryText");
            if (summary != null)
                summary.Text = $"载入失败：{ex.Message}";
        }
    }

    /// <summary>「重新载入」按钮：重新读取当天样本（点过手动拟合之后也能刷新）。</summary>
    private void OnReloadSamples(object? sender, RoutedEventArgs e) => ReloadSampleList();

    /// <summary>
    /// <summary>
    /// 主题变体变化（浅色/深色切换）：让本页的 <c>DynamicResource</c> 颜色重新解析。
    ///
    /// 为什么需要：本页的卡片底色与提示文字走 <c>ResourceDictionary.ThemeDictionaries</c>
    /// （见 axaml 顶部），正常情况下 Avalonia 自动跟随主题变体；但设置页是缓存的实例，
    /// 主题切换后已实例化的控件不一定会重新查找资源，于是会出现「切到浅色模式、提示文字仍是深色配色的白字」
    /// 这种半更新状态。内核自身的 <c>LessonControlExpanded</c> 也对手动刷新处理同一类问题
    /// （它用 <c>InvalidateVisual</c>，这里要的是重新解析资源）。
    ///
    /// 做法：先清空带语义类的控件上的本地值 —— 清空即让该处的 DynamicResource 重新求值，
    /// 按当前主题变体取到新画刷。只识别 <c>bellcard</c> / <c>bellhint</c> 两个类，
    /// 不会误伤按钮、输入框等自身配色由宿主主题决定的控件。
    /// </summary>
    private void OnActualThemeVariantChanged(object? sender, EventArgs e)
    {
        try
        {
            foreach (var descendant in this.GetVisualDescendants())
            {
                switch (descendant)
                {
                    case Border border when border.Classes.Contains("bellcard"):
                        border.ClearValue(Border.BackgroundProperty);
                        break;
                    case TextBlock text when text.Classes.Contains("bellhint"):
                        text.ClearValue(TextBlock.ForegroundProperty);
                        break;
                }
            }

            // 清空本地值只改属性、不保证立刻重画，这里补一次重绘
            InvalidateVisual();
        }
        catch (Exception ex)
        {
            // 主题刷新失败只影响观感，绝不影响设置页功能
            Logger.Warn($"[校时] 设置页主题刷新失败（不影响功能）：{ex.Message}");
        }
    }

    /// <summary>浏览并选择「上课铃长录音样本」。</summary>
    private async void OnBrowseClassBellSample(object? sender, RoutedEventArgs e)
        => await PickSampleAsync(path => BellTimeCalibrationPlugin.Config.ClassBellSamplePath = path);

    /// <summary>浏览并选择「下课铃长录音样本」。</summary>
    private async void OnBrowseBreakBellSample(object? sender, RoutedEventArgs e)
        => await PickSampleAsync(path => BellTimeCalibrationPlugin.Config.BreakBellSamplePath = path);

    /// <summary>
    /// 手动拟合并应用（v1.0.4）：读当天有效样本重新分析一次，把结果直接写入
    /// 「应用设置 → 时钟 → 时间偏移」——**无视学习模式与自动应用开关**（这是一次显式的手动动作）。
    /// 全程不抛异常：任何一步失败都把原因写进结果文字与日志，绝不影响设置页交互。
    /// </summary>
    private void OnManualFit(object? sender, RoutedEventArgs e)
    {
        try
        {
            var today = OffsetSampleStore.LoadToday(DateTime.Now, out var note);
            var result = ManualFitService.Analyze(today);
            if (result == null)
            {
                SetFitResult($"当天没有参与拟合的样本（{note}），未做任何写入。" +
                             "参与与否由设置页「样本管理」的勾选决定，勾上即参与。");
                Logger.Warn($"[校时] 手动拟合：{note}；无有效样本，未写入。");
                return;
            }

            var applier = BellTimeCalibrationPlugin.OffsetApplier;
            if (applier?.IsAvailable != true)
            {
                SetFitResult("偏移写入通道不可用（未能解析内核 Settings.TimeOffsetSeconds），拟合结果未写入。");
                Logger.Warn($"[校时] 手动拟合：写入通道不可用，拟合值 {result.OffsetSeconds:F3}s 未写入；{result.Note}");
                return;
            }

            var before = applier.CurrentOffsetSeconds;

            // 幂等保护（v1.0.4）：实测一次点击会执行两遍（两次调用读到的「旧值」相同，说明是两份状态各跑了一次，
            // 最可能是设置页实例被创建了两份）。写入前先比对：目标值已到位就不再写，
            // 避免重复写入与「由 X 改为 X」这种误导性结果文字。
            if (before != null && Math.Abs(before.Value - result.OffsetSeconds) < 0.0005)
            {
                SetFitResult($"当前偏移已等于拟合值 {before.Value:F3} 秒，未重复写入。" +
                             $"有效样本 {result.ValidCount}/{result.TotalCount}；{result.Note}");
                Logger.Info($"[校时] 手动拟合：目标值与当前偏移一致，跳过重复写入（幂等保护）；{result.Note}");
                return;
            }

            if (!applier.Apply(TimeSpan.FromSeconds(result.OffsetSeconds)))
            {
                SetFitResult("写入失败，详见插件日志。");
                return;
            }

            // 写入后让运行时估计器改用同一份样本，避免下一个边界用旧序列把手动结果顶回去
            BellTimeCalibrationPlugin.Runner?.ReloadTodaySamples();

            var summary = ManualFitService.Describe(result, before);
            SetFitResult(summary);
            Logger.Info($"[校时] 手动拟合并应用（设置页按钮）：{summary}（无视学习模式与自动应用开关）");
        }
        catch (Exception ex)
        {
            SetFitResult($"手动拟合异常：{ex.Message}");
            Logger.Warn($"[校时] 手动拟合异常：{ex}");
        }
    }

    /// <summary>写结果文字（控件在 XAML 里名为 FitResultText）。</summary>
    private void SetFitResult(string text)
    {
        var block = this.FindControl<TextBlock>("FitResultText");
        if (block != null)
            block.Text = text;
    }

    /// <summary>
    /// 发送一条测试用的「大误差人工复核提醒」（v1.0.4）：让用户随时确认提醒通道真的能弹出来，
    /// 而不必等下一次真实大误差（大误差是低频事件，可能几天才出现一次）。
    /// 阈值与「人工审核提醒」开关**都不参与**（这是一次显式的手动测试），因此直接走通知门面。
    /// </summary>
    private void OnTestReviewNotification(object? sender, RoutedEventArgs e)
    {
        try
        {
            var notifier = BellTimeCalibrationPlugin.ReviewNotifier;
            if (notifier == null)
            {
                Logger.Warn("[校时] 测试提醒失败：提醒通道尚未初始化（宿主提醒服务不可用）。");
                return;
            }

            notifier.PublishTestNotification();
        }
        catch (Exception ex)
        {
            Logger.Warn($"[校时] 测试提醒失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 打开文件选择器并把结果写回配置。
    /// 要点：
    /// <list type="bullet">
    /// <item><description>文件选择器挂在当前页面的 <c>TopLevel</c> 上，不新建窗口；</description></item>
    /// <item><description>选中后立刻重载模板库，无需重启 ClassIsland 即可生效（模板库平时只在启动时加载一次）；</description></item>
    /// <item><description>写回的是所选文件的**绝对路径**；「相对插件配置目录」与「绝对路径」两种写法都受支持；</description></item>
    /// <item><description>用户取消或环境不支持时静默返回，任何异常只记日志，绝不打断设置页交互。</description></item>
    /// </list>
    /// </summary>
    /// <param name="apply">把选中的绝对路径写回配置的委托。</param>
    private async Task PickSampleAsync(Action<string> apply)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider is not { CanOpen: true } storage)
            {
                Logger.Warn("[校时] 当前环境不支持文件选择器，请手动填写样本路径。");
                return;
            }

            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择铃声长录音样本（WAV）",
                AllowMultiple = false,
                FileTypeFilter = AudioFileTypes,
            });

            if (files.Count == 0)
                return; // 用户取消

            var path = files[0].TryGetLocalPath();
            if (string.IsNullOrEmpty(path))
            {
                Logger.Warn("[校时] 所选文件无法取得本地路径（可能是非本地存储），请手动填写样本路径。");
                return;
            }

            apply(path);

            // 主动重载模板库：否则要重启 ClassIsland 才会用上新选的样本
            TemplateLibrary.Reload(Logger.ConfigFolder ?? string.Empty, BellTimeCalibrationPlugin.Config);
            Logger.Info($"[校时] 已选择铃声样本：{path}；模板库已重载（{TemplateLibrary.StatusMessage}）");
        }
        catch (Exception ex)
        {
            Logger.Warn($"[校时] 选择样本文件失败：{ex.Message}");
        }
    }
}

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

        LogThemeDiagnostics("设置页首次加载");
    }

    /// <summary>
    /// 主题诊断（一次性自检）：把当前主题变体与两个画刷实际解析到的颜色写进日志。
    /// 目的：浅色模式的可读性只能靠人眼判断，但「资源到底有没有解析成功」可以机器判断 ——
    /// 解析成功时颜色必是 Light/Dark 组里的四个值之一；若出现回退色，说明主题字典没生效，
    /// 那时光看界面只会以为「颜色不对」，看日志能直接定位。主题切换时也会再记一次。
    /// </summary>
    private void LogThemeDiagnostics(string reason)
    {
        try
        {
            var variant = ActualThemeVariant;
            var card = (this.FindResource("BellCardBackgroundBrush") as ISolidColorBrush)?.Color;
            var hint = (this.FindResource("BellHintBrush") as ISolidColorBrush)?.Color;

            var expected = new[]
            {
                Color.Parse("#0A000000"), Color.Parse("#C0000000"),   // Light
                Color.Parse("#18FFFFFF"), Color.Parse("#AAFFFFFF"),   // Dark
            };
            var ok = card is { } c1 && hint is { } c2 && expected.Contains(c1) && expected.Contains(c2);

            if (ok)
            {
                Logger.Info($"[校时] 设置页主题自检（{reason}）：主题={variant}，" +
                            $"卡片底色={card}，提示文字={hint} → 主题资源解析正常。");
            }
            else
            {
                Logger.Warn($"[校时] 设置页主题自检（{reason}）：主题={variant}，" +
                            $"卡片底色={card?.ToString() ?? "未解析"}，提示文字={hint?.ToString() ?? "未解析"}" +
                            " → 未取到主题画刷（回退到默认前景色/透明底），浅色模式可读性可能受影响。");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[校时] 设置页主题自检失败（不影响功能）：{ex.Message}");
        }
    }

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

            LogThemeDiagnostics("主题变体切换");
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
    /// 手动拟合并应用（v1.0.2）：读当天有效样本重新分析一次，把结果直接写入
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
                SetFitResult($"当天没有有效样本（{note}），未做任何写入。" +
                             "有效样本 = 当天真正写入过偏移的干净测量；大误差拒写与死区带内的存量不计入。");
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
    /// 发送一条测试用的「大误差人工复核提醒」（v1.0.2）：让用户随时确认提醒通道真的能弹出来，
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

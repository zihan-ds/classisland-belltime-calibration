using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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
    }

    /// <summary>浏览并选择「上课铃长录音样本」。</summary>
    private async void OnBrowseClassBellSample(object? sender, RoutedEventArgs e)
        => await PickSampleAsync(path => BellTimeCalibrationPlugin.Config.ClassBellSamplePath = path);

    /// <summary>浏览并选择「下课铃长录音样本」。</summary>
    private async void OnBrowseBreakBellSample(object? sender, RoutedEventArgs e)
        => await PickSampleAsync(path => BellTimeCalibrationPlugin.Config.BreakBellSamplePath = path);

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

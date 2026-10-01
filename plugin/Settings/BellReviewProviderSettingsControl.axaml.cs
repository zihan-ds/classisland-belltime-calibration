using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using BellTimeCalibration.Services;

namespace BellTimeCalibration.Settings;

/// <summary>
/// 「设置 → 提醒 → 铃声校时复核 → 基本设置」里的设置控件（v1.0.4 增补）。
///
/// 为什么要有它：提醒提供方登记时 <c>SettingsElement</c> 为 null，宿主会显示「该提醒提供方没有设置。」
/// （ClassIsland 的 <c>NotificationSettingsPage.axaml</c> 对空设置就是这样渲染的）——本插件的开关与测试按钮
/// 原本只在「设置 → 插件 → 铃声自动校时」里，用户按提示跑到「提醒」页却看不到任何可操作项。
/// 这里把同一份配置（<see cref="BellTimeCalibrationPlugin.Config"/>）的开关与测试按钮搬过来一份，
/// 两处改的是同一个属性，**不存在第二套设置**。
///
/// 线程要求：控件由 <see cref="HostNotificationSender"/> 在 AppStarted（UI 线程）里创建。
/// </summary>
public partial class BellReviewProviderSettingsControl : UserControl
{
    private readonly CheckBox? _reviewToggle;
    private bool _syncing;

    public BellReviewProviderSettingsControl()
    {
        InitializeComponent();

        // 刻意**不用数据绑定**：本控件由宿主的 ContentPresenter 承载，其 DataContext 会跟着
        // 「提醒提供方列表项」走，绑定的落点不由我们决定。这里直接读写同一份配置
        // （BellTimeCalibrationPlugin.Config），与插件设置页里的开关是同一个属性。
        _reviewToggle = this.FindControl<CheckBox>("ReviewEnabledToggle");
        if (_reviewToggle == null)
            return;

        RefreshFromConfig();
        _reviewToggle.IsCheckedChanged += OnReviewToggleChanged;
    }

    /// <summary>用配置里的当前值刷新控件（配置是唯一真源，控件只是它的视图）。</summary>
    private void RefreshFromConfig()
    {
        if (_reviewToggle == null)
            return;

        _syncing = true;
        try
        {
            _reviewToggle.IsChecked = BellTimeCalibrationPlugin.Config.EnableReviewNotification;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnReviewToggleChanged(object? sender, RoutedEventArgs e)
    {
        if (_syncing || _reviewToggle == null)
            return;

        var want = _reviewToggle.IsChecked == true;
        if (BellTimeCalibrationPlugin.Config.EnableReviewNotification == want)
            return;

        // 属性 setter 触发 PropertyChanged → 插件订阅后自动落盘；插件设置页里的同名开关会同步显示新值。
        BellTimeCalibrationPlugin.Config.EnableReviewNotification = want;
        Logger.Info($"[校时] 提醒提供方设置页：人工审核提醒 → {(want ? "开" : "关")}。");
    }

    /// <summary>
    /// 发送一条测试用的复核提醒（与插件设置页里那个按钮走同一条路）。
    /// 阈值与「人工审核提醒」开关都不参与——这是一次显式的手动测试。
    /// </summary>
    private void OnTestReviewNotification(object? sender, RoutedEventArgs e)
    {
        try
        {
            var notifier = BellTimeCalibrationPlugin.ReviewNotifier;
            if (notifier == null)
            {
                ShowResult("提醒通道尚未初始化（宿主提醒服务不可用），详见插件日志。");
                Logger.Warn("[校时] 测试提醒失败：提醒通道尚未初始化（宿主提醒服务不可用）。");
                return;
            }

            var ok = notifier.PublishTestNotification();
            ShowResult(ok ? "已发送：请在界面上确认提醒是否弹出。" : "发送失败（通道不可用），详见插件日志。");
        }
        catch (Exception ex)
        {
            ShowResult($"发送失败：{ex.Message}");
            Logger.Warn($"[校时] 测试提醒失败：{ex.Message}");
        }
    }

    private void ShowResult(string text)
    {
        var block = this.FindControl<TextBlock>("TestResultText");
        if (block == null)
            return;
        block.Text = text;
        block.IsVisible = true;
    }
}

using System;
using System.Linq;
using System.Reflection;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Models.Notification;
using ClassIsland.Shared.Interfaces;
using ClassIsland.Shared.Models.Notification;
// 同名类型消歧：本插件只用 v2 提醒请求（ClassIsland.Core），Shared 里那个已弃用
using NotificationRequest = ClassIsland.Core.Models.Notification.NotificationRequest;

namespace BellTimeCalibration.Services;

/// <summary>
/// 本插件在宿主提醒系统里的提供方 GUID。
/// </summary>
public static class BellReviewNotificationIds
{
    /// <summary>提醒提供方 GUID。</summary>
    public static readonly Guid ProviderGuid = Guid.Parse("0B7E1C4A-9D53-4F62-8E10-3C5A7B2D9F41");

    /// <summary>提醒渠道 GUID（不使用渠道，仅作提醒请求的占位）。</summary>
    public static readonly Guid ChannelGuid = Guid.Parse("5C2D8E71-4A36-4B90-9F58-1E7D3A6C0B24");
}

/// <summary>
/// 把一条提醒交给宿主的发送通道（v1.0.2）。
///
/// 背景（实测结论，务必保留）：宿主 <see cref="INotificationHostService"/> 的
/// <c>ShowNotification(request, providerGuid, channelGuid, pushNotifications, isPlayed)</c> 是 internal，
/// 公开基类 <c>NotificationProviderBase</c> 虽然暴露了公开的发送方法，但**在本机内核 2.1.0.1 上不可用**：
/// 插件派生类的构造函数确实以 autoRegister=true 调进了基类（已用 IL 反汇编确认），
/// 基类也拿到了提供方信息（Name/ProviderGuid 都正确填好了），却始终没有走到
/// <c>INotificationHostService.RegisterNotificationProvider</c>，宿主日志里没有任何注册记录 ——
/// 提醒能力会静默缺失。因此这里不依赖基类，改为：
/// <list type="number">
/// <item><description>用 <c>RegisterNotificationProvider</c>（公开接口方法）把本插件登记进宿主的提醒提供方表，
/// 使其出现在「设置 → 提醒」并可被单独开关；</description></item>
/// <item><description>用反射调用上面那个 internal 的 <c>ShowNotification</c>，真正把提醒交给宿主派发。</description></item>
/// </list>
/// 与 <see cref="SettingsOffsetApplier"/> 写内核时间偏移同样的路子：只用宿主**已有**的公开/可反射成员，
/// 不改内核、不加内核补丁。
/// </summary>
public sealed class HostNotificationSender
{
    private readonly INotificationHostService _host;

    /// <summary>internal 的 ShowNotification(NotificationRequest, Guid, Guid, bool, bool)；取不到时为 null。</summary>
    private readonly MethodInfo? _showMethod;

    /// <summary>
    /// 构造发送通道并立即把本插件登记进宿主的提醒提供方表。
    /// </summary>
    /// <param name="host">宿主提醒主机服务（公开接口实例）。</param>
    /// <param name="log">日志回调（由调用方提供，便于把探测结果写进插件日志）。</param>
    public HostNotificationSender(INotificationHostService host, Action<string> log)
    {
        _host = host;

        _showMethod = host.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(m => m.Name == "ShowNotification"
                                 && m.GetParameters().Length == 5
                                 && m.GetParameters()[0].ParameterType == typeof(NotificationRequest));

        if (_showMethod == null)
        {
            log("[校时] 提醒发送通道不可用：宿主未提供 ShowNotification(请求,提供方,渠道,推送,已播放)，大误差只记日志。");
            return;
        }

        // 登记进宿主提醒提供方表：出现在「设置 → 提醒」，并会被写进 Settings.json 的
        // NotificationProvidersPriority（提醒调度按该列表排序，缺失也能显示，但登记后才可被用户单独关闭）。
        try
        {
            _host.RegisterNotificationProvider(new BellTimeCalibrationProviderRegistration());
            log("[校时] 已登记提醒提供方「铃声校时复核」（设置 → 提醒 中可单独开关）。");
        }
        catch (Exception ex)
        {
            log($"[校时] 登记提醒提供方失败（提醒仍可发送，但设置页里不会出现开关）：{ex.Message}");
        }

        log($"[校时] 提醒发送通道已就绪（反射调用 {_showMethod.DeclaringType?.FullName}.ShowNotification）。");
    }

    /// <summary>
    /// 把一条提醒交给宿主派发。任何异常只记日志，绝不影响校准主链路。
    /// </summary>
    /// <param name="request">提醒请求（MaskContent 必填）。</param>
    /// <param name="log">日志回调。</param>
    /// <returns>true = 已交给宿主。</returns>
    public bool Send(NotificationRequest request, Action<string> log)
    {
        if (_showMethod == null)
            return false;

        try
        {
            _showMethod.Invoke(_host,
                new object[] { request, BellReviewNotificationIds.ProviderGuid, BellReviewNotificationIds.ChannelGuid, true, false });
            return true;
        }
        catch (Exception ex)
        {
            log($"[校时] 发送提醒失败（不影响校准）：{ex.InnerException?.Message ?? ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 最小可用的提醒提供方实现：只为让本插件出现在宿主的提醒提供方表里（可被单独开关）。
    /// 真正的发送不经过它（见类注释），因此这里只提供名称、GUID 与空设置占位。
    /// </summary>
    private sealed class BellTimeCalibrationProviderRegistration : INotificationProvider
    {
        public string Name { get; set; } = "铃声校时复核";

        public string Description { get; set; } = "校铃校时出现大误差时，提醒人工复核。";

        public Guid ProviderGuid { get; set; } = BellReviewNotificationIds.ProviderGuid;

        public object? SettingsElement { get; set; }

        public object? IconElement { get; set; }
    }
}

/// <summary>
/// 大误差人工复核提醒的门面（v1.0.2）：把「要提醒什么」翻译成一条 ClassIsland 提醒，
/// 把「要不要弹」交给纯函数 <see cref="NotificationReviewDecision"/> 判定（判定单独成文件，
/// 便于离线断言；本文件才有宿主依赖）。
///
/// 容错口径：提醒只是附加能力，**任何异常都不得影响校准主链路**——
/// 捕获 <see cref="Exception"/> 后只写日志；发送通道不可用时静默跳过（仍写日志说明原因）。
/// </summary>
public sealed class BellReviewNotifier
{
    private readonly HostNotificationSender? _sender;

    /// <summary>
    /// 构造提醒门面。
    /// </summary>
    /// <param name="sender">提醒发送通道；宿主未提供时为 null（此时只记日志，不弹提醒）。</param>
    public BellReviewNotifier(HostNotificationSender? sender)
    {
        _sender = sender;
    }

    /// <summary>是否具备弹提醒的能力（供启动日志说明）。</summary>
    public bool IsAvailable => _sender != null;

    /// <summary>
    /// 发送一条测试提醒（设置页「发送测试提醒」按钮）：不参与阈值与开关判定，
    /// 用于随时确认提醒通道真的能在界面上弹出来。
    /// </summary>
    /// <returns>true = 已交给宿主。</returns>
    public bool PublishTestNotification()
    {
        if (_sender == null)
        {
            Logger.Warn("[校时] 测试提醒失败：提醒发送通道不可用。");
            return false;
        }

        var request = new NotificationRequest
        {
            MaskContent = NotificationContent.CreateTwoIconsMask("铃声校时需人工复核", rightIcon: "\uE7BA"),
            OverlayContent = NotificationContent.CreateSimpleTextContent(
                "这是一条测试提醒（来自设置页「发送测试提醒」）。真实的大误差复核提醒格式与此相同，" +
                "会写明边界时刻、当前偏移与测得所需偏移。")
        };

        var ok = _sender.Send(request, m => Logger.Warn(m));
        Logger.Info(ok
            ? "[校时] 测试提醒已交给宿主派发。"
            : "[校时] 测试提醒发送失败（详见上方日志）。");
        return ok;
    }

    /// <summary>
    /// 大误差复核提醒：按「设置开关 + 大误差阈值」判定后弹出一条提醒，要求用户人工复核。
    /// </summary>
    /// <param name="args">提醒内容参数（全部为基元类型）。</param>
    /// <returns>true = 本次确实弹出了提醒。</returns>
    public bool NotifyLargeError(BellReviewNotifyArgs args)
    {
        try
        {
            var (shouldNotify, why) = NotificationReviewDecision.Evaluate(
                args.ChangeSeconds, args.LimitSeconds, args.NotificationEnabled);

            if (!shouldNotify)
            {
                Logger.Info($"[校时] 人工复核提醒：不弹出（{why}）。");
                return false;
            }

            if (_sender == null)
            {
                Logger.Warn($"[校时] 人工复核提醒：需要弹出（{why}），但宿主未提供提醒发送通道，本次只记日志。");
                return false;
            }

            var text =
                $"边界 {args.BoundaryKind} {args.BoundaryDisplay:HH:mm:ss}：本次测得所需偏移 {args.RequiredOffsetSeconds:F3}s，" +
                $"与当前偏移 {args.CurrentOffsetSeconds:F3}s 相差 {args.ChangeSeconds:+0.000;-0.000;0.000}s" +
                $"（超过 ±{args.LimitSeconds:F1}s），已拒绝写入。请人工确认铃况与校铃钟后再决定是否手动校准。{args.Basis}";

            var request = new NotificationRequest
            {
                MaskContent = NotificationContent.CreateTwoIconsMask("铃声校时需人工复核", rightIcon: "\uE7BA"),
                OverlayContent = NotificationContent.CreateSimpleTextContent(text),
                RequestNotificationSettings = new NotificationSettings
                {
                    // 不覆盖用户在宿主里设置的提醒音/置顶等偏好（IsSettingsEnabled=false 时该对象不生效）
                    IsSettingsEnabled = false
                }
            };

            if (!_sender.Send(request, m => Logger.Warn(m)))
                return false;

            Logger.Warn($"[校时] 人工复核提醒：已弹出（{why}）。{text}");
            return true;
        }
        catch (Exception ex)
        {
            // 提醒失败绝不影响校准：这里只记日志
            Logger.Warn($"[校时] 人工复核提醒弹出失败（不影响校准）：{ex.Message}");
            return false;
        }
    }
}

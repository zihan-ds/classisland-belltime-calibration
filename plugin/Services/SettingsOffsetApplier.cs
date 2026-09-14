using System;
using System.Linq;
using System.Reflection;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared;

namespace BellTimeCalibration.Services;

/// <summary>
/// 「应用设置 → 时钟 → 时间偏移」应用通道（v0.7.0：<b>原版内核即可用</b>，不需要任何内核改动）。
/// 写入目标就是 ClassIsland 自身的设置项 <c>Settings.TimeOffsetSeconds</c>：
/// <list type="bullet">
/// <item><description><c>ExactTimeService.GetCurrentLocalDateTime()</c> 每次调用都读取该设置（+ 调试偏移），写入后立即生效；</description></item>
/// <item><description><c>SettingsService</c> 在 <c>Settings.PropertyChanged</c> 时落盘 <c>Settings.json</c>，因此写入即持久化；</description></item>
/// <item><description>设置页「时钟 → 时间偏移」绑定同一实例，写入后界面同步可见。</description></item>
/// </list>
/// 获取通道（全程反射，失败只使通道标记为不可用，绝不抛异常、绝不改课表）：
/// ① 从 <see cref="IExactTimeService"/> 实例的 <c>SettingsService</c> 属性（内核私有属性）取设置服务；
/// ② 回退：<c>IAppHost.Host</c>（public static IHost）→ DI 按类型名解析 <c>ClassIsland.Services.SettingsService</c>；
/// 再取该服务上的 <c>Settings</c> 对象与其 public 可读写 <c>TimeOffsetSeconds</c>。
/// </summary>
public sealed class SettingsOffsetApplier : IOffsetApplier
{
    private readonly object? _settings;
    private readonly PropertyInfo? _offsetProperty;

    /// <inheritdoc />
    public string Name => "应用设置·时间偏移";

    /// <inheritdoc />
    public bool IsAvailable => _settings != null && _offsetProperty != null;

    /// <inheritdoc />
    public string StatusMessage { get; }

    /// <inheritdoc />
    public double? CurrentOffsetSeconds
    {
        get
        {
            if (!IsAvailable)
                return null;

            try
            {
                return (double)_offsetProperty!.GetValue(_settings)!;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[校时] {Name} 读取当前偏移失败：{ex.Message}");
                return null;
            }
        }
    }

    /// <summary>
    /// 构造并立即解析写入目标（解析失败只记录警告，通道判定为不可用）。
    /// </summary>
    /// <param name="exactTimeService">精确时间服务实例（用于取得内核 SettingsService）。</param>
    public SettingsOffsetApplier(IExactTimeService exactTimeService)
    {
        if (exactTimeService == null)
            throw new ArgumentNullException(nameof(exactTimeService));

        try
        {
            _settings = ResolveSettingsObject(exactTimeService);
            _offsetProperty = _settings?.GetType()
                .GetProperty("TimeOffsetSeconds", BindingFlags.Public | BindingFlags.Instance);

            if (IsAvailable)
            {
                StatusMessage =
                    "已解析到设置项 Settings.TimeOffsetSeconds（应用设置 → 时钟 → 时间偏移）。" +
                    $"当前值 {CurrentOffsetSeconds:F3} 秒。";
                Logger.Info($"[校时] {Name} 可用：{StatusMessage}");
            }
            else
            {
                StatusMessage =
                    "未能解析内核设置对象（SettingsService.Settings）或 TimeOffsetSeconds 属性，自动应用停用。";
                Logger.Warn($"[校时] {Name} 不可用：{StatusMessage}");
            }
        }
        catch (Exception ex)
        {
            _settings = null;
            _offsetProperty = null;
            StatusMessage = $"解析设置对象异常：{ex.Message}";
            Logger.Warn($"[校时] {Name} 解析异常：{ex}");
        }
    }

    /// <summary>
    /// 写入绝对时间偏移（秒）：设置项 TimeOffsetSeconds，内核立即生效并自动落盘。
    /// </summary>
    /// <param name="correction">绝对偏移秒数。</param>
    /// <returns>是否写入成功。</returns>
    public bool Apply(TimeSpan correction)
    {
        if (!IsAvailable)
        {
            Logger.Warn($"[校时] {Name} 不可用，跳过时间偏移写入。");
            return false;
        }

        try
        {
            var oldValue = (double)_offsetProperty!.GetValue(_settings)!;
            var newValue = correction.TotalSeconds;
            _offsetProperty.SetValue(_settings, newValue);
            Logger.Info($"[校时] {Name} 应用时间偏移：{oldValue:F3} 秒 → {newValue:F3} 秒（Settings.TimeOffsetSeconds）。");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"[校时] {Name} 写入失败：{ex}");
            return false;
        }
    }

    /// <summary>
    /// 解析内核 Settings 对象：优先经 IExactTimeService 实例的 SettingsService 属性，回退走宿主 DI。
    /// </summary>
    private static object? ResolveSettingsObject(IExactTimeService exactTimeService)
    {
        var settingsService = exactTimeService.GetType()
            .GetProperty("SettingsService", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(exactTimeService);

        settingsService ??= ResolveSettingsServiceFromHost();

        return settingsService?.GetType()
            .GetProperty("Settings", BindingFlags.Instance | BindingFlags.Public)
            ?.GetValue(settingsService);
    }

    /// <summary>回退路径：宿主 DI（IAppHost.Host.Services）按类型名解析 SettingsService。</summary>
    private static object? ResolveSettingsServiceFromHost()
    {
        var type = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("ClassIsland.Services.SettingsService", false))
            .FirstOrDefault(t => t != null);
        return type == null ? null : IAppHost.Host?.Services.GetService(type);
    }
}

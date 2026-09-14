using System.IO;
using BellTimeCalibration.Models;
using BellTimeCalibration.Services;
using BellTimeCalibration.Settings;
using ClassIsland.Core;
using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;
using ClassIsland.Shared;
using ClassIsland.Shared.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BellTimeCalibration;

/// <summary>
/// 插件入口类。必须继承 <see cref="PluginBase"/> 并添加 <see cref="PluginEntrance"/> 特性。
/// </summary>
[PluginEntrance]
public class BellTimeCalibrationPlugin : PluginBase
{
    /// <summary>插件全局配置实例（设置页及各服务通过此静态属性访问）。</summary>
    public static BellCalibrationSettings Config { get; private set; } = new();

    private static string _configPath = "";

    /// <summary>铃声监听调度器实例（宿主应用启动完成后创建）。</summary>
    private CalibrationScheduler? _scheduler;

    /// <summary>校准执行器实例（监听窗口内开麦检测响铃；由插件字段持有防止被 GC）。</summary>
    private CalibrationRunner? _runner;

    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        // 0. 初始化日志（必须最早，确保后续所有 Logger.xxx 调用能写入）
        Logger.Initialize(PluginConfigFolder);

        // 0.1 初始化结构化校准历史（与人类日志同目录，JSONL；供离线调参分析）
        CalibrationHistory.Initialize(PluginConfigFolder);

        Logger.Info("BellTimeCalibration 插件初始化完成");

        // 1. 加载配置：文件不存在则使用默认值并落盘一次；随后任何属性变更自动保存
        _configPath = Path.Combine(PluginConfigFolder, "BellCalibrationSettings.json");

        if (!File.Exists(_configPath))
        {
            Config = new BellCalibrationSettings();
            Save();
            Logger.Info("未找到配置文件，已创建默认配置并保存。");
        }
        else
        {
            Config = ConfigureFileHelper.LoadConfig<BellCalibrationSettings>(_configPath) ?? new BellCalibrationSettings();
        }

        Config.PropertyChanged += (_, _) => Save();

        // 1.1 加载铃声模板（v0.9.0）：长录音样本 → 模板；失败仅告警，选铃回退启发式
        TemplateLibrary.Reload(PluginConfigFolder, Config);

        // 2. 注册设置页
        services.AddSettingsPage<BellCalibrationSettingsPage>();

        // 3. 宿主应用启动完成后创建并启动铃声监听调度器（解析 ILessonsService / IExactTimeService）
        AppBase.Current.AppStarted += OnAppStarted;
    }

    /// <summary>
    /// 宿主应用启动完成：解析服务并启动调度器。
    /// 服务解析不到时仅记录日志，不得使宿主启动流程崩溃。
    /// </summary>
    private void OnAppStarted(object? sender, EventArgs e)
    {
        try
        {
            var lessonsService = IAppHost.TryGetService<ILessonsService>();
            var exactTimeService = IAppHost.TryGetService<IExactTimeService>();
            if (lessonsService == null || exactTimeService == null)
            {
                Logger.Error("[校时] 启动调度器失败：未能解析 ILessonsService 或 IExactTimeService。");
                return;
            }

            _scheduler = new CalibrationScheduler(lessonsService, exactTimeService);
            _scheduler.Start();

            // v0.7.0 起唯一应用通道为「应用设置 → 时钟 → 时间偏移」（Settings.TimeOffsetSeconds，反射写入）：
            // 无需任何内核改动（原版内核即可用）；课表平移通道已彻底移除，任何情况下不再修改课表档案。
            // （IProfileService 不再被插件解析/使用。）
            var offsetApplier = new SettingsOffsetApplier(exactTimeService);
            _runner = new CalibrationRunner(_scheduler, offsetApplier);
            Logger.Info("[校时] 校准执行器已就绪（v1.0：起响沿闸门 + 中位数估计（音频样本由用户提供），原版内核可用，不改课表）。");
        }
        catch (Exception ex)
        {
            Logger.Error($"[校时] 启动调度器异常：{ex}");
        }
    }

    /// <summary>将当前配置写入磁盘。</summary>
    public static void Save()
    {
        if (string.IsNullOrEmpty(_configPath)) return;
        try
        {
            ConfigureFileHelper.SaveConfig(_configPath, Config);
        }
        catch (Exception ex)
        {
            Logger.Error($"保存配置失败：{ex}");
        }
    }
}



using System;
using System.Collections.Generic;
using System.IO;
using BellTimeCalibration.Models;

namespace BellTimeCalibration.Services;

/// <summary>
/// 铃声模板库（v0.9.0）：启动时按配置加载「上课铃 / 下课铃」模板，供模板匹配使用。
/// 两种来源（设置页可配）：
/// <list type="bullet">
/// <item><description>长录音样本 + 截取区间（如样本文件 + "12.0-14.5"；留空则自动取最响且持续 ≥1.5s 的段）；</description></item>
/// <item><description>已截取的模板 WAV：<c>&lt;插件配置目录&gt;/&lt;TemplateDirectory&gt;/上课铃.wav</c> 与 <c>下课铃.wav</c>（文件名里的“上课/下课”用于识别）。</description></item>
/// </list>
/// 加载失败一律只告警并继续（插件回退到启发式选铃），绝不因模板问题影响校准主链路。
/// </summary>
public static class TemplateLibrary
{
    private static readonly List<BellTemplate> Loaded = new();

    /// <summary>已加载的模板（可能为空 = 未配置模板，走启发式回退）。</summary>
    public static IReadOnlyList<BellTemplate> Templates => Loaded;

    /// <summary>加载状态说明（供启动日志与设置页展示）。</summary>
    public static string StatusMessage { get; private set; } = "尚未加载。";

    /// <summary>
    /// 按配置重新加载模板（插件启动时调用一次；模板文件变化后重启插件即可生效）。
    /// </summary>
    /// <param name="configFolder">插件配置目录（模板相对路径的基准）。</param>
    /// <param name="config">当前插件配置。</param>
    public static void Reload(string configFolder, BellCalibrationSettings config)
    {
        Loaded.Clear();
        var notes = new List<string>();

        if (!config.EnableTemplateMatch)
        {
            StatusMessage = "模板匹配已关闭（设置项 EnableTemplateMatch=false），选铃使用启发式。";
            Logger.Info($"[校时] {StatusMessage}");
            return;
        }

        TryAdd(Loaded, notes, config.ClassBellSamplePath, configFolder, "class", "上课铃", config.ClassBellTrimSeconds);
        TryAdd(Loaded, notes, config.BreakBellSamplePath, configFolder, "break", "下课铃", config.BreakBellTrimSeconds);

        // 若样本路径未配置，则尝试模板目录下的现成模板文件
        if (Loaded.Count == 0)
        {
            var dir = ResolvePath(configFolder, config.TemplateDirectory is { Length: > 0 } ? config.TemplateDirectory : "Templates");
            foreach (var file in SafeEnumerateWav(dir))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var label = name.Contains("下课") || name.Contains("break", StringComparison.OrdinalIgnoreCase) ? "下课铃"
                    : name.Contains("上课") || name.Contains("class", StringComparison.OrdinalIgnoreCase) ? "上课铃"
                    : name;
                var id = label == "下课铃" ? "break" : label == "上课铃" ? "class" : name;
                var tpl = BellTemplate.LoadFromTemplateFile(file, id, label, out var note);
                if (tpl == null)
                {
                    notes.Add($"跳过 {Path.GetFileName(file)}：{note}");
                    continue;
                }

                // 随包分发的是**静音占位**（全零样本），用户尚未放入自己的录音样本。
                // 这类文件必须跳过：模板若由静音构成，包络恒为零，模板匹配会退化成随机命中。
                // 判定方式取「所有样本的绝对值之和」——浮点上更稳健，且不依赖具体的归一化实现。
                if (IsSilentPlaceholder(tpl))
                {
                    notes.Add($"跳过 {Path.GetFileName(file)}：全零静音（随包占位文件，请放入你自己的铃声录音样本）");
                    continue;
                }

                Loaded.Add(tpl);
                notes.Add(note);
            }
        }

        foreach (var n in notes)
            Logger.Info($"[校时] 模板：{n}");

        StatusMessage = Loaded.Count > 0
            ? $"已加载 {Loaded.Count} 个模板（{string.Join("、", Loaded.ConvertAll(t => $"{t.Label} {t.DurationSeconds:F2}s"))}）"
            : "未加载到任何模板（未配置样本路径，且模板目录内没有可用的 WAV）→ 选铃回退启发式。";

        if (Loaded.Count > 0)
            Logger.Info($"[校时] {StatusMessage}");
        else
            Logger.Warn($"[校时] {StatusMessage}");
    }

    /// <summary>加载一个来源（长录音样本或模板文件）。</summary>
    private static void TryAdd(
        List<BellTemplate> target, List<string> notes, string? samplePath, string configFolder,
        string id, string label, string? trimSpec)
    {
        if (string.IsNullOrWhiteSpace(samplePath))
            return;

        var full = ResolvePath(configFolder, samplePath);
        var tpl = BellTemplate.LoadFromRecording(full, id, label, trimSpec, out var note);
        if (tpl != null)
        {
            target.Add(tpl);
            notes.Add(note);
        }
        else
        {
            notes.Add($"加载 {label} 模板失败：{note}");
        }
    }

    /// <summary>相对路径按插件配置目录解析；绝对路径原样返回。</summary>
    private static string ResolvePath(string configFolder, string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(configFolder, path);

    /// <summary>
    /// 判断模板是否为**静音占位**（随包分发、等待用户替换的全零文件）。
    /// 用「样本绝对值之和」判定：静音模板的能量包络恒为零，若被当作真模板加载，
    /// 模板匹配会退化成随机命中。阈值取得极小（仅排除真正的全零/近零文件），不会误杀真实录音。
    /// </summary>
    /// <param name="template">已加载的模板。</param>
    private static bool IsSilentPlaceholder(BellTemplate template)
    {
        double sum = 0;
        foreach (var s in template.Samples)
            sum += Math.Abs(s);
        return sum < 1e-6;
    }

    /// <summary>安全枚举 WAV（目录不存在时返回空）。</summary>
    private static IEnumerable<string> SafeEnumerateWav(string dir)
    {
        try
        {
            return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.wav") : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}

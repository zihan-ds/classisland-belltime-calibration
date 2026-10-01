using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BellTimeCalibration.Services;

/// <summary>
/// 简单的文件日志器，将日志写入插件配置目录下的 Logs 文件夹。
/// 方便不会 C# 的用户查看运行状态。文件操作加锁，保证多线程写入安全。
/// </summary>
public static class Logger
{
    private static string LogDir = null!;

    /// <summary>插件配置目录（v0.9.0：供调试转存等路径使用）。</summary>
    public static string? ConfigFolder { get; private set; }
    private static readonly object LockObj = new();

    /// <summary>
    /// 初始化日志目录。应在插件 Initialize 时调用（最早时机）。
    /// </summary>
    public static void Initialize(string pluginConfigFolder)
    {
        ConfigFolder = pluginConfigFolder;
        LogDir = Path.Combine(pluginConfigFolder, "Logs");
        try { Directory.CreateDirectory(LogDir); } catch { }
    }

    public static void Info(string message) => Write("INFO", message);

    /// <summary>
    /// 只在内容与上一次（同一 <paramref name="key"/>）不同时才写 INFO（v1.0.4：日志瘦身）。
    /// 用于「每窗口 / 每次打开设置页都会打、但内容经常一字不差」的摘要行（样本恢复、样本管理载入）。
    /// 只对 INFO 去重：WARN/ERROR 一律照打，免得把真正重复的异常吞掉。
    /// </summary>
    public static void InfoIfChanged(string key, string message)
    {
        lock (LastByKey)
        {
            if (LastByKey.TryGetValue(key, out var last) && last == message)
                return;
            LastByKey[key] = message;
        }

        Write("INFO", message);
    }

    private static readonly Dictionary<string, string> LastByKey = new();

    public static void Error(string message) => Write("ERROR", message);

    public static void Warn(string message) => Write("WARN", message);

    private static void Write(string level, string message)
    {
        if (LogDir == null) return;

        try
        {
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            var logFile = Path.Combine(LogDir, $"belltimecalibration-{today}.log");
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {message}";
            lock (LockObj)
            {
                File.AppendAllText(logFile, line + Environment.NewLine);
            }
        }
        catch
        {
            // 日志写入失败不应影响主功能
        }
    }

    /// <summary>
    /// 获取最新日志文件路径
    /// </summary>
    public static string GetLatestLogPath()
    {
        try
        {
            if (LogDir == null || !Directory.Exists(LogDir))
                return "(未初始化)";

            var files = Directory.GetFiles(LogDir, "belltimecalibration-*.log");
            return files.Length > 0
                ? files.OrderByDescending(f => f).First()
                : Path.Combine(LogDir, "(暂无日志)");
        }
        catch
        {
            return "(获取失败)";
        }
    }
}

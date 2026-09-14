using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using SoundFlow.Abstracts.Devices;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Enums;
using SoundFlow.Structs;

namespace BellTimeCalibration.Services;

/// <summary>
/// 一个监听窗口的捕获结果（成功结束捕获时返回）。
/// </summary>
public class CaptureWindowResult
{
    /// <summary>检测到的铃声段列表（v0.8.0：起响点 + 峰值 + 时长，UTC 升序），可能为空。</summary>
    public IReadOnlyList<RingBurst> Bursts { get; }

    /// <summary>检测到的突发候选起响点列表（UTC，升序），可能为空（= Bursts 的起响点投影）。</summary>
    public IReadOnlyList<DateTime> Candidates { get; }

    /// <summary>窗口音频缓冲（v0.9.0，仅内存，供模板匹配）；未启用/初始化失败时为 null。</summary>
    public CaptureAudio? Audio { get; }

    /// <summary>捕获期间峰值 RMS（dBFS），供日志/调参。</summary>
    public float MaxRmsDb { get; }

    /// <summary>捕获结束时噪声底（dBFS），供日志/调参（判断灵敏度是否合适）。</summary>
    public float NoiseFloorDb { get; }

    /// <summary>实际捕获时长。</summary>
    public TimeSpan CaptureDuration { get; }

    /// <summary>
    /// 捕获如何结束："normal" = shouldContinue 条件先满足（窗口自然结束）；
    /// "hard-timeout" = 达到硬超时上限仍未满足条件（兜底结束）。
    /// </summary>
    public string EndedBy { get; }

    /// <summary>
    /// 构造捕获结果。
    /// </summary>
    public CaptureWindowResult(
        IReadOnlyList<RingBurst> bursts,
        IReadOnlyList<DateTime> candidates,
        CaptureAudio? audio,
        float maxRmsDb,
        float noiseFloorDb,
        TimeSpan captureDuration,
        string endedBy)
    {
        Bursts = bursts;
        Candidates = candidates;
        Audio = audio;
        MaxRmsDb = maxRmsDb;
        NoiseFloorDb = noiseFloorDb;
        CaptureDuration = captureDuration;
        EndedBy = endedBy;
    }
}

/// <summary>
/// 麦克风捕获服务（M3）。
/// 隐私红线：音频只做内存内 RMS 突发检测，绝不落盘、不写任何文件；
/// 捕获仅在触发窗口内进行，结束后立即释放引擎与设备。
/// 每个监听窗口在后台工作线程内新建引擎 + 捕获设备并在结束时机内 Dispose（规避线程亲和坑）。
/// 本文件只产出内存统计结果，不落盘（结构化历史由 CalibrationHistory 在 Runner 层记录）。
/// </summary>
public static class MicCaptureService
{
    /// <summary>捕获采样率。</summary>
    private const int SampleRate = 48000;

    /// <summary>
    /// 执行一次监听窗口捕获（异步）：
    /// 在工作线程创建默认输入设备捕获，持续到 <paramref name="shouldContinue"/> 返回 false
    /// 或超过 <paramref name="hardTimeout"/>；音频样本逐块送入 <paramref name="detector"/> 做突发检测。
    /// </summary>
    /// <param name="detector">突发检测器（每窗口新建）。</param>
    /// <param name="shouldContinue">是否继续捕获（约每 200ms 轮询一次；线程安全）。</param>
    /// <param name="hardTimeout">硬超时上限（= 布防提前量 + 窗口时长 + 15s），防止窗口永不结束。</param>
    /// <param name="keepAudio">是否在内存中保留窗口音频（模板匹配需要；音频绝不落盘）。</param>
    /// <returns>成功结束返回 <see cref="CaptureWindowResult"/>；初始化失败（无默认设备/设备被占用等）返回 null。</returns>
    public static Task<CaptureWindowResult?> CaptureAsync(
        RingDetector detector,
        Func<bool> shouldContinue,
        TimeSpan hardTimeout,
        bool keepAudio = false)
    {
        return Task.Run(() => CaptureCore(detector, shouldContinue, hardTimeout, keepAudio));
    }

    private static CaptureWindowResult? CaptureCore(
        RingDetector detector,
        Func<bool> shouldContinue,
        TimeSpan hardTimeout,
        bool keepAudio)
    {
        MiniAudioEngine? engine = null;
        AudioCaptureDevice? captureDevice = null;
        try
        {
            // 默认输入设备（null = 系统默认），48kHz 单声道 F32
            engine = new MiniAudioEngine();
            var format = new AudioFormat
            {
                SampleRate = SampleRate,
                Channels = 1,
                Layout = ChannelLayout.Mono,
                Format = SampleFormat.F32
            };
            captureDevice = engine.InitializeCaptureDevice(null, format);

            // 窗口音频缓冲（v0.9.0）：仅当启用模板匹配时分配，音频只在内存中留存至窗口结束
            var audio = keepAudio ? new CaptureAudio() : null;

            // 音频线程回调：只做轻量检测与内存拷贝；时间戳取 DateTime.UtcNow；异常不外抛、不写日志（音频线程禁 IO）
            captureDevice.OnAudioProcessed += (Span<float> samples, Capability _) =>
            {
                try
                {
                    var now = DateTime.UtcNow;
                    detector.Feed(samples, now);
                    audio?.Write(samples, now);
                }
                catch
                {
                    // 静默吞掉：绝不让异常穿过原生音频回调
                }
            };

            captureDevice.Start();

            var sw = Stopwatch.StartNew();
            while (shouldContinue() && sw.Elapsed < hardTimeout)
            {
                Thread.Sleep(200);
            }
            sw.Stop();

            // 先停止再快照，避免读取中列表被回调线程追加
            captureDevice.Stop();

            // 结算尚未因静音结束的段（窗口末尾仍在响的铃声不能丢）
            detector.Flush();

            return new CaptureWindowResult(
                detector.Bursts,
                detector.Candidates,
                audio,
                detector.MaxRmsDb,
                detector.NoiseFloorDb,
                sw.Elapsed,
                sw.Elapsed >= hardTimeout ? "hard-timeout" : "normal");
        }
        catch (Exception ex)
        {
            // 初始化失败（无默认设备/设备被独占/被禁用等）：日志一句，返回 null 优雅跳过本窗口
            Logger.Error($"[校时] 麦克风捕获初始化失败：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
        finally
        {
            // 逆序释放；各释放步骤独立容错，不掩盖原始结果
            if (captureDevice != null)
            {
                try { captureDevice.Stop(); } catch { }
                try { captureDevice.Dispose(); } catch { }
            }
            if (engine != null)
            {
                try { engine.Dispose(); } catch { }
            }
        }
    }
}

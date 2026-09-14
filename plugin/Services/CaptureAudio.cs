using System;
using System.Collections.Generic;

namespace BellTimeCalibration.Services;

/// <summary>
/// 窗口音频缓冲（v0.9.0）：把监听窗口内的采样**仅保存在内存**中，供模板匹配使用。
/// 隐私：与既有设计一致——音频绝不落盘、绝不出内存，窗口结束即随对象释放。
/// 时间映射：每次写入记录 (采样索引, 块墙钟) 锚点，可按采样索引线性插值出墙钟时刻（40 s 窗口内误差 &lt;1 ms）。
/// 写入发生在音频回调线程：预分配、无锁外的分配、无 IO（锚点列表按块追加，块数有限）。
/// </summary>
public sealed class CaptureAudio
{
    /// <summary>缓冲容量（秒）：监听窗口最长 60 s，超出部分丢弃。</summary>
    public const int CapacitySeconds = 60;

    /// <summary>内部采样率（与捕获一致）。</summary>
    public const int SampleRate = WavReader.TargetSampleRate;

    private readonly float[] _samples = new float[CapacitySeconds * SampleRate];
    private readonly List<(int SampleIndex, DateTime WallUtc)> _anchors = new();

    /// <summary>已写入的有效样本数。</summary>
    public int Length { get; private set; }

    /// <summary>缓冲数组（前 <see cref="Length"/> 个样本有效）。</summary>
    public float[] Samples => _samples;

    /// <summary>
    /// 首个锚点对应的墙钟（**本地**）；尚未写入任何块时为 <see cref="DateTime.MinValue"/>。
    /// 供需要「样本索引 ↔ 墙钟」自行线性换算的调用方使用（如 <see cref="OnsetGate"/>）。
    /// </summary>
    public DateTime AnchorWallLocal => _anchors.Count > 0 ? _anchors[0].WallUtc.ToLocalTime() : DateTime.MinValue;

    /// <summary>
    /// 写入一个音频块（音频回调线程调用）。
    /// </summary>
    /// <param name="block">本块样本。</param>
    /// <param name="wallTimeUtc">本块起始对应的墙钟（UTC）。</param>
    public void Write(ReadOnlySpan<float> block, DateTime wallTimeUtc)
    {
        if (block.IsEmpty)
            return;

        var start = Length;
        var count = Math.Min(block.Length, _samples.Length - Length);
        if (count <= 0)
            return;

        block[..count].CopyTo(_samples.AsSpan(start));
        Length += count;
        _anchors.Add((start, wallTimeUtc));
    }

    /// <summary>
    /// 采样索引 → 墙钟（UTC）；无锚点或索引越界时返回 null。
    /// </summary>
    /// <param name="sampleIndex">缓冲内的采样索引。</param>
    public DateTime? WallTimeAt(int sampleIndex)
    {
        if (_anchors.Count == 0 || sampleIndex < 0)
            return null;

        // 锚点按写入顺序递增；线性查找（块数有限，调用发生在窗口结束后的后台线程）
        var last = _anchors[0];
        if (sampleIndex <= last.SampleIndex)
            return last.WallUtc.AddSeconds((sampleIndex - last.SampleIndex) / (double)SampleRate);

        for (var i = 1; i < _anchors.Count; i++)
        {
            var cur = _anchors[i];
            if (sampleIndex < cur.SampleIndex)
            {
                var span = cur.SampleIndex - last.SampleIndex;
                var frac = span <= 0 ? 0 : (sampleIndex - last.SampleIndex) / (double)span;
                return last.WallUtc.AddTicks((long)((cur.WallUtc - last.WallUtc).Ticks * frac));
            }

            last = cur;
        }

        return last.WallUtc.AddSeconds((sampleIndex - last.SampleIndex) / (double)SampleRate);
    }

    /// <summary>
    /// 墙钟（UTC）→ 采样索引（v0.9.0，模板匹配需要把「边界 ± 容差」换算成搜索区间）。
    /// 目标时间早于首个锚点则按采样率线性外推；晚于末尾同理（由调用方裁剪到 [0, Length]）。
    /// </summary>
    /// <param name="wallUtc">目标墙钟时刻（UTC）。</param>
    /// <returns>缓冲内的采样索引（可能为负或超出 Length）。</returns>
    public int SampleIndexAt(DateTime wallUtc)
    {
        if (_anchors.Count == 0)
            return 0;

        var anchor = _anchors[0];
        if (wallUtc <= anchor.WallUtc)
        {
            return anchor.SampleIndex
                   + (int)Math.Floor((wallUtc - anchor.WallUtc).TotalSeconds * SampleRate);
        }

        for (var i = 1; i < _anchors.Count; i++)
        {
            var cur = _anchors[i];
            if (wallUtc < cur.WallUtc)
            {
                var span = cur.SampleIndex - anchor.SampleIndex;
                var totalTicks = (cur.WallUtc - anchor.WallUtc).Ticks;
                var frac = totalTicks <= 0 ? 0 : (wallUtc - anchor.WallUtc).Ticks / (double)totalTicks;
                return anchor.SampleIndex + (int)Math.Round(span * frac);
            }

            anchor = cur;
        }

        return anchor.SampleIndex
               + (int)Math.Floor((wallUtc - anchor.WallUtc).TotalSeconds * SampleRate);
    }

    /// <summary>
    /// 把窗口音频写为 48 kHz 单声道 PCM16 WAV（v0.9.0：仅在「调试音频转存」开启时由调用方调用）。
    /// 隐私：默认关闭；只有用户在设置里显式开启后，音频才会离开内存落到该文件。
    /// </summary>
    /// <param name="path">目标路径（目录不存在会自动创建）。</param>
    /// <param name="note">说明（中文，供日志）。</param>
    /// <returns>是否写出成功。</returns>
    public bool SaveWav(string path, out string note)
    {
        note = "";
        try
        {
            var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir))
                System.IO.Directory.CreateDirectory(dir);

            using var fs = new System.IO.FileStream(path, System.IO.FileMode.Create, System.IO.FileAccess.Write);
            using var bw = new System.IO.BinaryWriter(fs);
            var dataBytes = Length * 2;
            bw.Write("RIFF"u8.ToArray());
            bw.Write(36 + dataBytes);
            bw.Write("WAVE"u8.ToArray());
            bw.Write("fmt "u8.ToArray());
            bw.Write(16);
            bw.Write((short)1);
            bw.Write((short)1);
            bw.Write(SampleRate);
            bw.Write(SampleRate * 2);
            bw.Write((short)2);
            bw.Write((short)16);
            bw.Write("data"u8.ToArray());
            bw.Write(dataBytes);
            for (var i = 0; i < Length; i++)
            {
                // ×32768 而不是 ×32767：满量程 ±1.0 时 ×32767 会先到 32767 上限再被钳位，
                // 任何 ≥1.0 的样本都被削成同一个值（模板匹配最怕的就是攻击段被削平）。
                var v = (short)Math.Clamp(_samples[i] * 32768.0, short.MinValue, short.MaxValue);
                bw.Write(v);
            }

            note = $"{Length / (double)SampleRate:F1}s";
            return true;
        }
        catch (Exception ex)
        {
            note = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }
}

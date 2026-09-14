using System;
using System.IO;

namespace BellTimeCalibration.Services;

/// <summary>
/// 极简 WAV 读取器（v0.9.0）：只依赖 BCL，够用即可——模板匹配只需要「48 kHz 单声道 float」。
/// 支持：RIFF/WAVE、PCM 16/24/32 位与 IEEE float32、单/双声道；其余（压缩格式、非 WAV 容器）返回 null 并给出说明。
/// 立体声按等权降混；采样率非 48 kHz 时线性重采样（模板是一次性离线素材，线性插值精度足够）。
/// 不写任何文件、不做任何缓冲之外的处理。
/// </summary>
public static class WavReader
{
    /// <summary>插件内部统一采样率（与麦克风捕获一致）。</summary>
    public const int TargetSampleRate = 48000;

    /// <summary>
    /// 读取 WAV 并转换为 48 kHz 单声道 float 数组。
    /// </summary>
    /// <param name="path">WAV 路径。</param>
    /// <param name="note">失败原因或摘要（中文，供日志）。</param>
    /// <returns>样本数组（-1..1）；失败返回 null。</returns>
    public static float[]? ReadMono48k(string path, out string note)
    {
        note = "";
        try
        {
            if (!File.Exists(path))
            {
                note = $"文件不存在：{path}";
                return null;
            }

            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 44
                || !Matches(bytes, 0, "RIFF")
                || !Matches(bytes, 8, "WAVE"))
            {
                note = "不是 RIFF/WAVE 文件（若为 .m4a/.mp3 等需先转成 WAV）";
                return null;
            }

            int fmtPos = -1, fmtSize = 0, dataPos = -1, dataSize = 0;
            var pos = 12;
            while (pos + 8 <= bytes.Length)
            {
                var id = System.Text.Encoding.ASCII.GetString(bytes, pos, 4);
                var size = BitConverter.ToInt32(bytes, pos + 4);
                if (size < 0 || pos + 8 + size > bytes.Length)
                {
                    // data 块大小可以不精确，容错处理
                    size = Math.Min(Math.Max(size, 0), bytes.Length - pos - 8);
                }

                if (id == "fmt ")
                {
                    fmtPos = pos + 8;
                    fmtSize = size;
                }
                else if (id == "data")
                {
                    dataPos = pos + 8;
                    dataSize = size;
                }

                pos += 8 + size + (size % 2);
                if (dataPos >= 0 && fmtPos >= 0)
                    break;
            }

            if (fmtPos < 0 || dataPos < 0 || fmtSize < 16)
            {
                note = "缺少 fmt/data 块";
                return null;
            }

            var audioFormat = BitConverter.ToUInt16(bytes, fmtPos);
            var channels = BitConverter.ToUInt16(bytes, fmtPos + 2);
            var sampleRate = BitConverter.ToInt32(bytes, fmtPos + 4);
            var bitsPerSample = BitConverter.ToUInt16(bytes, fmtPos + 14);

            if (channels is < 1 or > 2)
            {
                note = $"声道数不支持：{channels}";
                return null;
            }

            var bytesPerSample = bitsPerSample / 8;
            if (bytesPerSample is < 2 or > 4 || sampleRate <= 0)
            {
                note = $"位深/采样率不支持：{bitsPerSample}bit @{sampleRate}Hz";
                return null;
            }

            var frameBytes = bytesPerSample * channels;
            var frameCount = dataSize / frameBytes;
            if (frameCount <= 0)
            {
                note = "data 块为空";
                return null;
            }

            var mono = new float[frameCount];
            for (var i = 0; i < frameCount; i++)
            {
                var o = dataPos + i * frameBytes;
                double sum = 0;
                for (var c = 0; c < channels; c++)
                    sum += ReadSample(bytes, o + c * bytesPerSample, audioFormat, bitsPerSample);
                mono[i] = (float)(sum / channels);
            }

            var result = sampleRate == TargetSampleRate ? mono : Resample(mono, sampleRate, TargetSampleRate);
            note = $"{sampleRate}Hz/{channels}ch/{bitsPerSample}bit → 48kHz 单声道，{result.Length / (double)TargetSampleRate:F2}s";
            return result;
        }
        catch (Exception ex)
        {
            note = $"读取异常：{ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    /// <summary>按格式读一个采样点并归一化到 -1..1。</summary>
    private static double ReadSample(byte[] b, int offset, int audioFormat, int bits)
    {
        if (audioFormat == 3 && bits == 32)
            return BitConverter.ToSingle(b, offset);

        return bits switch
        {
            16 => BitConverter.ToInt16(b, offset) / 32768.0,
            24 => ((b[offset] | (b[offset + 1] << 8) | (b[offset + 2] << 16)) << 8 >> 8) / 8388608.0,
            32 => BitConverter.ToInt32(b, offset) / 2147483648.0,
            _ => 0
        };
    }

    /// <summary>线性重采样到目标采样率。</summary>
    private static float[] Resample(float[] src, int srcRate, int dstRate)
    {
        var ratio = (double)dstRate / srcRate;
        var dstLen = (int)Math.Round(src.Length * ratio);
        var dst = new float[dstLen];
        for (var i = 0; i < dstLen; i++)
        {
            var srcPos = i / ratio;
            var i0 = (int)srcPos;
            var frac = srcPos - i0;
            var s0 = i0 < src.Length ? src[i0] : 0;
            var s1 = i0 + 1 < src.Length ? src[i0 + 1] : s0;
            dst[i] = (float)(s0 + (s1 - s0) * frac);
        }

        return dst;
    }

    private static bool Matches(byte[] b, int offset, string tag) =>
        offset + tag.Length <= b.Length
        && b[offset] == tag[0] && b[offset + 1] == tag[1] && b[offset + 2] == tag[2] && b[offset + 3] == tag[3];
}

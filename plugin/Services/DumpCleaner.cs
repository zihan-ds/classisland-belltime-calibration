using System;
using System.Globalization;
using System.IO;

namespace BellTimeCalibration.Services;

/// <summary>
/// 「无用录音」= 插件配置目录下 <c>Dumps\</c> 里的调试转存 WAV（写入方见
/// <see cref="CalibrationRunner"/> 的 DumpWindowAudio，只在设置页「调试音频转存」开启时才写）。
/// 这里只做两件事：清点它们、删掉**确实无用**的那些。
///
/// 什么算「无用」（v1.0.5 修正）：原先的实现是「整个 Dumps 全清」，结果把离线断言
/// （<c>gate-reconcile-test</c>）逐个点名用到的真实录音也一起删了 —— 那些录音是复跑判据回归的
/// 唯一输入，删掉只能重新攒窗口（而且清空回收站后就再也找不回来）。现在只删**同时**满足两条的文件：
/// <list type="number">
/// <item><description>文件名里的时刻距现在 ≥ <see cref="RetentionDays"/> 天（近期排障还要看）；</description></item>
/// <item><description>不在 <see cref="ProtectedFiles"/> 名单里（离线断言用到的录音，永远不删）。</description></item>
/// </list>
/// 其余一律保留：认不出文件名格式的 WAV 也保留 —— 宁可留着，也不猜着删。
///
/// 边界（安全）：
/// <list type="bullet">
/// <item><description>只碰 <c>Dumps\</c> 目录**下面一层**的 <c>*.wav</c>：不递归子目录、不删非 wav 文件、
/// 更不会碰到模板（<c>Templates\</c>）、样本记录、配置与日志；</description></item>
/// <item><description>不抛异常给界面：单个文件删不掉（被占用/权限）只计入失败数，其余照删。</description></item>
/// </list>
/// </summary>
internal static class DumpCleaner
{
    /// <summary>转存目录名（相对插件配置目录）。</summary>
    private const string FolderName = "Dumps";

    /// <summary>保留天数：这个天数以内的调试录音一律不清（排障通常只看最近几天）。</summary>
    public const int RetentionDays = 7;

    /// <summary>
    /// 永久保留的录音：离线断言 <c>gate-reconcile-test</c> 逐个点名用到的真实窗口。
    /// 与 ReplayTool 的用例表必须一致 —— 那边有一条断言核对「每个用例都在本名单里」，
    /// 谁加了用例却忘了加名字，跑一次 gate-reconcile-test 就会红。
    /// </summary>
    public static readonly string[] ProtectedFiles =
    {
        "20260917-085946-上课-B090000.wav",
        "20260918-093948-下课-B094000.wav",
        "20260920-084933-下课-B085000.wav",
        "20260920-093933-下课-B094000.wav",
        "20260920-104932-下课-B105000.wav",
        "20260920-113931-下课-B114000.wav",
        "20260920-143941-下课-B144000.wav",
        "20260920-173941-下课-B174000.wav",
        "20261007-195956-下课-B200000.wav",
        "20261007-200932-上课-B201000.wav",
        "20261007-212932-下课-B213000.wav",
        "20261012-085938-上课-B090000.wav",
    };

    /// <summary>
    /// 清点调试录音：返回「可清除」的数量与体积，以及要保留的数量。
    /// 目录不存在或配置目录未知时返回 (0, 0, 0)。
    /// </summary>
    /// <param name="configFolder">插件配置目录（Logger.ConfigFolder）。</param>
    /// <param name="nowLocal">判定「近期」的基准时刻（默认现在；测试可显式传入）。</param>
    public static (int Deletable, long Bytes, int Kept) Inspect(string? configFolder, DateTime? nowLocal = null)
    {
        var dir = ResolveDirectory(configFolder);
        if (dir == null)
            return (0, 0, 0);

        var now = nowLocal ?? DateTime.Now;
        var deletable = 0;
        var kept = 0;
        long bytes = 0;
        foreach (var file in Directory.GetFiles(dir, "*.wav", SearchOption.TopDirectoryOnly))
        {
            if (!IsDeletable(file, now))
            {
                kept++;
                continue;
            }

            deletable++;
            try { bytes += new FileInfo(file).Length; }
            catch (Exception) { /* 读不到大小只影响体积显示，不影响删除 */ }
        }

        return (deletable, bytes, kept);
    }

    /// <summary>
    /// 删除可清除的调试录音（保留 <see cref="RetentionDays"/> 天以内与 <see cref="ProtectedFiles"/>）。
    /// 目录不存在时返回 (0, 0, 0, 0)，可重复调用（幂等）。
    /// </summary>
    /// <param name="configFolder">插件配置目录（Logger.ConfigFolder）。</param>
    /// <param name="nowLocal">判定「近期」的基准时刻（默认现在；测试可显式传入）。</param>
    /// <returns>已删除数、释放字节数、失败数、保留数。</returns>
    public static (int Deleted, long FreedBytes, int Failed, int Kept) Clear(string? configFolder, DateTime? nowLocal = null)
    {
        var dir = ResolveDirectory(configFolder);
        if (dir == null)
            return (0, 0, 0, 0);

        var now = nowLocal ?? DateTime.Now;
        var deleted = 0;
        var failed = 0;
        var kept = 0;
        long freed = 0;
        // 先取全量清单再删：边枚举边删在 Windows 上要看目录缓存脸色，文件量不大，列表化最稳。
        foreach (var file in Directory.GetFiles(dir, "*.wav", SearchOption.TopDirectoryOnly))
        {
            if (!IsDeletable(file, now))
            {
                kept++;
                continue;
            }

            try
            {
                var length = new FileInfo(file).Length;
                File.Delete(file);
                deleted++;
                freed += length;
            }
            catch (Exception)
            {
                failed++;
            }
        }

        return (deleted, freed, failed, kept);
    }

    /// <summary>
    /// 一个文件是否可以清除：受保护名单里的、太新的、以及文件名认不出时刻的一律不可以。
    /// </summary>
    private static bool IsDeletable(string path, DateTime nowLocal)
    {
        var name = Path.GetFileName(path);
        if (Array.IndexOf(ProtectedFiles, name) >= 0)
            return false;

        if (!TryParseDumpTime(name, out var dumpedAt))
            return false;   // 认不出命名格式就不猜着删

        return (nowLocal - dumpedAt).TotalDays >= RetentionDays;
    }

    /// <summary>
    /// 从转存文件名里取时刻。命名格式见 <c>CalibrationRunner.DumpWindowAudio</c>：
    /// <c>yyyyMMdd-HHmmss-上课|下课-BHHmmss.wav</c>（本地墙钟）。
    /// </summary>
    private static bool TryParseDumpTime(string name, out DateTime dumpedAt)
    {
        dumpedAt = default;
        if (name.Length < 15)
            return false;

        return DateTime.TryParseExact(name.Substring(0, 15), "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal, out dumpedAt);
    }

    /// <summary>把插件配置目录解析成转存目录；目录不存在（或配置目录未知）返回 null。</summary>
    private static string? ResolveDirectory(string? configFolder)
    {
        if (string.IsNullOrWhiteSpace(configFolder))
            return null;

        var dir = Path.Combine(configFolder, FolderName);
        return Directory.Exists(dir) ? dir : null;
    }
}

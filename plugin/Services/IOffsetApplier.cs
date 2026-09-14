using System;

namespace BellTimeCalibration.Services;

/// <summary>
/// 校准偏移应用通道接口。
/// v0.4.0 起唯一实现为内核通道 <see cref="KernelOffsetApplier"/>：correction 为<b>绝对偏移秒数</b>，
/// 即 delta_abs = B_display − t_ring，直接写入内核时间偏移属性（写入后课表显示时钟整体对齐墙钟）。
/// 注：历史版本曾存在「课表平移」通道（ProfileShiftOffsetApplier，增量平移课表时间点），
/// 因实机与宿主档案编辑器冲突、违背用户「禁止通过调整时间表调整偏移」的要求，
/// 已在 v0.4.0 彻底移除——任何情况下插件都不再修改课表档案。
/// </summary>
public interface IOffsetApplier
{
    /// <summary>通道显示名（中文，用于日志）。</summary>
    string Name { get; }

    /// <summary>该通道当前是否可用（取决于反射探测是否命中宿主内核可写偏移 API）。</summary>
    bool IsAvailable { get; }

    /// <summary>通道状态说明（探测结果/不可用原因），供日志与将来设置页展示。</summary>
    string StatusMessage { get; }

    /// <summary>
    /// 当前生效的绝对偏移秒数（v0.6.0 起供残差门控使用）；读取失败/通道不支持读取时为 null。
    /// </summary>
    double? CurrentOffsetSeconds { get; }

    /// <summary>
    /// 应用一次校正。
    /// </summary>
    /// <param name="correction">校正量：绝对偏移秒数（delta_abs 最新值）。</param>
    /// <returns>应用是否成功（失败已自行记录日志）。</returns>
    bool Apply(TimeSpan correction);
}

# 铃声自动校时（BellTime Calibration）

监听校园广播铃声、自动把 [ClassIsland](https://github.com/ClassIsland/ClassIsland) 的
**应用设置 → 时钟 → 时间偏移**校准到学校广播铃钟的 2.x 插件。

> **目标**：铃响时刻 ClassIsland 的时间误差尽量小。**不改课表**、**不改内核**（原版内核即可用）。

| 项 | 值 |
|---|---|
| 插件 ID | `belltime.calibration` |
| 版本 | **1.0.1** |
| apiVersion | 2.0.0.0（ClassIsland 2.x） |
| 平台 | Windows（miniaudio/WASAPI，需要可用的默认输入设备） |
| 作者 | zihan_ds（GitHub：[@zihan-ds](https://github.com/zihan-ds)） |
| 许可 | GPL-3.0-only（见 [`LICENSE`](LICENSE)） |

---

## 它解决什么问题

学校铃声不一定和电脑时钟对齐，而 ClassIsland 依据本机时间切换上下课。
本插件在每个课表边界附近短暂开麦，**听取真实校铃的起响时刻**，据此推算 ClassIsland 应有的时间偏移，
让课表切换与实际铃声对齐——校准语义为「跟随学校广播铃钟」。

安全默认：装好后默认处于**学习模式**，只记录识别结果、不写任何设置。观察几天、核对日志中误差合理后再手动开启自动应用。

---

## 工作原理

```
课表边界前 20s 布防 → 开麦捕获 40s（仅内存）
   ↓
起响沿闸门：边界 ±2s 内找「起响后 0.5s 峰值 RMS ÷ 起响前 0.3s 基线 RMS」最大处   ← 主判据
   ↓（未命中时）
模板匹配：NCC + 音色阈值 + 攻击沿校验                                          ← 辅助判据
   ↓（两者都未命中）
记 no-trusted-match，本次作废（**不回退启发式**）
   ↓
e = t_ring − t_switch（铃响墙钟 − 实际切换墙钟）
   ↓
偏移估计 = 近期样本中位数（趋势外推仅在门槛满足时启用）
   ↓
死区门控 → 写入 Settings.TimeOffsetSeconds
```

### 三条判据为什么长这样（均来自实机数据）

1. **起响沿闸门（主判据）**
   精确波形模板在本场景**不可用**：同一个「下课铃」在各边界的波形互相关只有 0.08~0.64，
   实录段当模板也只命中自己的 dump。但「铃声是突然起响」这一物理事实很稳定——
   真铃的起响比值 4~346×，而连续噪声里的伪匹配只有 1~3×。
   同时，**每个边界附近有两次事件**（主铃在 ±0.6 s 内，另有一次提前 6~15 s 且往往更强），
   ±2 s 的时间门一步把它排除。

2. **模板匹配（辅助）**
   在个别窗口能给出毫秒级起响点（实机最好一次 NCC 0.968），但跨边界不稳定，故降级为辅助，
   且必须同时通过 NCC、音色、攻击沿三道判据。

3. **不回退启发式**
   旧版的「最响持续段」启发式已被实测证伪（误差 −7.8 / −6.2 / +8.1 / +4.9 s）。
   现在只有两条测量路径，两条都不中即判「无可信测量」——**宁可不校准，也不用脏值**。

### 防御性设计（都对应一次真实故障）

| 机制 | 触发案例 |
|---|---|
| 静音窗口守卫（绝对电平下限） | 麦克风整窗未进音时，噪声除噪声也能凑出 6.2× 的假起响沿 |
| 整窗静默在入口作废（`silent-window`） | 同上的窗口曾以 `detected` 写进历史，污染统计与拟合 |
| 大误差复核（变更 > ±3 s 拒写） | 防止校铃钟被人工大规模校准/时钟跳变时静默改掉几秒 |
| 中位数估计替代趋势外推 | 从 ±1.5 s 的噪声里估斜率并外推，会让偏移在边界间震荡 ±1.5 s |
| 放学校铃布防回退 | 当天最后一节之后内核 `OnBreakingTimeLeftTime` 恒为 0，导致放学边界从不布防 |

---

## 目录结构

```
plugin/                       插件源码（.NET 8）
├─ Plugin.cs                  入口：日志/配置/模板加载、设置页注册
├─ manifest.yml               id=belltime.calibration
├─ Models/                    设置项
├─ Services/
│  ├─ CalibrationScheduler.cs 布防：边界前 N 秒入窗（含放学校铃回退）
│  ├─ MicCaptureService.cs    窗口捕获（SoundFlow/miniaudio，48k 单声道）
│  ├─ CaptureAudio.cs         内存音频缓冲 + 采样↔墙钟映射
│  ├─ OnsetGate.cs            起响沿闸门（主判据）
│  ├─ RingDetector.cs         突发起响点检测
│  ├─ TemplateMatcher.cs      模板匹配（粗定位→精定位→音色→攻击沿）
│  ├─ DriftFitter.cs          偏移估计（中位数 + 受门槛约束的趋势）
│  ├─ OffsetSampleStore.cs    偏移样本按天落盘（重启恢复当天序列，供离线分析）
│  ├─ CorrectionPolicy.cs     死区门控
│  ├─ SettingsOffsetApplier.cs 写「应用设置·时间偏移」（原版内核可用）
│  └─ ...
├─ Settings/                  插件设置页（Avalonia）
├─ Templates/                 铃声模板（随包为**静音占位**，请替换为你自己的录音，见下文）
└─ tools/
   ├─ TemplateTool/           离线工具：scan 事件定位 / extract 截模板 / selftest / match / probe / xcorr
   └─ ReplayTool/             回放工具：对实机 dump 重跑插件完整判定链；fitter 子命令可离线验证估计算法

docs/HANDOFF.md               开发交接记录（含完整调试历程与实测数据）
tools-harvest.ps1             批量收割实机铃声段并互为对照
```

> **本仓库不含任何录音。** 铃声样本是学校特有的，必须由使用者自行录制提供——见下一节。

---

## 提供你自己的铃声样本（必做）

插件的**主判据「起响沿闸门」不依赖样本**，装好即可工作；样本只用于辅助的模板匹配路径
（在个别窗口能把起响点精确到毫秒级）。所以样本是**可选增强**，但强烈建议提供。

### 录制要求

- 用**本机（装 ClassIsland 的那台电脑）的麦克风**录，位置与日常使用一致；
- 在**正在响铃时**录一段，长度几秒到几十秒都行；开头多录 5~10 秒环境声更好（便于自动截取）；
- 保存为 **WAV**（插件自带 WAV 解析器；`.m4a`/`.mp3` 请先用 ffmpeg 转成 WAV）；
- 上课铃、下课铃**各录一段**（上下课铃音色差别很大，别混用同一段）。

### 导入方式（两种，任选）

**A. 设置页导入（推荐）**
1. 打开 ClassIsland「设置 → 插件 → 铃声自动校时 → 模板匹配（铃声样本）」；
2. 在「上课铃样本」/「下课铃样本」行点 **「浏览…」**，选中你录的 WAV；
3. 若你知道铃声响在录音的第几秒，在下面的「截取区间」填 `起始秒-结束秒`（如 `12.0-14.5`）；
   留空则由插件自动取「最响且持续 ≥1.5 s」的段；
4. 选完立即生效（设置页会主动重载模板库，无需重启），日志会打印 `已选择铃声样本：… 模板库已重载（已加载 N 个模板）`。

**B. 直接放文件**
把你截好的 2.5 s 模板放到插件配置目录的 `Templates/` 下，命名含「上课」/「下课」即可
（如 `上课铃.wav`、`下课铃.wav`），重启 ClassIsland。随包的两个同名文件是**静音占位**，
插件会识别并跳过它们（日志：`跳过 上课铃.wav：全零静音（随包占位文件…）`），直接覆盖即可。

### 验证样本是否可用

```powershell
# 用生产的匹配器在录音上自测（同源应接近 1.000）
TemplateTool selftest plugin/Templates/上课铃.wav 你的录音.wav 0.6 0.45 15
```

`tools/README.md` 里有完整的工具用法与实测参考值。

---

## 构建与安装

```powershell
dotnet build plugin/BellTimeCalibration.csproj -c Release     # 输出 plugin/bin/Release/net8.0/
dotnet build plugin/tools/TemplateTool/TemplateTool.csproj -c Release
dotnet build plugin/tools/ReplayTool/ReplayTool.csproj -c Release
```

安装：把 `bin/Release/net8.0/` 下的 `manifest.yml` + `BellTimeCalibration.dll` + `BellTimeCalibration.deps.json`
+ `SoundFlow.dll` + `miniaudio.dll` 复制到 ClassIsland 数据目录的 `Plugins/belltime.calibration/`，重启 ClassIsland。
首次使用需在系统设置中授予 ClassIsland 麦克风权限。

> 部署前必须先停止 ClassIsland 进程，否则 DLL 被占用。

---

## 配置与调参

设置项见 `plugin/README.md`。关键三项：

| 设置 | 默认 | 说明 |
|---|---|---|
| 学习模式 | 开 | 只记录不写；确认稳定后关闭 |
| 自动应用校准偏移 | 关 | 关闭学习模式后才生效 |
| 起响沿闸门参数 | 内建常量 | `OnsetGate.GateSeconds=2.0`、`RatioMin=4.0`、`AbsoluteMinRms≈−50 dBFS` |

日志：

- `<插件配置目录>/Logs/belltimecalibration-<日期>.log`（人类可读）
- `<插件配置目录>/Logs/calibration-history.jsonl`（结构化，每窗口一行，含结局、起响点、误差、门控结果）
- `<插件配置目录>/Logs/offset-samples.jsonl`（**偏移样本序列**，见下）
- `DebugDumpAudio=true` 时窗口音频转存到 `<插件配置目录>/Dumps/`（**默认关闭**，音频平时只在内存）

### 偏移样本序列（`offset-samples.jsonl`）

每个拿到**可信测量**的监听窗口追加一行，记录参与偏移估计的样本：

```json
{"Ts":"2026-09-15 08:10:21.683","RequiredSec":-1.6850,"CurrentSec":-1.3440,"Kind":"上课","B":"08:10:00.001","Applied":true}
```

| 字段 | 含义 |
|---|---|
| `Ts` | 样本时刻（本地墙钟，取铃响起响点） |
| `RequiredSec` | 本次测得的「让误差归零所需绝对偏移」（秒） |
| `CurrentSec` | 测量时的当前偏移（秒）——`RequiredSec` 与它的差就是本次实际改动量 |
| `Kind` / `B` | 边界类型（上课/下课）与课表显示时刻 |
| `Applied` | 本次是否真的写入了偏移（带内不写、大误差拒写时为 `false`） |

用途有两个：

1. **重启恢复**：插件启动后首次布防时会**只载入当天**的样本喂给估计器，
   避免进程重启后前几个边界退回「单次测量直接写入」；跨天样本不载入（基准可能已被人为校准改变）。
   日志会写明 `偏移样本恢复：载入当天样本 N 个（跳过其它日期 M 个）`。
2. **离线分析**：可直接画偏移随时间的变化（`Ts` vs `RequiredSec`），
   或用 `ReplayTool fitter <样本序列文件>` 离线复算不同估计策略的稳定性。

---

## 已知限制

- **校铃自身抖动是误差下限**：实测同一批边界「所需偏移」跨度可达 ±1.5 s，
  因此单一偏移量无法让每个边界都落进 ±0.5 s；本插件的目标是**中位误差最小且误差不累积**。
- **吵闹窗口不校准**：噪声底高于约 −25 dBFS 时闸门命中率明显下降，这些边界记为「无可信测量」并跳过。
- **一天内不同时段的最优偏移可能不同**（实测上午与晚上差 3 s 以上），
  因此估计器用 6 小时窗口，并只在残差足够小（<0.3 s）时才启用趋势外推。
- 需要麦克风；无设备/无权限时优雅跳过并记日志，不影响 ClassIsland 其它功能。

---

## 许可

Copyright (C) 2026 zihan_ds

本插件以 **GNU General Public License v3.0（仅 v3，SPDX：`GPL-3.0-only`）** 发布，
完整条款见 [`LICENSE`](LICENSE)；宿主 [ClassIsland](https://github.com/ClassIsland/ClassIsland)
同样以 GPL-3.0 授权，两者兼容。

你可以自由使用、修改、再分发本插件；再分发（含修改版）时须同样以 GPL-3.0 授权、
提供完整源码并保留版权声明。本插件按「现状」提供，不含任何担保。

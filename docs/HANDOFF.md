> 本文件是**开发过程记录**（会话交接日志），包含当时的调试历程、实测数据与结论，其中的本机路径已替换为占位符。
> 使用者向的说明（怎么装、怎么配、每个设置项干什么）请看仓库根目录 `README.md` 与 `plugin/README.md`；
> 每个版本的改动清单在根目录 `CHANGELOG.md`（v1.0.5.0 起逐版一节；更早版本的说明文件已删，见对应 tag 的提交信息）。
>
> **文中的 `samples\`、`Dumps\`、`Templates\*.wav` 等录音均不随仓库分发**：铃声样本由使用者自行录制导入；
> `Dumps\` 是插件在开启「调试音频转存」时对本机窗口音频的临时转存（默认关闭，音频平时只在内存）。
> 出现这些路径处，指的是开发机上当时存在的本地文件。

---

# 铃声自动校时（BellTimeCalibration）· 会话交接日志

> 插件版本 **1.2.0.0**（四段，与发布 tag 一致） · 交接目标：让新会话能**零上下文**接手继续开发与调参。

---

## 0. 交接速读（30 秒）

| 项 | 状态 |
|---|---|
| 实机版本 | **v1.1.2.0**（2026-10-27 10:34:53 部署）：已覆盖到 `<ClassIsland 便携版根目录>\data\Plugins\belltime.calibration\`，启动横幅确认 `启动 铃声自动校时 v1.1.2.0 就绪｜偏移通道 应用设置·时间偏移（当前 6.300s）`；`BellTimeCalibration.dll` 194560 字节，旧 DLL 备份为 `.bak-v1.1.1.1`。上一版 v1.1.1.1（2026-10-22 12:23 的纯元数据版：只覆盖 `manifest.yml` + `README.md`，DLL 与 v1.1.1.0 字节一致）已被本次覆盖。**当前工作区已是 v1.2.0.0，且已于 2026-10-29 20:24:31 部署实机**：启动横幅确认 `启动 铃声自动校时 v1.2.0.0 就绪｜偏移通道 应用设置·时间偏移（当前 1.870s）`；`BellTimeCalibration.dll` 197120 字节（MD5 `de45a85cdd4e06fcf32cd3be984e511b`），旧 DLL 备份为 `.bak-v1.1.2.0` |
| 工作模式 | 非学习模式、**自动应用已开启**、当前偏移 `1.870 s`（2026-10-29 20:24 启动横幅读数；v1.1.2.0 于 2026-10-27 10:34 时为 6.300 s） |
| 仓库 HEAD | `b6a6e73`（v1.1.2.0 提交，2026-10-27）；**v1.2.0.0 尚未提交**——工作区改动 = `plugin/Services/` 下 `OnsetGate.cs` / `TemplateMatcher.cs` / `RingCandidateSelector.cs` / `DriftFitter.cs` / `CalibrationRunner.cs` + `plugin/tools/ReplayTool/Program.cs`（`bell-match-test` 第②项上限收紧、`estimate-test` ③c 新增 5 条）+ `plugin/manifest.yml`（`version: 1.2.0.0`）+ 文档；四段 tag `1.2.0.0` 未打、未推送（等用户指令） |
| 本地 `.cipx` | `plugin/cipx/BellTimeCalibration.cipx`（**v1.2.0.0**：806547 字节，MD5 `8806E6107F6EB2E946B008290DA31A8D`，包内 manifest `version: 1.2.0.0`；上一版 v1.1.2.0 是 805169 字节 / MD5 `B0A9CB06952D3CD4ED23545EBE6EF127`）；`plugin/cipx/` 已在 `.gitignore`，本地打包产物不入库，CI 打 tag 时会重建 |
| 远程 / 推送 | **已推送**：`origin = https://github.com/zihan-ds/classisland-belltime-calibration.git`（公开），`main` 已跟踪 `origin/main`，2026-10-15 22:13 推送 `720dbbd..ee1974a`（普通快进，未用 force）；2026-10-22 18:04 再次推送 `1bb0edb..本提交`（普通快进，含 v1.1.1.0 两个提交 + v1.1.1.1 一个提交；本机直连 `github.com` 被重置，本次经 `ssh.github.com:443` + 临时写权限 deploy key，推送后 key 已删除）；2026-10-22 20:05 打四段 tag `1.1.1.1`（指向本提交）由 CI 发 Release。推送记录见 `.github/workflows/release.yml` 与 git log。**v1.1.2.0 已于 2026-10-27 提交（`b6a6e73`）并部署；v1.2.0.0 尚未提交/推送/打 tag**（等用户指令） |
| 最近一次实机发现 | **2026-10-29**：起响沿闸门的「长铃起点回溯 + 攻击沿归位」能越出模板锚点光圈——14:50 上课窗偏早 **3.52 s**、14:40 下课窗偏晚 **4.60 s**，两个窗都因此被判离群拒写；同日下午 5 个离群窗口里 14:40 / 15:30 / 16:20 三个下课窗的铃声**根本不存在**，而日志只会说「模板匹配未命中」。两项已在 v1.2.0.0 修（见 8.10）。更早的 2026-10-26「测量选错事件」四项根因见 8.9（v1.1.2.0 已修），2026-10-15 两处判据缺陷见 8.3，均已修并部署 |
| 立刻要做的三件事 | ① 部署 v1.2.0.0 后看头几个自然窗口：锚定闸门的命中点是否**始终落在锚 ±3.0 s**（`bell-match-test` ②已离线验证 0 越界）；② 模板未命中时看说明里的新结论是「疑为未响铃（不是测错）」还是「铃声在，但没过判据」——前者说明该边界本来就没铃，别去查麦克风；③ 决定是否 `git commit` + 推送 + 打 tag `1.2.0.0` |
---

## 1. 目标与硬约束

**目标**：插件在课表边界开麦监听校园铃声，通过**多次测量拟合**把 ClassIsland 的
**「应用设置 → 时钟 → 时间偏移」**（内核设置项 `Settings.TimeOffsetSeconds`）校准到与学校广播铃钟同步。

**硬约束（用户明确要求，逐条都在代码里有对应实现）**：

1. **绝不修改课表档案**；
2. **不修改内核代码**——用原版内核即可（早期为插件加的可写偏移属性补丁**已撤回**）；
3. 铃响时刻 ClassIsland 的时间误差 **≤ 0.5 s**；
4. **遇到较大误差时坚决不用脏值**，也不用已被确认不准的办法（启发式选铃因此被删除，见第 5 节）；
5. **合法修正保持单次 3 s 上限，但所需偏移量可能超过 20 s** → 分几个窗口**分步写入**，而不是整笔拒写；
6. 校铃误差**呈一定规律变化**；**不存在按上下课分开拟合**——上下课误差规律不应有大差别，若不一致说明拟合有误；
7. 版本 **v1.2.0.0**，分发 = 「本地 git 提交 + 推送 → 打四段 tag 由 CI 发 Release」，**push / 打 tag 需用户指令**（历史：2026-10-15 已推 `origin/main` 并打 tag `1.1.0.0`；v1.1.1.0 / v1.1.1.1 于 2026-10-22 已推送，v1.1.1.1 已打四段 tag 发 Release，v1.1.1.0 未单独打 tag；v1.1.2.0 已于 2026-10-27 提交（`b6a6e73`）并部署；v1.2.0.0 已改完但**尚未提交、未打 tag**）；
8. 文档（README、设置页提示、更新说明）必须与实际行为一致；
9. 音频（铃声样本、`Dumps\`）**不随仓库分发**。

---

## 2. 环境与两棵树（新会话先读这一节）

| 占位符 | 含义 / 本机实际值 |
|---|---|
| `<ClassIsland 便携版根目录>` | 便携版根；本机 `D:\ClassIsland_app_windows_x64_full_folder` |
| …运行目录 | `<便携版>\app-2.1.0.1-0\ClassIsland.Desktop.exe`（入口是外层的 `ClassIsland.exe`） |
| …数据目录 | `<便携版>\data\` |
| 插件程序集目录 | `<便携版>\data\Plugins\belltime.calibration\`（`BellTimeCalibration.dll`、`SoundFlow.dll`、`miniaudio.dll`、`manifest.yml`、`README.md`） |
| 插件数据目录 | `<便携版>\data\Config\Plugins\belltime.calibration\`（`BellCalibrationSettings.json`、`Templates\`、`Logs\`、`Dumps\`） |
| `<工作树>` | **构建 / 跑离线工具 / 放录音**的那棵；本机 `D:\dshwork\classisland auto calibration`（含 `bell-recordings\`、`samples\`、`tools\`、`deploy-snapshot\`） |
| `<git 树>` | **只放要提交的内容**的那棵；本机 `D:\dshwork\git-classisland auto calibration` |
| `<工作区根>` | 两棵树的父目录；本机 `D:\dshwork`（临时脚本如 `replay-all.ps1` 放在这里） |
| 内核源码 | `<ClassIsland 内核源码>`（插件**不依赖**任何内核改动，工作区干净） |
| 系统校时 | 已执行 `w32tm /config /manualpeerlist:"ntp.aliyun.com,..." /syncfromflags:manual /update` + 服务设为自动；此前为 `Local CMOS Clock` 从未同步 |
| 本机时间基准核对 | 系统钟 vs ntp.aliyun.com = **+0.14 s**；ClassIsland 显示 = NTP + `TimeOffsetSeconds` |

### 2.1 两棵树的工作流（踩过坑，务必按这个顺序）

1. **源码改在 `<git 树>`**（提交用）。要让 `<工作树>` 的构建或离线工具跑到新代码，**必须先把改过的文件复制到 `<工作树>` 再 build**。
   > 踩过的坑：改了 `<git 树>` 的源码，却在 `<工作树>` 跑了旧二进制，于是得出「改动没有生效 / 没有变化」的错误结论。
2. **实机部署取 `<工作树>\plugin\bin\Release\net8.0\` 的产物**（`BellTimeCalibration.dll`、`.deps.json`、`manifest.yml`）。
3. **提交前**：`<git 树>` 下三个项目（`plugin`、`tools\TemplateTool`、`tools\ReplayTool`）都要 Release 构建通过（0 warning / 0 error）。
4. 提交消息写得足够细（现存两个大提交的消息本身就是变更清单）——本仓库的习惯是**提交信息即变更记录**，不额外维护 CHANGELOG 片段。

---

## 3. 运行配置快照

默认值来自 `plugin/Models/BellCalibrationSettings.cs`；「本机当前值」来自实机 `BellCalibrationSettings.json`。

| 设置项 | 默认 | 本机当前 | 作用 |
|---|---|---|---|
| `IsEnabled` | true | true | 总开关：关闭后不监听、不应用（手动拟合除外） |
| `IsLearningMode` | **true** | false | 学习模式：只测不写 |
| `IsAutoApplyEnabled` | false | **true** | 自动应用校准结果（需关闭学习模式） |
| `ArmedLeadSeconds` | 15 | 25 | 布防提前量的**下限**；实际提前量 = `CurrentLead()`（见下） |
| `WindowSeconds` | 8 | 25 | 越界后继续听多久 |
| `DeadZoneSeconds` | 0.3 | 0.3 | 死区：`\|预测偏移 − 当前偏移\| < 此值` 不动作 |
| `LargeErrorLimitSeconds` | 3.0 | 3.0 | **单次写入上限（v1.1.0 起是步进上限，不再是拒写阈值）**；同时是弹复核提醒的阈值，范围钳制 1~120 |
| `EnableReviewNotification` | true | false | 步进写入是否弹提醒（关掉只是不弹，步进照做） |
| `ToleranceSeconds` | 12 | 15 | **模板搜索半窗的下限**；实际取 `max(本项, \|当前偏移\| + 8s)` |
| `DetectionSensitivity` | 2.0 | 2.0 | 突发能量判为「有铃」需超过噪声底的倍数（`RingDetector`） |
| `IgnoredBoundaries` | `""`（空） | `08:00,17:10,18:30` | 忽略的边界时刻（`HH:mm`）；默认空是刻意的——早先默认屏蔽 08:00/18:30 属本校结论，不该当所有人的默认 |
| `EnableTemplateMatch` | true | true | 是否启用模板匹配（关掉**不会**回退启发式选铃——那个已删除） |
| `TemplateDirectory` | `Templates` | `Templates` | 模板目录（相对插件配置目录；**非递归**枚举 `*.wav`） |
| `ClassBellSamplePath` / `BreakBellSamplePath` | `上课铃样本.wav` / `下课铃样本.wav` | `""` | 长录音样本路径；留空则改用模板目录里的模板文件 |
| `MatchNccMin` / `MatchSpectralMin` | 0.60 / 0.45 | 0.60 / 0.45 | 模板匹配阈值 |
| `DebugClockDiagnostic` | false | false | 内核时钟自检（怀疑系统钟/NTP 时开） |
| `DebugDumpAudio` | false | **true** | 调试音频转存（隐私：只有显式开启才落盘） |

**内建常量（不受设置项影响，改行为要改代码）**：

| 常量 | 值 | 含义 |
|---|---|---|
| `OnsetGate.GateSeconds` | 8.0 | **无锚点**时起响沿闸门的半窗（边界 ±） |
| `OnsetGate.AnchorApertureSeconds` | 3.0 | **有模板锚点**时闸门的半窗（锚 ±，v1.1.0 新增） |
| `OnsetGate.RatioMin` | 4.0 | 候选合格起响比值下限（另有「高声压」或条件） |
| `OnsetGate.SegmentLookbackSeconds` / `SegmentDropDb` / `SegmentHoldSeconds` | 8.0 / 12.0 / 1.2 | 长铃起点回溯：最多回看 8 s、低于「选中击峰值 − 12 dB」且持续 1.2 s 才算出段；**有锚点时上限被钳到光圈内**（v1.2.0.0：`lookbackLimit = max(gateFrom, 锚 − 8 s)`，而 `gateFrom` 就是「锚 − 光圈」，回溯不得越出锚点光圈；攻击沿归位的 `attackLimit` 同样被 `gateTo` 钳住，见 8.10 第 1 项） |
| `OnsetGate.AttackWindowSeconds` | 0.05 | 回溯后「起声归位」的短窗长度 |
| `OnsetGate.DominanceDb` / `SameStrengthDb` | 6.0 / 2.0 | 双窗口复核：一侧强出 >6 dB 用那一侧；差距落在 2~6 dB 判冲突 |
| `CalibrationScheduler.CaptureMarginSeconds` | 10.0 | `CurrentLead()` 里给偏移留的余量 |
| `CalibrationScheduler.MaxLeadSeconds` | 60.0 | 提前量上限 |
| `CalibrationRunner.SeedLookbackHours` | 12.0 | 首次布防时恢复**最近 12 小时**的样本（v1.1.0 修，见 8.3）；**进食口径** = `OffsetSampleStore.IsSeedableForRuntime`：旁证与已拒写（`outlier` / `large-error`）一律不进估计器（v1.1.0.0 收严，见 8.5） |
| `DriftFitter.OutlierRejectSeconds` | 2.0 | 与稳健拟合差超过它即判离群（不入列、不清空） |
| `DriftFitter.BimodalGapSeconds` / `BimodalMinPerCluster` | 4.0 / 2 | **双峰拒写**（v1.1.2.0）：样本排序后最大间隔 ≥ 4 s 且两侧各 ≥ 2 个 → 判双峰、拒写并告警（`SampleReason.Bimodal`），判定在离群复核**之前** |
| `DriftFitter.SegmentRestartRequiresTemplate` | true | **基准跳变确认需模板命中**（v1.1.2.0）：两条互相印证还不够，至少一条要来自模板命中（见 8.9 第 4 项） |
| `DriftFitter.SegmentRestartAgreements` / `SegmentRestartAgreementSeconds` | 2 / 1.0 | 连续两条彼此一致（≤1 s）才认定基准真跳变、重开样本段（v1.1.2.0 起另需模板确认） |
| `OnsetGate.PreferPeakOverRatio` | true | 强候选组内按**峰值**择优而非按起响比值（v1.1.2.0，见 8.9 第 2 项） |
| `TemplateMatcher.StrongNcc` / `StrongSpectralMin` | 0.85 / 0.80 | NCC 与音色同时达到即免攻击沿校验、不再改去别处重采（v1.1.2.0，见 8.9 第 1 项） |
| `TemplateMatcher.NoBellNcc` | 0.35 | 模板未命中时的**旁证**门槛（v1.2.0.0）：`PeakNcc`（**未被攻击沿重采改写**的全区间最高 NCC）低于它 → 说明里追加「疑为未响铃（不是测错）」，否则 → 「铃声在，但没过判据」。只写日志与命中说明，**不改变任何写入判据**（见 8.10 第 2 项） |
| `DriftFitter.Seed(..., trimBimodal)` | 默认 false | **启动恢复专用**开关（v1.2.0.0）：为 true 且恢复后 `IsBimodal` 时调私有方法 `TrimOldestCluster()` 按中位时刻丢掉较旧那一簇并重算（并写日志）；`CalibrationRunner.RestoreTodaySamplesOnce()` 传 true，`ManualFitService` 保持默认 false（手动拟合要让用户看到真实双峰，不能被悄悄修剪）（见 8.10 第 3 项） |
| `DriftFitter.TrendMinSamples` / `TrendMinSpanHours` / `TrendMaxResidualSeconds` / `MaxExtrapolationHours` / `MaxSlopeSecondsPerHour` | 6 / 4 / 0.6 / 24 / 2.0 | 趋势外推的三项门槛 + 外推上限 + 斜率钳制 |

`CurrentLead()`（`plugin/Services/CalibrationScheduler.cs:35`）：
`clamp(max(配置 ArmedLeadSeconds, |当前偏移| + 10 s), 配置值, 60 s)`——硬超时用同一个函数，不再各写一套。

---

## 4. 单窗口数据流（v1.1.0）

```
边界前 CurrentLead() 秒开麦
   ├─ 音频回调：RMS 持续段检测（RingDetector）+ 音频缓冲写 + 采样↔墙钟锚点
   └─ 窗内冻结内核偏移：所有与音频比较的量统一到「裸墙钟域」
窗口结束（越界 / 硬超时）
   ↓
① 模板匹配（每个保留下音频的窗口**先**跑；命中即给出模板锚点）
   ↓
② 起响沿闸门（OnsetGate.Find）
     有锚点 → 扫描区间 = 锚 ± 3.0 s
     无锚点 → 扫描区间 = 边界 ± 8.0 s（= v1.0.5 行为）
     命中后：长铃起点回溯（≤8 s，**有锚点时不得越出光圈**，v1.2.0.0）→ 攻击沿归位（同样被钳在光圈内）→ 得到 t_ring
   ↓
③ 双窗口复核：同一判据再扫一遍「以标称边界为中心」的窗口
     一致 / 只有开关窗命中 → 照旧
     一侧强出 >6 dB → 用那一侧的坐标（对称判据）
     只有课表窗命中 / 差距 2~6 dB → 不写偏移，落**旁证样本**（默认不参与拟合）
   ↓
④ e = t_ring − t_switch  →  所需偏移 = 当前偏移 − e
   ↓
⑤ DriftFitter.Add(测量, templateConfirmed)  →  PredictedSeconds(now)  →  diff = 预测 − 当前
   ↓
⑥ 写入决策链（**顺序即优先级**，v1.1.0 修过顺序，v1.1.2.0 在最前加了双峰一步）：
     0. 双峰？（样本最大间隔 ≥ 4 s 且两侧各 ≥ 2 个）→ 拒写，样本 `bimodal`（v1.1.2.0，见 8.9 第 3 项）
     1. 离群？（|测量 − 稳健拟合| > 2.0 s）→ 拒写，样本 `outlier`（默认不勾）
     2. 死区？（|diff| < 0.3 s）        → 不写，样本 `dead-zone`
     3. 大差值？(|diff| > 3 s)          → **步进**写入 ±3 s，样本 `stepped`（默认勾）+ 弹复核提醒
     4. 否则                            → 写入，样本 `applied`
   ↓
⑦ SettingsOffsetApplier 写「应用设置 · 时钟 · 时间偏移」（原版内核可用）
   ↓
⑧ 日志 + Logs/offset-samples.jsonl（结构化样本，含 Applied 与 Reason）
```

---

## 5. 代码结构（`plugin\`）

```
plugin/
├─ manifest.yml                     id=belltime.calibration，apiVersion 2.0.0.0，url=https://github.com/zihan-ds/classisland-belltime-calibration，version 1.2.0.0
├─ Plugin.cs                        入口：日志/配置加载与自动保存、模板加载、设置页注册、AppStarted 自举
├─ Models/BellCalibrationSettings.cs 全部设置项 + 忽略列表解析（IsBoundaryIgnored）
├─ Services/
│  ├─ Logger.cs                     文件日志（InfoIfChanged 用于「只在变化时打印」的行）
│  ├─ CalibrationScheduler.cs       布防状态机：CurrentLead() 自适应提前量；发 BoundaryApproaching/Reached；忽略列表
│  ├─ MicCaptureService.cs          窗口捕获（SoundFlow/miniaudio，48k 单声道 F32）
│  ├─ CaptureAudio.cs               内存音频缓冲 + 采样索引↔墙钟双向映射 + SaveWav（仅调试转存）
│  ├─ RingDetector.cs               噪声底「响铃期冻结」+ 持续段检测
│  ├─ OnsetGate.cs                  **主判据**：起响沿闸门 + 长铃起点回溯 + 攻击沿归位 + 双窗口复核的复用件
│  ├─ TemplateMatcher.cs            模板匹配：包络粗定位 → 4kHz 精定位(0.25ms) → 攻击沿优先重采 → 音色确认 → 攻击沿校验
│  ├─ TemplateLibrary.cs            启动加载「上课铃/下课铃」模板（非递归；静音占位会被识别并跳过）
│  ├─ BellTemplate.cs               模板对象：10ms 能量包络 + 32 点 dB 域音色向量
│  ├─ RingCandidateSelector.cs      仅剩模板选段（启发式 SelectBell 已删除，原处保留「为何移除」说明）
│  ├─ CalibrationRunner.cs          **窗口编排**：模板锚点 → 闸门 → 双窗口复核 → 拟合 → 判据链 → 写偏移 → 落样本
│  ├─ DriftFitter.cs                稳健拟合：中位数 + 离群挂起（不清空）+ 连续两条一致才重开段 + 趋势三项门槛
│  ├─ CorrectionPolicy.cs           死区门控 + StepTowards（步进限幅，纯函数）
│  ├─ OffsetSampleStore.cs          样本读写：LoadSince/LoadToday、Summarize、旁挂勾选（sample-exclusions.jsonl）
│  ├─ ManualFitService.cs           「手动拟合并应用」：当天有效样本 → 重算 → 立即写入 → Seed 回运行时
│  ├─ BellReviewDecision.cs         复核提醒的判定与文案（步进 / 离群 / 冲突的措辞都在这里）
│  ├─ BellReviewNotifier.cs         复核提醒发送（走宿主提醒主机服务，公开接口 + 反射调 internal 发送方法）
│  ├─ CalibrationHistory.cs         结构化 JSONL（协议 v1，含 MatchedTemplate/MatchNcc/MatchSpectral）
│  ├─ ClockDiagnostic.cs            内核时钟自检（DebugClockDiagnostic）
│  ├─ DumpCleaner.cs                「清除无用录音」：7 天内保留 + 夹具名单保留 + 认不出名字保留
│  ├─ SettingsOffsetApplier.cs      写「应用设置·时钟·时间偏移」
│  ├─ IOffsetApplier.cs             应用通道接口（含 CurrentOffsetSeconds 读取）
│  └─ WavReader.cs                  零依赖 WAV 解析（PCM16/24/32、float32、单双声道、重采样到 48k 单声道）
├─ Settings/
│  ├─ BellCalibrationSettingsPage.axaml(.cs)      设置页（监听参数 / 模板匹配 / 样本管理 / 手动拟合 / 清除录音）
│  └─ BellReviewProviderSettingsControl.axaml(.cs) 提醒提供方的设置控件（发送测试提醒）
├─ Templates/{上课铃,下课铃}.wav   2 个模板（各 2.5s、48k 单声道 PCM16）；**仓库内是静音占位**，
│                                 使用者录自己的样本后经设置页导入；旧模板目录 `Templates-bak-*` 留在同级会被当第 3、4 个模板（枚举非递归）
└─ tools/
   ├─ README.md                    工具用法 + 历史实测数据（事件定位表、匹配矩阵、验收表）
   ├─ TemplateTool/                scan / extract / selftest / match / probe / xcorr
   └─ ReplayTool/                  对已采集音频重跑插件完整判定链 + 一批离线断言（见第 7 节）
```

---

## 6. 关键设计取舍（为什么长这样）

每一条都是被一次真实故障逼出来的。

| 设计取舍 | 触发原因（真实故障） |
|---|---|
| 只写「应用设置·时钟·时间偏移」，**彻底不做课表平移** | 平移会污染用户课表档案 |
| **起响沿闸门**为主判据，精确波形模板只作辅助/锚点 | 10 个实机 dump 证明精确波形模板跨边界 0 命中；且同一「下课铃」跨边界互相关只有 0.08~0.64（v1.1.0 全量回放：模板命中率 37%，且命中点落在铃段**内部**、比可听第一击晚 1.15~1.9 s） |
| **模板只给锚点，不给坐标**（v1.1.0） | 模板对齐点在铃段内部，直接采信会把 1~2 s 偏差写进偏移；锚点只把闸门扫描区间从 ±8 s 收窄到 ±3 s，具体起声仍由回溯 + 归位定 |
| 删除「启发式选铃」回退路径 | 它只能回答「哪个段最响」，实测误差 −7.8 / −6.2 / +8.1 / +4.9 s |
| 测量口径取 `e = t_ring − t_switch` | 旧的「残差」口径看不到陈旧偏移，符号交替时永不动作（实机曾长期挂着 4.6 s 误差） |
| 窗内**冻结内核偏移**，音频相关量统一到裸墙钟域 | 时间戳取自系统钟、边界取自内核时钟（含偏移），直接相减会整体错位一个偏移量 |
| 整窗静默在入口作废 | 麦克风未进音时「噪声除噪声」也能凑出假的起响比值，且该结果曾以 `detected` 写进历史 |
| 偏移估计用**近期样本中位数**，趋势外推三门槛 | 从 ±1.5 s 的测量噪声里估斜率并外推，会让偏移在相邻边界间震荡 ±1.5 s |
| **离群拒写**（v1.1.0）：测量本身与稳健拟合差 >2.0 s 即不入列、不清空样本段 | 早先「与基准差 >2.5 s 就 `_samples.Clear()`」→ 偏移被坏值拽跑后每窗退化成「样本1/离散0.000」的单样本直写，中位数/趋势/离散度全失效（2026-10-14 几乎整天如此） |
| **单次 3 s 改为分步写入**（v1.1.0，取代「大误差一律拒写」） | 10-14 一天 8 条 large-error 拒写，逐条回放后多数是**被一条坏值钉住的干净测量**：坏值把偏移拽跑，之后的正确测量相对当前偏移就差出几秒，一次真实的大校准永远等不到人工确认 |
| **布防提前量与搜索半窗随当前偏移自适应**（v1.1.0） | 真铃落在 `B − 真偏移`；偏移一旦漂出提前量/半窗，窗口里只剩噪声，**永远拿不到能纠正它的测量**——10-12~10-14 的 ±4~8 s 漂移就是这么被放大的 |
| 大误差复核提醒（v1.0.4 起，v1.1.0 改为步进提醒） | 校铃钟被人工大规模校准或系统钟跳变时，不应被静默改掉几秒 |
| 双窗口复核 + 对称支配判据（v1.0.5） | 偏移被改到别处时，开关窗口会锚在杂音上、只剩几个 20 ms 单点瞬态，而课表窗口抓到的是真铃、强出一大截（两天里 3 个窗口差 8.4 / 12.0 / 16.9 dB） |
| 长铃起点回溯 + 起声归位（v1.0.5） | 本校钟声是约 15 s 的多击长铃，选中的那一击常常不是第一击（差 1~4 s）；回溯还会停在起声**之前**的静音里（实测早 0.39 s） |
| 闸门半窗 2 s → 8 s（v1.0.5） | 半窗窄于偏移自身的误差 = 自我锁死：偏移偏出 2 s 后真铃在窗外，闸门抓窗内 −40 dB 房间噪声当选，算出来还像一次正常的小修正 |
| 样本落盘带 `Reason` 字段（v1.0.5） | 上一版用几何关系**反推**原因，10-07 有 6 条被标「大误差拒写」的样本一条都不是（实际是预测落死区）。倒推一个错误的原因比不显示更坏 |
| 旁证样本（v1.0.5）：判据不采信的窗口也落一条、默认不参与 | 否则使用者看到的是「样本全废」；落旁证既不污染估计器、也不让人「什么都看不到」 |
| 越界事件在主计时器 tick 上兜底补发（v1.0.5） | 状态切换事件与 tick 抢跑时旧代码会先清掉 armed，`BoundaryReached` 永不触发、窗口录到硬超时（55.03 s） |
| 装载样本是**替换**而不是追加（v1.0.5） | `Seed()` 只加不清 → 15:40 的 11 个样本到 16:20 变成 23 个 |
| 布防恢复窗口 12 h（v1.1.0） | 只恢复「当天」时，每天早上第一个窗口序列为空 → 当天第一条样本自己当基准（`样本1/离散0.000`）并被直写 |
| **判据顺序：离群 → 死区 → 步进 → 写入**（v1.1.0） | 死区分支原先在离群之前 return，导致已被拟合器判离群的测量落盘成 `dead-zone`，而按既有口径「死区带内默认勾选」→ 设置页把脏值显示成干净样本并打勾 |
| 噪声底只在安静期更新；同簇合并（连击铃算一次） | 旧实现把铃声持续段中段当成起响点（偏晚 2.3 s）；连击铃被时长门槛整段滤掉 |

---

## 7. 离线工具与断言（复用生产代码，不随插件打包）

### 7.1 ReplayTool

```powershell
$tool = '<工作树>\plugin\tools\ReplayTool\bin\Release\net8.0\ReplayTool.exe'
& $tool                     # 根命令：对 <工作树>\samples\ 或指定音频跑完整判定链
& $tool estimate-test       # 步进写入的方向/边界/限幅、离群不入列也不清空样本段、连续两条一致才重开段、趋势门槛；
                           # v1.1.2.0 新增 8 条：双峰判定、跳变确认需模板、单声源散布不误判、孤立脏值不误判
                           # v1.2.0.0 ③c 再加 5 条：启动恢复修剪旧簇（双峰解除、保留较新 2 个、中位数回到 7.598 s、日志可读、默认 false 时 4 个原样保留）
& $tool absolute-required-test   # 4 条退化口径
& $tool notify-test         # 7 条提醒判定
& $tool sample-manage-test  # 43 条样本管理（含跨天恢复、当时偏移列口径、采用偏移预览、两位小数、已拒写样本不得回灌估计器）
& $tool dumps-clear-test    # 20 条「清除无用录音」删除/保留判据
& $tool gate-reconcile-test # 1 条真实窗口 + 6 条合成分支 + 11 条夹具已丢失（打印「跳过」，只有真回归才失败）
& $tool gate-guard          # 全量 dump：没有任何命中依赖「除以数字静默」的比值
& $tool bell-match-test "<Dumps 目录>" "<模板目录>"   # v1.1.2.0：强匹配被攻击沿误拒次数、锚定闸门抓错事件数、相邻窗口差 >2.5s 的比例；v1.2.0.0 第②项上限由「光圈+回溯窗=11.0 s」收紧到「光圈+0.05 s」
& $tool fitter / manual-fit / envelope / gate-scan / gate-reconcile   # 诊断类
```

**根命令（v1.1.0 改过）**：先跑模板匹配，再**并排打印**「老行为闸门（边界 ±8.0 s）」与「v1.1.0 锚定闸门（锚 ±3.0 s）」两套结果；模板未命中时明示「与老行为完全一致」。
回放批次脚本在本机 `<工作区根>\replay-all.ps1`，输出表在 `replay-all.txt`；dump 文件名形如 `20261014-080937-上课-B081000.wav`，`边界在文件内秒数 = B(文件名里的 HHMMSS) − 文件起点`。

### 7.2 TemplateTool

```powershell
& $tool scan     "<dump.wav>"                                  # 铃响在哪几秒、是否被环境瞬态污染
& $tool selftest "<模板.wav>" "<dump.wav>" 0.6 0.45            # 模板 vs 实机音频的 NCC/音色
& $tool extract  "<录音.wav>" "<输出模板.wav>" <起>-<止>       # 截模板（**别用自动截取**：会截到环境噪声）
& $tool match / probe / xcorr                                  # 限定区间匹配 / 攻击沿逐点 / 纯波形互相关
```

---

## 8. 已验证 / 未验证

### 8.1 已验证 ✅（离线，全部返回 0）

| 断言 | 规模 |
|---|---|
| `estimate-test` | 步进方向/边界/限幅、离群挂起、连续两条一致才重开段、趋势门槛；**v1.1.2.0 加 8 条**（双峰判定、跳变确认需模板、单声源散布不误判、孤立脏值不误判）；**v1.2.0.0 再加 5 条**（③c 启动恢复修剪旧簇：双峰解除、保留较新 2 个、中位数回到 7.598 s、日志可读、默认 false 时 4 个原样保留） |
| `bell-match-test` | **353 个 dump / 224 个模板命中窗口**：强匹配被攻击沿误拒 **0** 次、锚定闸门抓错事件 **0** 个（命中点越出锚点光圈 **0** 个，上限已由「光圈+回溯窗=11.0 s」收紧到「光圈+0.05 s」）、相邻窗口差 >2.5 s 占 **16/204 = 8%**（v1.1.2.0 时的口径是 16/164 = 10%，基数由 182 个窗口涨到 224 个）；模板未命中 **116** 个窗口，其中「疑为未响铃」**26** 个、「铃声在，但没过判据」**90** 个（见 8.9 / 8.10） |
| `absolute-required-test` | 4 条退化口径 |
| `notify-test` | 7 条提醒判定 + 无冷却 + 2 条冲突提醒 |
| `sample-manage-test` | 43 条：默认勾选/人工覆盖/乱行容错 + 跨天恢复 4 条 + 当时偏移列 4 条 + 采用偏移预览/两位小数 6 条 + 进食口径 6 条 |
| `dumps-clear-test` | 20 条删除/保留判据 |
| `gate-reconcile-test` | 1 真实 + 6 合成；11 条夹具录音已丢失 → 打印「跳过」 |
| `gate-guard` | 81 个 dump：命中 72、**0 个依赖数字静默**、命中最大比值 979.0× |
| 三个项目 Release 构建 | 0 warning / 0 error |

### 8.2 v1.1.0 全量回放（2026-10-10 ~ 10-14，75 个窗口）✅

- 模板命中 **28 个（37%）**；老行为闸门命中 72 个；锚定闸门命中 28 个（只有模板命中的窗口才走新路径）。
- 28 个里 **20 个与老行为一致（≤0.05 s）**、1 个差 0.64 s、**7 个被改写（差值 6.7~12.7 s）**。
- 被改写的 7 窗里，老行为给出的偏移是 −2.1~−8.2 s 且方向互相矛盾；锚定后落在 **+2.3~+5.2 s**，与同期其它窗口一致。

**单例（10-14 08:10）**：老行为把窗口里最强的一段当真铃——dump 内 25.06 s（边界 +2.06 s，13.9×），明细写着「向前超过 8.0 s 仍未回落到 12.0 dB 以下，定位不到起点，保持选中击」；模板命中在 19.545 s（NCC 0.856 / 音色 0.893）；锚定后闸门给出 17.995 s（边界 −5.00 s，5.7×，回溯 −1.54 s、归位 +0.01 s）→ 所需偏移 **+5.005 s**，与包络里的真起声（+4.6 s）只差 0.4 s，而老行为差了约 7 s。

### 8.3 2026-10-15 实机发现的两处缺陷（已修，`0f72911`）✅

修复前的当天 8 条样本（`Logs/offset-samples.jsonl`）：

| 时刻 | 类型 | 所需偏移 | 当时当前偏移 | 预测值 | 预测−当前 | 落盘 Reason |
|---|---|---|---|---|---|---|
| 08:10:06.508 | 上课 | −6.456 | 1.438 | −6.456（**样本1**/离散0.000） | −7.894 | `stepped`（写 −1.562） |
| 08:49:56.282 | 下课 | +3.784 | −1.562 | −1.336 | +0.226 | `dead-zone` |
| 08:59:59.301 | 上课 | +0.701 | −1.562 | 0.701 | +2.263 | `applied`（把偏移拉回 0.701） |
| 09:39:54.088 | 下课 | **+5.924** | 0.701 | 0.701（样本 **3**，没涨） | 0.000 | `dead-zone` ← **其实是离群** |
| 10:09:59.310 | 上课 | +0.710 | 0.701 | 0.705 | +0.004 | `dead-zone` |
| 10:50:00.254 | 下课 | −0.250 | 0.701 | 0.701 | 0.000 | `dead-zone` |
| 10:59:59.560 | 上课 | +0.452 | 0.701 | 0.576 | −0.125 | `dead-zone` |
| 11:39:52.672 | 下课 | **+7.334** | 0.701 | 0.576（样本 **6**，没涨） | −0.125 | `dead-zone` ← **其实是离群** |

- **缺陷 ①（判据顺序）**：09:40 与 11:40 **确实被拟合器判成离群了**（样本数停在 3 和 6、离散度被拽到 7.0，`PredictSeconds` 没用它们），但死区分支先 `return`，`Reason` 落盘成 `dead-zone`；按既有口径「死区带内默认参与手动拟合」，设置页把两条脏值显示成干净样本并打勾。
  → 修：**离群判定整块提到死区之前**（与稳健拟合差 >2.0 s 一律记 `outlier`、默认不勾）。
- **缺陷 ②（恢复窗口太短）**：08:10 是当天第一条样本，序列为空 → 它自己成了中位数（`样本1/离散0.000`）并被单样本直写。
  → 修：布防恢复从「当天」改为**最近 12 小时**（`SeedLookbackHours = 12.0`，新增 `OffsetSampleStore.LoadSince`），日志行从 `样本 当天 N 条` 改成 `样本 近12h N 条`。
- 已按用户口径把当天那两条脏值追加进旁挂文件 `sample-exclusions.jsonl`（`Invalid:true`），设置页点「重新载入」即可看到。

**当天「所需偏移」为什么散成 ±7 s**（离线包络复算，非写入策略问题）：8 个窗口模板只命中 2 个；未命中的窗口里边界附近本来就有人声/桌椅的多处瞬态——08:50 最强单点在边界处 −14.6 dB，09:40 最响单点在**边界 −6.4 s**（−12.8 dB），11:40 有持续声响从**边界 −3.6 s** 起（−14.9 dB）。即「每个窗口挑中了不同的那一段声音」。

### 8.4 2026-10-15 晚：样本管理「所需偏移」列改为「当时偏移」（`963ed03`）✅

用户口径：「将『所需偏移』更改为样本记录拟合后输入的偏移量」。改动只有显示层：设置页样本管理第 4 列改取
`OffsetSample.CurrentSec`（= 该次测量当时生效的偏移值，也是使用者手填进设置的那个数），原先取的是
`RequiredSec`（拟合算出来的「所需偏移」）。表头随之改为「当时偏移」、列宽 110→130、列文字与 ReplayTool 的
`manual-fit` 共用 `OffsetSampleStore.InputOffsetLabel`（工具里同时打印两列，便于离线对照）。落盘字段、
`DriftFitter` 的输入、死区/离群/分步判据、手动拟合写入值一字未动；`sample-manage-test` 由 22 条扩到 30 条
（新增 4 条钉住列口径与格式）。部署：21:57 覆盖 DLL（备份 `.bak-v1.1.0-col`），启动横幅 `v1.1.0.0`。
**看这张表的口径**：同一段窗口里没写偏移的几条样本会显示**同一个**「当时偏移」——那不是它们各自算出的数，
而是「上一次真正写入留下的值」；今天这列变过 3 次（−1.562 → 0.701 → 1.168 → 0.254），每次都对应前面某一行
`判据位置=写入过`。另外 11:39 那条显示「死区带内（未写）」是 **12:31 部署前的旧版残留**（那版死区判定排在
离群判定之前），文件里就是 `dead-zone`，不会被新版本重算。

### 8.5 2026-10-16：恢复序列的进食口径收严（判据已拒写的样本不得回灌）✅

**实测**：10-16 07:07 重启，08:09 首窗恢复走了 12 h 窗口（20:09~08:09），捞到 4 条 ——
`20:10 −6.395 outlier`、`21:29 +0.098 dead-zone`、`21:40 −6.427 outlier`、`22:30 −0.323 applied`，
日志 `样本 当天 4（参与 2｜死区 1｜拒写 2） 导入4｜中位-3.359s(4)`。两条**判据白天已明确拒写**的离群值
（`Reason=outlier`）被当成普通测量喂回估计器，恢复中位数被拖到 **−3.359 s**（只留干净样本应为 **−0.112 s**），
首窗因此多走了一小步（−0.322 → −1.414）；偏移本身没错，但「恢复」的意义被削弱。
根因：`RestoreTodaySamplesOnce` 的 `seedable` 只排了旁证样本（`!IsSideEvidence`）。

**修正**：进食口径收敛到一处 —— 新增 `OffsetSample.IsRejected`（`outlier` / 旧版 `large-error`）与
`OffsetSampleStore.IsSeedableForRuntime(sample)`（`!IsSideEvidence && !IsRejected`），
`RestoreTodaySamplesOnce` 改为 `recent.Where(OffsetSampleStore.IsSeedableForRuntime)`。
旧数据（`Reason=Unknown`，原因未记）无法判定，**仍按普通测量处理**；`dead-zone` / `applied` / `stepped` 照常进食。
手动拟合不受影响（那边是「勾选即参与」口径，人工把拒写样本拉回来仍有效）。
离线断言 `sample-manage-test` 新增 6 条钉住：拒写样本不被摄取、旁证与之分开标记、干净测量与旧数据仍可摄取、
以及「同一批样本排除拒写后中位数由 −3.359 → −0.112」的数值复核。

### 8.6 未验证 / 待观察 ⚠️

- **恢复窗口是否会漏掉前一晚**：窗口是「首个布防时刻往前 12 h」的滑动窗口，首窗越晚起点越晚（10-16 首窗 08:09 → 起点 20:09，昨晚最后一条 22:30 仍在窗内；若首窗推到 08:10 之后，更早的样本就会被丢）。10-16 尚未触发，**等用户决定是否放宽到 14~16 h 或改锚点**。
- **下课铃偏离标称边界 ±5~6 s**（10-15 20:10/21:40、10-16 09:39 +4.441、10-16 10:50 −6.182，均被离群门拦下）：是铃声本身如此还是判据挑错，需要实听核对；确认后决定「勾选参与拟合」还是「填进忽略的边界时刻」。
- **跨天闭环**：`outlier` / `stepped` 标签与模板锚定已在 10-16 上午的自然窗口上验证通过（5 条干净样本、中位 −1.360、离散 0.296 s）；**模板未命中的窗口要不要加更硬的门**（例如「模板未命中就完全不写入」）仍未拍板——代价是可用窗口从约 3/4 降到约 1/3、收敛变慢。
- 运行中的内核二进制仍是 9/7 由工作区源码构建的版本（比原版多一个**未被使用**的可写属性）；插件与该差异无关。

### 8.7 2026-10-19：v1.1.1.0「采用偏移」列 + 两位小数 + README 打包修复 ✅

- 样本管理新增第 4 列「采用偏移」：当前勾选集合 + 本行强制纳入后的手动拟合写入值（B 口径），勾选变化实时重算；已勾选行显示当前拟合结果，未勾选行显示勾上后的结果。
- 预览复用 `ManualFitService.AnalyzeWithForcedSample` → `Analyze`，包含 `DriftFitter` 的 6h 窗口/趋势逻辑；手动拟合走 `Seed`，不做 `Add` 的跳变/离群拒写；窗口外的行通常不改变预览，窗口内按中位数变化，只有中位数恰好不变时才不变。
- 所有实际写入统一经 `CorrectionPolicy.RoundOffsetSeconds`（两位、AwayFromZero）：`SettingsOffsetApplier.Apply` 与 `ManualFitService.Analyze` 两处调用；「当时偏移」「采用偏移」两列也显示两位小数，诊断值仍保留三位。
- 插件页概览找不到 `README.md`：`.cipx` 由 SDK `CreateCipx` 打包整个 `$(OutputPath)`，`plugin/README.md` 通过 csproj `CopyToOutputDirectory=Always` 进入输出/包；部署时同时拷 `README.md` 到插件目录，重启后概览可加载。
- 清理过时的分发指引文档与引用（README / HANDOFF / 更新说明-v1.1.0），只清当前树，不改写 git 历史、不强推。
- `sample-manage-test` 37 → 43 条（采用偏移预览、两位小数）；三个项目 Release 构建 0 warning / 0 error。
- 部署：2026-10-19 21:46 覆盖 `BellTimeCalibration.dll` / `deps.json` / `manifest.yml` / `README.md`（备份 `.bak-v1.1.0.0-preAdoptColumn`），21:47:02 启动横幅 `v1.1.1.0 就绪`；`.cipx` 已含 README.md。
- 2026-10-19 复验（当天 19 条样本、8 条勾选）：勾选行及窗口外行预览 `2.77`；强制 16:20/16:30/20:10 三条窗口内离群行为 `2.52`（6h 窗口中位数变化），均与生产 `AnalyzeWithForcedSample` 一致；据此把「离群挂起」说明改为 6h 窗口/中位数，21:59 二次部署（备份 `.bak-v1.1.1.0-preHintFix`），启动横幅确认。

### 8.9 2026-10-27：v1.1.2.0 修「偏移整天摇摆」（四项修复）

**问题（2026-10-26 全天 20 个监听窗口的离线复算结论）**：同一天「所需偏移」跨度达 **15.91 s**（−4.07 ~ +11.84），
而校铃钟真实漂移只有 **0.1 s/小时**量级。根因不是噪声，是**测量系统性地选错了事件**：

- 真铃（模板 NCC 峰）稳定落在 relB ≈ **−6.5 s**（比标称边界早约 6.5 s），但起响沿闸门只在 **8/20** 个窗口选对；
  错法三类：贴边界的小瞬态（6 窗，所需 +0.1~+0.7）、过早/过晚的别处事件（3 窗，所需 +9.2~+11.8）、其余 2 窗。
- 双窗口复核（开关窗口 + 课表窗口）**不独立**：两窗中心只差一个「偏移」（当天 4.6~8.3 s），半窗都 ±8 s，
  看的是同一批事件，几乎必然 Agree，**不能当交叉验证**。

**四项修复**（每项的长注释都写在源码里，这里只列要点与实测数字）：

1. **强匹配免攻击沿校验**（`Services/TemplateMatcher.cs`，新常量 `StrongNcc = 0.85` / `StrongSpectralMin = 0.80`）
   - 攻击沿门槛（起响比值 ≥ 4×）是**相对**门槛，假设铃响前有安静基线。教室有人声时真铃的起响比值只有 1.0~3.7×，
     而安静间隙的小瞬态能到 16.7×。实测：全体位置最高 NCC 落在真铃的有 **17/20**（NCC 0.83~0.98），
     加上攻击沿门槛后只剩 **9/20**（其余掉到 0.25~0.53 → 报未命中），闸门因此失去锚点、退回 ±8 s 盲扫。
   - 改为：NCC ≥ 0.85 **且**音色 ≥ 0.80 时跳过攻击沿校验（也不再「改去别处重采」）。
     0.85 卡在真铃 0.83~0.98 与 2026-09-11 那个 0.804 假命中之间。
2. **闸门组内按峰值择优**（`Services/OnsetGate.cs`，新常量 `PreferPeakOverRatio = true`）
   - 旧判据按「起响比值」在强候选组里挑，而比值 = 峰值 ÷ 起响前 0.3 s 基线，**奖励的是背景安静而不是像铃**。
     实例（2026-10-26 15:40 上课窗）：真铃 relB −6.25 s、峰值 −16.6 dBFS、比值 3.4×，输给 relB −0.25 s、
     峰值 −19.2 dBFS、比值 **16.7×** 的瞬态，于是「所需偏移」从 6.5 变成 0.27。
   - 改为组内按峰值挑（窗内最响的那一下）。
3. **双峰拒写**（`Services/DriftFitter.cs`，新常量 `BimodalGapSeconds = 4.0` / `BimodalMinPerCluster = 2`；
   `Services/OffsetSampleStore.cs` 新增 `SampleReason.Bimodal`）
   - 稳健中位数在**双峰**样本上会给出既非真值、也不对应任何实际状态的中间值，而离群门以它为中心，
     于是同时拒真纳假。实机 14:40 实例：参与样本 −4.072 / −3.220 / +7.285 / +7.912，中位数 **2.033**；
     正确的 6.052 距中心 4.019 s 被判离群拒写，错误的 0.583 距中心 1.450 s 被接受，偏移从 7.60 被拖到 4.60；
     此后 **13 次连续拒写真值**、段永不重开。
   - 改为：排序后找最大间隔，若 ≥ 4.0 s 且两侧各 ≥ 2 个样本 → 判双峰，**拒写并告警**
     （`SampleReason.Bimodal`，界面显示「双峰拒写」），提示用户去样本管理复核。
     判定顺序在离群复核**之前**（双峰时「离群」这个标签是误导的）。
4. **基准跳变确认需模板命中**（`Services/DriftFitter.cs`，新常量 `SegmentRestartRequiresTemplate = true`；
   `CalibrationRunner` 新增 `templateConfirmed` 参数）
   - 实机 09:40 测得 +7.971、10:10 测得 +7.912，两条**都是闸门选错事件**的错值，但彼此只差 0.059 s（＜ 1.0 s 容差）
     而互相印证，触发「基准跳变确认」重开样本段，把偏移从 −3.10 直接改到 −0.10、再到 +2.90。
     错值互相印证是最危险的伪一致——它们共享同一个系统性错误。
   - 改为：跳变确认要求至少一条来自**模板命中**（`templateConfirmed` = 模板命中位置，或闸门命中位置落在
     模板锚点 ±`OnsetGate.AnchorApertureSeconds` 内）。

**验证**（`plugin/tools/ReplayTool`，新增子命令 `bell-match-test <Dumps 目录> <模板目录>`）：

- ① 强匹配被攻击沿误拒：**0 次**（改前当天 20 窗只有 9 个能过）
- ② 锚定闸门抓到别的事件：**0 个**（182 个模板命中窗口）
- ③ 相邻窗口差 ＞ 2.5 s 的比例：**16/164 = 10%**（改前 2026-10-26 当天 20 窗里 9 对 ＞ 2.5 s，45%）
- `estimate-test` 新增 8 条断言（双峰判定、模板确认门槛、单声源散布不误判、孤立脏值不误判），全部通过
- 2026-10-26 当天：改前 12 个模板命中窗口的所需偏移跨度 15.91 s，改后 **11/12 落在 7.60±2.5 s**
  （唯一离群是 08:10 那个「持续人声段里模板误匹配」的固有难例，改前也是同一个值）

**已知遗留**（如实记下，别当成已解决）：

- 08:10 那类「整窗持续人声、铃响淹没其中」的窗口，模板仍可能误匹配到人声段（NCC 0.907）。
  这类窗口需要更强的判据（如要求模板匹配段的包络衰减形状符合钟声），本次未做。
- 模板命中率约一半（今天 12/20），未命中的窗口仍走无锚点闸门；第 2 项修复让这类窗口按峰值选，
  比改前好，但仍不如有锚点时可靠。

**状态**：四项修复与 `bell-match-test` 均已在工作区完成；**已部署（2026-10-27 10:34:53，启动横幅 `v1.1.2.0 就绪`）**，`.cipx` 已在本地打好（不入库），2026-10-27 提交为 `b6a6e73`。

### 8.10 2026-10-29：v1.2.0.0 修「闸门跑出锚点光圈」与两处旁证/恢复缺陷（三项）

**问题（2026-10-29 下午的 5 个离群窗口）**：三个独立缺陷，其中一个是**判据本身写错**，一个是**日志不给旁证**，一个是**恢复期自我冻结**。

- ① **回溯能越出锚点光圈**：模板命中后闸门只在锚 ±`AnchorApertureSeconds`(3.0 s) 内搜索，但「长铃起点回溯」的上限是 `SegmentLookbackSeconds`(8.0 s)，于是「回溯 + 攻击沿归位」能把落点推到离模板 **3.5~4.6 s** 的地方。实机：14:50 上课窗偏早 **3.52 s**、14:40 下课窗偏晚 **4.60 s**，两个窗都因此被判离群拒写。
- ② **未命中时分不清「铃没响」还是「测错了」**：日志只有一句「模板匹配未命中」，运维不知道该查麦克风还是查判据。当天下午 5 个离群窗口里，14:40 / 15:30 / 16:20 三个下课窗的铃声**根本不存在**。
- ③ **启动恢复遇双峰会把插件锁死**：`IsBimodal` 的语义是「本次不写偏移」，拦单次可疑测量是对的；但它在**启动恢复**时会让此后每个窗口都被双峰拒写，而拒写的样本又进不了 `_samples`，双峰要等旧簇滑出 6 小时窗口才消失（最长冻结 **6 小时**）。

**三项修复**：

1. **回溯与归位不得越出光圈**（`plugin/Services/OnsetGate.cs`）
   - 有锚点时 `lookbackLimit` 取 `Math.Max(gateFrom, bestIdx - SegmentLookbackSeconds*rate)`——`gateFrom` 就是「锚 − 光圈」，回溯到不了光圈之外。
   - 攻击沿归位的 `attackLimit` 同样 `Math.Min(..., gateTo)`。落点因此被硬性约束在锚点光圈内。
2. **未命中时给出「铃没响」还是「测错了」的旁证**（`plugin/Services/TemplateMatcher.cs`、`RingCandidateSelector.cs`、`CalibrationRunner.cs`、`plugin/tools/ReplayTool/Program.cs`）
   - `TemplateMatch` 增加 `PeakNcc` 字段：**未被攻击沿重采改写**的全区间最高 NCC。不能复用 `Ncc`——非强匹配且攻击沿不足时 `Ncc` 会被换成别处的位置、值大幅下降，判断不了「本窗有没有铃」。
   - `RingCandidateSelector.SelectByTemplate` 新增 `out double peakNcc`；未命中时在说明里追加结论：`PeakNcc < TemplateMatcher.NoBellNcc`(0.35) → 「疑为未响铃（不是测错）」，否则 → 「铃声在，但没过判据」。`CalibrationRunner` 在疑为未响铃时额外打一条 `[校时] 本窗疑为未响铃：…` 的 Warn。
   - 0.35 的依据：确实没响铃的三个窗全窗最高 NCC 只有 **0.035 / 0.038 / 0.037**（另两个 0.132 / 0.326），真铃在的窗是 **0.87~0.94**；但「铃在但被拒」的窗落在 **0.25~0.53**，与「没铃」段有重叠，所以措辞是「**疑为**」，且**不改变任何写入判据**。
3. **启动恢复遇双峰丢掉较旧那一簇**（`plugin/Services/DriftFitter.cs`、`CalibrationRunner.cs`）
   - `DriftFitter.Seed()` 增加 `bool trimBimodal = false` 参数；为 true 且恢复后 `IsBimodal` 时调用新的私有方法 `TrimOldestCluster()`：按中位时刻判断哪簇更新、丢掉较旧的、重算，并写日志「启动恢复发现样本呈双峰（间隔 x s），已丢弃较旧的 N 个样本、保留较新的 M 个」。
   - `CalibrationRunner.RestoreTodaySamplesOnce()` 传 `trimBimodal: true`；`ManualFitService` 保持默认 false（手动拟合要让用户看到真实双峰，不能被悄悄修剪）。

**验证**（`plugin/tools/ReplayTool`）：

- `bell-match-test` 第②项的上限从「光圈 + 回溯窗 = **11.0 s**」收紧到「**光圈 + 0.05 s**」：**353 个 dump、224 个模板命中窗口，0 个越界**。
- 同一次全量回放的另两项：强匹配被攻击沿误拒仍 **0** 次、相邻窗口差 >2.5 s 仍 **16/204 = 8%**；模板未命中 **116** 个窗口，其中「疑为未响铃」**26** 个、「铃声在，但没过判据」**90** 个。
- `estimate-test` ③c 新增 5 条：双峰解除、保留较新 2 个、中位数回到 **7.598 s**、日志可读、默认 false 时 4 个原样保留。
- 光圈约束对**全部 224 个模板命中窗口**生效，不是只针对 10-29 那两个实例窗。

**已知遗留**（如实记下，别当成已解决）：

- 「疑为未响铃」只是**旁证**：0.25~0.53 与「没铃」段（0.035~0.326）有重叠，所以措辞是「疑为」；它只写日志与命中说明，**不参与任何写入判据**。要下硬结论得看该窗口的整段包络。
- `TrimOldestCluster()` 只解决「冻结」，**不判断哪一簇是对的**：它按中位时刻取较新的一簇，若较新那一簇本身是脏值，仍需人工去设置页「样本管理」复核。
- 8.9 的两条遗留仍在：整窗持续人声时模板仍可能误匹配（NCC 0.907）；模板命中率约一半，未命中窗口仍走无锚点闸门。

**状态**：三项修复与两条断言均已在工作区完成并跑通；`plugin/manifest.yml` 已改为 `version: 1.2.0.0`。**尚未 git 提交、未打 tag、未推送**（等用户指令）。

---

## 9. 常用操作

```powershell
# ── 构建（两个项目都要）────────────────────────────────────────────
cd '<git 树>\plugin';                      dotnet build -c Release
cd '<git 树>\plugin\tools\ReplayTool';     dotnet build -c Release
cd '<git 树>\plugin\tools\TemplateTool';   dotnet build -c Release

# ── 部署（必须先停进程，否则 DLL 被占用）──────────────────────────────
$dst = '<便携版>\data\Plugins\belltime.calibration'
Copy-Item "$dst\BellTimeCalibration.dll" "$dst\BellTimeCalibration.dll.bak-v1.1.0.0-preAdoptColumn"   # 先备份当前版本
Copy-Item "$dst\manifest.yml" "$dst\manifest.yml.bak-v1.1.0.0-preAdoptColumn"
Stop-Process -Name 'ClassIsland.Desktop' -Force; Start-Sleep 3
Copy-Item '<工作树>\plugin\bin\Release\net8.0\BellTimeCalibration.dll' `
          '<工作树>\plugin\bin\Release\net8.0\BellTimeCalibration.deps.json' `
          '<工作树>\plugin\bin\Release\net8.0\manifest.yml' `
          '<工作树>\plugin\bin\Release\net8.0\README.md' $dst -Force
Start-Process '<便携版>\app-2.1.0.1-0\ClassIsland.Desktop.exe'
# 启动日志应出现： [校时] 启动 铃声自动校时 v1.2.0.0 就绪｜偏移通道 …｜提醒 …｜手动拟合 …

# ── 看日志 / 看样本 ────────────────────────────────────────────────
$cfg = '<便携版>\data\Config\Plugins\belltime.calibration'
Get-Content "$cfg\Logs\belltimecalibration-$(Get-Date -Format yyyy-MM-dd).log" -Tail 40
Get-Content "$cfg\Logs\offset-samples.jsonl" | Select-String (Get-Date -Format yyyy-MM-dd)
Select-String -Path '<便携版>\data\Settings.json' -Pattern 'TimeOffsetSeconds'   # 当前偏移
```

**日志行的读法**：

```
[校时] 样本 近12h 8 条                       ← 首次布防时恢复样本（v1.1.0 起是 12 小时）
[校时] 响铃 上课 B=08:10:00 铃=08:10:06.508 Δ-7.947 源=闸门(两窗一致→开关) 噪-28.7 峰-12.4 监听51.0s
[校时] 拟合 所需-6.456 当前1.438 预测-6.456 差-7.894 样本1/离散0.000 → 写入
[校时] 拟合 … → 带内不写 / 步进写入 / 离群拒写 / 大误差拒写
```

`源=` 里的 `闸门(…→开关/课表)` 就是双窗口复核的结论；`噪`/`峰` 是噪声底与峰值 dBFS（判断这一窗是不是吵闹窗口的第一手依据）。

---

## 10. 坑与注意事项

1. **插件配置只在启动时读一次**（无文件监听）：改 `BellCalibrationSettings.json` 必须重启 ClassIsland；运行中改文件会被内存值回写覆盖。
2. **DLL 被运行中的进程占用**：部署前必须停进程，且要确认进程真的退出再复制。
3. **课表项必须按时间升序**：宿主用 `validTimeLayoutItems.FirstOrDefault(TimeType 匹配 && EndTime >= now)` 取「下一个课间」，**按数组顺序**。若课表项乱序（如把 `11:40–14:00` 午休插在 `08:05` 之前），上午多节课的 `OnBreakingTimeLeftTime` 会解析成数小时之后 → **下课倒计时（EntertainingIsland）与本插件的下课边界布防同时静默失效**。改课表后务必检查顺序（本会话已重排三张课表，备份 `Default.json.bak-before-layout-reorder` / `-weekend-reorder`）。
4. **忽略列表**按 `HH:mm` 精确匹配边界**显示**时刻；本机当前填 `08:00,17:10,18:30`。
5. **隐私**：默认音频只在内存（`CaptureAudio`），只有 `DebugDumpAudio=true` 才写 `Dumps\`——排障期本机开着，注意磁盘与隐私，**用完关掉**。
6. **`Dumps\` 的清理**：设置页「清除无用录音」只删配置目录 `Dumps\` 下**一层**的 `*.wav`，7 天以内、夹具名单（`DumpCleaner.ProtectedFiles`，12 个离线断言用例）与认不出时刻的文件一律保留。**别手工全清**——夹具录音是判据回归唯一的输入（历史上被误删过一次，回收站也被清空）。
7. **模板目录是非递归枚举**：留在 `Templates\` 里的旧模板（如 `Templates-bak-*` 若放错位置）会被当成第 3、4 个模板。
8. **别用「自动截取」做模板**：本机麦样本尾部可能有比真铃高 6~7 dB 的短瞬态（椅子/关门级），自动截取会截到环境噪声；模板要按实测区间手工截（当前模板取自 2026-10-07 实机录音，各 2.5 s）。
9. **静默段被降噪压到 −87 dB**（旧手机样本甚至下溢）：任何**绝对阈值**判据必然失准，只能用相对峰值判据；这也是「起响前数字静默的候选一律排除」的原因。
10. **样本勾选是旁挂文件**：`Logs/sample-exclusions.jsonl`（只追加、以最后一行为准）；删掉即恢复默认。**不要重写** `offset-samples.jsonl`——那是测量流水，也是离线分析的输入。
11. **分发**：本地 git 提交 + 推送 `origin/main`；发版 = 打四段 tag（`a.b.c.d`，如 `1.1.1.1`），`.github/workflows/release.yml` 会自动构建 `.cipx` 并发 Release（正文 = 更新说明 + `CLASSISLAND_PKG_MD5`）。启动横幅的版本号直接读 `manifest.yml`（早先写死在 `Plugin.cs` 里，改版本时漏改会在日志里谎报版本）。
    **本机推送通道（2026-10-22 实测）**：直连 `github.com:443` 会被重置（`git ls-remote` / `curl` 均失败，`api.github.com` 正常），走 `ssh.github.com:443` + 临时 deploy key：API（凭据 `~/.git-credentials`，scope 含 repo）加写权限 key → `GIT_SSH_COMMAND='ssh -i <tmpkey> -p 443' git push ssh://git@ssh.github.com:443/zihan-ds/classisland-belltime-calibration.git main` → API 删除 key。
12. **`CreateFromDirectory` 目标 zip 已存在会抛异常**（历史的打包脚本坑）：先删 zip。

---

## 11. 待办 / 待用户确认

1. **已推送**（`origin/main`，2026-10-15 22:13 快进 `720dbbd..ee1974a`；22:23 把全仓 7 个提交的作者改成仓库所有者 `zihan_ds <261094359+zihan-ds@users.noreply.github.com>` 后强推为 `b2b7ce4`，tree 逐字节不变）。后续正常 `git push` 即可，作者信息已写进仓库级配置。
2. **观察下一个自然边界窗口**：确认 ① 脏值落 `outlier` 且默认不勾；② 大差值落 `stepped` 且只写 ±3 s；③ 恢复行是 `样本 近12h N 条`；④ 模板锚点行按预期出现。
3. **是否要「模板未命中就不写入」**这条更硬的门（代价：可用窗口 3/4 → 1/3）——等用户拍板。
4. 下课铃与上课铃是否来自同一套打铃系统（同一钟）？（当前实现按用户口径**共用一条拟合序列**，不分开拟合）
5. 运行中的内核二进制是否需要替换为真原版？
6. 排障结束后关掉 `DebugDumpAudio`。
7. **v1.1.1.0 / v1.1.1.1**：均已本地提交/部署（v1.1.1.1 为 2026-10-22 12:23），2026-10-22 18:04 已推送 `origin/main`，20:05 打四段 tag `1.1.1.1` 由 CI 发 Release；v1.1.1.0 未单独打 tag（改动含在 1.1.1.1 的 Release 里）。**已完成**。
8. **v1.1.2.0（四项修复，见 8.9）**：**已完成** —— 2026-10-27 提交 `b6a6e73`、10:34:53 部署到实机（启动横幅 `v1.1.2.0 就绪`），`.cipx` 本地已打包（`plugin/cipx/`，不入库）。
9. **v1.2.0.0（三项修复，见 8.10）**：源码与 `manifest.yml`（`version: 1.2.0.0`）已改完，八套离线断言全通过（`bell-match-test` 353 个 dump、224 个模板命中窗口、0 越界），**已于 2026-10-29 20:24:31 部署到实机**（启动横幅 `v1.2.0.0 就绪｜…当前 1.870s`，`.cipx` 806547 字节 / MD5 `8806E6107F6EB2E946B008290DA31A8D`）。**剩下三步等用户指令**：① 观察锚定闸门的命中点是否始终落在锚 ±3.0 s；② 看模板未命中窗口的「疑为未响铃」旁证是否与实听一致（26 个疑为未响铃的窗口若实听有铃，说明 0.35 偏低）；③ git 提交 + 推送 `origin/main` + 打四段 tag `1.2.0.0` 由 CI 发 Release（正文取自 `CHANGELOG.md` 的 v1.2.0.0 小节）。

---

## 12. 本仓库内容说明

```
<git 树>\
├─ README.md                使用者向：安装、配置、原理、设置项表、目录结构、已知限制
├─ CHANGELOG.md             版本更新日志（1.0.5.0 起逐版一节，Release 正文的来源）
├─ docs/HANDOFF.md          ← 本文件（新会话从这里开始读）
├─ tools-harvest.ps1        开发期辅助脚本
└─ plugin\                  插件源码（Templates 下是静音占位；tools 下是离线工具）

<工作树>\（不提交）
├─ bell-recordings\         实机录音与对照记录
├─ samples\                 回放用的边界音频
├─ tools\                   工作树里的工具构建产物
└─ deploy-snapshot\         部署快照（当时 DLL/manifest/配置/模板/宿主 Settings.json/日志）
```

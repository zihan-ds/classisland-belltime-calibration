> 本文件是**开发过程记录**（会话交接日志），包含当时的调试历程、实测数据与结论，其中的本机路径已替换为占位符。
> 使用者向的说明（怎么装、怎么配、每个设置项干什么）请看仓库根目录 `README.md` 与 `plugin/README.md`；
> 每个版本的改动清单在根目录《更新说明-v1.x.x.md》。
>
> **文中的 `samples\`、`Dumps\`、`Templates\*.wav` 等录音均不随仓库分发**：铃声样本由使用者自行录制导入；
> `Dumps\` 是插件在开启「调试音频转存」时对本机窗口音频的临时转存（默认关闭，音频平时只在内存）。
> 出现这些路径处，指的是开发机上当时存在的本地文件。

---

# 铃声自动校时（BellTimeCalibration）· 会话交接日志

> 插件版本 **1.1.0.0**（四段，与发布 tag 一致） · 交接目标：让新会话能**零上下文**接手继续开发与调参。

---

## 0. 交接速读（30 秒）

| 项 | 状态 |
|---|---|
| 实机版本 | **v1.1.0.0**（含 2026-10-15 晚的样本管理列修订），已部署到 `<ClassIsland 便携版根目录>\data\Plugins\belltime.calibration\`（21:57 部署，启动横幅已确认 `v1.1.0.0 就绪`） |
| 工作模式 | 非学习模式、**自动应用已开启**、当前偏移 `0.254 s` |
| 仓库 HEAD | `b2b7ce4`（文档）→ `7a443dc`（合并提交）→ `c1954ea`「v1.1.0 修订：样本管理『所需偏移』列改为『当时偏移』」→ `5409c20` → `3b99685`「v1.1.0 修：离群判定提前 + 布防恢复窗口 12h」→ `71b8f16`（v1.1.0 主体，提交信息即完整变更清单）→ `427e9a5` = v1.0.5。**注**：2026-10-15 22:23 做过一次全仓作者改写（占位作者 → `zihan_ds <261094359+zihan-ds@users.noreply.github.com>`），改写前的哈希是 `328282f / ee1974a / 963ed03 / 0f72911`，两者 tree 完全一致 || 远程 / 推送 | **已推送**：`origin = https://github.com/zihan-ds/classisland-belltime-calibration.git`（公开），`main` 已跟踪 `origin/main`，2026-10-15 22:13 推送 `720dbbd..ee1974a`（普通快进，未用 force）。详情见根目录《迁移与推送.md》 |
| 最近一次实机发现 | 2026-10-15 上午两处判据缺陷（见第 8.3 节）：① 当天第一条样本自己当基准被单样本直写；② 真正被拟合器判离群的测量被标成「死区带内」并默认勾选。**两处都已修并部署**，两棵树的源码与实机 DLL 一致 |
| 立刻要做的三件事 | ① 下一个自然边界窗口的日志里：脏值应落 `outlier`、大差值应落 `stepped`；② 设置页样本管理应显示「当时偏移」列（表头与同一窗口同值的现象见 8.4 节）；③ 判断 20:10 / 21:40 那两窗「真铃晚 6.4 s」是否为真实基准偏移 |
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
7. 版本 **v1.1.0.0**，分发 = 「本地 git 提交 + 推送 → 打四段 tag 由 CI 发 Release」，**push / 打 tag 需用户指令**（2026-10-15 22:13 已按指令推 `origin/main`，22:31 打 tag `1.1.0.0`）；
8. 文档（README、设置页提示、更新说明）必须与实际行为一致；
9. 音频（铃声样本、`Dumps\`）**不随仓库分发**。

---

## 2. 环境与两棵树（新会话先读这一节）

| 占位符 | 含义 / 本机实际值 |
|---|---|
| `<ClassIsland 便携版根目录>` | 便携版根；本机 `D:\ClassIsland_app_windows_x64_full_folder` |
| …运行目录 | `<便携版>\app-2.1.0.1-0\ClassIsland.Desktop.exe`（入口是外层的 `ClassIsland.exe`） |
| …数据目录 | `<便携版>\data\` |
| 插件程序集目录 | `<便携版>\data\Plugins\belltime.calibration\`（`BellTimeCalibration.dll`、`SoundFlow.dll`、`miniaudio.dll`、`manifest.yml`） |
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
| `OnsetGate.SegmentLookbackSeconds` / `SegmentDropDb` / `SegmentHoldSeconds` | 8.0 / 12.0 / 1.2 | 长铃起点回溯：最多回看 8 s、低于「选中击峰值 − 12 dB」且持续 1.2 s 才算出段 |
| `OnsetGate.AttackWindowSeconds` | 0.05 | 回溯后「起声归位」的短窗长度 |
| `OnsetGate.DominanceDb` / `SameStrengthDb` | 6.0 / 2.0 | 双窗口复核：一侧强出 >6 dB 用那一侧；差距落在 2~6 dB 判冲突 |
| `CalibrationScheduler.CaptureMarginSeconds` | 10.0 | `CurrentLead()` 里给偏移留的余量 |
| `CalibrationScheduler.MaxLeadSeconds` | 60.0 | 提前量上限 |
| `CalibrationRunner.SeedLookbackHours` | 12.0 | 首次布防时恢复**最近 12 小时**的样本（v1.1.0 修，见 8.3） |
| `DriftFitter.OutlierRejectSeconds` | 2.0 | 与稳健拟合差超过它即判离群（不入列、不清空） |
| `DriftFitter.SegmentRestartAgreements` / `SegmentRestartAgreementSeconds` | 2 / 1.0 | 连续两条彼此一致（≤1 s）才认定基准真跳变、重开样本段 |
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
     命中后：长铃起点回溯（≤8 s）→ 攻击沿归位 → 得到 t_ring
   ↓
③ 双窗口复核：同一判据再扫一遍「以标称边界为中心」的窗口
     一致 / 只有开关窗命中 → 照旧
     一侧强出 >6 dB → 用那一侧的坐标（对称判据）
     只有课表窗命中 / 差距 2~6 dB → 不写偏移，落**旁证样本**（默认不参与拟合）
   ↓
④ e = t_ring − t_switch  →  所需偏移 = 当前偏移 − e
   ↓
⑤ DriftFitter.Add(测量)  →  PredictedSeconds(now)  →  diff = 预测 − 当前
   ↓
⑥ 写入决策链（**顺序即优先级**，v1.1.0 修过顺序）：
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
├─ manifest.yml                     id=belltime.calibration，apiVersion 2.0.0.0，version 1.1.0.0
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
& $tool estimate-test       # 步进写入的方向/边界/限幅、离群不入列也不清空样本段、连续两条一致才重开段、趋势门槛
& $tool absolute-required-test   # 4 条退化口径
& $tool notify-test         # 7 条提醒判定
& $tool sample-manage-test  # 22+4 条样本管理（含 v1.1.0 新增的跨天恢复 4 条）
& $tool dumps-clear-test    # 20 条「清除无用录音」删除/保留判据
& $tool gate-reconcile-test # 1 条真实窗口 + 6 条合成分支 + 11 条夹具已丢失（打印「跳过」，只有真回归才失败）
& $tool gate-guard          # 全量 dump：没有任何命中依赖「除以数字静默」的比值
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
| `estimate-test` | 步进方向/边界/限幅、离群挂起、连续两条一致才重开段、趋势门槛 |
| `absolute-required-test` | 4 条退化口径 |
| `notify-test` | 7 条提醒判定 + 无冷却 + 2 条冲突提醒 |
| `sample-manage-test` | 22 条 + v1.1.0 新增 4 条跨天恢复 |
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

### 8.5 未验证 / 待观察 ⚠️

- **跨天闭环**：v1.1.0 的 `outlier` / `stepped` 标签、`样本 近12h N 条` 恢复行、模板锚定在真实边界上的表现，都还没经过一个**自然窗口**的验证（部署时刻为 10-15 12:31，之后的下一个边界窗口才算）。
- **模板未命中的窗口要不要加更硬的门**（例如「模板未命中就完全不写入」）：代价是可用窗口从约 3/4 降到约 1/3、收敛变慢——**等用户拍板，未擅自加**。
- 运行中的内核二进制仍是 9/7 由工作区源码构建的版本（比原版多一个**未被使用**的可写属性）；插件与该差异无关。

---

## 9. 常用操作

```powershell
# ── 构建（两个项目都要）────────────────────────────────────────────
cd '<git 树>\plugin';                      dotnet build -c Release
cd '<git 树>\plugin\tools\ReplayTool';     dotnet build -c Release
cd '<git 树>\plugin\tools\TemplateTool';   dotnet build -c Release

# ── 部署（必须先停进程，否则 DLL 被占用）──────────────────────────────
$dst = '<便携版>\data\Plugins\belltime.calibration'
Copy-Item "$dst\BellTimeCalibration.dll" "$dst\BellTimeCalibration.dll.bak-v1.0.5"   # 先备份当前版本
Stop-Process -Name 'ClassIsland.Desktop' -Force; Start-Sleep 3
Copy-Item '<工作树>\plugin\bin\Release\net8.0\BellTimeCalibration.dll' `
          '<工作树>\plugin\bin\Release\net8.0\BellTimeCalibration.deps.json' `
          '<工作树>\plugin\manifest.yml' $dst -Force
Start-Process '<便携版>\app-2.1.0.1-0\ClassIsland.Desktop.exe'
# 启动日志应出现： [校时] 启动 铃声自动校时 v1.1.0.0 就绪｜偏移通道 …｜提醒 …｜手动拟合 …

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
11. **分发**：本地 git 提交 + 推送 `origin/main`；发版 = 打四段 tag（`a.b.c.d`，如 `1.1.0.0`），`.github/workflows/release.yml` 会自动构建 `.cipx` 并发 Release（正文 = 更新说明 + `CLASSISLAND_PKG_MD5`）。步骤见根目录《迁移与推送.md》。启动横幅的版本号直接读 `manifest.yml`（早先写死在 `Plugin.cs` 里，改版本时漏改会在日志里谎报版本）。
12. **`CreateFromDirectory` 目标 zip 已存在会抛异常**（历史的打包脚本坑）：先删 zip。

---

## 11. 待办 / 待用户确认

1. **已推送**（`origin/main`，2026-10-15 22:13 快进 `720dbbd..ee1974a`；22:23 把全仓 7 个提交的作者改成仓库所有者 `zihan_ds <261094359+zihan-ds@users.noreply.github.com>` 后强推为 `b2b7ce4`，tree 逐字节不变）。后续正常 `git push` 即可，作者信息已写进仓库级配置。
2. **观察下一个自然边界窗口**：确认 ① 脏值落 `outlier` 且默认不勾；② 大差值落 `stepped` 且只写 ±3 s；③ 恢复行是 `样本 近12h N 条`；④ 模板锚点行按预期出现。
3. **是否要「模板未命中就不写入」**这条更硬的门（代价：可用窗口 3/4 → 1/3）——等用户拍板。
4. 下课铃与上课铃是否来自同一套打铃系统（同一钟）？（当前实现按用户口径**共用一条拟合序列**，不分开拟合）
5. 运行中的内核二进制是否需要替换为真原版？
6. 排障结束后关掉 `DebugDumpAudio`。

---

## 12. 本仓库内容说明

```
<git 树>\
├─ README.md                使用者向：安装、配置、原理、设置项表、目录结构、已知限制
├─ 更新说明-v1.1.0.md        本版本完整改动清单（含设置项对照、文件清单、验证方式）
├─ 迁移与推送.md             分发方式：提交 + 推送 + 打 tag 发版（CI 自动构建 .cipx）
├─ docs/HANDOFF.md          ← 本文件（新会话从这里开始读）
├─ tools-harvest.ps1        开发期辅助脚本
└─ plugin\                  插件源码（Templates 下是静音占位；tools 下是离线工具）

<工作树>\（不提交）
├─ bell-recordings\         实机录音与对照记录
├─ samples\                 回放用的边界音频
├─ tools\                   工作树里的工具构建产物
└─ deploy-snapshot\         部署快照（当时 DLL/manifest/配置/模板/宿主 Settings.json/日志）
```

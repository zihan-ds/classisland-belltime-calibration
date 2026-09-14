> 本文件是**开发过程记录**，包含当时的调试历程、实测数据与结论，其中的本机路径已替换为占位符。
> 最新的使用方式（含「样本由用户自备」）请看仓库根目录 `README.md` 与 `plugin/README.md`。
>
> **文中的 `samples\`、`Dumps\`、`Templates\*.wav` 等录音均不随仓库分发**：铃声样本由使用者自行录制导入；
> `Dumps\` 是插件在开启「调试音频转存」时对本机窗口音频的临时转存（默认关闭，音频平时只在内存）。
> 出现这些路径处，指的是开发机上当时存在的本地文件。

---

# 铃声自动校时（BellTimeCalibration）· 会话交接日志

> 插件版本 **1.0** · 交接目标：让新会话能**零上下文**接手继续开发与调参。
> 本文件放在迁移目录根：`<本仓库开发目录>`

---

---

## 1. 目标与验收标准

**目标**：插件在课表边界开麦监听校园铃声，通过**多次测量拟合**把 ClassIsland 的
**「应用设置 → 时钟 → 时间偏移」**（内核设置项 `Settings.TimeOffsetSeconds`）校准到与学校广播铃钟同步。

**硬约束**（用户明确要求）：

1. **绝不修改课表档案**；
2. **不修改内核代码**——用原版内核即可（早期为插件加的可写偏移属性补丁**已撤回**）；
3. 铃响时刻 ClassIsland 的时间误差 **≤ 0.5 s**；
4. 自动屏蔽 **08:00 / 18:30** 两次铃声（音色与样本不一致）；
5. 校园铃钟走时不稳：误差随时间规律性增大（最多 ~10 s）后被人工校准归零，随后继续增大 → 插件需持续跟踪。

---

## 2. 环境与路径（新会话先读这一节）

| 项 | 路径 / 值 |
|---|---|
| ClassIsland 便携版 | `<ClassIsland 便携版根目录>`（运行目录 `app-2.1.0.1-0\ClassIsland.Desktop.exe`，数据目录同级 `data\`） |
| 插件安装目录 | `data\Plugins\belltime.calibration\`（`BellTimeCalibration.dll` + `SoundFlow.dll` + `miniaudio.dll` + `manifest.yml`） |
| 插件配置目录 | `data\Config\Plugins\belltime.calibration\`（`BellCalibrationSettings.json`；`Templates\`；`Logs\`；`Dumps\`） |
| 插件日志 | 配置目录 `Logs\belltimecalibration-<日期>.log`（人类可读）+ `Logs\calibration-history.jsonl`（结构化，协议 v1） |
| 宿主主日志 | `data\Logs\log-<时间戳>-1.log`（每天/每次启动一个文件，旧文件会被轮转删除） |
| 宿主设置 | `data\Settings.json` → `TimeOffsetSeconds` 即「应用设置→时钟→时间偏移」 |
| 课表档案 | `data\Profiles\Default.json`（工作日用 `2b17976d`、周六 `a3338692`、周日 `8c3396da`） |
| 内核源码（工作区） | `<ClassIsland 内核源码>`（本地改造已 `git checkout` 撤回，工作区干净；插件**不依赖**任何内核改动） |
| 铃声样本 | **不随仓库分发**：由使用者用本机麦克风自行录制（见仓库根 README 的「提供你自己的铃声样本」） |
| 系统校时 | 已执行 `w32tm /config /manualpeerlist:"ntp.aliyun.com,..." /syncfromflags:manual /update` + 服务设为自动；此前为 `Local CMOS Clock` 从未同步 |
| 本机时间基准核对 | 系统钟 vs ntp.aliyun.com = **+0.14 s**；ClassIsland 显示 = NTP + `TimeOffsetSeconds` |

**当前运行配置快照**（见 `deploy-snapshot\BellCalibrationSettings.json`）：

```json
{"IsEnabled":true,"IsAutoApplyEnabled":false,"IsLearningMode":true,"ArmedLeadSeconds":20,"WindowSeconds":20,
 "DeadZoneSeconds":0.3,"ToleranceSeconds":12,"DetectionSensitivity":2,"IgnoredBoundaries":"08:00,18:30",
 "EnableTemplateMatch":true,"TemplateDirectory":"Templates","ClassBellSamplePath":"","BreakBellSamplePath":"",
 "ClassBellTrimSeconds":"","BreakBellTrimSeconds":"","MatchNccMin":0.6,"MatchSpectralMin":0.45,"DebugDumpAudio":true}
```

> 注意：**仍处于学习模式**（只测量不写入），内核偏移当前由用户手动设为 **0.8 s**。

---

## 3. 本轮排查得到的关键结论（重要，避免重复踩坑）

1. **写偏移的原版通道**：`IAppHost.Host`（public static IHost）→ 宿主 DI → 反射取内核 `ClassIsland.Services.SettingsService`
   → 其 `Settings` 对象上的 public 可读写 `TimeOffsetSeconds`。写入后立即生效（宿主每次取时间都读它）、
   `SettingsService` 自动落盘 `Settings.json`、设置页同步可见。**因此内核补丁不再需要**（已撤回）。
2. **课表项顺序是硬约束**：宿主用 `validTimeLayoutItems.FirstOrDefault(TimeType 匹配 && EndTime >= now)` 取「下一个课间」，
   **按数组顺序而不是时间顺序**。若课表项乱序（如把 `11:40–14:00` 午休插在 `08:05` 之前），
   上午多节课的 `OnBreakingTimeLeftTime` 会解析成数小时之后 → **下课倒计时（EntertainingIsland）与本插件的下课边界布防同时静默失效**。
   本会话已把三张课表按 `StartTime` 重排（备份 `Default.json.bak-before-layout-reorder` / `-weekend-reorder`）。**以后改课表后务必检查顺序**。
3. **校铃走时速率实测 ≈ 0.107 秒/小时**（由 `DriftFitter` 最小二乘拟合得出，约 2.6 s/天），人工校准会造成跳变 → 拟合器用「新样本偏离预测 >2.5 s 即重开拟合段」处理。
4. **预备铃现象**：学校在正铃前约 **15.2~15.5 s** 会打一次**同音色**的铃。若全窗口搜索模板，最优点会被预备铃抢走、正铃整段被丢弃 → 因此**搜索范围必须限制在「标称边界 ± 搜索半窗」内**（已实现）。
5. **模板匹配"实机不命中"的成因已查明（不是"跨设备不匹配"）**：早期实测 NCC 0.13~0.78 由**两个独立缺陷**造成——
   ① **钟域错位**：音频时间戳/候选起响点取自 `DateTime.UtcNow`（裸墙钟域），而 `B_display` 取自内核时钟（= 墙钟 + `TimeOffsetSeconds`），
   早先的实现直接相减判"起响点是否在边界 ±搜索半窗内"，等于整体错位一个偏移量 —— 偏移每 1 s 吃掉 1 s 搜索余量。
   15:40 日志"命中但起响点偏差 15.18s 超出搜索半窗 ±12s"正是 15.44 − 0.8 的错位（不是预备铃，也不是设备差异）。
   ② **模板自动截取选错段**：旧判据"最长的超阈值连续段"在本机麦降噪样本上会截出环境噪声（实测截出 37.04 s 处 0.16 s 的瞬态）。
   两者均已修正（见第 5 节设计取舍与第 4 节代码结构）。
6. **上下课铃音色差异极大**：交叉匹配 NCC ≈ **0.021**（旧样本）/ **0.295~0.314**（本机麦样本）→ 模板匹配能可靠区分两者，也能自然拒绝异音色铃（08:00/18:30 无需特判）。
7. **"预备铃"结论已撤回**：用户确认每边界**最多一段铃声**、样本中**无预备铃**；`ToleranceSeconds=12` 的 ±搜索半窗继续保留（用于覆盖钟漂移），但不再是"挡预备铃"的手段。
8. **本机麦降噪样本的两个特性（调参必须知道）**：静默段被降噪压到 −87 dB（旧手机样本甚至 −240 dB 下溢）→ 任何**绝对阈值**判据必然失准，必须用"相对峰值"判据；录音尾部可能有比真铃高 6~7 dB 的**短瞬态**（0.1~0.2 s，椅子/关门级），任何"最响点/最长段"式选段都会选错。
9. **一次性故障记录**：麦克风偶发整窗静音（噪声底 −80 dBFS，如 09-11 08:50），与权限/设备占用有关，重连即恢复。

---

## 4. 代码结构（`plugin\` 目录）

```
BellTimeCalibration/
├─ Plugin.cs                     入口：日志/配置加载与自动保存、模板加载、设置页注册、AppStarted 自举
├─ manifest.yml                  id=belltime.calibration，apiVersion 2.0.0.0，version 1.0.0.0
├─ Models/BellCalibrationSettings.cs   全部设置项 + 忽略列表解析（IsBoundaryIgnored）
├─ Services/
│  ├─ Logger.cs                  文件日志（ConfigFolder 对外暴露）
│  ├─ CalibrationScheduler.cs    布防：状态机 + 边界前 N 秒入窗；忽略列表跳过；发 BoundaryApproaching/Reached
│  ├─ MicCaptureService.cs       窗口捕获（SoundFlow/miniaudio，48k 单声道 F32）；keepAudio 时保留音频
│  ├─ CaptureAudio.cs            内存音频缓冲（60s）+ 采样索引↔墙钟双向映射 + SaveWav（仅调试转存）
│  ├─ RingDetector.cs            噪声底「响铃期冻结」+ 持续段检测（起响点/时长/峰值）
│  ├─ RingCandidateSelector.cs   主路径：模板匹配；回退：最响持续段（同簇合并）
│  ├─ BellTemplate.cs            模板对象：10ms 能量包络 + 32 点 dB 域音色向量；长录音+区间/模板文件两种来源
│  ├─ TemplateMatcher.cs         包络粗定位 → 4kHz 精定位（0.25ms）→ 音色确认
│  ├─ TemplateLibrary.cs         启动加载「上课铃/下课铃」模板
│  ├─ WavReader.cs               零依赖 WAV 解析（PCM16/24/32、float32、单双声道、线性重采样到 48k 单声道）
│  ├─ CalibrationRunner.cs       窗口编排：匹配/选铃 → e=t_ring−t_switch → 拟合 → 死区 → 写偏移 → 历史
│  ├─ DriftFitter.cs             最小二乘拟合 + 人工校准跳变检测 + 斜率/外推护栏
│  ├─ CorrectionPolicy.cs        死区门控（判据＝所需偏移与当前偏移之差）
│  ├─ SettingsOffsetApplier.cs   写「应用设置·时钟·时间偏移」（原版内核可用）
│  ├─ IOffsetApplier.cs          应用通道接口（含 CurrentOffsetSeconds 读取）
│  └─ CalibrationHistory.cs      结构化 JSONL（协议 v1，含 MatchedTemplate/MatchNcc/MatchSpectral）
├─ Settings/BellCalibrationSettingsPage.axaml(.cs)   设置页（含模板匹配卡片与调试转存开关）
├─ Templates/{上课铃,下课铃}.wav  2 个模板（各 2.5s，48k 单声道 PCM16），**均取自本机麦克风实录样本**
│                          （上课铃样本 4.16s 起、下课铃样本 2.06s 起）。旧手机样本模板已弃用，
│                          模板目录内的其它 wav 若不参与匹配，请改名或移除。
│                          若实机实测仍与真实铃声不符（NCC ≤0.45）→ 下一步用 dump 实录铃声重建模板
└─ tools/TemplateTool/           离线工具（复用生产代码）：scan 事件定位 / extract 提取模板 / selftest 匹配自测
   tools/README.md               工具用法与 2026-09-11 实测数据（事件定位表 + 4×4 匹配矩阵）
```

**单窗口数据流**：
边界前 20s 开麦 → 音频回调并行做「RMS 持续段检测」与「音频缓冲写 + 采样↔墙钟锚点」→ 窗口结束 →
**模板匹配**（搜索范围 = 标称边界 ± `ToleranceSeconds`，取「通过阈值中 NCC 最高」者，t_ring = 模板起响点，毫秒级）→
命中则用之，否则回退启发式（边界 ±12s 内「持续 ≥1s 且最响」段）→
`e = t_ring − t_switch`（t_switch 由 `BoundaryReached` 记录，两者同墙钟，未知偏移自动抵消）→
`DriftFitter` 采样并预测「当前所需偏移」→ `CorrectionPolicy` 死区判定 → 写 `Settings.TimeOffsetSeconds` → 日志 + JSONL。

---

## 5. 关键设计取舍（为什么长这样）

下面每一条都是被一次真实故障逼出来的；按「触发原因」读即可理解设计意图。

| 设计取舍 | 触发原因（真实故障） |
|---|---|
| 只写「应用设置·时间偏移」，**彻底不做课表平移** | 平移会污染用户课表档案 |
| 删除「启发式选铃」这条回退路径 | 它只能回答「哪个段最响」，实测误差 −7.8 / −6.2 / +8.1 / +4.9 s |
| 测量口径取 `e = t_ring − t_switch`（改用实测误差） | 旧的「残差」口径看不到陈旧偏移，符号交替时永不动作（实机曾长期挂着 4.6 s 误差） |
| 判据换代：**起响沿闸门**为主判据 | 10 个实机 dump 证明精确波形模板跨边界 0 命中；且每个边界附近有两次事件（主铃在 ±0.6 s 内、另有一次提前 6~15 s 的更强起响） |
| 窗内**冻结内核偏移**，所有与音频比较的量统一到裸墙钟域 | 时间戳取自系统钟、边界取自内核时钟（含偏移），直接相减会整体错位一个偏移量 |
| 模板匹配加「攻击沿校验」并要求攻击沿**优先** | 噪声底 −23 dBFS 的窗口里，模板在连续噪声中挑到局部最大（NCC 0.804）并给出偏晚 1 s 的起响点 |
| 整窗静默在入口作废 | 麦克风未进音时「噪声除噪声」也能凑出假的起响比值，且该结果曾以 `detected` 写进历史 |
| 偏移估计改用**近期样本中位数**，趋势外推默认关闭 | 从 ±1.5 s 的测量噪声里估斜率并外推，会让偏移在相邻边界间震荡 ±1.5 s |
| 大误差复核：一次要改偏移超过 ±3 s 即拒写 | 校铃钟被人工大规模校准或系统钟跳变时，不应被静默改掉几秒 |
| 放学边界回退用「本节课剩余时间」布防 | 当天最后一节之后内核下课倒计时恒为 0，原先放学边界从不布防 |
| 噪声底只在安静期更新；同簇合并（连击铃算一次） | 旧实现把铃声持续段中段当成起响点（实测偏晚 2.3 s）；连击铃被时长门槛整段滤掉 |

---
---

## 6. 已验证 / 未验证

**已验证 ✅**

- **实机模板匹配（2026-09-11 晚，本机麦模板，两个 dump）**：
  - 21:30 下课（噪声底 −52.7 dBFS）：下课铃 **NCC 0.968 / 音色 0.902**（对齐点 22.75 s，分离度 0.929），上课铃 0.020 → 0 命中；插件记录 `MatchedTemplate=下课铃, MatchNcc=0.9682`、`t_ring=21:30:02.177`。
    对齐点 + dump 起点 ≈ 插件 `t_ring` → **钟域修复生效**。
  - 21:40 上课（噪声底 −23.0 dBFS）：上课铃最高 NCC 0.804 / 音色 0.766 —— 两项都过阈值但**是连续噪声里的伪匹配**（该窗整段无静默，`e` 比相邻边界差 0.35 s）。
    攻击沿校验把它拒掉（命中数 59/74 → 0/74），而干净窗口不受影响（59/75 不变）。
  - **结论：干净窗口可校准，吵闹窗口宁可不校准也不用脏值**（详见 `plugin/tools/README.md` 的验收表）。
- 写「应用设置·时间偏移」通道解析成功（启动日志：`已解析到设置项 Settings.TimeOffsetSeconds`）；
- 模板加载成功（`已加载 2 个模板`，均为本机麦样本）；
- **离线匹配判别力**（`tools/TemplateTool selftest`，2026-09-11 实测，详见 `tools/README.md` 的 4×4 矩阵）：
  同源 NCC 1.000 / 音色 1.000；跨类（上课模板 ↔ 下课录音）最高仅 0.314；本机麦新样本 ↔ 手机旧样本 0.541~0.879；
- **事件定位正确**（`TemplateTool scan`）：本机麦上课铃 4.16–6.01 s、下课铃 2.06–3.54 s；旧手机上课铃 13.13–15.26 s、下课铃 12.27–14.52 s
  —— 尾部 30~37 s 的环境瞬态已不再被误选为模板；
- 实测误差可达 **9 ms**（09-10 17:40 边界，当时偏移已收敛）；
- 课表乱序导致的「下课倒计时 + 下课边界布防」双重失效已定位并修复（时间表重排后两者同时恢复）；
- 内核补丁已撤回、系统校时已开启。

**未验证 / 待办 ⚠️**

- **起响沿闸门实机验证（2026-09-12 下午 5 个边界）**：

| 边界 | 类型 | 闸门 | 比值 | 起响点偏差 | e（铃−切换） | 噪声底 |
|---|---|---|---|---|---|---|
| 14:00 | 上课 | ✅ | **79.0×** | +0.30 s | −0.30 s | −54.7 dBFS |
| 14:40 | 下课 | ✅ | 5.1× | +0.53 s | −0.53 s | −53.9 dBFS |
| 14:50 | 上课 | ❌ 最佳 3.5× | — | — | −6.20 s（启发式脏值） | **−23.7 dBFS** |
| 15:30 | 下课 | ✅ | 35.6× | −1.38 s | +1.38 s | −52.2 dBFS |
| 15:40 | 上课 | ✅ | 4.1× | +1.83 s | −1.83 s | −24.4 dBFS |

  **4/5 命中**（含一次上课边界、比值 79×）；命中时 `e` 全部落在 −1.83~+1.38 s，
  比改用闸门之前的同一批边界（−9.6~+8.1 s）散乱度降了一个数量级。
- **兜底拒写保护**。隐患 = 14:50 闸门未命中 → 回退启发式 → e=−6.2 s 脏测量，
  自动应用开着的话**一次就能把偏移打飞 6 秒**。修法 = `FallbackErrorLimitSeconds = 2.0`：
  非可信来源（启发式）且 |e| 超过该值即**拒写**；可信来源（闸门/模板）一律放行。
  依据：收敛状态真实误差观测范围是 −1.97~+1.23 s，「启发式 + |e|>2 s」必然是选错事件。
  已用 10 个边界回放逐条验证：只有 14:50 被拦，其余全部放行。
- **启发式选铃已彻底移除 + 大误差复核**（用户要求「遇到较大误差时坚决不用脏值、也不用已被确认不准的办法」）：
  - `RingCandidateSelector.SelectBell` 从源码删除（原处保留一段「为何移除」的说明，防止被重新接上）。
    实测其误差：08:10 → −7.8 s、14:50 → −6.2 s、11:20 → +8.1 s、10:30 → +4.9 s。
  - 现在只有两条测量路径，**二者都带独立真伪判据**：起响沿闸门（±2 s + 比值 ≥4×）、
    模板匹配（NCC/音色阈值 + 攻击沿校验）。两条都不中 → 结局 `no-trusted-match`，不推算、不写偏移。
  - 新增 `LargeErrorLimitSeconds = 3.0`：即便来源可信，一次要改偏移 >3 s 也拒写并告警。
  - 回放 15 个实机 dump：**10 个有可信测量、5 个无可信测量**（5 个全是噪声底 > −30 dBFS 的吵闹窗口）。
- **自动应用仍关闭（学习模式观察中）**。2026-09-12 12:05~12:06 曾短暂开启 47 分钟（期间无边界，偏移未被改动）。
  用户明确的最终意图：**自动校准开启时直接写参数，不受手动校准影响**；`DriftFitter` 行为与之相符
  （样本 1 个时直接取该样本写入，≥2 个才拟合），重新开启时无需改代码。
  配置备份：`BellCalibrationSettings.json.bak-before-autopapply`；当前偏移 **−1.99 s**。
  **开启前的判据**：闸门命中率稳定（2026-09-12 全天 10/15）、命中时 `e` 落在 ±2 s 内 —— 该条件已满足；
  且当前版本已保证「脏值进不来」。
- **闸门未命中的两次都不在窗口宽度上**（11:20 最佳 3.7×、14:50 最佳 3.5×）：
  15:40 那次 +1.83 s 已在 ±2 s 边缘命中，说明 `GateSeconds=2.0` 够用；问题是**比值低于下限 4.0×**，
  这是当前约 20% 边界被漏掉的来源。若后续确认这些是缓起响的真主铃，再把 `OnsetGate.RatioMin` 降到 3.0~2.5
  （代价：候选变多，需防误判）。
- **`DeadZoneSeconds=0.3`** 仍决定「写不写偏移」：|所需偏移 − 当前偏移| < 0.3 s 时不动作。
- **`e` 的可用样本口径已变**：改用起响沿闸门之前的那些 −9.6 / +8.1 / −7.8 s 记录全部作废
  （选错了提前事件），不要拿它们评估收敛性。
- **每个边界附近有两次事件**（主铃 + 提前 6~15 s 的强起响）—— 这条推翻了本文档早期的「每边界最多一段铃声、无预备铃」假设。

---

## 7. 下一步（按优先级，含具体命令）

### 步骤 0：确保实机跑的是当前版本

```powershell
$src = '<本仓库开发目录>\plugin'
$dst = '<ClassIsland 便携版根目录>\data\Plugins\belltime.calibration'
$cfg = '<ClassIsland 便携版根目录>\data\Config\Plugins\belltime.calibration'
Stop-Process -Name 'ClassIsland.Desktop' -Force; Start-Sleep 3   # 必须确认进程已退出，否则 DLL 被占用
Copy-Item "$src\bin\Release\net8.0\BellTimeCalibration.dll","$src\bin\Release\net8.0\BellTimeCalibration.deps.json","$src\manifest.yml" $dst -Force
Copy-Item "$src\Templates\*.wav" "$cfg\Templates\" -Force          # 4 个模板
Start-Process '<ClassIsland 便携版根目录>\app-2.1.0.1-0\ClassIsland.Desktop.exe'
```

部署后启动日志应看到 `已加载 4 个模板（上课铃 2.50s、下课铃 2.50s、上课铃 2.50s、下课铃 2.50s）`。

### 步骤 1：抓一个边界 dump，实测本机麦 NCC（决定阈值的唯一依据）

边界前 ≥1 分钟启动进程，等窗口结束（约 40 s）后：

```powershell
$cfg = '<ClassIsland 便携版根目录>\data\Config\Plugins\belltime.calibration'
Get-ChildItem "$cfg\Dumps"
$tool = '<本仓库开发目录>\plugin\tools\TemplateTool\bin\Release\net8.0\TemplateTool.exe'
& $tool scan "<dump.wav>"                                            # 铃响在哪几秒、是否被环境瞬态污染
& $tool selftest "$cfg\Templates\上课铃.wav" "<dump.wav>" 0.6 0.45   # 本机麦模板 vs 实机音频
& $tool selftest "$cfg\Templates\下课铃.wav" "<dump.wav>" 0.6 0.45
```

要点：本机麦新样本与实机 dump 同设备，期望 NCC ≥0.8；若 ≤0.45，则改用 dump 本身截模板（见 `tools/README.md` 的 `extract` 用法）。

### 步骤 2：标定阈值

- 正样本：dump 中铃声段 → 实测 NCC；
- 负样本：无铃声的窗口 → 实测 NCC（离线已知跨类上限 0.314）；
- 取两者中间值作为 `MatchNccMin`（设置页可改，无需重启生效）。

### 步骤 3：开启自动应用并观察闭环

1. 设置页：关闭「学习模式」、打开「自动应用校准偏移」；
2. 观察日志：`拟合判定：实测误差 e=… → 预测当前所需偏移 … → 写不写`、`应用设置·时间偏移 应用时间偏移：旧 → 新`；
3. 核对 `Logs\calibration-history.jsonl` 中 `e(铃−切换)` 是否稳定落在 ±0.3 s。

### 步骤 4：收尾

- 关闭 `DebugDumpAudio`（隐私：默认绝不落盘）；
- 可选：把运行中的内核二进制换成真原版（换 `app-2.1.0.1-0.bak-official` 或重新构建发布）；
- 同步 README/文档。

---

## 8. 常用操作

```powershell
# 构建插件
cd "<本仓库开发目录>\plugin"; dotnet build -c Release

# 部署（必须先停进程，否则 DLL 被占用）
$dst='<ClassIsland 便携版根目录>\data\Plugins\belltime.calibration'
Stop-Process -Name 'ClassIsland.Desktop' -Force
Copy-Item bin\Release\net8.0\BellTimeCalibration.dll,bin\Release\net8.0\BellTimeCalibration.deps.json,bin\Release\net8.0\manifest.yml $dst -Force
Start-Process '<ClassIsland 便携版根目录>\app-2.1.0.1-0\ClassIsland.Desktop.exe'

# 看日志
Get-Content '<ClassIsland 便携版根目录>\data\Config\Plugins\belltime.calibration\Logs\belltimecalibration-2026-09-11.log' -Tail 30

# 当前偏移
Select-String -Path '<ClassIsland 便携版根目录>\data\Settings.json' -Pattern 'TimeOffsetSeconds'
```

---

## 9. 坑与注意事项

1. **插件配置只在启动时读一次**（无文件监听）：改 `BellCalibrationSettings.json` 必须重启 ClassIsland 才生效；运行中改文件会被内存值回写覆盖。
2. **DLL 被运行中的进程占用**：部署前必须停进程；`taskkill` 后要确认进程真的退出再复制。
3. **课表项必须按时间升序**（见第 3 节第 2 条）。
4. **忽略列表**按 `HH:mm` 精确匹配边界显示时刻（默认 `08:00,18:30`），新出现的边界要加进去。
5. **隐私**：默认音频只在内存（`CaptureAudio`），仅当 `DebugDumpAudio=true` 才写 `Dumps\`；关闭后不再落盘。
6. **准备铃/预备铃**：搜索范围已限制在边界 ±`ToleranceSeconds`（当前 12s）；若正铃本身漂移超过该范围会搜不到，需要相应放大。
7. 运行中的内核二进制仍是 9/7 由工作区源码构建的版本（比原版多一个**未被使用**的可写属性）；插件与该差异无关。

---

## 10. 待用户确认的问题

1. 下课铃与上课铃是否来自同一套打铃系统（同一钟）？若是，两者的漂移应一致，可共用拟合。
2. 是否同意开启「自动应用」（插件接管偏移，会覆盖手动设置）；何时开启？
3. 正铃前约 15s 的预备铃是否需要参与校准？（当前策略：忽略，只对正铃校准）
4. 运行中的内核二进制是否需要替换为真原版？

---

## 11. 本目录内容说明

```
<本仓库开发目录>\
├─ HANDOFF.md                 ← 本文件（新会话从这里开始读）
├─ plugin\                    ← 插件源码（含 Templates、tools；bin 已含最近构建产物，obj 未迁移）
├─ (无音频)                   ← 录音不随仓库分发；使用者自行录制并放在插件配置目录或经设置页导入
└─ deploy-snapshot\           ← 部署快照（当前 DLL、manifest、插件配置、模板、宿主 Settings.json、当日日志与历史）
```

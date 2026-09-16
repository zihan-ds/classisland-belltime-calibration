# 铃声自动校时（BellTime Calibration）

监听校园广播铃声、自动把 ClassIsland 的 **应用设置 → 时钟 → 时间偏移** 校准到学校铃声系统时间的 ClassIsland 2.x 插件。

> **目标**：铃响时刻 ClassIsland 的时间误差 ≤ 0.5s。**不改课表**、**不需要修改内核**（原版内核即可用）。

| 项 | 值 |
|---|---|
| 插件 ID | `belltime.calibration` |
| 版本 | **1.0.1** |
| apiVersion | 2.0.0.0（ClassIsland 2.x） |
| 状态 | 学习模式默认开启；实机多日连续运行验证 |

> **安全默认**：安装后默认处于**学习模式**——只记录铃声识别与偏移推算结果，**不会**自动改动任何时间设置。请观察数日、核对日志中误差合理后再手动开启自动应用（见下文「快速上手」）。

---

## 它解决什么问题

学校的钟不一定和电脑准，ClassIsland 依据本机时间来切换上课/下课。本插件在每个课表时间边界附近短暂开启麦克风，**听取真实校铃的起响时刻**，据此推算 ClassIsland 应设的时间偏移，使课表切换与实际铃声对齐——**校准语义为「跟随学校广播铃钟」**。

## 工作原理

1. **布防**：跟随 ClassIsland 内核的主计时器事件（`ILessonsService`），在距某一边界（上课或下课）≤「布防提前秒数」时进入监听窗口，同一边界只布防一次。
   当天最后一节课（放学铃）之后内核给不出下课倒计时，此时回退用「本节课剩余时间」，**保证放学边界同样布防**。
2. **开麦**：窗口期内临时启用系统默认输入设备（`SoundFlow`/miniaudio，48 kHz 单声道），**仅在内存中**处理，不落盘。
3. **判铃**：两条路径，**都带真伪判据**；两条都不中即判「无可信测量」并跳过本次：
   - **起响沿闸门（主判据）**：在「课表边界 ±2 s」内找**起响比值最大**处；比值 =「起响后 0.5 s 峰值 RMS ÷ 起响前 0.3 s 基线 RMS」。
     真铃声是突然起响（实测比值 4~346×），环境声是渐强的（连续噪声里的伪匹配仅 1~3×）；
     ±2 s 的时间门同时排除每个边界附近**提前 6~15 s 的那次更强起响**。
   - **模板匹配（辅助）**：NCC 粗定位 → 4 kHz 精定位 → 音色确认 → 攻击沿校验，四道判据全过才采用。
     需要使用者自行提供铃声样本（见下文「提供你自己的铃声样本」）。
   - **不回退「启发式选铃」**：该判据已被实机证伪（误差可达数秒），**宁可不校准也不用脏值**。
4. **推算**：`e = t_ring − t_switch`（铃响墙钟 − 宿主实际切换墙钟）。二者同为墙钟，
   任何未知的偏移来源（内核额外偏移、手工调整、系统钟偏差）自动抵消。
5. **偏移估计与门控**：
   - 每次测量得到「让误差归零所需的绝对偏移」= 当前偏移 − e；
   - **写值 = 近期样本的中位数**（默认 6 小时窗口；样本不足 3 个时用全部）。
     中位数对单次测量的抖动天然稳健；且「所需偏移」只取决于铃的真实滞后、与当前偏移无关，从任何起点都会收敛到同一值；
   - 趋势外推**默认关闭**，仅当「样本 ≥12 个、时间跨度 ≥6 小时、拟合残差 <0.3 s」三项同时满足才启用
     ——即噪声已被平均到能看出真实走时之后，才允许追趋势；
   - **死区门控**：|写值 − 当前偏移| < 死区（默认 0.3 s）时不动，避免跟着抖动反复微调。
6. **防御**：整窗静默（麦克风未进音）直接作废；一次要改偏移超过 ±3 s 时**拒写并告警**。

## 校准结果如何应用（通道：应用设置 → 时钟 → 时间偏移）

| 通道 | 说明 |
|---|---|
| **应用设置 → 时钟 → 时间偏移** | 即内核设置项 `Settings.TimeOffsetSeconds`。插件经 `IAppHost.Host`（public static IHost）在宿主 DI 中取到内核 `SettingsService`，反射读取/写入其 `Settings.TimeOffsetSeconds`——**原版内核即可用，无需任何内核改动**。写入后立即生效（宿主每次取时间都会读该设置）、由 `SettingsService` 自动落盘 `Settings.json`、设置页「时钟 → 时间偏移」同步可见 |

> ⚠️ 写偏移是**绝对写入**（写入「所需偏移」），会覆盖你之前在 ClassIsland 里手动设置的时间偏移——这正是「跟随校铃」的语义。
>
> ⚠️ 插件任何情况下都**不修改课表档案**（只写时间偏移，不碰时间表），也**不依赖任何内核补丁**，原版内核即可运行。

## 系统要求

- ClassIsland **2.x**（插件 apiVersion 2.0.0.0）
- Windows（miniaudio/WASAPI），需要可用的默认输入设备（麦克风）
- 首次使用需在系统设置中授予 ClassIsland **麦克风权限**，否则窗口期采到全静音，窗口会被判作无效跳过
- **自动应用**额外要求宿主内核含可写偏移属性（`TimeOffsetSeconds`/`OffsetSeconds`），否则仅学习模式记录可用

## 安装

把 `BellTimeCalibration-<版本>.cipx` 拖入 ClassIsland「设置 → 插件」安装；或手动把 `manifest.yml` +
`BellTimeCalibration.dll` + `BellTimeCalibration.deps.json` + `SoundFlow.dll` + `miniaudio.dll` 复制到
ClassIsland 数据目录的 `Plugins\belltime.calibration\`（安装版默认在 `data\Plugins\belltime.calibration\`）后重启 ClassIsland。
主日志出现 `铃声自动校时(belltime.calibration,<版本>)` 即加载成功。首次使用需在系统设置中授予 ClassIsland 麦克风权限。

> **本插件不自带铃声样本。** 随包的 `Templates\上课铃.wav`、`Templates\下课铃.wav` 是**静音占位**，
> 启动日志会显示「跳过 …：全零静音（随包占位文件…）」。请按下节导入本校录音样本，模板匹配才会启用。
> 主判据「起响沿闸门」不依赖样本，未导入样本时插件照常工作（只是少了毫秒级精定位这一路辅助）。

## 提供你自己的铃声样本

铃声音色每所学校都不同，必须自行录制。摘要（完整说明见仓库根目录 `README.md`）：

- 用**装 ClassIsland 那台电脑的麦克风**、在真实响铃时录一段 WAV（几秒到几十秒，开头多留些环境声更好）；
- 上课铃、下课铃**各录一段**（两者音色差别很大，不可混用同一段）；`.m4a`/`.mp3` 需先用 ffmpeg 转 WAV；
- **设置页导入（推荐）**：设置 → 插件 → 铃声自动校时 →「模板匹配（铃声样本）」→ 点「浏览…」选中录音，
  需要时填「截取区间」（如 `12.0-14.5`，留空=自动取最响且持续 ≥1.5 s 的段）；选完**立即生效**，日志会打印
  `已选择铃声样本：…模板库已重载（已加载 N 个模板）`；
- **直接放文件**：把截好的 2.5 s 模板放到插件配置目录 `Templates\`（文件名含「上课」/「下课」），重启 ClassIsland。

## 快速上手

1. 安装后**保持学习模式开启**，正常使用数日。
2. 每次边界若识别到铃声，插件日志会记录 `检测到铃声 → t_ring / delta`。
3. 核对 delta 是否符合预期（对齐良好时应趋近 0；稳定为一个非零值说明本机时间与校铃时钟存在固有偏差，该值即期望的偏移量；忽大忽小则多为环境误检）。
4. 确认合理后：在插件设置页**关闭学习模式**，再开启「自动应用校准偏移」。

## 设置项

| 设置 | 默认 | 说明 |
|---|---|---|
| 启用铃声自动校时 | 开 | 总开关，关闭后不监听也不应用 |
| 学习模式 | **开** | 只记录识别结果，不应用偏移 |
| 自动应用校准偏移 | 关 | 需先关闭学习模式才生效；且要求宿主内核含可写偏移属性（`TimeOffsetSeconds`/`OffsetSeconds`），否则自动应用停用、仅学习记录 |
| 布防提前秒数 | 15 | 距时间边界多少秒开始布防监听 |
| 边界后监听秒数 | 8 | 越过边界后继续监听多少秒 |
| 响铃判定容差（±秒） | 3 | 过滤开麦静置期（1.5 s）后，取距边界时刻最近的突发候选，仅当偏差 ≤ 本容差才判为有效铃；容差内无候选则本次「未听到铃」并作废（环境突发不得当铃）。此容差同时用作模板匹配的搜索半窗 |
| 应用死区（秒） | 0.3 | 偏移估计值（近期样本中位数）与当前偏移之差小于此值时不动作，否则写入。0.3 s 是把稳态误差压进 0.5 s 的取值——校铃自身抖动的中位值约 0.5 s，是残余误差的物理下限 |
| 识别灵敏度 | 2.0 | 突发能量须超过环境噪声底的倍数（占位参数，语义随版本细化） |

以上设置持久化于 `BellCalibrationSettings.json`（插件配置目录，改动即自动保存）。

## 隐私与安全

- **绝不常驻监听**：麦克风仅在「边界前布防秒数 + 边界后监听秒数」的窗口期内临时启用，其余时间零占用（Windows 任务栏可观察到窗口期的麦克风指示）。
- **绝不落盘**：音频只做内存 RMS 统计，不保存任何音频数据。
- 麦克风被占用 / 无设备 / 无权限时优雅跳过本窗口并记日志，不影响 ClassIsland 其它功能。

## 日志与排查

- 插件日志：`<插件配置目录>\Logs\belltimecalibration-<日期>.log`（安装版默认 `data\Config\Plugins\belltime.calibration\Logs\`）。
- **校准历史（结构化）**：`<插件配置目录>\Logs\calibration-history.jsonl`——每个监听窗口终结时追加一行 JSON（协议 v1），含配置快照、布防/越界墙钟时刻、捕获结局（`skipped-busy`/`init-failed`/`silent-window`/`no-burst`/`no-trusted-match`/`no-ring`/`detected`/`error`；`silent-window`=整窗静默被作废、`no-trusted-match`=闸门与模板均未命中）、全部原始突发候选（含开麦静置伪影，便于复核）、噪声底/峰值 dB、delta/shift 与门控结果。供后续离线调参分析（灵敏度/容差/布防提前量/窗口/死区）。
- 启动时应依次看到：初始化 → 调度器已启动 → `应用设置·时间偏移 可用：已解析到设置项 Settings.TimeOffsetSeconds…` + `应用通道选定：应用设置·时间偏移`（唯一通道；原版内核即可用）→ 执行器就绪；解析失败则见 `[WARN] 应用设置·时间偏移 不可用…`（此时自动应用停用，仅学习模式记录）。
- **偏移样本序列**：`<插件配置目录>\Logs\offset-samples.jsonl`——每个**可信测量**追加一行（`Ts` / `RequiredSec` / `CurrentSec` / `Kind` / `B` / `Applied`），记录参与偏移估计的样本。两个用途：① 进程重启后只载入**当天**样本喂给估计器，避免重启后前几个边界退回「单次测量直接写入」（日志：`偏移样本恢复：载入当天样本 N 个（跳过其它日期 M 个）`）；② 离线画偏移走势、复算估计策略（`ReplayTool fitter <样本序列文件>`）。
- 每次有效铃后会看到一行 `拟合判定：… 预测当前所需偏移 … 与当前差 …`，即「测量 → 拟合 → 写入」的完整判定过程。
- 若某边界日志为「在忽略列表内，本次不监听、不校准」，即命中 `IgnoredBoundaries`（默认 08:00 / 18:30）。
- 若某边界日志为「无突发能量」，多为窗口期内没有实际铃响或麦克风权限问题，属正常作废。
- 若插件未加载：检查 manifest 的 `apiVersion` 需 ≥ 2.0.0.0，且插件目录内 `BellTimeCalibration.dll` / `SoundFlow.dll` / `miniaudio.dll` 齐备。

## 已知限制与路线

- **自触发**：若 ClassIsland 自身被设置为在边界放铃，其铃声可能被本机麦克风拾取并当作校铃——通过学习模式观察与死区缓解；正式采用前请留意日志来源。
- **铃声样本需自行录制导入**：`tools/TemplateTool` 可从长录音提取模板，设置页「浏览…」直接导入；未提供样本时模板匹配不启用，主判据照常工作。
- 学习模式需要数日真实数据才能评估跨天收敛效果。
- **无需任何内核改动**：插件直接写内核设置项 `Settings.TimeOffsetSeconds`，原版内核即可运行。

## 从源码构建

```
dotnet build -c Release          # 输出 bin\Release\net8.0\
```

- 工程引用 `ClassIsland.PluginSdk 2.0.0.2`（`ExcludeAssets=runtime`）与 `SoundFlow 1.4.1`。
- 由于 SDK 的 `ExcludeAssets=runtime` 会沿依赖闭合压制托管运行时拷贝，csproj 对 `SoundFlow.dll` 与 `miniaudio.dll` 做了显式拷贝（含根因注释）——不要删这两行。
- `.cipx` 打包：把 `bin\Release\net8.0` 下 `manifest.yml` + `BellTimeCalibration.dll` + `SoundFlow.dll` + `miniaudio.dll` + 资源（不含 `.pdb`）压入 zip 根目录，扩展名改为 `.cipx`。产物样例：`artifacts\BellTimeCalibration-1.0.0.0.cipx`。

## 目录结构

```
BellTimeCalibration/
├─ BellTimeCalibration.csproj     # net8.0；SDK 2.0.0.2 + SoundFlow 1.4.1（显式拷贝两 DLL）
├─ manifest.yml                   # id/name/apiVersion/entranceAssembly/version
├─ icon.png
├─ Plugin.cs                      # 入口：日志、配置加载与自动保存、设置页注册、AppStarted 自举
├─ Models/BellCalibrationSettings.cs
├─ Services/
│  ├─ Logger.cs                   # 静态日志 → PluginConfigFolder\Logs
│  ├─ CalibrationScheduler.cs     # 订阅内核 tick，边界布防 + BoundaryApproaching/BoundaryReached
│  ├─ MicCaptureService.cs        # 窗口期麦克风捕获（内存处理，不落盘）
│  ├─ RingDetector.cs             # RMS 突发能量检测（纯算法，可单测）
│  ├─ CalibrationRunner.cs        # 窗口编排、delta 推算、门控后触发应用
│  ├─ RingCandidateSelector.cs    # 有效铃选择：开麦静置过滤 + 容差内最近边界候选（纯函数）
│  ├─ CalibrationHistory.cs       # 结构化校准历史 JSONL（协议 v1，供离线调参）
│  ├─ CorrectionPolicy.cs         # 残差死区门控（纯逻辑）
│  ├─ IOffsetApplier.cs           # 应用通道接口（唯一实现为内核设置通道）
│  └─ KernelOffsetApplier.cs      # 反射探测内核可写偏移（唯一应用通道；课表平移已移除）
└─ Settings/BellCalibrationSettingsPage.axaml(.cs)
```

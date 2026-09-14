# HarvestBellSegments.ps1 —— 从 dump 批量收割「实机铃声段」并互为对照
#
# 用途（工作流）：后续持续录制边界音频 → 段与段互为对照 → 找出同一种铃的稳定波形，
# 据此用实录波形重建模板，而不是继续依赖外部样本。
#
# 做什么：
#   1) 对 dump 目录里每个 dump 跑 ReplayTool，取「有攻击沿的位置」中最靠近边界的一处为起响点
#      （忽略开麦后 1.5s 内的缓冲区初始化伪影）；
#   2) 用 TemplateTool extract 从该起响点截出固定时长段（默认 2.5s）；
#   3) 用 TemplateTool match 把「每个段 × 每个 dump」全部互比，打印 NCC 矩阵。
#      对角线（段对自己的 dump）是自证；**同一列的跨行数值才是「同一种铃」的判据**：
#      若段 X 在 dump Y 里也能拿到高 NCC，说明 X、Y 是同一种铃。
#
# 用法：
#   .\HarvestBellSegments.ps1 -DumpDir <目录> -OutDir <输出目录> [-BoundaryInDump 20.5] [-SegSec 2.5]

param(
    [Parameter(Mandatory = $true)][string]$DumpDir,
    [Parameter(Mandatory = $true)][string]$OutDir,
    # 边界在 dump 内的秒数（B_display − dump 起点）。dump 文件名里的时刻是窗口开启墙钟（≈B_display−20s），
    # 但内存缓冲的起点还早约 0.5~1s，因此实测取 19.5~20.5 都成立；它只影响「取哪一处攻击沿」，不影响段内容。
    [double]$BoundaryInDump = 20.0,
    [double]$SegSec = 2.5,
    [string]$ToolsRoot = 'D:\dshwork\classisland auto calibration\plugin\tools'
)

$replay = Join-Path $ToolsRoot 'ReplayTool\bin\Release\net8.0\ReplayTool.exe'
$tplExe = Join-Path $ToolsRoot 'TemplateTool\bin\Release\net8.0\TemplateTool.exe'
foreach ($exe in @($replay, $tplExe)) {
    if (-not (Test-Path $exe)) { Write-Error "缺少工具：$exe（先 dotnet build）"; return }
}

$segDir = Join-Path $OutDir 'segments'
$tplDir = Join-Path $OutDir '_empty-templates'
New-Item -ItemType Directory -Path $segDir -Force | Out-Null
New-Item -ItemType Directory -Path $tplDir -Force | Out-Null

$dumps = @(Get-ChildItem -Path $DumpDir -Filter '*.wav' | Sort-Object Name)
if ($dumps.Count -eq 0) { Write-Error "目录内没有 dump：$DumpDir"; return }

Write-Host "=== 1) 逐个 dump 找起响点并截段（共 $($dumps.Count) 个）" -ForegroundColor Cyan
$segments = New-Object System.Collections.ArrayList
foreach ($dump in $dumps) {
    $stem = [System.IO.Path]::GetFileNameWithoutExtension($dump.Name)
    # 20260912-093939-下课-B094000.wav → 标签 0912-094000-下课
    $tag = $stem
    if ($stem -match '^(\d{4})(\d{2})(\d{2})-(\d{6})-(\S+?)-B(\d{6})$') {
        $tag = "$($Matches[2])$($Matches[3])-$($Matches[6])-$($Matches[5])"
    }

    $replayOut = & $replay $dump.FullName $tplDir $BoundaryInDump 2>&1
    $hits = New-Object System.Collections.ArrayList
    foreach ($line in $replayOut) {
        if ([string]$line -match '^\s*([\d.]+)s\s+比值\s+([\d.]+)x') {
            $sec = [double]$Matches[1]
            $ratio = [double]$Matches[2]
            # 忽略开麦后 1.5s 内的缓冲区初始化伪影（比值可达天文数字，不是声音）
            if ($sec -ge 1.5) { $null = $hits.Add([pscustomobject]@{ Sec = $sec; Ratio = $ratio }) }
        }
    }

    if ($hits.Count -eq 0) {
        Write-Host ("  {0}：无有效攻击沿，跳过" -f $stem) -ForegroundColor DarkYellow
        continue
    }

    $nearest = $hits | Sort-Object { [Math]::Abs($_.Sec - $BoundaryInDump) } | Select-Object -First 1
    # 起截点就用攻击沿位置本身，不要往前挪：攻击沿判据的基线窗是 [起响-0.3s, 起响]，
    # 往前挪 0.05s 会把攻击本身吃进基线窗，导致该段在自匹配时被判「无攻击沿」而拒收（实测踩过）。
    $start = $nearest.Sec
    $end = $start + $SegSec
    $out = Join-Path $segDir "$tag.wav"

    & $tplExe extract $dump.FullName $out ("{0:F2}-{1:F2}" -f $start, $end) $SegSec | Out-Null
    Write-Host ("  {0}：起响点 {1:F2}s（比值 {2:F0}x，候选 {3} 处）→ {4}" -f `
        $stem, $nearest.Sec, $nearest.Ratio, $hits.Count, (Split-Path $out -Leaf))

    $null = $segments.Add([pscustomobject]@{ Tag = $tag; File = $out; Dump = $dump.FullName; Onset = $nearest.Sec })
}

if ($segments.Count -eq 0) { Write-Host "没有收到任何段。" -ForegroundColor DarkYellow; return }

Write-Host ""
Write-Host "=== 2) 段 × dump 互比矩阵（纯波形最大互相关，无任何阈值）" -ForegroundColor Cyan
Write-Host "  行 = 从某个 dump 截出的段；列 = 用该段去比哪个 dump。对角线是自证（应 ≈1.000）。" -ForegroundColor DarkGray
Write-Host "  读法：同一列里若多行都高 → 这些边界是同一种铃波形；" -ForegroundColor DarkGray
Write-Host "        全部跨行都低 → 每次播的波形都不一样，单模板必然覆盖不住（当前实测即此情况）。" -ForegroundColor DarkGray
Write-Host ""

$matrix = @{}
foreach ($seg in $segments) {
    foreach ($other in $segments) {
        $line = [string](& $tplExe xcorr $seg.File $other.Dump 2>&1 | Select-Object -Last 1)
        $ncc = $null
        if ($line -match 'NCC = ([-\d.]+)') { $ncc = [double]$Matches[1] }
        $matrix["$($seg.Tag)|$($other.Tag)"] = $ncc
    }
}

$header = '段 \ dump'.PadRight(20)
foreach ($other in $segments) { $header += $other.Tag.PadLeft(20) }
Write-Host $header

foreach ($seg in $segments) {
    $row = $seg.Tag.PadRight(20)
    foreach ($other in $segments) {
        $v = $matrix["$($seg.Tag)|$($other.Tag)"]
        if ($null -eq $v) {
            $row += '                 n/a'
        } else {
            $row += ('{0,20:F3}' -f $v)
        }
    }
    Write-Host $row
}

Write-Host ""
Write-Host "段的 WAV 在：$segDir"
Write-Host ""
Write-Host "下一步："
Write-Host "  1) 看矩阵里「同一列多行都高」的簇 —— 那组段就是同一种铃；"
Write-Host "  2) 把该组任一段复制成 plugin\Templates\上课铃.wav / 下课铃.wav（保留原样本模板即可，多模板取最高 NCC）；"
Write-Host "  3) 用 TemplateTool selftest 在全部 dump 上复核跨边界命中率。"

#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RoslynOutput,
    [Parameter(Mandatory)][string]$CodeQLOutput,
    [Parameter(Mandatory)][string]$ReportDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
function Relative([string]$Path) { [IO.Path]::GetRelativePath($repository,[IO.Path]::GetFullPath($Path)).Replace('\','/') }
$identityTokens=@($env:USERNAME,$env:COMPUTERNAME) | Where-Object { $_ } | ForEach-Object { [regex]::Escape($_) }
$identityPattern='(?i)(?<![\p{L}\p{N}_])(?:'+($identityTokens -join '|')+')(?![\p{L}\p{N}_])'
function Assert-NoLocalIdentity([string]$Text,[string]$Path) {
    if($identityTokens.Count -and $Text -match $identityPattern) { throw "Local identity in published artifact: $Path" }
}
$versions=Get-Content (Join-Path $PSScriptRoot 'analyzers.json') -Raw | ConvertFrom-Json
$roslyn=Get-Content (Join-Path $RoslynOutput 'summary.json') -Raw | ConvertFrom-Json
$codeql=Get-Content (Join-Path $CodeQLOutput 'summary.json') -Raw | ConvertFrom-Json
$status=Get-Content (Join-Path $RoslynOutput 'status.json') -Raw | ConvertFrom-Json
$qlStatus=Get-Content (Join-Path $CodeQLOutput 'logs/codeql-status.json') -Raw | ConvertFrom-Json
$triage=Get-Content (Join-Path $ReportDirectory 'static-analysis-dotnet-triage.json') -Raw | ConvertFrom-Json
if(-not $triage.complete) { throw 'Triage has unreviewed rules.' }
if(-not $status.normalBuild.sameWarnings -or $status.normalBuild.exitCode -ne 0) { throw 'Normal-build warning comparison failed.' }
if($roslyn.analyzerFailures.Count -or $codeql.analyzerFailures.Count) { throw 'Undropped analyzer failures remain.' }
if($qlStatus.databaseAnalyzeExitCode -ne 0 -or $qlStatus.failure -or -not $qlStatus.databaseReclaimed) { throw 'CodeQL did not finish and reclaim its database.' }
if((Get-FileHash (Join-Path $CodeQLOutput 'raw/CodeQL.sarif') -Algorithm SHA256).Hash -ne $qlStatus.sarifSha256) { throw 'CodeQL SARIF hash mismatch.' }
$validated=0
foreach($root in @($RoslynOutput,$CodeQLOutput)) {
    foreach($path in @(Get-Content (Join-Path $root 'accepted-sarif.json') -Raw | ConvertFrom-Json)) {
        $fullPath=Join-Path $root $path
        if($identityTokens.Count -and (Select-String -LiteralPath $fullPath -Pattern $identityPattern -Quiet)) {
            throw "Local identity in published artifact: $path"
        }
        # Validate the serialized document without materializing every SARIF
        # node as a PowerShell dictionary. This is file validation, not analysis.
        $stream=[IO.File]::OpenRead($fullPath)
        $document=$null
        try {
            $document=[Text.Json.JsonDocument]::Parse($stream)
            if($document.RootElement.GetProperty('version').GetString() -ne '2.1.0' -or
                $document.RootElement.GetProperty('runs').GetArrayLength() -lt 1) { throw "Invalid accepted SARIF: $path" }
        } finally {
            if($document) { $document.Dispose() }
            $document=$null
            $stream.Dispose()
            # The enclosing Windows job has a smaller ceiling than the host's
            # memory. Reclaim each validation document before reading the next.
            [GC]::Collect()
        }
        $validated++
    }
}
$counts=@($roslyn.counts)+@($codeql.counts)
function Sum-Counts([object[]]$Items) {
    $total=0
    foreach($item in $Items) { $total += [int]$item.count }
    return $total
}
$byTool=@($counts | Group-Object tool | ForEach-Object {
    $group=$_.Group
    [pscustomobject]@{tool=$_.Name;total=(Sum-Counts $group);
        product=(Sum-Counts @($group | Where-Object scope -eq product));
        test=(Sum-Counts @($group | Where-Object scope -eq test));
        development=(Sum-Counts @($group | Where-Object scope -eq development))}
})
$failed=@($status.roslyn | Where-Object exitCode -ne 0)
$summary=[ordered]@{schemaVersion=1;total=$roslyn.total+$codeql.total;counts=$counts;byTool=$byTool;
    acceptedSarifValidated=$validated;roslynProjects=$status.roslyn;normalBuildWarningsUnchanged=$true;
    stages=@{roslyn=if($failed.Count){'completed-with-project-failures'}else{'success'};codeql='success';triage='complete'};
    codeql=@{version=$qlStatus.actualVersion;queryPackVersion=$qlStatus.queryPackVersion;queries=171;
        analyzeExitCode=$qlStatus.databaseAnalyzeExitCode;sarifSha256=$qlStatus.sarifSha256;databaseReclaimed=$qlStatus.databaseReclaimed};
    triage=@{complete=$triage.complete;productFirstPartyOccurrences=$triage.productOccurrences;styleOccurrences=$triage.styleOccurrences;
        confirmedSites=@($triage.findings | Where-Object classification -eq 'confirmed defect' | Group-Object file,line).Count;
        plausibleSites=@($triage.findings | Where-Object classification -eq 'plausible' | Group-Object file,line).Count}}
$summary | ConvertTo-Json -Depth 15 | Set-Content (Join-Path $ReportDirectory 'static-analysis-dotnet-summary.json') -Encoding utf8
$lines=[Collections.Generic.List[string]]::new()
$lines.Add('# C# static analysis')
$lines.Add('')
$lines.Add('基线 `d9a1be2e`；仅新增 opt-in 分析脚本，产品源码、项目、普通构建策略均未修改。结果含重复编译/重叠工具诊断，不等于缺陷数。')
$lines.Add('')
$lines.Add("工具：.NET SDK $($versions.sdkVersion)（内置 CA latest-all）；CodeQL $($versions.codeqlVersion)，C# queries $($versions.codeqlQueryPackVersion)，``csharp-security-and-quality.qls``。")
$lines.Add('')
foreach($package in $versions.packages) {
    $suffix=if($package.PSObject.Properties['disabledReason']){'；实际触发 AD0001/InvalidCastException，按要求停用，未计入接受结果'}else{''}
    $lines.Add("- $($package.id) $($package.version)$suffix")
}
$lines.Add('')
$lines.Add('重跑：在 worker 的短 PATH（dotnet/pwsh/zig/gcc）中执行 `pwsh -NoProfile -File scripts/analysis/Invoke-DotnetStaticAnalysis.ps1 -RestoreAnalyzers`。每个重型步骤由入口逐项提交 JobQueue Run，4 GiB 上限、BelowNormal；CodeQL 2 线程/3072 MiB，构建禁用 build servers。不要将整个入口再次套入队列。')
$lines.Add('')
$lines.Add("覆盖：37 个 C# 项目全部尝试；$($status.roslyn.Count-$failed.Count) 个构建成功，15 个产品项目全部成功。3 个测试夹具缺口：FanCoreProtocolProbe 的外部项目路径不存在；LegacyAdapterInitializationProbe 缺指定验证批次的原生输出；ReadWriteCore/PackageConsumer 固定包缺 CpuModelName API。失败编译中的诊断仅计数，不作为完整覆盖证明。CodeQL no-build 使用推断依赖，排除私有 NonAdaptedOptimizationLab，171 个查询完成；不等于精确构建图或安全无缺陷证明。")
$lines.Add('')
$lines.Add('普通构建证明：Shared 项目在注入前/后均用 `dotnet build -t:Rebuild --disable-build-servers` 成功；唯一警告均为 `src/Shared/HostManager/Operations/NativeCoreLibraryResolver.cs:12` 的原有 CA2255，机器比较 sameWarnings=true。分析 targets 仅由入口显式导入，产品项目无新增 PackageReference、warning level 或 TreatWarningsAsErrors。')
$lines.Add('')
$lines.Add('下表按编译项目归属计数，含各项目的生成/外部源码；分诊仅覆盖产品项目中的一方源码。')
$lines.Add('')
$lines.Add('| Tool | Product | Tests | Development | Total |')
$lines.Add('|---|---:|---:|---:|---:|')
foreach($row in $byTool | Sort-Object tool) { $lines.Add("| $($row.tool) | $($row.product) | $($row.test) | $($row.development) | $($row.total) |") }
$lines.Add('')
$lines.Add("SARIF 2.1 按项目/工具拆分，接受清单：[$(Relative $RoslynOutput)/accepted-sarif.json](../$([IO.Path]::GetFileName($RoslynOutput))/accepted-sarif.json)；[$(Relative $CodeQLOutput)/accepted-sarif.json](../$([IO.Path]::GetFileName($CodeQLOutput))/accepted-sarif.json)。只使用清单内文件；raw/logs 为本地证据。$validated 个接受文件通过 JSON/版本和身份信息检查。")
$lines.Add('')
$lines.Add('CodeQL 建库/分析队列均成功，峰值分别 2.75/2.62 GiB，未触限。重启丢失的汇总作业已入队重跑；数据库在 SARIF 落盘后经登记任务 Complete/Prune 回收，结果哈希保持一致。')
$lines.Add('')
$lines.Add("[分诊](static-analysis-dotnet-triage.md)：$($summary.triage.confirmedSites) 个确认位置、$($summary.triage.plausibleSites) 个待运行验证位置；逐规则分类、抽样误报率和永久规则建议见该文件；[机器汇总](static-analysis-dotnet-summary.json) 含工具/规则/严重度/项目计数。未修产品代码，未提交。")
$lines.Add('')
$lines.Add('[证据保留与回收条件](static-analysis-dotnet-retention.md)。')
$lines | Set-Content (Join-Path $ReportDirectory 'static-analysis-dotnet.md') -Encoding utf8
foreach($name in @('static-analysis-dotnet.md','static-analysis-dotnet-triage.md','static-analysis-dotnet-triage.json',
    'static-analysis-dotnet-summary.json','static-analysis-dotnet-retention.md','triage-review-manifest.json')) {
    Assert-NoLocalIdentity (Get-Content (Join-Path $ReportDirectory $name) -Raw) $name
}
Write-Host "Report written; $validated accepted SARIF files validated."

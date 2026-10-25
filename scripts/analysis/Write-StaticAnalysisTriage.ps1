#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Output,
    [Parameter(Mandatory)][string]$ReviewManifest,
    [Parameter(Mandatory)][string]$Report,
    [string[]]$AdditionalFindings = @()
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$manifest=Get-Content -LiteralPath $ReviewManifest -Raw | ConvertFrom-Json
$raw=@(foreach($inputPath in @((Join-Path $Output 'findings.json')) + $AdditionalFindings) {
    Get-Content -LiteralPath $inputPath -Raw | ConvertFrom-Json |
        Where-Object { $_.scope -eq 'product' -and $_.sourceKind -eq 'first-party' }
})
$style=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach($id in $manifest.styleRules) { [void]$style.Add([string]$id) }
$reviews=@{}
$classes=@('confirmed defect','plausible','false positive','intentional')
foreach($path in $manifest.reviewFiles) {
    foreach($row in @(Get-Content -LiteralPath (Join-Path $repository $path) -Raw | ConvertFrom-Json)) {
        if($row.classification -notin $classes) { throw "Invalid review classification in $path" }
        if($row.file -match '^tests/|\.Tests/|^src/Development/') { throw "Out-of-scope review row in ${path}: $($row.file):$($row.line)" }
        $key="$($row.tool)|$($row.rule)|$($row.file)|$($row.line)"
        if($reviews.ContainsKey($key) -and $reviews[$key].classification -ne $row.classification) {
            throw "Conflicting review decisions: $key"
        }
        $reviews[$key]=$row
    }
}
# Explicit curator corrections retain the original review files as evidence.
if($manifest.PSObject.Properties['overrides']) {
    foreach($row in $manifest.overrides) {
        $key="$($row.tool)|$($row.rule)|$($row.file)|$($row.line)"
        if(-not $reviews.ContainsKey($key) -or $row.classification -notin $classes) { throw "Invalid review override: $key" }
        $reviews[$key]=$row
    }
}
$coverage=[Collections.Generic.List[object]]::new()
$accepted=[Collections.Generic.List[object]]::new()
$gaps=[Collections.Generic.List[string]]::new()
$ruleMetadata=@{}
foreach($group in ($raw | Group-Object tool,rule)) {
    $first=$group.Group[0]
    $ruleMetadata["$($first.tool)|$($first.rule)"]=$first
    if($style.Contains($first.rule)) { continue }
    $sites=@($group.Group | Group-Object file,line | ForEach-Object { $_.Group[0] })
    $reviewed=@($sites | Where-Object { $reviews.ContainsKey("$($_.tool)|$($_.rule)|$($_.file)|$($_.line)") })
    $needed=if($group.Count -ge 100) { [Math]::Min(20,$sites.Count) } else { $sites.Count }
    if($reviewed.Count -lt $needed) { $gaps.Add("$($first.tool)/$($first.rule): $($reviewed.Count)/$needed reviewed sites ($($group.Count) occurrences)") }
    $decisions=@($reviewed | ForEach-Object { $reviews["$($_.tool)|$($_.rule)|$($_.file)|$($_.line)"] })
    foreach($decision in $decisions) { $accepted.Add($decision) }
    $counts=@{}
    foreach($class in $classes) { $counts[$class]=@($decisions | Where-Object classification -eq $class).Count }
    $coverage.Add([pscustomobject]@{tool=$first.tool; rule=$first.rule; hits=$group.Count; reviewed=$reviewed.Count; required=$needed;
        sampled=($group.Count -ge 100); confirmed=$counts['confirmed defect']; plausible=$counts['plausible']; falsePositive=$counts['false positive'];
        intentional=$counts['intentional']; sampledFalsePositiveRate=if($reviewed.Count){[Math]::Round($counts['false positive']/$reviewed.Count,4)}else{$null}})
}
$result=[pscustomobject]@{complete=($gaps.Count -eq 0); productOccurrences=$raw.Count;
    styleOccurrences=@($raw | Where-Object { $style.Contains($_.rule) }).Count;
    coverage=$coverage.ToArray(); gaps=$gaps.ToArray(); findings=$accepted.ToArray()}
$jsonPath=[IO.Path]::ChangeExtension($Report,'.json')
$result | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $jsonPath -Encoding utf8
$lines=[Collections.Generic.List[string]]::new()
$lines.Add('# C# static analysis triage')
$lines.Add('')
$lines.Add('范围：产品一方源码；测试、开发工具和生成/外部源码另计。诊断按工具/规则计数，同一源码被多个项目编译时先合并代码位置进行核查。')
$lines.Add('confirmed defect 指代码和接口合同已证明的失败路径；plausible 仍需运行验证。性能候选仅证明重复工作/分配，未测量实际影响；本轮未运行安装后的产品或修改产品代码。')
$lines.Add('最终分类以同名 JSON 为准，已应用主审的明确修订；独立分组审查文件保留为原始输入。')
$lines.Add('')
$lines.Add("纯风格合并：$($result.styleOccurrences) 条诊断；保留原始机器计数，不逐条列举。")
$lines.Add('')
if($gaps.Count) {
    $lines.Add("**尚未完成分诊：$($gaps.Count) 个规则有覆盖缺口。**")
    $lines.Add('')
    foreach($gap in $gaps) { $lines.Add("- $gap") }
    $lines.Add('')
}
$lines.Add('## Confirmed defects and plausible findings')
$lines.Add('')
function Get-FindingPriority($Finding) {
    $rule=[string]$Finding.rule
    if([string]$Finding.scenario -match '(?i)AccessViolation|out.of.bounds|越界|内存破坏') { return 0 }
    if($rule -match '^VSTHRD|^CA(1849|2007|2012|2016|2020|2025|2219)$|^MA(0004|0040|0042|0072|0100|0166)$|^S(1163|4586|5034|6966|8949)$') { return 1 }
    if($rule -match '^IDISP|^CA(1001|1063|1065|1816|2000|2213|2215|2216)$|^S(2930|3881)$') { return 2 }
    if($rule -match '^cs/(dispose-not-called-on-throw|local-not-disposed)$') { return 2 }
    if($rule -match '^CA(2100|2101|5392)$|^S(1075|2077|3869|4036|4200|6444|6640)$|^MA0009$') { return 3 }
    if($rule -match '^cs/(path-combine|call-to-unmanaged-code|unmanaged-code)$') { return 3 }
    if($rule -match '^cs/(linq/|stringbuilder-creation-in-loop|inefficient-containskey)') { return 4 }
    $metadata=$ruleMetadata["$($Finding.tool)|$rule"]
    if($rule -eq 'RCS1077' -or ($metadata -and $metadata.category -eq 'Performance')) { return 4 }
    if($rule -match '^MA(0002|0150)$|^RCS1213$|^cs/(call-to-object-tostring|dereferenced-value-may-be-null|loss-of-precision|constant-condition|comparison-of-identical-expressions)$') { return 0 }
    return 5
}
$actionable=@($accepted | Where-Object { $_.classification -in @('confirmed defect','plausible') } |
    Sort-Object @{Expression={Get-FindingPriority $_}},classification,file,line)
if(-not $actionable.Count) { $lines.Add('已核查范围内没有可行动项。') }
foreach($group in ($actionable | Group-Object file,line | Sort-Object @{Expression={Get-FindingPriority $_.Group[0]}},Name)) {
    $finding=@($group.Group | Sort-Object classification)[0]
    $ruleIds=(@($group.Group | ForEach-Object { "$($_.tool)/$($_.rule)" } | Sort-Object -Unique) -join ', ')
    $scenarios=@($group.Group.scenario | Sort-Object Length -Descending | Select-Object -First 1)[0]
    $evidence=(@($group.Group | ForEach-Object { $_.evidence } | Sort-Object -Unique) -join '; ').Replace("`r",' ').Replace("`n",' ')
    $lines.Add("- **$($finding.classification)** ``$($finding.file):$($finding.line)``（$ruleIds）：$scenarios 证据：$evidence")
}
$lines.Add('')
$lines.Add('## Rule coverage')
$lines.Add('')
$lines.Add('高量规则至少抽查 20 个代码位置；低量规则核查全部代码位置。抽样误报率仅指 false positive，intentional 单独计数；不外推为全体缺陷率。逐位置依据见同名 JSON。')
$lines.Add('')
$lines.Add('| Tool / rule | Product hits | Reviewed | Confirmed | Plausible | False positive | Intentional | Sample FP rate |')
$lines.Add('|---|---:|---:|---:|---:|---:|---:|---:|')
foreach($row in ($coverage | Sort-Object tool,rule)) {
    $rate=if($row.sampled){'{0:P1}' -f $row.sampledFalsePositiveRate}else{'—'}
    $lines.Add("| $($row.tool) / $($row.rule) | $($row.hits) | $($row.reviewed) | $($row.confirmed) | $($row.plausible) | $($row.falsePositive) | $($row.intentional) | $rate |")
}
$lines.Add('')
$lines.Add('## Rules to consider enabling later')
$lines.Add('')
foreach($recommendation in $manifest.recommendations) { $lines.Add("- $recommendation") }
$lines | Set-Content -LiteralPath $Report -Encoding utf8
Write-Host "Triage: $($accepted.Count) reviewed sites; $($gaps.Count) incomplete rules."
if($gaps.Count) { exit 1 }

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'third_party/mesa-wgl/gldrv.h'
if ((Get-FileHash -LiteralPath $source).Hash -cne 'ADDE554BD382DFB583937C72DECF2F5E7DDA16F8E1A69AAD1218BE8C0ABD24F4') {
    throw 'The pinned ICD declaration has changed.'
}
$text = [IO.File]::ReadAllText($source)
$table = [regex]::Match($text, '(?s)typedef struct _GLDISPATCHTABLE\s*\{(.*?)\}\s*GLDISPATCHTABLE')
if (!$table.Success) { throw 'ICD dispatch table not found.' }
$pattern = '(?m)^\s*.+?\(APIENTRY\s+\*(gl[A-Za-z0-9_]+)\s*\)\s*\([^;\r\n]*\);\s*$'
$fields = [regex]::Matches($table.Groups[1].Value, $pattern)
$remainder = [regex]::Replace($table.Groups[1].Value, $pattern, '').Trim()
$remainder = [regex]::Replace($remainder, '(?m)^\s*// OpenGL version 1\.[01] entries (end|begin) here\s*$', '').Trim()
$names = @($fields | ForEach-Object { $_.Groups[1].Value })
if ($fields.Count -ne 336 -or $remainder.Length -or @($names | Sort-Object -Unique).Count -ne 336) {
    throw 'The complete, unique ICD core table was not consumed.'
}
$licenseEnd = $text.IndexOf('*/')
if ($licenseEnd -lt 0) { throw 'Original license not found.' }
$lines = [Collections.Generic.List[string]]::new()
$lines.Add($text.Substring(0, $licenseEnd + 2))
$lines.Add('// Generated names only; C++ derives and checks every function signature.')
for ($index = 0; $index -lt $names.Count; $index++) {
    $lines.Add("RM_GL_CORE($($names[$index]),$index)")
}
$output = Join-Path $PSScriptRoot 'OpenGlCoreEntries.inc'
$bytes = [Text.UTF8Encoding]::new($false).GetBytes(($lines -join "`r`n") + "`r`n")
[IO.File]::WriteAllBytes($output, $bytes)

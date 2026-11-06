# Renders the social preview SVG to a 1280x640 PNG with headless Microsoft Edge.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Svg,
    [Parameter(Mandatory)][string]$Png,
    [ValidateRange(1, 4)][int]$Scale = 1,
    [string]$Edge = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
)
$ErrorActionPreference = 'Stop'
$work = Join-Path ([IO.Path]::GetTempPath()) ('social-preview-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($work)
try {
    $html = Join-Path $work 'page.html'
    $raw = Join-Path $work 'raw.png'
    $body = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $Svg).Path)
    [IO.File]::WriteAllText($html, "<!doctype html><html><head><meta charset='utf-8'><style>html,body{margin:0}svg{display:block}</style></head><body>$body</body></html>")
    # The window is larger than the image: headless Edge keeps part of it for its own frame.
    $page = 'file:///' + $html.Replace([string][char]92, '/')
    # Edge writes unrelated log lines to stderr; Start-Process keeps them from failing the script.
    $edgeArgs = @('--headless=new', '--disable-gpu', '--hide-scrollbars', "--force-device-scale-factor=$Scale",
        '--window-size=1600,1000', "`"--screenshot=$raw`"", "`"$page`"")
    Start-Process -FilePath $Edge -ArgumentList $edgeArgs -Wait -WindowStyle Hidden
    if (-not [IO.File]::Exists($raw)) { throw "Edge did not write the screenshot: $raw" }
    Add-Type -AssemblyName System.Drawing
    $image = [Drawing.Bitmap]::FromFile($raw)
    try {
        $crop = $image.Clone([Drawing.Rectangle]::new(0, 0, 1280 * $Scale, 640 * $Scale), $image.PixelFormat)
        try { $crop.Save([IO.Path]::GetFullPath($Png), [Drawing.Imaging.ImageFormat]::Png) } finally { $crop.Dispose() }
    } finally { $image.Dispose() }
} finally {
    foreach ($file in @('page.html', 'raw.png')) {
        $path = Join-Path $work $file
        if ([IO.File]::Exists($path)) { [IO.File]::Delete($path) }
    }
    [IO.Directory]::Delete($work, $false)
}

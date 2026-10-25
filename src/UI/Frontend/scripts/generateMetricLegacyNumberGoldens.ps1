[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
try {
    $culture = [Globalization.CultureInfo]::InvariantCulture
    $inputs = @(
        '0', '-0', '0.5', '-0.5', '0.49999999999999994', '-0.49999999999999994',
        '0.49999999999999', '-0.49999999999999', '0.50000000000001', '-0.50000000000001',
        '4212.5', '-4212.5', '4212.499999999999', '-4212.499999999999', '4212.4999999999', '-4212.4999999999',
        '1.25', '-1.25', '1.2499999999999998', '-1.2499999999999998', '1.24999999999999', '-1.24999999999999',
        '1.2500000000000002', '-1.2500000000000002', '65.25', '-65.25',
        '0.05', '-0.05', '0.049999999999999996', '-0.049999999999999996', '0.049999999999999', '-0.049999999999999',
        '0.050000000000001', '-0.050000000000001',
        '1.2345', '-1.2345', '1.2344999999999998', '-1.2344999999999998', '1.23449999999999', '-1.23449999999999',
        '1.2345000000000002', '-1.2345000000000002',
        '0.0005', '-0.0005', '0.0004999999999999999', '-0.0004999999999999999',
        '0.00049999999999999', '-0.00049999999999999', '0.00050000000000001', '-0.00050000000000001',
        '9.9995', '-9.9995', '99.95', '-99.95', '999.5', '-999.5',
        '1.234567890123456e20', '-1.234567890123456e20', '1e-7', '-1e-7',
        '9007199254740991', '-9007199254740991', '1.7976931348623157e308', '5e-324', '0.0001', '-0.0001'
    )
    $formats = @('0', '0.#', '0.##', '0.###')
    $rows = foreach ($inputText in $inputs) {
        $value = [double]::Parse($inputText, $culture)
        $expected = foreach ($format in $formats) { $value.ToString($format, $culture) }
        [ordered]@{ input = $inputText; expected = @($expected) }
    }
    $fixture = [ordered]@{
        source = 'System.Double.ToString(customFormat, CultureInfo.InvariantCulture)'
        framework = [Runtime.InteropServices.RuntimeInformation]::FrameworkDescription
        formats = $formats
        scales = @(0, 1, 2, 3)
        cases = @($rows)
    }
    $destination = Join-Path $PSScriptRoot '../tests/fixtures/metricLegacyNumberGoldens.json'
    $fixture | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $destination -Encoding utf8
    Write-Output ('Recorded ' + $rows.Count + ' doubles x ' + $formats.Count + ' invariant .NET custom formats (' + $fixture.framework + ').')
    exit 0
} catch {
    Write-Error $_
    exit 1
}

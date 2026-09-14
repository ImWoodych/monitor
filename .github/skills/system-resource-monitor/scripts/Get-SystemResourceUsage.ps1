[CmdletBinding()]
param(
    [ValidateRange(1, 3600)]
    [int]$IntervalSeconds = 2,

    [ValidateRange(0, 1000000)]
    [int]$Samples = 0,

    [ValidateNotNullOrEmpty()]
    [string]$CsvPath
)

$sampleNumber = 0
$csvInitialized = $false

Write-Host "CPU/RAM monitor started. Press Ctrl+C to stop." -ForegroundColor Cyan

try {
    do {
        $sampleNumber++
        $operatingSystem = Get-CimInstance -ClassName Win32_OperatingSystem
        $processorLoads = Get-CimInstance -ClassName Win32_Processor |
            Where-Object { $null -ne $_.LoadPercentage } |
            Select-Object -ExpandProperty LoadPercentage
        if (-not $processorLoads) {
            throw 'CPU usage is unavailable through Win32_Processor.'
        }
        $cpuPercent = [math]::Round(($processorLoads | Measure-Object -Average).Average, 1)

        $totalMemoryGb = [math]::Round($operatingSystem.TotalVisibleMemorySize / 1MB, 2)
        $freeMemoryGb = [math]::Round($operatingSystem.FreePhysicalMemory / 1MB, 2)
        $usedMemoryGb = [math]::Round($totalMemoryGb - $freeMemoryGb, 2)
        $memoryPercent = [math]::Round(($usedMemoryGb / $totalMemoryGb) * 100, 1)

        $result = [pscustomobject]@{
            Time       = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
            CpuPercent = $cpuPercent
            RamUsedGb  = $usedMemoryGb
            RamTotalGb = $totalMemoryGb
            RamPercent = $memoryPercent
        }

        $result | Format-Table -AutoSize

        if ($CsvPath) {
            if (-not $csvInitialized) {
                $result | Export-Csv -Path $CsvPath -NoTypeInformation -Encoding UTF8
                $csvInitialized = $true
            }
            else {
                $result | Export-Csv -Path $CsvPath -NoTypeInformation -Append -Encoding UTF8
            }
        }

        if ($Samples -gt 0 -and $sampleNumber -ge $Samples) {
            break
        }

        Start-Sleep -Seconds $IntervalSeconds
    } while ($true)
}
catch {
    Write-Error "Unable to read system resource usage: $($_.Exception.Message)"
    exit 1
}

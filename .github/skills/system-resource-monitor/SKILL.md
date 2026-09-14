---
name: system-resource-monitor
description: 'Monitor Windows CPU and RAM usage with a compiled C# executable or PowerShell fallback. Use when the user needs live CPU/RAM metrics, bounded samples, or CSV export.'
argument-hint: '[--interval seconds] [--samples count] [--csv path]'
user-invocable: true
---

# System Resource Monitor

## What It Produces

A Windows monitor that reports:

- Total CPU utilization in percent.
- Used and total RAM in gigabytes.
- RAM utilization in percent.
- Timestamp for every sample.
- Optional CSV output for later analysis.
- A standalone C# executable with no external dependencies.

## Procedure

1. Run [SystemResourceMonitor.exe](./bin/SystemResourceMonitor.exe).
2. For continuous monitoring, use the default `2` second interval and stop with `Ctrl+C`.
3. To collect a fixed number of samples, pass `--samples`.
4. To change the polling interval, pass `--interval`.
5. To save measurements, pass `--csv`.
6. Use [Get-SystemResourceUsage.ps1](./scripts/Get-SystemResourceUsage.ps1) when a PowerShell-only fallback is preferred.
7. Confirm that the displayed values are plausible and that the CSV file was created when export was requested.

## Examples

```powershell
# Monitor continuously every two seconds
.\\bin\\SystemResourceMonitor.exe

# Take 10 samples, one second apart
.\\bin\\SystemResourceMonitor.exe --interval 1 --samples 10

# Monitor continuously and append each sample to a CSV file
.\\bin\\SystemResourceMonitor.exe --csv .\\resource-usage.csv
```

## Requirements

- Windows 10/11.
- The compiled executable uses Windows API calls and does not require .NET SDK or PowerShell.
- The PowerShell fallback requires Windows PowerShell 5.1 or PowerShell 7 on Windows.

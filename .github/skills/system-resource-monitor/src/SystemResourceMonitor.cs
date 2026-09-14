using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

internal static class SystemResourceMonitor
{
    private static readonly object ConsoleLock = new object();

    private static void Main(string[] args)
    {
        int intervalSeconds = GetIntOption(args, "--interval", 2, 1, 3600);
        int samples = GetIntOption(args, "--samples", 0, 0, 1000000);
        string csvPath = GetStringOption(args, "--csv");

        Console.WriteLine("CPU/RAM monitor started. Press Ctrl+C to stop.");
        CpuSnapshot previousCpu = ReadCpuSnapshot();
        bool csvInitialized = false;
        int sampleNumber = 0;

        try
        {
            while (samples == 0 || sampleNumber < samples)
            {
                Thread.Sleep(TimeSpan.FromSeconds(intervalSeconds));

                CpuSnapshot currentCpu = ReadCpuSnapshot();
                double cpuPercent = CalculateCpuPercent(previousCpu, currentCpu);
                previousCpu = currentCpu;
                MemorySnapshot memory = ReadMemorySnapshot();
                DateTime timestamp = DateTime.Now;
                sampleNumber++;

                ResourceSample sample = new ResourceSample
                {
                    Time = timestamp,
                    CpuPercent = cpuPercent,
                    RamUsedGb = memory.UsedGb,
                    RamTotalGb = memory.TotalGb,
                    RamPercent = memory.UsedPercent
                };

                PrintSample(sample);

                if (!string.IsNullOrWhiteSpace(csvPath))
                {
                    AppendCsv(csvPath, sample, ref csvInitialized);
                }
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Unable to read system resource usage: " + exception.Message);
            Environment.ExitCode = 1;
        }
    }

    private static int GetIntOption(string[] args, string option, int fallback, int minimum, int maximum)
    {
        string value = GetStringOption(args, option);
        int parsed;
        if (value == null)
        {
            return fallback;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ||
            parsed < minimum || parsed > maximum)
        {
            throw new ArgumentException(option + " must be between " + minimum + " and " + maximum + ".");
        }

        return parsed;
    }

    private static string GetStringOption(string[] args, string option)
    {
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], option, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static void PrintSample(ResourceSample sample)
    {
        lock (ConsoleLock)
        {
            Console.WriteLine();
            Console.WriteLine("Time                CPU %     RAM used       RAM total      RAM %");
            Console.WriteLine("{0:yyyy-MM-dd HH:mm:ss}  {1,6:0.0}    {2,7:0.00} GB    {3,7:0.00} GB    {4,6:0.0}",
                sample.Time, sample.CpuPercent, sample.RamUsedGb, sample.RamTotalGb, sample.RamPercent);
        }
    }

    private static void AppendCsv(string path, ResourceSample sample, ref bool initialized)
    {
        bool needsHeader = !initialized && !File.Exists(path);
        using (StreamWriter writer = new StreamWriter(path, true, new UTF8Encoding(false)))
        {
            if (needsHeader)
            {
                writer.WriteLine("Time,CpuPercent,RamUsedGb,RamTotalGb,RamPercent");
            }

            writer.WriteLine("{0},{1},{2},{3},{4}",
                sample.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                sample.CpuPercent.ToString("0.0", CultureInfo.InvariantCulture),
                sample.RamUsedGb.ToString("0.00", CultureInfo.InvariantCulture),
                sample.RamTotalGb.ToString("0.00", CultureInfo.InvariantCulture),
                sample.RamPercent.ToString("0.0", CultureInfo.InvariantCulture));
        }

        initialized = true;
    }

    private static CpuSnapshot ReadCpuSnapshot()
    {
        long idle;
        long kernel;
        long user;
        if (!GetSystemTimes(out idle, out kernel, out user))
        {
            throw new InvalidOperationException("Windows CPU counters are unavailable.");
        }

        return new CpuSnapshot { Idle = idle, Kernel = kernel, User = user };
    }

    private static double CalculateCpuPercent(CpuSnapshot previous, CpuSnapshot current)
    {
        long idleDelta = current.Idle - previous.Idle;
        long totalDelta = (current.Kernel - previous.Kernel) + (current.User - previous.User);
        if (totalDelta <= 0)
        {
            return 0;
        }

        return Math.Round((1.0 - (double)idleDelta / totalDelta) * 100.0, 1);
    }

    private static MemorySnapshot ReadMemorySnapshot()
    {
        MemoryStatus status = new MemoryStatus();
        status.Length = (uint)Marshal.SizeOf(typeof(MemoryStatus));
        if (!GlobalMemoryStatusEx(ref status))
        {
            throw new InvalidOperationException("Windows memory counters are unavailable.");
        }

        double totalGb = status.TotalPhysicalMemory / 1073741824.0;
        double freeGb = status.AvailablePhysicalMemory / 1073741824.0;
        double usedGb = totalGb - freeGb;
        return new MemorySnapshot
        {
            TotalGb = Math.Round(totalGb, 2),
            UsedGb = Math.Round(usedGb, 2),
            UsedPercent = Math.Round((usedGb / totalGb) * 100.0, 1)
        };
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysicalMemory;
        public ulong AvailablePhysicalMemory;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    private struct CpuSnapshot
    {
        public long Idle;
        public long Kernel;
        public long User;
    }

    private struct MemorySnapshot
    {
        public double TotalGb;
        public double UsedGb;
        public double UsedPercent;
    }

    private sealed class ResourceSample
    {
        public DateTime Time;
        public double CpuPercent;
        public double RamUsedGb;
        public double RamTotalGb;
        public double RamPercent;
    }
}
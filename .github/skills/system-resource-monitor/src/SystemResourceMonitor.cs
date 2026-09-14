using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

internal static class SystemResourceMonitor
{
    private static readonly object ConsoleLock = new object();
    private static volatile int currentInterval;
    private static volatile bool showProcesses;
    private static volatile string sortMode;
    private static bool supportsCursorControl;
    private static int dataLine;
    private static int processHeaderLine;
    private const int MaxProcessLines = 18; // header + separator + 15 processes + blank

    private static void Main(string[] args)
    {
        int intervalSeconds = GetIntOption(args, "--interval", 2, 1, 3600);
        int samples = GetIntOption(args, "--samples", 0, 0, 1000000);
        string csvPath = GetStringOption(args, "--csv");

        currentInterval = intervalSeconds;
        showProcesses = false;
        sortMode = "cpu";

        // Detect if console supports cursor control
        supportsCursorControl = DetectCursorSupport();

        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("CPU/RAM monitor started. Press Ctrl+C to stop.");
        Console.WriteLine("Commands: [i] change interval, [p] toggle processes, [sc] sort by CPU, [sr] sort by RAM");
        if (!supportsCursorControl)
        {
            Console.WriteLine("Note: cursor control unavailable, running in scroll mode.");
        }
        Console.WriteLine();

        CpuSnapshot previousCpu = ReadCpuSnapshot();
        bool csvInitialized = false;
        int sampleNumber = 0;

        // Hardware info
        string cpuManufacturer = ReadCpuManufacturer();
        string cpuModel = ReadCpuModel();
        string cpuSpeed = ReadCpuSpeed();
        string ramManufacturer = ReadRamManufacturer();
        string ramPartNumber = ReadRamPartNumber();
        string ramSpeed = ReadRamSpeed();

        // Print static header once
        Console.WriteLine("=== Hardware Info ===");
        Console.WriteLine("CPU:  {0} ({1}) - {2}", cpuManufacturer, cpuModel, cpuSpeed);
        Console.WriteLine("RAM:  {0} | Part: {1} | Speed: {2}", ramManufacturer, ramPartNumber, ramSpeed);
        Console.WriteLine();

        if (supportsCursorControl)
        {
            dataLine = Console.CursorTop;
            processHeaderLine = dataLine + 4;
        }

        try
        {
            while (samples == 0 || sampleNumber < samples)
            {
                // Handle user input in a non-blocking way
                CheckUserInput();

                Thread.Sleep(TimeSpan.FromSeconds(currentInterval));

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

                if (supportsCursorControl)
                {
                    UpdateSample(sample, currentInterval, showProcesses);

                    if (showProcesses)
                    {
                        UpdateProcesses(sortMode);
                    }
                    else
                    {
                        ClearArea(processHeaderLine, MaxProcessLines);
                    }
                }
                else
                {
                    PrintSample(sample, currentInterval, showProcesses);
                    if (showProcesses)
                    {
                        PrintProcessesScroll(sortMode);
                    }
                }

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

    private static bool DetectCursorSupport()
    {
        try
        {
            int top = Console.CursorTop;
            int left = Console.CursorLeft;
            int width = Console.WindowWidth;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void CheckUserInput()
    {
        try
        {
            if (Console.KeyAvailable)
            {
                ConsoleKeyInfo key = Console.ReadKey(true);
                lock (ConsoleLock)
                {
                    if (key.Key == ConsoleKey.I)
                    {
                        Console.WriteLine();
                        Console.Write("New interval in seconds (1-3600): ");
                        string input = Console.ReadLine();
                        int newInterval;
                        if (int.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out newInterval)
                            && newInterval >= 1 && newInterval <= 3600)
                        {
                            currentInterval = newInterval;
                            Console.WriteLine("Interval changed to " + newInterval + " seconds.");
                        }
                        else
                        {
                            Console.WriteLine("Invalid interval. Kept previous value: " + currentInterval);
                        }
                    }
                    else if (key.Key == ConsoleKey.P)
                    {
                        showProcesses = !showProcesses;
                        Console.WriteLine();
                        Console.WriteLine(showProcesses ? "Process list enabled." : "Process list disabled.");
                    }
                    else if (key.Key == ConsoleKey.S)
                    {
                        // Check if next key is C or R
                        ConsoleKeyInfo nextKey = Console.ReadKey(true);
                        if (nextKey.Key == ConsoleKey.C)
                        {
                            sortMode = "cpu";
                            Console.WriteLine();
                            Console.WriteLine("Sorting processes by CPU usage.");
                        }
                        else if (nextKey.Key == ConsoleKey.R)
                        {
                            sortMode = "ram";
                            Console.WriteLine();
                            Console.WriteLine("Sorting processes by RAM usage.");
                        }
                        else
                        {
                            Console.WriteLine();
                            Console.WriteLine("Invalid command.");
                        }
                    }
                }
            }
        }
        catch
        {
            // Console input unavailable (e.g. piped or no console)
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

    private static string ReadCpuManufacturer()
    {
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT Manufacturer FROM Win32_Processor"))
            {
                foreach (var obj in searcher.Get())
                {
                    object manufacturerObj = obj["Manufacturer"];
                    string manufacturer = manufacturerObj != null ? manufacturerObj.ToString() : null;
                    if (!string.IsNullOrEmpty(manufacturer))
                    {
                        return manufacturer;
                    }
                }
            }
        }
        catch { }
        return "Unknown";
    }

    private static string ReadCpuModel()
    {
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor"))
            {
                foreach (var obj in searcher.Get())
                {
                    object nameObj = obj["Name"];
                    string name = nameObj != null ? nameObj.ToString() : null;
                    if (!string.IsNullOrEmpty(name))
                    {
                        return name;
                    }
                }
            }
        }
        catch { }
        return "Unknown";
    }

    private static string ReadCpuSpeed()
    {
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT MaxClockSpeed FROM Win32_Processor"))
            {
                foreach (var obj in searcher.Get())
                {
                    object speedObj = obj["MaxClockSpeed"];
                    uint speed = 0;
                    if (speedObj != null)
                    {
                        speed = Convert.ToUInt32(speedObj);
                    }
                    if (speed > 0)
                    {
                        double ghz = (double)speed / 1000.0;
                        return ghz.ToString("0.0", CultureInfo.InvariantCulture) + " GHz";
                    }
                }
            }
        }
        catch { }
        return "Unknown";
    }

    private static string ReadRamManufacturer()
    {
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT Manufacturer FROM Win32_PhysicalMemory"))
            {
                var manufacturers = new HashSet<string>();
                foreach (var obj in searcher.Get())
                {
                    object manufacturerObj = obj["Manufacturer"];
                    string manufacturer = manufacturerObj != null ? manufacturerObj.ToString() : null;
                    if (!string.IsNullOrEmpty(manufacturer) && manufacturer != "Unknown")
                    {
                        manufacturers.Add(manufacturer);
                    }
                }
                if (manufacturers.Count > 0)
                {
                    return string.Join(", ", manufacturers);
                }
            }
        }
        catch { }
        return "Unknown";
    }

    private static string ReadRamPartNumber()
    {
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT PartNumber FROM Win32_PhysicalMemory"))
            {
                var partNumbers = new HashSet<string>();
                foreach (var obj in searcher.Get())
                {
                    object partNumberObj = obj["PartNumber"];
                    string partNumber = partNumberObj != null ? partNumberObj.ToString() : null;
                    if (!string.IsNullOrEmpty(partNumber))
                    {
                        partNumbers.Add(partNumber);
                    }
                }
                if (partNumbers.Count > 0)
                {
                    return string.Join(", ", partNumbers);
                }
            }
        }
        catch { }
        return "Unknown";
    }

    private static string ReadRamSpeed()
    {
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT Speed FROM Win32_PhysicalMemory"))
            {
                var speeds = new HashSet<int>();
                foreach (var obj in searcher.Get())
                {
                    uint speed = Convert.ToUInt32(obj["Speed"] ?? 0);
                    if (speed > 0)
                    {
                        speeds.Add((int)speed);
                    }
                }
                if (speeds.Count > 0)
                {
                    var speedStrings = new List<string>();
                    foreach (int s in speeds)
                    {
                        speedStrings.Add(s + " MHz");
                    }
                    return string.Join(", ", speedStrings);
                }
            }
        }
        catch { }
        return "Unknown";
    }

    private static void PrintSample(ResourceSample sample, int interval, bool processesEnabled)
    {
        Console.WriteLine("Time                CPU %     RAM used       RAM total      RAM %");
        Console.WriteLine("{0:yyyy-MM-dd HH:mm:ss}  {1,6:0.0}    {2,7:0.00} GB    {3,7:0.00} GB    {4,6:0.0}",
            sample.Time, sample.CpuPercent, sample.RamUsedGb, sample.RamTotalGb, sample.RamPercent);
        Console.WriteLine("Interval: {0}s | Processes: {1}", interval, processesEnabled ? "ON" : "OFF");
    }

    private static void PrintProcessesScroll(string sortBy)
    {
        try
        {
            var processes = new List<ProcessInfo>();
            Process[] systemProcesses = Process.GetProcesses();
            foreach (Process proc in systemProcesses)
            {
                try
                {
                    string name = proc.ProcessName;
                    ulong workingSet = (ulong)proc.WorkingSet64;
                    processes.Add(new ProcessInfo { Name = name, RamBytes = workingSet });
                }
                catch
                {
                    // Process may have exited or be inaccessible
                }
            }

            var sorted = processes.OrderByDescending(p => p.RamBytes);

            Console.WriteLine();
            Console.WriteLine("=== Top 15 Processes (sorted by {0}) ===", sortBy == "ram" ? "RAM" : "CPU");
            Console.WriteLine("{0,-40} {1,12}", "Process", "RAM (MB)");
            Console.WriteLine(new string('-', 55));

            int count = 0;
            foreach (var proc in sorted.Take(15))
            {
                double ramMb = proc.RamBytes / (1024.0 * 1024.0);
                Console.WriteLine("{0,-40} {1,12:0.00}", proc.Name, ramMb);
                count++;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("  Error reading processes: " + ex.Message);
        }
    }

    private static void UpdateSample(ResourceSample sample, int interval, bool processesEnabled)
    {
        lock (ConsoleLock)
        {
            int savedTop = Console.CursorTop;
            int savedLeft = Console.CursorLeft;

            Console.SetCursorPosition(0, dataLine);
            Console.Write(new string(' ', Console.WindowWidth));
            Console.SetCursorPosition(0, dataLine);

            Console.WriteLine("Time                CPU %     RAM used       RAM total      RAM %");
            Console.WriteLine("{0:yyyy-MM-dd HH:mm:ss}  {1,6:0.0}    {2,7:0.00} GB    {3,7:0.00} GB    {4,6:0.0}",
                sample.Time, sample.CpuPercent, sample.RamUsedGb, sample.RamTotalGb, sample.RamPercent);
            Console.WriteLine("Interval: {0}s | Processes: {1}", interval, processesEnabled ? "ON" : "OFF");

            Console.SetCursorPosition(savedLeft, savedTop);
        }
    }

    private static void UpdateProcesses(string sortBy)
    {
        lock (ConsoleLock)
        {
            try
            {
                var processes = new List<ProcessInfo>();
                Process[] systemProcesses = Process.GetProcesses();
                foreach (Process proc in systemProcesses)
                {
                    try
                    {
                        string name = proc.ProcessName;
                        ulong workingSet = (ulong)proc.WorkingSet64;
                        processes.Add(new ProcessInfo { Name = name, RamBytes = workingSet });
                    }
                    catch
                    {
                        // Process may have exited or be inaccessible
                    }
                }

                var sorted = processes.OrderByDescending(p => p.RamBytes);

                int savedTop = Console.CursorTop;
                int savedLeft = Console.CursorLeft;

                Console.SetCursorPosition(0, processHeaderLine);
                Console.Write(new string(' ', Console.WindowWidth));
                Console.SetCursorPosition(0, processHeaderLine);

                Console.WriteLine("=== Top 15 Processes (sorted by {0}) ===", sortBy == "ram" ? "RAM" : "CPU");
                Console.WriteLine("{0,-40} {1,12}", "Process", "RAM (MB)");
                Console.WriteLine(new string('-', 55));

                int count = 0;
                foreach (var proc in sorted.Take(15))
                {
                    double ramMb = proc.RamBytes / (1024.0 * 1024.0);
                    Console.WriteLine("{0,-40} {1,12:0.00}", proc.Name, ramMb);
                    count++;
                }

                // Clear remaining lines if fewer than 15 processes
                for (int i = count; i < 15; i++)
                {
                    Console.SetCursorPosition(0, processHeaderLine + 2 + i);
                    Console.Write(new string(' ', Console.WindowWidth));
                }

                Console.SetCursorPosition(savedLeft, savedTop);
            }
            catch (Exception ex)
            {
                Console.WriteLine("  Error reading processes: " + ex.Message);
            }
        }
    }

    private static void ClearArea(int startLine, int lineCount)
    {
        for (int i = 0; i < lineCount; i++)
        {
            if (startLine + i < Console.CursorTop)
            {
                Console.SetCursorPosition(0, startLine + i);
                Console.Write(new string(' ', Console.WindowWidth));
            }
        }
        Console.SetCursorPosition(0, startLine);
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

    private sealed class ProcessInfo
    {
        public string Name;
        public ulong RamBytes;
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

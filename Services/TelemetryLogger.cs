using System.Collections.Concurrent;
using System.Globalization;
using System.IO;

namespace ThermoSync;

/// <summary>
/// Asynchronously writes rolling CSV telemetry logs to disk (%AppData%\ThermoSync\telemetry).
/// Retains daily logs and automatically cleans up records older than 7 days.
/// </summary>
public sealed class TelemetryLogger : IDisposable
{
    public static string LogDir => Path.Combine(AppSettings.Folder, "telemetry");
    private readonly ConcurrentQueue<string> _writeQueue = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _workerTask;
    private readonly AutoResetEvent _signal = new(false);
    private string _currentDateStr = "";
    private string _currentFilePath = "";
    private bool _disposed;

    public bool IsEnabled { get; set; } = true;
    public long TotalSamplesWritten { get; private set; }
    public string? CurrentLogFile => _currentFilePath;

    public TelemetryLogger()
    {
        Directory.CreateDirectory(LogDir);
        _workerTask = Task.Run(ProcessQueueAsync);
    }

    public void LogSample(
        DateTime timestamp,
        float? cpuTemp,
        float? cpuLoad,
        float? cpuClock,
        float? cpuPowerW,
        float? gpuTemp,
        double? gpuHotspot,
        double? gpuLoad,
        float? gpuClock,
        float? gpuPowerW,
        double? vramUsedGb,
        double? vramTotalGb,
        double ramUsedGb,
        double ramTotalGb,
        double ramPct,
        float? diskTemp,
        float? diskLoad,
        string? throttlingStatus,
        string? activeGame)
    {
        if (!IsEnabled || _disposed) return;

        var culture = CultureInfo.InvariantCulture;
        string line = string.Format(
            culture,
            "{0:yyyy-MM-dd HH:mm:ss};{1};{2};{3};{4};{5};{6};{7};{8};{9};{10};{11};{12:0.00};{13:0.00};{14:0.0};{15};{16};{17};{18}",
            timestamp,
            cpuTemp.HasValue ? cpuTemp.Value.ToString("0.0", culture) : "",
            cpuLoad.HasValue ? cpuLoad.Value.ToString("0.0", culture) : "",
            cpuClock.HasValue ? cpuClock.Value.ToString("0", culture) : "",
            cpuPowerW.HasValue ? cpuPowerW.Value.ToString("0.0", culture) : "",
            gpuTemp.HasValue ? gpuTemp.Value.ToString("0.0", culture) : "",
            gpuHotspot.HasValue ? gpuHotspot.Value.ToString("0.0", culture) : "",
            gpuLoad.HasValue ? gpuLoad.Value.ToString("0.0", culture) : "",
            gpuClock.HasValue ? gpuClock.Value.ToString("0", culture) : "",
            gpuPowerW.HasValue ? gpuPowerW.Value.ToString("0.0", culture) : "",
            vramUsedGb.HasValue ? vramUsedGb.Value.ToString("0.00", culture) : "",
            vramTotalGb.HasValue ? vramTotalGb.Value.ToString("0.00", culture) : "",
            ramUsedGb,
            ramTotalGb,
            ramPct,
            diskTemp.HasValue ? diskTemp.Value.ToString("0.0", culture) : "",
            diskLoad.HasValue ? diskLoad.Value.ToString("0.0", culture) : "",
            EscapeCsv(throttlingStatus ?? ""),
            EscapeCsv(activeGame ?? "")
        );

        _writeQueue.Enqueue(line);
        _signal.Set();
    }

    private static string EscapeCsv(string val)
    {
        if (string.IsNullOrEmpty(val)) return "";
        if (val.Contains(';') || val.Contains(',') || val.Contains('"') || val.Contains('\n'))
            return $"\"{val.Replace("\"", "\"\"")}\"";
        return val;
    }

    private async Task ProcessQueueAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            _signal.WaitOne(1000);

            if (_writeQueue.IsEmpty) continue;

            EnsureFileReady();

            var batch = new List<string>();
            while (_writeQueue.TryDequeue(out var line))
            {
                batch.Add(line);
            }

            if (batch.Count > 0 && !string.IsNullOrEmpty(_currentFilePath))
            {
                try
                {
                    await File.AppendAllLinesAsync(_currentFilePath, batch, _cts.Token);
                    TotalSamplesWritten += batch.Count;
                }
                catch { }
            }
        }

        // Flush remaining lines on exit
        if (!_writeQueue.IsEmpty && !string.IsNullOrEmpty(_currentFilePath))
        {
            var remaining = new List<string>();
            while (_writeQueue.TryDequeue(out var line)) remaining.Add(line);
            try
            {
                File.AppendAllLines(_currentFilePath, remaining);
                TotalSamplesWritten += remaining.Count;
            }
            catch { }
        }
    }

    private void EnsureFileReady()
    {
        string dateStr = DateTime.Now.ToString("yyyy-MM-dd");
        if (_currentDateStr != dateStr || string.IsNullOrEmpty(_currentFilePath))
        {
            _currentDateStr = dateStr;
            _currentFilePath = Path.Combine(LogDir, $"telemetry-{_currentDateStr}.csv");

            if (!File.Exists(_currentFilePath))
            {
                try
                {
                    const string header = "sep=;\nTimestamp;CpuTemp;CpuLoad;CpuClock;CpuPowerW;GpuTemp;GpuHotspot;GpuLoad;GpuClock;GpuPowerW;GpuVramUsedGb;GpuVramTotalGb;RamUsedGb;RamTotalGb;RamPct;DiskTemp;DiskLoad;ThrottlingStatus;ActiveGame";
                    File.WriteAllText(_currentFilePath, header + Environment.NewLine, new System.Text.UTF8Encoding(true));
                }
                catch { }
            }

            CleanupOldLogs();
        }
    }

    private void CleanupOldLogs()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-7);
            var dir = new DirectoryInfo(LogDir);
            foreach (var file in dir.GetFiles("telemetry-*.csv"))
            {
                if (file.CreationTime < cutoff)
                {
                    try { file.Delete(); } catch { }
                }
            }
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _signal.Set();
        try { _workerTask.Wait(1500); } catch { }
        _signal.Dispose();
        _cts.Dispose();
    }
}

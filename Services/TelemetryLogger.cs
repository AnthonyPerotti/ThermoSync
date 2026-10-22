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
    private readonly string _logDir;
    private readonly ConcurrentQueue<string> _writeQueue = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _workerTask;
    private readonly AutoResetEvent _signal = new(false);
    private string _currentDateStr = "";
    private string _currentFilePath = "";
    private bool _disposed;

    public bool IsEnabled { get; set; } = true;

    public TelemetryLogger()
    {
        _logDir = Path.Combine(AppSettings.Folder, "telemetry");
        Directory.CreateDirectory(_logDir);
        _workerTask = Task.Run(ProcessQueueAsync);
    }

    public void LogSample(
        DateTime timestamp,
        float? cpuTemp,
        float? cpuLoad,
        float? cpuClock,
        float? gpuTemp,
        double? gpuHotspot,
        double? gpuLoad,
        float? gpuClock,
        double ramUsedGb,
        double ramTotalGb,
        double ramPct,
        string? activeGame)
    {
        if (!IsEnabled || _disposed) return;

        var culture = CultureInfo.InvariantCulture;
        string line = string.Format(
            culture,
            "{0:yyyy-MM-dd HH:mm:ss},{1},{2},{3},{4},{5},{6},{7},{8:0.00},{9:0.00},{10:0.0},{11}",
            timestamp,
            cpuTemp.HasValue ? cpuTemp.Value.ToString("0.0", culture) : "",
            cpuLoad.HasValue ? cpuLoad.Value.ToString("0.0", culture) : "",
            cpuClock.HasValue ? cpuClock.Value.ToString("0", culture) : "",
            gpuTemp.HasValue ? gpuTemp.Value.ToString("0.0", culture) : "",
            gpuHotspot.HasValue ? gpuHotspot.Value.ToString("0.0", culture) : "",
            gpuLoad.HasValue ? gpuLoad.Value.ToString("0.0", culture) : "",
            gpuClock.HasValue ? gpuClock.Value.ToString("0", culture) : "",
            ramUsedGb,
            ramTotalGb,
            ramPct,
            EscapeCsv(activeGame ?? "")
        );

        _writeQueue.Enqueue(line);
        _signal.Set();
    }

    private static string EscapeCsv(string val)
    {
        if (string.IsNullOrEmpty(val)) return "";
        if (val.Contains(',') || val.Contains('"') || val.Contains('\n'))
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
                }
                catch { }
            }
        }

        // Flush remaining lines on exit
        if (!_writeQueue.IsEmpty && !string.IsNullOrEmpty(_currentFilePath))
        {
            var remaining = new List<string>();
            while (_writeQueue.TryDequeue(out var line)) remaining.Add(line);
            try { File.AppendAllLines(_currentFilePath, remaining); } catch { }
        }
    }

    private void EnsureFileReady()
    {
        string dateStr = DateTime.Now.ToString("yyyy-MM-dd");
        if (_currentDateStr != dateStr || string.IsNullOrEmpty(_currentFilePath))
        {
            _currentDateStr = dateStr;
            _currentFilePath = Path.Combine(_logDir, $"telemetry-{_currentDateStr}.csv");

            if (!File.Exists(_currentFilePath))
            {
                try
                {
                    const string header = "Timestamp,CpuTemp,CpuLoad,CpuClock,GpuTemp,GpuHotspot,GpuLoad,GpuClock,RamUsedGb,RamTotalGb,RamPct,ActiveGame";
                    File.WriteAllText(_currentFilePath, header + Environment.NewLine);
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
            var dir = new DirectoryInfo(_logDir);
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

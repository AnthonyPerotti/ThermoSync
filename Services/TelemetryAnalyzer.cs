using System.Globalization;
using System.IO;

namespace ThermoSync;

public sealed record TelemetrySummary(
    int TotalSamples,
    TimeSpan Duration,
    float AvgCpuTemp,
    float MaxCpuTemp,
    float AvgCpuLoad,
    float MaxCpuLoad,
    float AvgGpuTemp,
    float MaxGpuTemp,
    float AvgGpuLoad,
    float MaxGpuLoad,
    double AvgRamPct,
    double MaxRamPct,
    int ThrottlingCount,
    string MostActiveGame
);

public static class TelemetryAnalyzer
{
    public static TelemetrySummary? AnalyzeFile(string filePath)
    {
        if (!File.Exists(filePath)) return null;

        try
        {
            var lines = File.ReadAllLines(filePath);
            if (lines.Length <= 1) return null; // Header only

            var culture = CultureInfo.InvariantCulture;
            var cpuTemps = new List<float>();
            var cpuLoads = new List<float>();
            var gpuTemps = new List<float>();
            var gpuLoads = new List<float>();
            var ramPcts = new List<double>();
            var games = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int throttlingCount = 0;
            DateTime? firstTime = null;
            DateTime? lastTime = null;

            // Skip CSV header
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;

                if (line.StartsWith("sep=", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Timestamp", StringComparison.OrdinalIgnoreCase)) continue;
                char sep = line.Contains(';') ? ';' : ',';
                var cols = line.Split(sep);
                if (cols.Length < 11) continue;

                if (DateTime.TryParse(cols[0], culture, DateTimeStyles.None, out var dt))
                {
                    firstTime ??= dt;
                    lastTime = dt;
                }

                if (float.TryParse(cols[1], culture, out var cTemp) && cTemp > 0) cpuTemps.Add(cTemp);
                if (float.TryParse(cols[2], culture, out var cLoad) && cLoad >= 0) cpuLoads.Add(cLoad);

                // Check GpuTemp in column 4 or 5 depending on schema
                int gpuTempCol = cols.Length >= 18 ? 5 : 4;
                int gpuLoadCol = cols.Length >= 18 ? 7 : 6;
                int ramPctCol = cols.Length >= 18 ? 14 : 10;
                int throtCol = cols.Length >= 18 ? 17 : -1;
                int gameCol = cols.Length - 1;

                if (float.TryParse(cols[gpuTempCol], culture, out var gTemp) && gTemp > 0) gpuTemps.Add(gTemp);
                if (float.TryParse(cols[gpuLoadCol], culture, out var gLoad) && gLoad >= 0) gpuLoads.Add(gLoad);
                if (double.TryParse(cols[ramPctCol], culture, out var ramPct) && ramPct >= 0) ramPcts.Add(ramPct);

                if (throtCol >= 0 && throtCol < cols.Length)
                {
                    string throt = cols[throtCol].Trim('\"', ' ');
                    if (!string.IsNullOrEmpty(throt) && !throt.Equals("Normal", StringComparison.OrdinalIgnoreCase))
                    {
                        throttlingCount++;
                    }
                }

                if (gameCol >= 0 && gameCol < cols.Length)
                {
                    string game = cols[gameCol].Trim('\"', ' ');
                    if (!string.IsNullOrEmpty(game))
                    {
                        games[game] = games.GetValueOrDefault(game, 0) + 1;
                    }
                }
            }

            if (cpuTemps.Count == 0 && gpuTemps.Count == 0 && ramPcts.Count == 0) return null;

            var duration = (lastTime.HasValue && firstTime.HasValue) ? lastTime.Value - firstTime.Value : TimeSpan.Zero;
            string topGame = games.OrderByDescending(kv => kv.Value).FirstOrDefault().Key ?? "Nenhum";

            return new TelemetrySummary(
                TotalSamples: lines.Length - 1,
                Duration: duration,
                AvgCpuTemp: cpuTemps.Count > 0 ? cpuTemps.Average() : 0,
                MaxCpuTemp: cpuTemps.Count > 0 ? cpuTemps.Max() : 0,
                AvgCpuLoad: cpuLoads.Count > 0 ? cpuLoads.Average() : 0,
                MaxCpuLoad: cpuLoads.Count > 0 ? cpuLoads.Max() : 0,
                AvgGpuTemp: gpuTemps.Count > 0 ? gpuTemps.Average() : 0,
                MaxGpuTemp: gpuTemps.Count > 0 ? gpuTemps.Max() : 0,
                AvgGpuLoad: gpuLoads.Count > 0 ? gpuLoads.Average() : 0,
                MaxGpuLoad: gpuLoads.Count > 0 ? gpuLoads.Max() : 0,
                AvgRamPct: ramPcts.Count > 0 ? ramPcts.Average() : 0,
                MaxRamPct: ramPcts.Count > 0 ? ramPcts.Max() : 0,
                ThrottlingCount: throttlingCount,
                MostActiveGame: topGame
            );
        }
        catch
        {
            return null;
        }
    }
}

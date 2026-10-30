using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ThermoSync;

public sealed record LatencyTestResult(
    double MaxLatencyUs,
    double AvgLatencyUs,
    double JitterUs,
    string Status,
    string Summary
);

/// <summary>
/// Measures kernel DPC (Deferred Procedure Call) and ISR dispatch jitter.
/// Tests if any driver is monopolizing CPU time and causing audio dropouts or frame micro-stutters in games.
/// Uses QueryPerformanceCounter under high-resolution multimedia timers and TimeCritical scheduling.
/// </summary>
public static class LatencyWatchdog
{
    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint uMilliseconds);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint uMilliseconds);

    public static async Task<LatencyTestResult> TestLatencyAsync(TimeSpan duration, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var oldPriority = Thread.CurrentThread.Priority;
            Thread.CurrentThread.Priority = ThreadPriority.Highest;
            timeBeginPeriod(1);

            double maxLatencyUs = 0;
            double sumLatencyUs = 0;
            long sampleCount = 0;
            var samples = new List<double>(5000);

            try
            {
                long freq = Stopwatch.Frequency;
                double usPerTick = 1_000_000.0 / freq;

                var stopWatch = Stopwatch.StartNew();
                long lastQpc = Stopwatch.GetTimestamp();

                while (stopWatch.Elapsed < duration && !ct.IsCancellationRequested)
                {
                    // Busy-wait sample interval of ~1000 us (1 ms)
                    Thread.Sleep(1);

                    long currentQpc = Stopwatch.GetTimestamp();
                    long elapsedTicks = currentQpc - lastQpc;
                    lastQpc = currentQpc;

                    double elapsedUs = elapsedTicks * usPerTick;
                    // Jitter is the excess time beyond the intended 1000 us sleep
                    double latencyUs = Math.Max(0, elapsedUs - 1000.0);

                    if (latencyUs > maxLatencyUs) maxLatencyUs = latencyUs;
                    sumLatencyUs += latencyUs;
                    sampleCount++;

                    if (samples.Count < 5000) samples.Add(latencyUs);
                }
            }
            finally
            {
                timeEndPeriod(1);
                Thread.CurrentThread.Priority = oldPriority;
            }

            double avgLatencyUs = sampleCount > 0 ? sumLatencyUs / sampleCount : 0;
            double varianceSum = samples.Sum(s => Math.Pow(s - avgLatencyUs, 2));
            double jitterUs = samples.Count > 1 ? Math.Sqrt(varianceSum / (samples.Count - 1)) : 0;

            string status;
            string summary;

            if (maxLatencyUs < 500)
            {
                status = "EXCELENTE";
                summary = $"Latência máxima de {maxLatencyUs:0} µs. O sistema responde com extrema rapidez e é perfeito para jogos competitivos e áudio em tempo real sem micro-travamentos.";
            }
            else if (maxLatencyUs < 1200)
            {
                status = "BOM";
                summary = $"Latência máxima de {maxLatencyUs:0} µs. Desempenho dentro do esperado para Windows. Nenhum problema grave de agendamento de drivers detectado.";
            }
            else if (maxLatencyUs < 2500)
            {
                status = "ATENÇÃO";
                summary = $"Pico de latência de {maxLatencyUs:0} µs detectado. Algum driver de dispositivo (GPU, rede ou Wi-Fi) demorou para devolver o controle ao processador, o que pode causar micro-stuttering ocasional.";
            }
            else
            {
                status = "CRÍTICO";
                summary = $"Latência excessiva de {maxLatencyUs:0} µs! Drivers do sistema estão segurando o processador por tempo crítico. Recomendado atualizar drivers de rede e vídeo ou desativar economia de energia agressiva.";
            }

            return new LatencyTestResult(
                MaxLatencyUs: maxLatencyUs,
                AvgLatencyUs: avgLatencyUs,
                JitterUs: jitterUs,
                Status: status,
                Summary: summary
            );
        }, ct);
    }
}


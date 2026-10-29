using System.Diagnostics;
using LibreHardwareMonitor.Hardware;

namespace ThermoSync;

public sealed class FanDeviceVm
{
    public string Id { get; init; } = "";
    public string HardwareName { get; init; } = "";
    public string Name { get; init; } = "";
    public float? Rpm { get; set; }
    public float? ControlPercent { get; set; }
    public bool HasControl { get; init; }
    public IControl? Control { get; init; }
    public string Mode { get; set; } = "Auto"; // Auto | Manual | Curve
    public int StallCycleCount { get; set; }
    public bool IsStalled { get; set; }
}

/// <summary>
/// Manages active PWM fan speed control and curve profiles across Motherboard and GPU fans
/// via LibreHardwareMonitorLib IControl.
/// </summary>
public sealed class FanControlService : IDisposable
{
    private readonly SensorMonitor _monitor;
    private readonly List<FanDeviceVm> _fans = new();
    private readonly object _lock = new();

    public IReadOnlyList<FanDeviceVm> Fans
    {
        get
        {
            lock (_lock) return _fans.ToList();
        }
    }

    public string ActiveProfile { get; private set; } = "Auto";
    public int ManualPercent { get; private set; } = 60;

    /// <summary>Fires when a fan appears stalled (PWM above threshold but RPM is zero for several cycles).</summary>
    public event Action<FanDeviceVm>? OnFanStalled;

    private const int StallThresholdCycles = 5;
    private const float StallMinPwm = 35f;

    public FanControlService(SensorMonitor monitor)
    {
        _monitor = monitor;
    }

    /// <summary>
    /// Scans hardware tree for Fan sensors and their corresponding PWM Control objects.
    /// </summary>
    public void DiscoverFans(Computer computer)
    {
        lock (_lock)
        {
            _fans.Clear();
            foreach (var hw in computer.Hardware)
            {
                ScanHardware(hw);
            }
        }
    }

    private void ScanHardware(IHardware hw)
    {
        hw.Update();

        var fanSensors = hw.Sensors.Where(s => s.SensorType == SensorType.Fan).ToList();
        var controlSensors = hw.Sensors.Where(s => s.SensorType == SensorType.Control).ToList();

        // 1. Pair each Fan sensor with its control if available
        foreach (var fan in fanSensors)
        {
            var matchingCtrl = controlSensors.FirstOrDefault(c =>
                c.Index == fan.Index ||
                c.Name.Contains(fan.Name, StringComparison.OrdinalIgnoreCase) ||
                fan.Name.Contains(c.Name, StringComparison.OrdinalIgnoreCase));

            IControl? ctrl = matchingCtrl?.Control ?? fan.Control;

            _fans.Add(new FanDeviceVm
            {
                Id = fan.Identifier.ToString(),
                HardwareName = hw.Name,
                Name = fan.Name,
                Rpm = fan.Value,
                ControlPercent = matchingCtrl?.Value,
                HasControl = ctrl != null,
                Control = ctrl,
                Mode = "Auto"
            });
        }

        // 2. Add orphan controls that didn't have an RPM sensor (common on some GPUs/motherboards)
        foreach (var ctrl in controlSensors)
        {
            if (!_fans.Any(f => f.Control == ctrl.Control))
            {
                _fans.Add(new FanDeviceVm
                {
                    Id = ctrl.Identifier.ToString(),
                    HardwareName = hw.Name,
                    Name = ctrl.Name,
                    Rpm = null,
                    ControlPercent = ctrl.Value,
                    HasControl = ctrl.Control != null,
                    Control = ctrl.Control,
                    Mode = "Auto"
                });
            }
        }

        foreach (var sub in hw.SubHardware)
        {
            ScanHardware(sub);
        }
    }

    /// <summary>
    /// Refreshes RPM and percentage values for discovered fans.
    /// </summary>
    public void UpdateValues()
    {
        lock (_lock)
        {
            foreach (var fan in _fans)
            {
                var row = _monitor.Rows.FirstOrDefault(r => r.Id == fan.Id);
                if (row != null && row.RawValue.HasValue)
                {
                    if (row.Type == SensorType.Fan)
                        fan.Rpm = row.RawValue.Value;
                    else if (row.Type == SensorType.Control)
                        fan.ControlPercent = row.RawValue.Value;
                }

                // Stall detection: PWM commanded above threshold but RPM reads zero
                if (fan.HasControl && fan.ControlPercent >= StallMinPwm && fan.Rpm is 0 or null)
                {
                    fan.StallCycleCount++;
                    if (fan.StallCycleCount >= StallThresholdCycles && !fan.IsStalled)
                    {
                        fan.IsStalled = true;
                        OnFanStalled?.Invoke(fan);
                    }
                }
                else
                {
                    fan.StallCycleCount = 0;
                    fan.IsStalled = false;
                }
            }
        }
    }

    private readonly Dictionary<string, float> _lastAppliedPwm = new();
    private readonly Dictionary<string, float> _lastRefTemp = new();
    private DateTime _lastUpdateTime = DateTime.UtcNow;

    /// <summary>
    /// Applies profile: "Auto", "Quiet", "Balanced", "Performance", "Manual", or "Custom".
    /// </summary>
    public void ApplyProfile(
        string profile,
        int manualPct = 60,
        float? cpuTemp = null,
        float? gpuTemp = null,
        string curveSource = "Max",
        (float Temp, float Pct)[]? customPoints = null,
        float hysteresis = 3.0f,
        float maxRampDownPerSec = 4.0f)
    {
        ActiveProfile = profile;
        ManualPercent = manualPct;

        DateTime now = DateTime.UtcNow;
        double deltaSec = Math.Clamp((now - _lastUpdateTime).TotalSeconds, 0.2, 5.0);
        _lastUpdateTime = now;

        float maxTemp = Math.Max(cpuTemp ?? 40f, gpuTemp ?? 40f);

        lock (_lock)
        {
            foreach (var fan in _fans)
            {
                if (!fan.HasControl || fan.Control == null) continue;

                try
                {
                    switch (profile.ToLowerInvariant())
                    {
                        case "auto":
                            fan.Control.SetDefault();
                            fan.Mode = "Auto";
                            _lastAppliedPwm.Remove(fan.Id);
                            _lastRefTemp.Remove(fan.Id);
                            break;

                        case "manual":
                            float targetManual = Math.Clamp(manualPct, 15f, 100f);
                            fan.Control.SetSoftware(targetManual);
                            fan.ControlPercent = targetManual;
                            fan.Mode = "Manual";
                            _lastAppliedPwm[fan.Id] = targetManual;
                            break;

                        case "custom":
                            float refTemp = curveSource.ToLowerInvariant() switch
                            {
                                "cpu" => cpuTemp ?? 40f,
                                "gpu" => gpuTemp ?? 40f,
                                _ => Math.Max(cpuTemp ?? 40f, gpuTemp ?? 40f)
                            };
                            var pts = customPoints is { Length: >= 2 } ? customPoints : new (float, float)[]
                            {
                                (35f, 25f), (55f, 45f), (70f, 70f), (85f, 100f)
                            };
                            float rawTarget = InterpolateCurve(refTemp, pts);
                            float customTarget = SmoothPwm(fan.Id, rawTarget, refTemp, hysteresis, maxRampDownPerSec, deltaSec);
                            fan.Control.SetSoftware(customTarget);
                            fan.ControlPercent = customTarget;
                            fan.Mode = "Personalizado";
                            break;

                        case "quiet":
                            // Silent profile: quiet up to 65C, ramps up to 100% at 85C
                            float rawQuiet = InterpolateCurve(maxTemp,
                                (35f, 25f),
                                (55f, 35f),
                                (70f, 55f),
                                (85f, 100f));
                            float quietTarget = SmoothPwm(fan.Id, rawQuiet, maxTemp, hysteresis, maxRampDownPerSec, deltaSec);
                            fan.Control.SetSoftware(quietTarget);
                            fan.ControlPercent = quietTarget;
                            fan.Mode = "Silencioso";
                            break;

                        case "balanced":
                            // Balanced profile: reasonable noise, ramp up at 60C
                            float rawBalanced = InterpolateCurve(maxTemp,
                                (35f, 35f),
                                (50f, 50f),
                                (68f, 75f),
                                (80f, 100f));
                            float balancedTarget = SmoothPwm(fan.Id, rawBalanced, maxTemp, hysteresis, maxRampDownPerSec, deltaSec);
                            fan.Control.SetSoftware(balancedTarget);
                            fan.ControlPercent = balancedTarget;
                            fan.Mode = "Equilibrado";
                            break;

                        case "performance":
                            // Performance: aggressive airflow for low temps
                            float rawPerf = InterpolateCurve(maxTemp,
                                (35f, 50f),
                                (50f, 70f),
                                (65f, 90f),
                                (75f, 100f));
                            float perfTarget = SmoothPwm(fan.Id, rawPerf, maxTemp, hysteresis, maxRampDownPerSec, deltaSec);
                            fan.Control.SetSoftware(perfTarget);
                            fan.ControlPercent = perfTarget;
                            fan.Mode = "Desempenho";
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[FanControlService] Error setting fan {fan.Name}: {ex.Message}");
                }
            }
        }
    }

    private float SmoothPwm(string fanId, float targetPwm, float currentRefTemp, float hysteresis, float maxRampDownPerSec, double deltaSec)
    {
        if (!_lastAppliedPwm.TryGetValue(fanId, out float lastPwm))
        {
            _lastAppliedPwm[fanId] = targetPwm;
            _lastRefTemp[fanId] = currentRefTemp;
            return targetPwm;
        }

        float lastTemp = _lastRefTemp.TryGetValue(fanId, out float lt) ? lt : currentRefTemp;

        // Resposta imediata de subida (prioridade para refrigeracao segura)
        if (targetPwm >= lastPwm || currentRefTemp > lastTemp)
        {
            _lastAppliedPwm[fanId] = targetPwm;
            _lastRefTemp[fanId] = currentRefTemp;
            return targetPwm;
        }

        // Histerese termica: se a queda for menor que o limiar de histerese, mantem o PWM
        if (lastTemp - currentRefTemp < hysteresis)
        {
            return lastPwm;
        }

        // Desaceleracao suave amortecida por segundo
        float maxDrop = (float)(maxRampDownPerSec * Math.Max(0.5, deltaSec));
        float smoothedPwm = Math.Max(targetPwm, lastPwm - maxDrop);

        _lastAppliedPwm[fanId] = smoothedPwm;
        _lastRefTemp[fanId] = currentRefTemp;
        return smoothedPwm;
    }

    private static float InterpolateCurve(float temp, params (float Temp, float Pct)[] points)
    {
        if (points.Length == 0) return 50f;
        if (temp <= points[0].Temp) return points[0].Pct;
        if (temp >= points[^1].Temp) return points[^1].Pct;

        for (int i = 0; i < points.Length - 1; i++)
        {
            var p1 = points[i];
            var p2 = points[i + 1];
            if (temp >= p1.Temp && temp <= p2.Temp)
            {
                float t = (temp - p1.Temp) / (p2.Temp - p1.Temp);
                return p1.Pct + t * (p2.Pct - p1.Pct);
            }
        }
        return points[^1].Pct;
    }

    /// <summary>
    /// Restores all fans to firmware/BIOS default control.
    /// Safety watchdog must call this upon shutdown or crash.
    /// </summary>
    public void RestoreDefaults()
    {
        lock (_lock)
        {
            foreach (var fan in _fans)
            {
                try
                {
                    fan.Control?.SetDefault();
                    fan.Mode = "Auto";
                }
                catch { }
            }
        }
    }

    public void Dispose()
    {
        RestoreDefaults();
    }
}

namespace ThermoSync;

public enum ThrottlingType
{
    None,
    PowerLimit,
    ThermalThrottling
}

public sealed record ThrottlingStatus(
    ThrottlingType CpuThrottling,
    string? CpuReason,
    ThrottlingType GpuThrottling,
    string? GpuReason
)
{
    public bool HasThermal => CpuThrottling == ThrottlingType.ThermalThrottling || GpuThrottling == ThrottlingType.ThermalThrottling;
    public bool HasPower => CpuThrottling == ThrottlingType.PowerLimit || GpuThrottling == ThrottlingType.PowerLimit;

    public string BadgeText => HasThermal
        ? "Thermal Throttling Detectado"
        : (HasPower ? "Power Limit Ativo" : "Estável / Sem Throttling");

    public string BadgeColorHex => HasThermal
        ? "#EF4444"
        : (HasPower ? "#F59E0B" : "#10B981");
}

/// <summary>
/// Monitors and evaluates active thermal and power throttling on CPU and GPU.
/// Uses hardware telemetry, TjMax boundaries, and NVML throttle reasons.
/// </summary>
public static class ThrottlingDetector
{
    // NVML reason constants
    private const ulong NVML_THROTTLE_SW_POWER_CAP = 0x0000000000000004UL;
    private const ulong NVML_THROTTLE_HW_SLOWDOWN = 0x0000000000000008UL;
    private const ulong NVML_THROTTLE_SW_THERMAL = 0x0000000000000020UL;
    private const ulong NVML_THROTTLE_HW_THERMAL = 0x0000000000000040UL;
    private const ulong NVML_THROTTLE_HW_POWER_BRAKE = 0x0000000000000080UL;

    public static ThrottlingStatus Evaluate(
        float? cpuTemp,
        float? cpuLoad,
        float? cpuClockMhz,
        float? gpuTemp,
        float? gpuHotspot,
        float? gpuLoad,
        float? gpuPowerW,
        IntPtr nvmlHandle = default)
    {
        // 1. Evaluate CPU Throttling
        var cpuType = ThrottlingType.None;
        string? cpuReason = null;

        if (cpuTemp.HasValue && cpuTemp.Value >= 95f && (cpuLoad ?? 0f) >= 70f)
        {
            cpuType = ThrottlingType.ThermalThrottling;
            cpuReason = $"CPU a {cpuTemp.Value:0}°C atingiu limite térmico (TjMax)";
        }
        else if (cpuTemp.HasValue && cpuTemp.Value >= 90f && (cpuLoad ?? 0f) >= 85f)
        {
            cpuType = ThrottlingType.PowerLimit;
            cpuReason = $"CPU a {cpuTemp.Value:0}°C próxima ao limite sustentado (PL1/PL2)";
        }

        // 2. Evaluate GPU Throttling
        var gpuType = ThrottlingType.None;
        string? gpuReason = null;

        if (nvmlHandle != IntPtr.Zero)
        {
            ulong? reasons = Nvml.ThrottleReasons(nvmlHandle);
            if (reasons.HasValue && reasons.Value > 0)
            {
                ulong r = reasons.Value;
                if ((r & (NVML_THROTTLE_SW_THERMAL | NVML_THROTTLE_HW_THERMAL)) != 0)
                {
                    gpuType = ThrottlingType.ThermalThrottling;
                    gpuReason = "NVML: Corte térmico ativo (SW/HW Thermal Slowdown)";
                }
                else if ((r & (NVML_THROTTLE_SW_POWER_CAP | NVML_THROTTLE_HW_POWER_BRAKE)) != 0 && (gpuLoad ?? 0f) >= 70f)
                {
                    gpuType = ThrottlingType.PowerLimit;
                    gpuReason = "NVML: Limite de consumo atingido (Power Limit)";
                }
            }
        }

        // Generic fallback if NVML didn't flag or on AMD / Intel GPUs
        if (gpuType == ThrottlingType.None)
        {
            if ((gpuHotspot.HasValue && gpuHotspot.Value >= 102f) || (gpuTemp.HasValue && gpuTemp.Value >= 86f))
            {
                if ((gpuLoad ?? 0f) >= 70f)
                {
                    gpuType = ThrottlingType.ThermalThrottling;
                    gpuReason = $"GPU Hotspot a {(gpuHotspot ?? gpuTemp):0}°C atingiu limite térmico";
                }
            }
            else if (gpuTemp.HasValue && gpuTemp.Value >= 80f && (gpuLoad ?? 0f) >= 85f)
            {
                gpuType = ThrottlingType.PowerLimit;
                gpuReason = $"GPU sob alta carga ({gpuTemp.Value:0}°C)";
            }
        }

        return new ThrottlingStatus(cpuType, cpuReason, gpuType, gpuReason);
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ThermoSync;

/// <summary>
/// Direct Win32 PowrProf wrapper to query and switch Windows power schemes with zero latency.
/// Eliminates slow powercfg.exe process spawning and handles automatic rollback on game exit.
/// </summary>
public static class PowerPlanManager
{
    // Canonical Windows Power Scheme GUIDs
    public static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid PowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    public static readonly Guid UltimatePerformance = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private static readonly object _lock = new();
    private static Guid? _savedScheme;
    private static int _activeGameUsers;

    /// <summary>
    /// Reads the currently active Windows power scheme GUID.
    /// </summary>
    public static Guid? GetActiveScheme()
    {
        try
        {
            uint status = PowerGetActiveScheme(IntPtr.Zero, out IntPtr pGuid);
            if (status == 0 && pGuid != IntPtr.Zero)
            {
                try
                {
                    return Marshal.PtrToStructure<Guid>(pGuid);
                }
                finally
                {
                    LocalFree(pGuid);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PowerPlanManager] Error getting active power scheme: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// Activates the specified power scheme via Win32 API.
    /// </summary>
    public static bool SetActiveScheme(Guid schemeGuid)
    {
        try
        {
            uint status = PowerSetActiveScheme(IntPtr.Zero, ref schemeGuid);
            return status == 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PowerPlanManager] Error setting active power scheme: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Switches Windows power scheme to High Performance, remembering the user's previous scheme.
    /// Thread-safe and reference-counted across active launchers and foreground detectors.
    /// </summary>
    public static bool EnterHighPerformanceMode()
    {
        lock (_lock)
        {
            _activeGameUsers++;
            if (_activeGameUsers > 1)
            {
                return true; // Already boosted
            }

            var current = GetActiveScheme();
            if (current.HasValue && current.Value != HighPerformance && current.Value != UltimatePerformance)
            {
                _savedScheme = current.Value;
                bool ok = SetActiveScheme(HighPerformance);
                Debug.WriteLine($"[PowerPlanManager] Boosted to High Performance (saved: {_savedScheme})");
                return ok;
            }

            return true;
        }
    }

    /// <summary>
    /// Restores the previously active Windows power scheme when all games terminate.
    /// </summary>
    public static bool RestorePreviousMode()
    {
        lock (_lock)
        {
            if (_activeGameUsers <= 0) return true;

            _activeGameUsers--;
            if (_activeGameUsers == 0 && _savedScheme.HasValue)
            {
                var schemeToRestore = _savedScheme.Value;
                _savedScheme = null;
                bool ok = SetActiveScheme(schemeToRestore);
                Debug.WriteLine($"[PowerPlanManager] Restored previous power scheme: {schemeToRestore}");
                return ok;
            }

            return true;
        }
    }

    /// <summary>
    /// Safety reset called on application shutdown.
    /// </summary>
    public static void ResetOnShutdown()
    {
        lock (_lock)
        {
            if (_savedScheme.HasValue)
            {
                var scheme = _savedScheme.Value;
                _savedScheme = null;
                _activeGameUsers = 0;
                SetActiveScheme(scheme);
            }
        }
    }
}
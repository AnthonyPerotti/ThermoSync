using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ThermoSync;

public enum CpuAffinityMode
{
    All = 0,
    PCoresOnly = 1,
    ECoresOnly = 2,
    VCacheCcd = 3,
    NoSmt = 4
}

/// <summary>
/// Manages CPU core affinity and thread topology targeting for gaming processes.
/// Detects Intel Hybrid architectures (P-cores vs E-cores) and AMD dual-CCD setups (such as X3D cache CCDs).
/// </summary>
public static class CpuAffinityManager
{
    private static readonly Lazy<CpuTopologyInfo> Topology = new(DetectTopology);

    public static CpuTopologyInfo CurrentTopology => Topology.Value;

    public static bool ApplyAffinity(Process process, CpuAffinityMode mode)
    {
        if (mode == CpuAffinityMode.All)
        {
            try
            {
                process.ProcessorAffinity = unchecked((IntPtr)(long)Topology.Value.AllMask);
                return true;
            }
            catch
            {
                return false;
            }
        }

        ulong mask = mode switch
        {
            CpuAffinityMode.PCoresOnly when Topology.Value.PCoreMask != 0 => Topology.Value.PCoreMask,
            CpuAffinityMode.ECoresOnly when Topology.Value.ECoreMask != 0 => Topology.Value.ECoreMask,
            CpuAffinityMode.VCacheCcd when Topology.Value.Ccd0Mask != 0 => Topology.Value.Ccd0Mask,
            CpuAffinityMode.NoSmt when Topology.Value.PhysicalCoresMask != 0 => Topology.Value.PhysicalCoresMask,
            _ => Topology.Value.AllMask
        };

        if (mask == 0) mask = Topology.Value.AllMask;

        try
        {
            process.ProcessorAffinity = unchecked((IntPtr)(long)mask);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CpuAffinityManager] Failed to apply affinity mask 0x{mask:X}: {ex.Message}");
            return false;
        }
    }

    private static CpuTopologyInfo DetectTopology()
    {
        int logicalCores = Environment.ProcessorCount;
        ulong allMask = logicalCores >= 64 ? ulong.MaxValue : (1UL << logicalCores) - 1UL;

        ulong pCoreMask = 0;
        ulong eCoreMask = 0;
        ulong ccd0Mask = 0;
        ulong physicalCoresMask = 0;
        bool isHybrid = false;
        bool isDualCcd = false;

        try
        {
            uint returnLength = 0;
            GetLogicalProcessorInformationEx(LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore, IntPtr.Zero, ref returnLength);

            if (returnLength > 0)
            {
                IntPtr buffer = Marshal.AllocHGlobal((int)returnLength);
                try
                {
                    if (GetLogicalProcessorInformationEx(LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore, buffer, ref returnLength))
                    {
                        int offset = 0;
                        var efficiencyClasses = new List<(byte Efficiency, ulong Mask)>();
                        int coreIndex = 0;

                        while (offset < returnLength)
                        {
                            var info = Marshal.PtrToStructure<PROCESSOR_RELATIONSHIP_CORE>(buffer + offset);
                            if (info.Relationship == LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore)
                            {
                                ulong groupMask = info.GroupMask0.Mask.ToUInt64();
                                efficiencyClasses.Add((info.EfficiencyClass, groupMask));

                                // Physical core (lowest bit of each core's mask represents the primary physical thread)
                                ulong primaryThread = groupMask & (~groupMask + 1UL);
                                physicalCoresMask |= primaryThread;

                                coreIndex++;
                            }

                            if (info.Size == 0) break;
                            offset += (int)info.Size;
                        }

                        byte minEff = efficiencyClasses.Count > 0 ? efficiencyClasses.Min(e => e.Efficiency) : (byte)0;
                        byte maxEff = efficiencyClasses.Count > 0 ? efficiencyClasses.Max(e => e.Efficiency) : (byte)0;

                        if (minEff != maxEff)
                        {
                            isHybrid = true;
                            foreach (var (eff, mask) in efficiencyClasses)
                            {
                                if (eff == maxEff) pCoreMask |= mask;
                                else eCoreMask |= mask;
                            }
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CpuAffinityManager] Topology scan failed: {ex.Message}");
        }

        // Check for AMD Dual CCD setup or fallback when Intel Hybrid is not present
        if (!isHybrid && logicalCores >= 12)
        {
            // Typical 12/16/24/32-core setups: first half of threads correspond to CCD0 (Cache CCD on X3D)
            int halfCores = logicalCores / 2;
            ccd0Mask = (1UL << halfCores) - 1UL;
            isDualCcd = true;
        }

        // Fallback for physical cores mask if Win32 API returned zero
        if (physicalCoresMask == 0)
        {
            for (int i = 0; i < logicalCores; i += 2)
            {
                physicalCoresMask |= (1UL << i);
            }
        }

        return new CpuTopologyInfo(
            logicalCores,
            allMask,
            pCoreMask,
            eCoreMask,
            ccd0Mask,
            physicalCoresMask,
            isHybrid,
            isDualCcd
        );
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(
        LOGICAL_PROCESSOR_RELATIONSHIP relationshipType,
        IntPtr buffer,
        ref uint returnedLength);

    private enum LOGICAL_PROCESSOR_RELATIONSHIP
    {
        RelationProcessorCore = 0,
        RelationNumaNode = 1,
        RelationCache = 2,
        RelationProcessorPackage = 3,
        RelationGroup = 4,
        RelationAll = 0xffff
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GROUP_AFFINITY
    {
        public UIntPtr Mask;
        public ushort Group;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
        public ushort[] Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESSOR_RELATIONSHIP_CORE
    {
        public LOGICAL_PROCESSOR_RELATIONSHIP Relationship;
        public uint Size;
        public byte Flags;
        public byte EfficiencyClass;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 20)]
        public byte[] Reserved;
        public ushort GroupCount;
        public GROUP_AFFINITY GroupMask0;
    }
}

public sealed record CpuTopologyInfo(
    int LogicalCores,
    ulong AllMask,
    ulong PCoreMask,
    ulong ECoreMask,
    ulong Ccd0Mask,
    ulong PhysicalCoresMask,
    bool IsHybrid,
    bool IsDualCcd
);


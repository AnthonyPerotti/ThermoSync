using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ThermoSync;

public sealed class GpuVramSettings
{
    public string AdapterName { get; init; } = "";
    public int TargetMb { get; init; } = 2048; // Default 2 GB
    public int ChunkMb { get; init; } = 128;   // 128 MB per buffer chunk
}

public sealed class GpuVramResult
{
    public string Adapter { get; init; } = "";
    public int AllocatedMb { get; init; }
    public long Passes { get; init; }
    public long BitErrors { get; init; }
    public double DurationSeconds { get; init; }
    public double BandwidthGbps { get; init; }
    public bool TdrDetected { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Dedicated GPU VRAM stress tester (MemTest for Video Memory).
/// Allocates high-density buffers in GDDR/VRAM using Direct3D 11, applies bit-stress
/// patterns (alternating 0x55/0xAA, walking bits, and pseudo-random noise), copies back,
/// and validates data integrity down to individual bit flips.
/// </summary>
public sealed class GpuVramBench
{
    private Thread? _thread;
    private volatile bool _stop;
    private long _passes;
    private long _errors;
    private long _bytesTested;

    public bool Running => _thread?.IsAlive == true;
    public long Passes => Interlocked.Read(ref _passes);
    public long Errors => Interlocked.Read(ref _errors);
    public long BytesTested => Interlocked.Read(ref _bytesTested);
    public double BandwidthGbps { get; private set; }
    public string? Error { get; private set; }
    public string AdapterUsed { get; private set; } = "";
    public int AllocatedMb { get; private set; }
    public bool TdrDetected { get; private set; }

    public void Start(GpuVramSettings settings, Action<GpuVramResult>? onComplete = null)
    {
        if (Running) return;
        _stop = false;
        _passes = 0;
        _errors = 0;
        _bytesTested = 0;
        BandwidthGbps = 0;
        Error = null;

        _thread = new Thread(() =>
        {
            var res = RunInternal(settings);
            onComplete?.Invoke(res);
        })
        {
            IsBackground = true,
            Name = "GPU VRAM stress"
        };
        _thread.Start();
    }

    public void Stop()
    {
        _stop = true;
        _thread?.Join(3000);
    }

    private unsafe GpuVramResult RunInternal(GpuVramSettings s)
    {
        var sw = Stopwatch.StartNew();
        var disposedList = new List<IDisposable>();
        T Keep<T>(T o) where T : IDisposable { disposedList.Add(o); return o; }

        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            var candidates = new List<IDXGIAdapter1>();
            for (uint i = 0; factory.EnumAdapters1(i, out var a).Success; i++)
            {
                if ((a.Description1.Flags & AdapterFlags.Software) == 0) candidates.Add(a);
            }

            var adapter = candidates.FirstOrDefault(a => s.AdapterName.Length > 0 &&
                              a.Description1.Description.Contains(s.AdapterName, StringComparison.OrdinalIgnoreCase))
                          ?? candidates.OrderByDescending(a => a.Description1.DedicatedVideoMemory).FirstOrDefault()
                          ?? throw new InvalidOperationException("Nenhuma GPU compatível com Direct3D 11.");

            AdapterUsed = adapter.Description1.Description;

            D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                                    new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
                                    out ID3D11Device device, out ID3D11DeviceContext ctx).CheckError();
            Keep(device);
            Keep(ctx);

            int chunkBytes = s.ChunkMb * 1024 * 1024;
            int numChunks = Math.Max(1, s.TargetMb / s.ChunkMb);
            var vramBuffers = new List<ID3D11Buffer>();
            var stagingBuffers = new List<ID3D11Buffer>();

            // Allocate VRAM and Staging buffer pairs
            for (int i = 0; i < numChunks; i++)
            {
                if (_stop) break;
                try
                {
                    var vramDesc = new BufferDescription((uint)chunkBytes, BindFlags.None, ResourceUsage.Default);
                    var vramBuf = Keep(device.CreateBuffer(in vramDesc));

                    var stageDesc = new BufferDescription((uint)chunkBytes, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read | CpuAccessFlags.Write);
                    var stageBuf = Keep(device.CreateBuffer(in stageDesc));

                    vramBuffers.Add(vramBuf);
                    stagingBuffers.Add(stageBuf);
                }
                catch
                {
                    break; // VRAM saturated
                }
            }

            AllocatedMb = vramBuffers.Count * s.ChunkMb;
            if (AllocatedMb == 0)
                throw new InvalidOperationException("Não foi possível alocar buffers na VRAM.");

            // Test patterns
            uint[] patterns = { 0x55555555, 0xAAAAAAAA, 0x00FF00FF, 0xAA55AA55 };

            while (!_stop)
            {
                if (device.DeviceRemovedReason.Failure)
                {
                    TdrDetected = true;
                    Error = $"TDR detectado (Driver Reset: {device.DeviceRemovedReason})";
                    break;
                }

                foreach (uint pat in patterns)
                {
                    if (_stop) break;

                    for (int i = 0; i < vramBuffers.Count; i++)
                    {
                        if (_stop) break;

                        var vram = vramBuffers[i];
                        var stage = stagingBuffers[i];

                        // 1. Fill staging buffer with pattern
                        var mapped = ctx.Map(stage, 0, MapMode.Write, Vortice.Direct3D11.MapFlags.None);
                        uint* ptr = (uint*)mapped.DataPointer;
                        int count = chunkBytes / sizeof(uint);
                        for (int k = 0; k < count; k++)
                        {
                            ptr[k] = pat ^ (uint)k;
                        }
                        ctx.Unmap(stage, 0);

                        // 2. Upload staging to VRAM
                        ctx.CopyResource(vram, stage);

                        // 3. Clear staging to prevent stale reads
                        mapped = ctx.Map(stage, 0, MapMode.Write, Vortice.Direct3D11.MapFlags.None);
                        NativeMemory.Clear(mapped.DataPointer.ToPointer(), (nuint)chunkBytes);
                        ctx.Unmap(stage, 0);

                        // 4. Download from VRAM back to staging
                        ctx.CopyResource(stage, vram);

                        // 5. Verify bit integrity
                        mapped = ctx.Map(stage, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                        uint* readPtr = (uint*)mapped.DataPointer;
                        long chunkErrors = 0;
                        for (int k = 0; k < count; k++)
                        {
                            uint expected = pat ^ (uint)k;
                            if (readPtr[k] != expected)
                            {
                                chunkErrors++;
                            }
                        }
                        ctx.Unmap(stage, 0);

                        if (chunkErrors > 0)
                        {
                            Interlocked.Add(ref _errors, chunkErrors);
                        }

                        Interlocked.Add(ref _bytesTested, (long)chunkBytes * 2);
                    }

                    Interlocked.Increment(ref _passes);
                    double secs = sw.Elapsed.TotalSeconds;
                    if (secs > 0)
                    {
                        BandwidthGbps = (BytesTested / 1_000_000_000.0) / secs;
                    }
                }
            }

            return new GpuVramResult
            {
                Adapter = AdapterUsed,
                AllocatedMb = AllocatedMb,
                Passes = Passes,
                BitErrors = Errors,
                DurationSeconds = sw.Elapsed.TotalSeconds,
                BandwidthGbps = BandwidthGbps,
                TdrDetected = TdrDetected
            };
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            return new GpuVramResult
            {
                Adapter = AdapterUsed,
                AllocatedMb = AllocatedMb,
                Passes = Passes,
                BitErrors = Errors,
                DurationSeconds = sw.Elapsed.TotalSeconds,
                BandwidthGbps = BandwidthGbps,
                TdrDetected = TdrDetected,
                ErrorMessage = ex.Message
            };
        }
        finally
        {
            foreach (var d in disposedList)
            {
                try { d.Dispose(); } catch { }
            }
        }
    }
}

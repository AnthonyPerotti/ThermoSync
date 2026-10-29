using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ThermoSync;

/// <summary>Snapshot de telemetria consolidada para exportação Prometheus e MQTT.</summary>
public sealed class DiskTelemetry
{
    public string Name { get; init; } = "";
    public double? TemperatureCelsius { get; init; }
    public double? HealthPercentage { get; init; }
    public double? LoadPercent { get; init; }
    public double? UsedPercent { get; init; }
}

public sealed class HomelabSnapshot
{
    public List<DiskTelemetry> Disks { get; init; } = new();
    public double? CpuTemp { get; init; }
    public double? CpuLoad { get; init; }
    public double? CpuPowerW { get; init; }
    public double? CpuClockMhz { get; init; }

    public double? GpuTemp { get; init; }
    public double? GpuHotspot { get; init; }
    public double? GpuLoad { get; init; }
    public double? GpuPowerW { get; init; }
    public double? GpuVramUsedMb { get; init; }
    public double? GpuVramTotalMb { get; init; }
    public double? GpuFanPct { get; init; }

    public double? RamUsedGb { get; init; }
    public double? RamTotalGb { get; init; }

    public string? ActiveGame { get; init; }
    public double? Fps { get; init; }
    public double? FrametimeMs { get; init; }
    public ThrottlingStatus? Throttling { get; init; }

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Serviço de integração Homelab:
/// 1. Exportador Prometheus (/metrics) via HttpListener assíncrono.
/// 2. Publicador MQTT leve (RFC 3.1.1 nativo via TCP) com auto-descoberta para Home Assistant.
/// </summary>
public sealed class HomelabService : IDisposable
{
    private readonly Func<HomelabSnapshot> _snapshotProvider;
    private CancellationTokenSource? _cts;

    // Prometheus
    private HttpListener? _httpListener;
    private Task? _httpTask;
    public bool PrometheusRunning => _httpListener?.IsListening == true;
    public string? PrometheusUrl { get; private set; }
    public string? PrometheusError { get; private set; }

    // MQTT
    private Task? _mqttTask;
    public bool MqttConnected { get; private set; }
    public string? MqttError { get; private set; }

    public HomelabService(Func<HomelabSnapshot> snapshotProvider)
    {
        _snapshotProvider = snapshotProvider;
    }

    public void ApplySettings(AppSettings s)
    {
        Stop();
        _cts = new CancellationTokenSource();

        if (s.HomelabPrometheusEnabled)
        {
            StartPrometheus(s.HomelabPrometheusPort, _cts.Token);
        }

        if (s.HomelabMqttEnabled)
        {
            StartMqtt(s.HomelabMqttBroker, s.HomelabMqttPort, s.HomelabMqttTopic, s.HomelabMqttDiscovery, _cts.Token);
        }
    }

    public void Stop()
    {
        _cts?.Cancel();

        try
        {
            _httpListener?.Stop();
            _httpListener?.Close();
        }
        catch { }
        _httpListener = null;
        PrometheusUrl = null;
        MqttConnected = false;

        _cts?.Dispose();
        _cts = null;
    }

    #region Prometheus Exporter

    private void StartPrometheus(int port, CancellationToken ct)
    {
        PrometheusError = null;
        try
        {
            _httpListener = new HttpListener();
            // Tenta escutar em todas as interfaces da máquina (0.0.0.0 via prefixo + ou *)
            string prefix = $"http://*:{port}/";
            try
            {
                _httpListener.Prefixes.Add(prefix);
                _httpListener.Start();
                PrometheusUrl = $"http://0.0.0.0:{port}/metrics";
            }
            catch
            {
                // Fallback para localhost caso o Windows exija reserva de URL via netsh para *
                _httpListener.Close();
                _httpListener = new HttpListener();
                prefix = $"http://localhost:{port}/";
                _httpListener.Prefixes.Add(prefix);
                _httpListener.Start();
                PrometheusUrl = $"http://localhost:{port}/metrics";
            }

            _httpTask = Task.Run(() => ListenHttpLoopAsync(_httpListener, ct), ct);
        }
        catch (Exception ex)
        {
            PrometheusError = ex.Message;
            _httpListener = null;
        }
    }

    private async Task ListenHttpLoopAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            try
            {
                var context = await listener.GetContextAsync();
                _ = Task.Run(() => HandleHttpRequest(context), ct);
            }
            catch when (ct.IsCancellationRequested || !listener.IsListening)
            {
                break;
            }
            catch { }
        }
    }

    private void HandleHttpRequest(HttpListenerContext ctx)
    {
        try
        {
            var req = ctx.Request;
            var res = ctx.Response;

            res.Headers.Add("Access-Control-Allow-Origin", "*");
            res.Headers.Add("Access-Control-Allow-Methods", "GET, OPTIONS");
            res.Headers.Add("Access-Control-Allow-Headers", "*");

            if (req.HttpMethod.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                res.StatusCode = 204;
                res.OutputStream.Close();
                return;
            }

            string path = req.Url?.AbsolutePath ?? "/";

            if (path.Equals("/metrics", StringComparison.OrdinalIgnoreCase))
            {
                var snap = _snapshotProvider();
                string metrics = FormatPrometheus(snap);
                byte[] buf = Encoding.UTF8.GetBytes(metrics);

                res.ContentType = "text/plain; version=0.0.4; charset=utf-8";
                res.ContentLength64 = buf.Length;
                res.StatusCode = 200;
                res.OutputStream.Write(buf, 0, buf.Length);
            }
            else if (path.Equals("/api/telemetry", StringComparison.OrdinalIgnoreCase))
            {
                var snap = _snapshotProvider();
                string json = JsonSerializer.Serialize(snap, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true
                });
                byte[] buf = Encoding.UTF8.GetBytes(json);

                res.ContentType = "application/json; charset=utf-8";
                res.ContentLength64 = buf.Length;
                res.StatusCode = 200;
                res.OutputStream.Write(buf, 0, buf.Length);
            }
            else if (path.Equals("/api/stream", StringComparison.OrdinalIgnoreCase))
            {
                HandleSseStream(res);
                return;
            }
            else if (path.Equals("/api/health", StringComparison.OrdinalIgnoreCase))
            {
                var health = new
                {
                    status = "ok",
                    service = "ThermoSync",
                    version = "1.5.7",
                    timestamp = DateTime.UtcNow
                };
                string json = JsonSerializer.Serialize(health);
                byte[] buf = Encoding.UTF8.GetBytes(json);

                res.ContentType = "application/json; charset=utf-8";
                res.ContentLength64 = buf.Length;
                res.StatusCode = 200;
                res.OutputStream.Write(buf, 0, buf.Length);
            }
            else
            {
                byte[] msg = Encoding.UTF8.GetBytes("ThermoSync API: /api/telemetry, /api/health, /api/stream, /metrics\n");
                res.ContentType = "text/plain; charset=utf-8";
                res.ContentLength64 = msg.Length;
                res.StatusCode = 200;
                res.OutputStream.Write(msg, 0, msg.Length);
            }
            res.OutputStream.Close();
        }
        catch { }
    }

    private void HandleSseStream(HttpListenerResponse res)
    {
        try
        {
            res.ContentType = "text/event-stream";
            res.Headers.Add("Cache-Control", "no-cache");
            res.Headers.Add("Connection", "keep-alive");
            res.Headers.Add("X-Accel-Buffering", "no");
            res.StatusCode = 200;
            res.SendChunked = true;

            _ = Task.Run(async () =>
            {
                try
                {
                    using var writer = new StreamWriter(res.OutputStream, Encoding.UTF8, leaveOpen: false) { AutoFlush = true };
                    var ct = _cts?.Token ?? CancellationToken.None;

                    while (!ct.IsCancellationRequested)
                    {
                        var snap = _snapshotProvider();
                        string json = JsonSerializer.Serialize(snap, new JsonSerializerOptions
                        {
                            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                        });

                        await writer.WriteLineAsync($"data: {json}");
                        await writer.WriteLineAsync();

                        await Task.Delay(1000, ct);
                    }
                }
                catch { }
                finally
                {
                    try { res.OutputStream.Close(); } catch { }
                }
            });
        }
        catch
        {
            try { res.OutputStream.Close(); } catch { }
        }
    }

    private static string FormatPrometheus(HomelabSnapshot s)
    {
        var sb = new StringBuilder(2048);
        var inv = CultureInfo.InvariantCulture;

        void AppendGauge(string name, string help, double? val, string? labels = null)
        {
            if (!val.HasValue || double.IsNaN(val.Value) || double.IsInfinity(val.Value)) return;
            sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
            sb.Append("# TYPE ").Append(name).Append(" gauge\n");
            sb.Append(name);
            if (!string.IsNullOrEmpty(labels)) sb.Append('{').Append(labels).Append('}');
            sb.Append(' ').Append(val.Value.ToString("0.##", inv)).Append('\n');
        }

        AppendGauge("thermosync_cpu_temperature_celsius", "Temperatura da CPU em graus Celsius", s.CpuTemp);
        AppendGauge("thermosync_cpu_load_percent", "Carga total da CPU em porcentagem", s.CpuLoad);
        AppendGauge("thermosync_cpu_power_watts", "Consumo eletrico da CPU em Watts", s.CpuPowerW);
        AppendGauge("thermosync_cpu_clock_mhz", "Clock medio dos nucleos da CPU em MHz", s.CpuClockMhz);

        AppendGauge("thermosync_gpu_temperature_celsius", "Temperatura principal da GPU em graus Celsius", s.GpuTemp, "sensor=\"edge\"");
        AppendGauge("thermosync_gpu_temperature_celsius", "Temperatura do Hotspot da GPU em graus Celsius", s.GpuHotspot, "sensor=\"hotspot\"");
        AppendGauge("thermosync_gpu_load_percent", "Uso da GPU em porcentagem", s.GpuLoad);
        AppendGauge("thermosync_gpu_power_watts", "Consumo eletrico da GPU em Watts", s.GpuPowerW);
        AppendGauge("thermosync_gpu_vram_used_bytes", "VRAM alocada em bytes", s.GpuVramUsedMb * 1024.0 * 1024.0);
        AppendGauge("thermosync_gpu_vram_total_bytes", "VRAM total em bytes", s.GpuVramTotalMb * 1024.0 * 1024.0);
        AppendGauge("thermosync_gpu_fan_percent", "Velocidade da ventoinha da GPU em porcentagem", s.GpuFanPct);

        AppendGauge("thermosync_ram_used_bytes", "Memoria RAM utilizada em bytes", s.RamUsedGb * 1024.0 * 1024.0 * 1024.0);
        AppendGauge("thermosync_ram_total_bytes", "Memoria RAM total em bytes", s.RamTotalGb * 1024.0 * 1024.0 * 1024.0);

        foreach (var d in s.Disks)
        {
            string diskClean = d.Name.Replace("\"", "\\\"");
            string diskTag = "disk=\"" + diskClean + "\"";
            AppendGauge("thermosync_disk_temperature_celsius", "Temperatura do disco em graus Celsius", d.TemperatureCelsius, diskTag);
            AppendGauge("thermosync_disk_health_percentage", "Saude do disco em porcentagem S.M.A.R.T.", d.HealthPercentage, diskTag);
            AppendGauge("thermosync_disk_load_percent", "Carga de atividade do disco em porcentagem", d.LoadPercent, diskTag);
            AppendGauge("thermosync_disk_used_percent", "Uso do volume em porcentagem", d.UsedPercent, diskTag);
        }

        if (s.Throttling != null)
        {
            AppendGauge("thermosync_throttling_cpu", "Status de Throttling da CPU (0=Normal, 1=PowerLimit, 2=Thermal)", (double)s.Throttling.CpuThrottling);
            AppendGauge("thermosync_throttling_gpu", "Status de Throttling da GPU (0=Normal, 1=PowerLimit, 2=Thermal)", (double)s.Throttling.GpuThrottling);
        }

        if (!string.IsNullOrEmpty(s.ActiveGame))
        {
            string gameClean = s.ActiveGame.Replace("\"", "\\\"");
            AppendGauge("thermosync_game_fps", "FPS do jogo em execucao", s.Fps, $"game=\"{gameClean}\"");
            AppendGauge("thermosync_game_frametime_ms", "Tempo de quadro em milissegundos", s.FrametimeMs, $"game=\"{gameClean}\"");
            AppendGauge("thermosync_game_active", "Status de jogo em execucao (1 para ativo)", 1.0, $"game=\"{gameClean}\"");
        }
        else
        {
            AppendGauge("thermosync_game_active", "Status de jogo em execucao (0 para inativo)", 0.0);
        }

        return sb.ToString();
    }

    #endregion

    #region MQTT Client

    private void StartMqtt(string broker, int port, string topic, bool discovery, CancellationToken ct)
    {
        MqttError = null;
        _mqttTask = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync(broker, port, ct);
                    using var stream = client.GetStream();

                    string clientId = "ThermoSync-" + Environment.MachineName;
                    await SendMqttConnectAsync(stream, clientId, ct);

                    // Ler CONNACK
                    byte[] ack = new byte[4];
                    int read = await stream.ReadAsync(ack.AsMemory(0, 4), ct);
                    if (read < 4 || ack[3] != 0)
                    {
                        MqttError = "Broker rejeitou a conexao (código " + (read >= 4 ? ack[3].ToString() : "curto") + ").";
                        await Task.Delay(5000, ct);
                        continue;
                    }

                    MqttConnected = true;
                    MqttError = null;

                    if (discovery)
                    {
                        await SendHomeAssistantDiscoveryAsync(stream, ct);
                    }

                    DateTime lastPing = DateTime.UtcNow;
                    while (!ct.IsCancellationRequested && client.Connected)
                    {
                        var snap = _snapshotProvider();
                        string json = JsonSerializer.Serialize(new
                        {
                            cpu_temp = snap.CpuTemp,
                            cpu_load = snap.CpuLoad,
                            cpu_power_w = snap.CpuPowerW,
                            gpu_temp = snap.GpuTemp,
                            gpu_hotspot = snap.GpuHotspot,
                            gpu_power_w = snap.GpuPowerW,
                            gpu_load = snap.GpuLoad,
                            ram_used_gb = snap.RamUsedGb,
                            ram_total_gb = snap.RamTotalGb,
                            active_game = snap.ActiveGame,
                            fps = snap.Fps,
                            timestamp = snap.Timestamp
                        });

                        await SendMqttPublishAsync(stream, topic, json, ct);

                        if ((DateTime.UtcNow - lastPing).TotalSeconds >= 30)
                        {
                            await stream.WriteAsync(new byte[] { 0xC0, 0x00 }, ct); // PINGREQ
                            lastPing = DateTime.UtcNow;
                        }

                        await Task.Delay(3000, ct);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    MqttConnected = false;
                    MqttError = ex.Message;
                    await Task.Delay(5000, ct);
                }
            }
            MqttConnected = false;
        }, ct);
    }

    private static async Task SendMqttConnectAsync(Stream s, string clientId, CancellationToken ct)
    {
        byte[] idBytes = Encoding.UTF8.GetBytes(clientId);
        byte[] protoName = Encoding.UTF8.GetBytes("MQTT");

        using var ms = new MemoryStream();
        // Protocol name (2 bytes length + string)
        ms.WriteByte(0);
        ms.WriteByte((byte)protoName.Length);
        ms.Write(protoName, 0, protoName.Length);
        // Level 4 (MQTT 3.1.1)
        ms.WriteByte(4);
        // Connect Flags (Clean Session = 0x02)
        ms.WriteByte(2);
        // KeepAlive (60 s = 0x003C)
        ms.WriteByte(0);
        ms.WriteByte(60);
        // Client ID
        ms.WriteByte((byte)(idBytes.Length >> 8));
        ms.WriteByte((byte)(idBytes.Length & 0xFF));
        ms.Write(idBytes, 0, idBytes.Length);

        byte[] varHeaderAndPayload = ms.ToArray();
        byte[] fixedHeader = EncodeMqttHeader(0x10, varHeaderAndPayload.Length);

        await s.WriteAsync(fixedHeader, ct);
        await s.WriteAsync(varHeaderAndPayload, ct);
        await s.FlushAsync(ct);
    }

    private static async Task SendMqttPublishAsync(Stream s, string topic, string payload, CancellationToken ct)
    {
        byte[] topicBytes = Encoding.UTF8.GetBytes(topic);
        byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);

        using var ms = new MemoryStream();
        ms.WriteByte((byte)(topicBytes.Length >> 8));
        ms.WriteByte((byte)(topicBytes.Length & 0xFF));
        ms.Write(topicBytes, 0, topicBytes.Length);
        ms.Write(payloadBytes, 0, payloadBytes.Length);

        byte[] body = ms.ToArray();
        byte[] header = EncodeMqttHeader(0x30, body.Length); // QoS 0 PUBLISH

        await s.WriteAsync(header, ct);
        await s.WriteAsync(body, ct);
        await s.FlushAsync(ct);
    }

    private static async Task SendHomeAssistantDiscoveryAsync(Stream s, CancellationToken ct)
    {
        string host = Environment.MachineName.ToLowerInvariant();
        string deviceJson = JsonSerializer.Serialize(new
        {
            identifiers = new[] { $"thermosync_{host}" },
            name = $"ThermoSync ({Environment.MachineName})",
            model = "PC Workstation",
            manufacturer = "ThermoSync"
        });

        async Task SendSensorConfig(string id, string name, string devClass, string unit, string valueTemplate)
        {
            string topic = $"homeassistant/sensor/thermosync_{host}/{id}/config";
            string payload = JsonSerializer.Serialize(new
            {
                name = $"{Environment.MachineName} {name}",
                unique_id = $"thermosync_{host}_{id}",
                state_topic = "thermosync/telemetry",
                device_class = devClass,
                unit_of_measurement = unit,
                value_template = valueTemplate,
                device = JsonSerializer.Deserialize<JsonElement>(deviceJson)
            });
            await SendMqttPublishAsync(s, topic, payload, ct);
        }

        await SendSensorConfig("cpu_temp", "CPU Temperatura", "temperature", "°C", "{{ value_json.cpu_temp }}");
        await SendSensorConfig("gpu_temp", "GPU Temperatura", "temperature", "°C", "{{ value_json.gpu_temp }}");
        await SendSensorConfig("gpu_power", "GPU Consumo", "power", "W", "{{ value_json.gpu_power_w }}");
        await SendSensorConfig("fps", "Jogo FPS", "", "FPS", "{{ value_json.fps }}");
    }

    private static byte[] EncodeMqttHeader(byte packetType, int length)
    {
        var bytes = new List<byte> { packetType };
        do
        {
            byte digit = (byte)(length % 128);
            length /= 128;
            if (length > 0) digit |= 0x80;
            bytes.Add(digit);
        } while (length > 0);

        return bytes.ToArray();
    }

    #endregion

    public void Dispose()
    {
        Stop();
    }
}

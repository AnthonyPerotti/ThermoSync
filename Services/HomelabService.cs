using System.Diagnostics;
using System.Runtime.InteropServices;
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
    public double? DpcLatencyUs { get; init; }
    public string? DpcStatus { get; init; }

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

            if (path.Equals("/") || path.Equals("/dash", StringComparison.OrdinalIgnoreCase) || path.Equals("/dashboard", StringComparison.OrdinalIgnoreCase))
            {
                byte[] html = Encoding.UTF8.GetBytes(GetDashboardHtml());
                res.ContentType = "text/html; charset=utf-8";
                res.ContentLength64 = html.Length;
                res.StatusCode = 200;
                res.OutputStream.Write(html, 0, html.Length);
                res.OutputStream.Close();
                return;
            }

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
                        else if (path.StartsWith("/api/power/", StringComparison.OrdinalIgnoreCase))
            {
                string subAction = path.Substring("/api/power/".Length).Trim().ToLowerInvariant();
                string actionMsg = subAction switch
                {
                    "sleep" => "Comando de suspensao (Sleep) recebido. Entrando em modo de espera.",
                    "lock" => "Sessao do Windows bloqueada.",
                    "reboot" => "Reinicializacao agendada para 5 segundos.",
                    "shutdown" => "Desligamento agendado para 5 segundos.",
                    "cancel" => "Agendamento de desligamento cancelado.",
                    _ => "Acao desconhecida. Use: sleep, lock, reboot, shutdown ou cancel."
                };

                bool isValid = subAction is "sleep" or "lock" or "reboot" or "shutdown" or "cancel";
                if (isValid)
                {
                    ExecutePowerAction(subAction);
                }

                var responseObj = new
                {
                    status = isValid ? "ok" : "error",
                    action = subAction,
                    message = actionMsg,
                    timestamp = DateTime.UtcNow
                };

                byte[] buf = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(responseObj));
                res.ContentType = "application/json; charset=utf-8";
                res.ContentLength64 = buf.Length;
                res.StatusCode = isValid ? 200 : 400;
                res.OutputStream.Write(buf, 0, buf.Length);
            }
else if (path.Equals("/api/health", StringComparison.OrdinalIgnoreCase))
            {
                var health = new
                {
                    status = "ok",
                    service = "ThermoSync",
                    version = "1.6.0",
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

    
    [DllImport("Powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();

    private static void ExecutePowerAction(string action)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(600);
            try
            {
                switch (action.ToLowerInvariant())
                {
                    case "sleep":
                        SetSuspendState(false, true, false);
                        break;
                    case "lock":
                        LockWorkStation();
                        break;
                    case "reboot":
                        Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 5 /c \"ThermoSync Homelab Webhook\"") { CreateNoWindow = true, UseShellExecute = false });
                        break;
                    case "shutdown":
                        Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 5 /c \"ThermoSync Homelab Webhook\"") { CreateNoWindow = true, UseShellExecute = false });
                        break;
                    case "cancel":
                        Process.Start(new ProcessStartInfo("shutdown.exe", "/a") { CreateNoWindow = true, UseShellExecute = false });
                        break;
                }
            }
            catch { }
        });
    }

    private static string GetDashboardHtml()
    {
        return """
<!DOCTYPE html>
<html lang="pt-BR">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>ThermoSync Homelab Dashboard</title>
    <style>
        :root {
            --bg: #090a0f;
            --card: #12151e;
            --border: #232936;
            --text: #e6edf3;
            --muted: #8b949e;
            --accent: #58a6ff;
            --green: #2ecc71;
            --yellow: #f1c40f;
            --red: #e74c3c;
        }
        * { box-sizing: border-box; margin: 0; padding: 0; }
        body {
            font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif;
            background: var(--bg);
            color: var(--text);
            padding: 24px;
            line-height: 1.5;
        }
        .header {
            display: flex;
            justify-content: space-between;
            align-items: center;
            margin-bottom: 24px;
            padding-bottom: 16px;
            border-bottom: 1px solid var(--border);
        }
        .brand {
            font-size: 20px;
            font-weight: 700;
            letter-spacing: 0.5px;
        }
        .brand span { color: var(--accent); }
        .status-pill {
            display: inline-flex;
            align-items: center;
            gap: 8px;
            padding: 6px 14px;
            border-radius: 20px;
            font-size: 13px;
            background: #102a1d;
            color: var(--green);
            border: 1px solid #1f5137;
        }
        .status-dot {
            width: 8px;
            height: 8px;
            border-radius: 50%;
            background: var(--green);
            box-shadow: 0 0 8px var(--green);
        }
        .grid {
            display: grid;
            grid-template-columns: repeat(auto-fit, minmax(280px, 1fr));
            gap: 20px;
            margin-bottom: 24px;
        }
        .card {
            background: var(--card);
            border: 1px solid var(--border);
            border-radius: 12px;
            padding: 20px;
        }
        .card-title {
            font-size: 13px;
            color: var(--muted);
            text-transform: uppercase;
            letter-spacing: 0.8px;
            margin-bottom: 12px;
            display: flex;
            justify-content: space-between;
        }
        .metric-main {
            font-size: 36px;
            font-weight: 700;
            line-height: 1;
            margin-bottom: 8px;
        }
        .metric-unit {
            font-size: 18px;
            color: var(--muted);
            font-weight: 400;
        }
        .metric-sub {
            font-size: 13px;
            color: var(--muted);
            display: flex;
            justify-content: space-between;
            border-top: 1px solid rgba(255,255,255,0.05);
            padding-top: 8px;
            margin-top: 8px;
        }
        .badge {
            padding: 2px 8px;
            border-radius: 4px;
            font-size: 11px;
            font-weight: 600;
        }
        .badge-ok { background: rgba(46, 204, 113, 0.15); color: var(--green); }
        .badge-warn { background: rgba(241, 196, 15, 0.15); color: var(--yellow); }
        .badge-bad { background: rgba(231, 76, 60, 0.15); color: var(--red); }

        .btn {
            padding: 8px 16px;
            border-radius: 6px;
            font-size: 13px;
            font-weight: 600;
            cursor: pointer;
            border: 1px solid var(--border);
            transition: all 0.2s;
        }
        .btn-warn { background: #3d2c00; color: #f1c40f; border-color: #664d03; }
        .btn-warn:hover { background: #523b00; }
        .btn-neutral { background: #1c2128; color: #adbac7; border-color: #373e47; }
        .btn-neutral:hover { background: #2d333b; }
        .btn-danger { background: #3c1618; color: #f85149; border-color: #672022; }
        .btn-danger:hover { background: #4e1c1f; }
        canvas {

            width: 100%;
            height: 50px;
            margin-top: 10px;
        }
        .footer {
            text-align: center;
            font-size: 12px;
            color: var(--muted);
            margin-top: 32px;
        }
    </style>
</head>
<body>
    <div class="header">
        <div class="brand">THERMO<span>SYNC</span> <span style="font-size:12px;color:var(--muted);font-weight:normal;">HOMELAB</span></div>
        <div class="status-pill" id="connStatus">
            <div class="status-dot"></div>
            <span id="connText">Ao Vivo (SSE)</span>
        </div>
    </div>

    <div class="grid">
        <!-- CPU Card -->
        <div class="card">
            <div class="card-title">Processador (CPU) <span id="cpuClock">-- MHz</span></div>
            <div class="metric-main"><span id="cpuTemp">--</span> <span class="metric-unit">°C</span></div>
            <div class="metric-sub">
                <span>Carga: <strong id="cpuLoad">--%</strong></span>
                <span>Consumo: <strong id="cpuPower">-- W</strong></span>
            </div>
            <canvas id="chartCpu"></canvas>
        </div>

        <!-- GPU Card -->
        <div class="card">
            <div class="card-title">Placa de Vídeo (GPU) <span id="gpuClock">--</span></div>
            <div class="metric-main"><span id="gpuTemp">--</span> <span class="metric-unit">°C</span></div>
            <div class="metric-sub">
                <span>Hotspot: <strong id="gpuHotspot">-- °C</strong></span>
                <span>Consumo: <strong id="gpuPower">-- W</strong></span>
            </div>
            <div class="metric-sub">
                <span>Carga: <strong id="gpuLoad">--%</strong></span>
                <span>VRAM: <strong id="gpuVram">-- GB</strong></span>
            </div>
            <canvas id="chartGpu"></canvas>
        </div>

        <!-- RAM & System Card -->
        <div class="card">
            <div class="card-title">Memória & Throttling</div>
            <div class="metric-main"><span id="ramUsed">--</span> <span class="metric-unit">GB</span></div>
            <div class="metric-sub">
                <span>Total: <strong id="ramTotal">-- GB</strong></span>
                <span>Uso: <strong id="ramPct">--%</strong></span>
            </div>
            <div class="metric-sub">
                <span>Status de Throttling:</span>
                <span id="throtBadge" class="badge badge-ok">Normal</span>
            </div>
            <div class="metric-sub">
                <span>Jogo em Execução:</span>
                <strong id="activeGame">Nenhum</strong>
            </div>
        </div>

        <!-- Disks Card -->
        <div class="card">
            <div class="card-title">Armazenamento</div>
            <div id="diskList" style="display:flex;flex-direction:column;gap:8px;">
                <div class="metric-sub" style="border:none;padding:0;">Aguardando telemetria de discos...</div>
            </div>
        </div>
        <!-- Power Control Card -->
        <div class="card" style="grid-column: 1 / -1;">
            <div class="card-title">Controle Remoto de Energia (Homelab Webhooks) <span id="dpcBadge" class="badge badge-ok">DPC: --</span></div>
            <div style="display: flex; gap: 10px; flex-wrap: wrap; margin-top: 8px;">
                <button class="btn btn-warn" onclick="sendPowerAction('sleep', 'Deseja colocar o computador em suspensao (Sleep)?')">Suspender (Sleep)</button>
                <button class="btn btn-neutral" onclick="sendPowerAction('lock', 'Deseja bloquear a sessao do Windows?')">Bloquear Tela</button>
                <button class="btn btn-warn" onclick="sendPowerAction('reboot', 'Deseja reiniciar o sistema em 5 segundos?')">Reiniciar</button>
                <button class="btn btn-danger" onclick="sendPowerAction('shutdown', 'Deseja desligar o computador em 5 segundos?')">Desligar</button>
                <button class="btn btn-neutral" onclick="sendPowerAction('cancel', 'Cancelar desligamento agendado?')">Cancelar Desligamento</button>
            </div>
            <div id="powerMsg" style="margin-top: 10px; font-size: 13px; color: var(--muted);">Endpoints REST disponiveis: POST /api/power/sleep, /reboot, /lock, /shutdown, /cancel</div>
        </div>
    </div>

    <div class="footer">
        ThermoSync v1.6.0 · Telemetria em tempo real transmitida via Server-Sent Events (/api/stream)
    </div>

    <script>
        const cpuHistory = [];
        const gpuHistory = [];
        const maxPoints = 30;

        function drawSparkline(canvasId, data, color) {
            const cvs = document.getElementById(canvasId);
            if (!cvs) return;
            const ctx = cvs.getContext('2d');
            const w = cvs.width = cvs.offsetWidth * window.devicePixelRatio || 300;
            const h = cvs.height = 50 * window.devicePixelRatio || 50;

            ctx.clearRect(0, 0, w, h);
            if (data.length < 2) return;

            const min = Math.min(...data) - 2;
            const max = Math.max(...data) + 2;
            const range = Math.max(1, max - min);

            ctx.beginPath();
            ctx.strokeStyle = color;
            ctx.lineWidth = 2 * (window.devicePixelRatio || 1);
            ctx.lineJoin = 'round';

            for (let i = 0; i < data.length; i++) {
                const x = (i / (maxPoints - 1)) * w;
                const y = h - ((data[i] - min) / range) * (h - 8) - 4;
                if (i === 0) ctx.moveTo(x, y);
                else ctx.lineTo(x, y);
            }
            ctx.stroke();
        }

        const evtSource = new EventSource("/api/stream");

        evtSource.onmessage = function(e) {
            try {
                const s = JSON.parse(e.data);

                // CPU
                if (s.cpuTemp != null) {
                    document.getElementById("cpuTemp").innerText = s.cpuTemp.toFixed(0);
                    cpuHistory.push(s.cpuTemp);
                    if (cpuHistory.length > maxPoints) cpuHistory.shift();
                    drawSparkline("chartCpu", cpuHistory, s.cpuTemp > 85 ? "#e74c3c" : "#58a6ff");
                }
                if (s.cpuLoad != null) document.getElementById("cpuLoad").innerText = s.cpuLoad.toFixed(0) + "%";
                if (s.cpuPowerW != null) document.getElementById("cpuPower").innerText = s.cpuPowerW.toFixed(0) + " W";
                if (s.cpuClockMhz != null) document.getElementById("cpuClock").innerText = s.cpuClockMhz.toFixed(0) + " MHz";

                // GPU
                if (s.gpuTemp != null) {
                    document.getElementById("gpuTemp").innerText = s.gpuTemp.toFixed(0);
                    gpuHistory.push(s.gpuTemp);
                    if (gpuHistory.length > maxPoints) gpuHistory.shift();
                    drawSparkline("chartGpu", gpuHistory, s.gpuTemp > 80 ? "#e74c3c" : "#2ecc71");
                }
                if (s.gpuHotspot != null) document.getElementById("gpuHotspot").innerText = s.gpuHotspot.toFixed(0) + " °C";
                if (s.gpuPowerW != null) document.getElementById("gpuPower").innerText = s.gpuPowerW.toFixed(0) + " W";
                if (s.gpuLoad != null) document.getElementById("gpuLoad").innerText = s.gpuLoad.toFixed(0) + "%";
                if (s.gpuVramUsedMb != null) document.getElementById("gpuVram").innerText = (s.gpuVramUsedMb / 1024).toFixed(1) + " GB";

                // RAM
                if (s.ramUsedGb != null) document.getElementById("ramUsed").innerText = s.ramUsedGb.toFixed(1);
                if (s.ramTotalGb != null) {
                    document.getElementById("ramTotal").innerText = s.ramTotalGb.toFixed(1) + " GB";
                    if (s.ramUsedGb != null) {
                        const pct = (s.ramUsedGb / s.ramTotalGb) * 100;
                        document.getElementById("ramPct").innerText = pct.toFixed(0) + "%";
                    }
                }

                // Throttling
                const throtBadge = document.getElementById("throtBadge");
                if (s.throttling && (s.throttling.isThrottling || s.throttling.shortDescription !== "Normal")) {
                    throtBadge.innerText = s.throttling.shortDescription || "Throttling Ativo";
                    throtBadge.className = "badge badge-bad";
                } else {
                    throtBadge.innerText = "Normal";
                    throtBadge.className = "badge badge-ok";
                }

                // Active Game
                const gameEl = document.getElementById("activeGame");
                if (s.activeGame && s.activeGame.length > 0) {
                    const fps = s.fps ? ` (${s.fps.toFixed(0)} FPS)` : "";
                    gameEl.innerText = s.activeGame + fps;
                    gameEl.style.color = "var(--green)";
                } else {
                    gameEl.innerText = "Nenhum";
                    gameEl.style.color = "var(--muted)";
                }

                // Disks
                if (s.disks && s.disks.length > 0) {
                    const list = document.getElementById("diskList");
                    list.innerHTML = "";
                    s.disks.forEach(d => {
                        const row = document.createElement("div");
                        row.className = "metric-sub";
                        const temp = d.temperatureCelsius ? `${d.temperatureCelsius.toFixed(0)} °C` : "--";
                        const health = d.healthPercentage != null ? `Saúde: ${d.healthPercentage.toFixed(0)}%` : "";
                        row.innerHTML = `<span>${d.name}</span><strong>${temp} · ${health}</strong>`;
                        list.appendChild(row);
                    });
                }
            } catch (err) { }
        };

        evtSource.onerror = function() {
            document.getElementById("connStatus").style.background = "#2a1215";
            document.getElementById("connStatus").style.color = "var(--red)";
            document.getElementById("connStatus").style.borderColor = "#511f24";
            document.querySelector(".status-dot").style.background = "var(--red)";
            document.querySelector(".status-dot").style.boxShadow = "0 0 8px var(--red)";
            document.getElementById("connText").innerText = "Desconectado (Reconectando...)";
        };

        evtSource.onopen = function() {
            document.getElementById("connStatus").style.background = "#102a1d";
            document.getElementById("connStatus").style.color = "var(--green)";
            document.getElementById("connStatus").style.borderColor = "#1f5137";
            document.querySelector(".status-dot").style.background = "var(--green)";
            document.querySelector(".status-dot").style.boxShadow = "0 0 8px var(--green)";
            document.getElementById("connText").innerText = "Ao Vivo (SSE)";
        };
    
        function sendPowerAction(action, confirmMsg) {
            if (confirmMsg && !confirm(confirmMsg)) return;
            const msgEl = document.getElementById('powerMsg');
            msgEl.textContent = 'Enviando comando para ThermoSync...';
            fetch('/api/power/' + action, { method: 'POST' })
                .then(r => r.json())
                .then(data => {
                    msgEl.textContent = '✓ ' + (data.message || ('Comando executado: ' + data.action));
                })
                .catch(err => {
                    msgEl.textContent = '✖ Falha ao comunicar com a API: ' + err;
                });
        }

    </script>
</body>
</html>
""";
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

        if (s.DpcLatencyUs is double dpc)
        {
            AppendGauge("thermosync_dpc_latency_microseconds", "Pico de latencia DPC do kernel Windows em microssegundos", dpc);
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

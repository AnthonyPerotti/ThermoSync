using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace ThermoSync;

/// <summary>
/// Diagnóstico de rede para lag: latência, jitter e perda de pacotes até o roteador e até a internet,
/// resolução de DNS, velocidade de download/upload e latência sob carga (bufferbloat).
/// Os testes de velocidade usam o servidor público speed.cloudflare.com. Não grava nada no disco.
/// </summary>
public static class NetworkTest
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private sealed record PingStat(string Name, string Address, int Sent, int Lost, double Min, double Avg, double Max, double Jitter)
    {
        public double LossPct => Sent == 0 ? 0 : 100.0 * Lost / Sent;
        public bool Answered => Sent - Lost > 0;
        public string Text => !Answered ? "não responde"
            : $"média {Avg:0.#} ms · mín {Min:0.#} · máx {Max:0.#} · jitter {Jitter:0.#} ms · perda {LossPct:0.#}%";
    }

    private static PingStat Stat(string name, string address, List<double> rtts, int sent)
    {
        if (rtts.Count == 0) return new PingStat(name, address, sent, sent, 0, 0, 0, 0);
        double jit = 0;
        for (int i = 1; i < rtts.Count; i++) jit += Math.Abs(rtts[i] - rtts[i - 1]);
        jit = rtts.Count > 1 ? jit / (rtts.Count - 1) : 0;
        return new PingStat(name, address, sent, sent - rtts.Count, rtts.Min(), rtts.Average(), rtts.Max(), jit);
    }

    private static async Task<(List<double> Rtts, int Sent)> PingLoop(IPAddress ip, int count, int intervalMs, CancellationToken ct)
    {
        var rtts = new List<double>();
        int sent = 0;
        using var p = new Ping();
        for (int i = 0; (count <= 0 || i < count) && !ct.IsCancellationRequested; i++)
        {
            var sw = Stopwatch.StartNew();
            sent++;
            try
            {
                var r = await p.SendPingAsync(ip, 1500);
                if (r.Status == IPStatus.Success) rtts.Add(Math.Max(1, r.RoundtripTime));
            }
            catch (PingException) { }
            catch (InvalidOperationException) { }
            int wait = intervalMs - (int)sw.ElapsedMilliseconds;
            if (wait > 0) { try { await Task.Delay(wait, ct); } catch (OperationCanceledException) { break; } }
        }
        return (rtts, sent);
    }

    private static async Task<double?> Download(int seconds, CancellationToken ct)
    {
        long total = 0;
        var sw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(seconds * 1000);
        var tasks = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            try
            {
                var buf = new byte[81920];
                while (!cts.IsCancellationRequested)
                {
                    using var resp = await Http.GetAsync("https://speed.cloudflare.com/__down?bytes=25000000", HttpCompletionOption.ResponseHeadersRead, cts.Token);
                    resp.EnsureSuccessStatusCode();
                    using var st = await resp.Content.ReadAsStreamAsync(cts.Token);
                    int n;
                    while ((n = await st.ReadAsync(buf, cts.Token)) > 0) Interlocked.Add(ref total, n);
                }
            }
            catch (OperationCanceledException) { }
            catch (HttpRequestException) { }
        })).ToArray();
        await Task.WhenAll(tasks);
        ct.ThrowIfCancellationRequested();
        double secs = sw.Elapsed.TotalSeconds;
        return total > 0 && secs > 0 ? total * 8 / 1e6 / secs : null;
    }

    private static async Task<double?> Upload(int seconds, CancellationToken ct)
    {
        long total = 0;
        var sw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(seconds * 1000);
        var payload = new byte[1_000_000];
        new Random(1).NextBytes(payload);
        var tasks = Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    using var resp = await Http.PostAsync("https://speed.cloudflare.com/__up", new ByteArrayContent(payload), cts.Token);
                    resp.EnsureSuccessStatusCode();
                    Interlocked.Add(ref total, payload.Length);
                }
            }
            catch (OperationCanceledException) { }
            catch (HttpRequestException) { }
        })).ToArray();
        await Task.WhenAll(tasks);
        ct.ThrowIfCancellationRequested();
        double secs = sw.Elapsed.TotalSeconds;
        return total > 0 && secs > 0 ? total * 8 / 1e6 / secs : null;
    }

    private static string AdapterType(NetworkInterfaceType t) => t switch
    {
        NetworkInterfaceType.Wireless80211 => "Wi-Fi",
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT => "Ethernet (cabo)",
        _ => t.ToString()
    };

    public static async Task<List<DiskHealthCard>> Run(Action<string, double> progress, CancellationToken ct)
    {
        progress("Procurando a conexão ativa…", 0.02);
        NetworkInterface? nic = null;
        try
        {
            nic = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                            && n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                                                                              && !g.Address.Equals(IPAddress.Any)))
                .OrderByDescending(n => n.Speed).FirstOrDefault();
        }
        catch { }
        if (nic == null)
            return new() { new DiskHealthCard
            {
                Title = "Sem conexão", Meta = "Nenhum adaptador conectado com gateway", Verdict = DiskVerdict.Critical, Label = "SEM REDE",
                Reasons = "O Windows não encontrou uma conexão ativa com acesso ao roteador. Verifique o cabo, o Wi-Fi ou o adaptador.",
            } };

        var props = nic.GetIPProperties();
        var gw = props.GatewayAddresses.First(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)).Address;
        string ip = props.UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString() ?? "—";
        string dns = props.DnsAddresses.FirstOrDefault(d => d.AddressFamily == AddressFamily.InterNetwork)?.ToString() ?? "—";
        string link = nic.Speed > 0 ? $"{nic.Speed / 1_000_000.0:0} Mbps" : "—";

        // 1) Latência / jitter / perda: roteador e dois DNS públicos, em paralelo (30 pacotes cada)
        progress("Medindo latência e perda de pacotes (≈ 7 s)…", 0.08);
        var targets = new (string Name, IPAddress Addr)[]
        {
            ("Roteador", gw), ("Internet · Cloudflare", IPAddress.Parse("1.1.1.1")), ("Internet · Google", IPAddress.Parse("8.8.8.8")),
        };
        var pings = await Task.WhenAll(targets.Select(async t =>
        {
            var (rtts, sent) = await PingLoop(t.Addr, 30, 200, ct);
            return Stat(t.Name, t.Addr.ToString(), rtts, sent);
        }));
        ct.ThrowIfCancellationRequested();

        // 2) DNS
        progress("Testando o DNS…", 0.30);
        double? dnsMs = null;
        try
        {
            var sw = Stopwatch.StartNew();
            await Dns.GetHostAddressesAsync("www.cloudflare.com", ct);
            dnsMs = sw.Elapsed.TotalMilliseconds;
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        // 3) Download com ping em paralelo (latência sob carga)
        progress("Medindo a velocidade de download (≈ 8 s)…", 0.36);
        using var loadStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var loadedPing = PingLoop(IPAddress.Parse("1.1.1.1"), 0, 200, loadStop.Token);
        double? down = null; string? netErr = null;
        try { down = await Download(8, ct); }
        catch (OperationCanceledException) { loadStop.Cancel(); throw; }
        catch (Exception ex) { netErr = ex.Message; }
        loadStop.Cancel();
        var (lrtts, lsent) = await loadedPing;
        double? loadedMs = lrtts.Count >= 5 ? lrtts.Average() : null;

        // 4) Upload
        progress("Medindo a velocidade de upload (≈ 6 s)…", 0.66);
        double? up = null;
        try { up = await Upload(6, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { netErr ??= ex.Message; }

        progress("Analisando…", 0.95);

        // ---- Veredito ----
        var gwS = pings[0];
        var net = pings.Skip(1).Where(p => p.Answered).ToList();
        double netAvg = net.Count > 0 ? net.Average(p => p.Avg) : 0;
        double netLoss = pings.Skip(1).Average(p => p.LossPct);
        double netJit = net.Count > 0 ? net.Max(p => p.Jitter) : 0;
        var warn = new List<string>();
        bool bad = false;

        bool lanBad = gwS.Answered && (gwS.LossPct > 2 || gwS.Avg > 30 || gwS.Jitter > 15);
        if (lanBad) warn.Add($"A rede local está instável (roteador: {gwS.Avg:0} ms, perda {gwS.LossPct:0.#}%, jitter {gwS.Jitter:0} ms). Se for Wi-Fi, aproxime-se do roteador ou use cabo.");
        if (!gwS.Answered) warn.Add("O roteador não responde a ping (alguns modelos bloqueiam). Isso por si só não é problema.");
        if (net.Count == 0) { bad = true; warn.Add("Nenhum dos servidores da internet respondeu: sem acesso à internet ou ping bloqueado pela rede."); }
        else
        {
            if (netLoss > 5 || netAvg > 150) bad = true;
            if (netLoss > 1) warn.Add($"Há perda de pacotes até a internet ({netLoss:0.#}%). {(lanBad ? "Provavelmente vem da rede local." : "Como o roteador está bem, o problema deve estar na linha ou no provedor.")}");
            if (netAvg > 80) warn.Add($"Latência alta até a internet ({netAvg:0} ms).{(lanBad ? "" : " O roteador responde bem, então o atraso vem do provedor.")}");
            if (netJit > 20) warn.Add($"O jitter está alto ({netJit:0} ms): a latência oscila, o que causa engasgos em jogos online.");
        }
        if (loadedMs != null && net.Count > 0 && loadedMs - net.Min(p => p.Avg) > 100)
            warn.Add($"A latência sobe para {loadedMs:0} ms quando a conexão está cheia (bufferbloat). Ative SQM/QoS no roteador ou limite a velocidade a ~90%.");
        if (dnsMs > 150) warn.Add($"O DNS demorou {dnsMs:0} ms. Trocar para 1.1.1.1 ou 8.8.8.8 pode acelerar o carregamento de sites.");
        if (down is < 10) warn.Add($"Download baixo ({down:0.#} Mbps) para jogos, streaming e atualizações.");
        if (netErr != null && down == null) warn.Add("Não foi possível medir a velocidade (speed.cloudflare.com inacessível ou bloqueado).");

        var verdict = bad ? DiskVerdict.Critical : warn.Any(w => !w.StartsWith("O roteador não responde")) ? DiskVerdict.Warning : DiskVerdict.Good;
        string label = verdict switch { DiskVerdict.Critical => "RUIM", DiskVerdict.Warning => "ATENÇÃO", _ => "ESTÁVEL" };
        string reasons = warn.Count > 0 ? string.Join(" ", warn)
            : "Latência baixa, sem perda de pacotes e sem sinais de bufferbloat. Se o jogo ainda engasga, o problema provavelmente não é a conexão: veja o Gargalo e as temperaturas.";

        var m = new List<KeyValuePair<string, string>>();
        foreach (var p in pings) m.Add(new($"{p.Name} ({p.Address})", p.Text));
        if (dnsMs != null) m.Add(new("Resolução de DNS", $"{dnsMs:0} ms"));
        m.Add(new("Download", down != null ? $"{down:0.#} Mbps" : "—"));
        m.Add(new("Upload", up != null ? $"{up:0.#} Mbps" : "—"));
        if (loadedMs != null) m.Add(new("Latência sob carga", $"{loadedMs:0} ms (durante o download)"));
        m.Add(new("Adaptador", $"{nic.Name} · {AdapterType(nic.NetworkInterfaceType)} · link {link}"));
        m.Add(new("IP / Gateway / DNS", $"{ip} / {gw} / {dns}"));

        progress("Concluído", 1);
        return new() { new DiskHealthCard
        {
            Title = "Diagnóstico de rede",
            Meta = $"{AdapterType(nic.NetworkInterfaceType)} · testado às {DateTime.Now:HH:mm:ss}",
            Verdict = verdict, Label = label, Reasons = reasons, Metrics = m,
        } };
    }

    /// <summary>
    /// Diagnóstico dedicado de infraestrutura Homelab/ZimaOS:
    /// 1. Latência e Jitter ponto a ponto (10 amostras ICMP)
    /// 2. Detecção de MTU Path (1500 vs 9000 Jumbo Frames)
    /// 3. Throughput de largura de banda LAN (suporta cliente iperf3 nativo ou stream TCP direto)
    /// </summary>
    public static async Task<List<DiskHealthCard>> RunHomelabNetTest(string host, int port, Action<string, double> progress, CancellationToken ct)
    {
        progress($"Resolvendo endereço de '{host}'…", 0.05);

        IPAddress targetIp;
        try
        {
            if (IPAddress.TryParse(host.Trim(), out var parsedIp))
                targetIp = parsedIp;
            else
            {
                var entry = await Dns.GetHostEntryAsync(host.Trim(), ct);
                targetIp = entry.AddressList.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                           ?? entry.AddressList.First();
            }
        }
        catch (Exception ex)
        {
            return new() { new DiskHealthCard
            {
                Title = $"Homelab: {host}",
                Meta = "Erro de resolução de nome",
                Verdict = DiskVerdict.Critical,
                Label = "INACESSÍVEL",
                Reasons = $"Não foi possível resolver o IP de '{host}': {ex.Message}. Verifique se o hostname está correto ou use o IP direto (ex.: 192.168.1.x).",
                Metrics = new() { new("Host de destino", host) }
            } };
        }

        // 1. Latência e Jitter
        progress($"Enviando pings para {targetIp}…", 0.15);
        var (rtts, sent) = await PingLoop(targetIp, 10, 100, ct);
        var pingStat = Stat(host, targetIp.ToString(), rtts, sent);

        // 2. MTU Detection
        progress("Testando MTU da rede local (Jumbo Frames / 1500 bytes)…", 0.35);
        int detectedMtu = 1500;
        using (var pinger = new Ping())
        {
            var options = new PingOptions { DontFragment = true };
            try
            {
                // Test 9000 MTU (payload 8972)
                var r9000 = await pinger.SendPingAsync(targetIp, 500, new byte[8972], options);
                if (r9000.Status == IPStatus.Success)
                    detectedMtu = 9000;
                else
                {
                    // Test standard 1500 MTU (payload 1472)
                    var r1500 = await pinger.SendPingAsync(targetIp, 500, new byte[1472], options);
                    if (r1500.Status == IPStatus.Success)
                        detectedMtu = 1500;
                    else
                        detectedMtu = 1492; // Common PPPoE or tunnel MTU
                }
            }
            catch { }
        }

        // 3. Throughput Test (iperf3 or TCP Stream)
        progress($"Medindo throughput com {targetIp}:{port}…", 0.50);
        double? throughputMbps = null;
        string protoUsed = "TCP";
        string? transferError = null;

        try
        {
            var throughputResult = await MeasureTcpThroughputAsync(targetIp, port, 5, p =>
            {
                progress($"Medindo throughput com {targetIp}:{port} ({p:0.0} s)…", 0.50 + (p / 5.0) * 0.40);
            }, ct);

            throughputMbps = throughputResult.Mbps;
            protoUsed = throughputResult.Protocol;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            transferError = ex.Message;
        }

        progress("Gerando diagnóstico…", 0.95);

        // Verdict analysis
        var warnings = new List<string>();
        bool critical = !pingStat.Answered;

        if (!pingStat.Answered)
            warnings.Add($"O servidor Homelab em {targetIp} não responde a ping ICMP. Verifique o cabo de rede, firewall ou IP.");
        else
        {
            if (pingStat.LossPct > 0)
                warnings.Add($"Detectada perda de pacotes de {pingStat.LossPct:0.#}% na rede local. Verifique cabos Ethernet, switch ou sinal Wi-Fi.");
            if (pingStat.Avg > 5.0)
                warnings.Add($"A latência média para o Homelab está em {pingStat.Avg:0.#} ms (normal em cabo gigabit é < 1 ms).");
            if (pingStat.Jitter > 2.0)
                warnings.Add($"Jitter de {pingStat.Jitter:0.#} ms na LAN indica variação de atraso ou tráfego concorrente intenso no switch/roteador.");
        }

        if (detectedMtu == 9000)
            warnings.Add("Jumbo Frames (MTU 9000) habilitados e operando com sucesso!");

        if (throughputMbps != null)
        {
            if (throughputMbps < 80)
                warnings.Add($"Velocidade de {throughputMbps:0.#} Mbps indica link negociado em 100 Mbps (Fast Ethernet) ou sinal Wi-Fi degradado. Verifique os pares do cabo de rede.");
            else if (throughputMbps >= 850 && throughputMbps <= 1000)
                warnings.Add($"Velocidade de {throughputMbps:0.#} Mbps satura perfeitamente um link cabeado padrão de 1 Gbps.");
            else if (throughputMbps > 1000)
                warnings.Add($"Desempenho de {throughputMbps / 1000.0:0.##} Gbps confirma operação em Multi-Gigabit (2.5 Gbps ou 10 Gbps).");
        }
        else if (transferError != null)
        {
            warnings.Add($"Não foi possível conectar na porta {port}: {transferError}. Se estiver usando iperf3 no ZimaOS/Homelab, inicie o container com 'iperf3 -s'.");
        }

        var verdict = critical ? DiskVerdict.Critical : warnings.Any(w => w.Contains("perda") || w.Contains("100 Mbps")) ? DiskVerdict.Warning : DiskVerdict.Good;
        string label = verdict switch
        {
            DiskVerdict.Critical => "INACESSÍVEL",
            DiskVerdict.Warning => "ATENÇÃO",
            _ => "EXCELENTE"
        };

        var metrics = new List<KeyValuePair<string, string>>
        {
            new("Servidor Alvo", $"{host} ({targetIp}:{port})"),
            new("Latência Ponto a Ponto", pingStat.Answered ? $"{pingStat.Avg:0.##} ms (mín {pingStat.Min:0.#} / máx {pingStat.Max:0.#})" : "Sem resposta"),
            new("Jitter Inter-Pacote", pingStat.Answered ? $"{pingStat.Jitter:0.##} ms" : "—"),
            new("Perda de Pacotes", pingStat.Answered ? $"{pingStat.LossPct:0.#}%" : "100%"),
            new("MTU Detectado", $"{detectedMtu} bytes {(detectedMtu == 9000 ? "(Jumbo Frames)" : "(Padrão Ethernet)")}"),
            new("Throughput LAN", throughputMbps != null ? $"{throughputMbps:0.#} Mbps (≈ {throughputMbps / 1000.0:0.##} Gbps) via {protoUsed}" : $"Falha na porta {port}")
        };

        string summary = warnings.Count > 0
            ? string.Join(" ", warnings)
            : "Conexão com o Homelab estável, com latência sub-milissegundo, zero perda de pacotes e excelente taxa de transferência.";

        progress("Concluído", 1.0);
        return new() { new DiskHealthCard
        {
            Title = $"Homelab: {host}",
            Meta = $"Porta {port} · Testado às {DateTime.Now:HH:mm:ss}",
            Verdict = verdict,
            Label = label,
            Reasons = summary,
            Metrics = metrics
        } };
    }

    private static async Task<(double? Mbps, string Protocol)> MeasureTcpThroughputAsync(IPAddress ip, int port, int durationSec, Action<double> onProgress, CancellationToken ct)
    {
        using var client = new TcpClient();
        client.NoDelay = true;
        client.ReceiveBufferSize = 256 * 1024;
        client.SendBufferSize = 256 * 1024;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(durationSec + 5));

        await client.ConnectAsync(ip, port, cts.Token);
        using var stream = client.GetStream();

        // 1. Check if port is iperf3 (port 5201)
        if (port == 5201)
        {
            try
            {
                var iperfResult = await RunIperf3ClientStreamAsync(stream, durationSec, onProgress, cts.Token);
                if (iperfResult.HasValue) return (iperfResult.Value, "iperf3 TCP");
            }
            catch { }
        }

        // 2. Generic high-throughput TCP socket stream
        var buffer = new byte[128 * 1024];
        new Random(42).NextBytes(buffer);

        long totalBytes = 0;
        var sw = Stopwatch.StartNew();
        var endAt = sw.Elapsed.TotalSeconds + durationSec;

        while (sw.Elapsed.TotalSeconds < endAt && !cts.IsCancellationRequested)
        {
            await stream.WriteAsync(buffer.AsMemory(0, buffer.Length), cts.Token);
            totalBytes += buffer.Length;
            onProgress(Math.Min(durationSec, sw.Elapsed.TotalSeconds));
        }

        double elapsed = sw.Elapsed.TotalSeconds;
        if (elapsed > 0 && totalBytes > 0)
        {
            double mbps = (totalBytes * 8.0) / (1e6 * elapsed);
            return (mbps, "TCP Socket Stream");
        }

        return (null, "TCP");
    }

    private static async Task<double?> RunIperf3ClientStreamAsync(NetworkStream stream, int durationSec, Action<double> onProgress, CancellationToken ct)
    {
        // iperf3 protocol handshake:
        // 1. Send 37-byte cookie (36 random chars + null)
        var cookie = Encoding.ASCII.GetBytes(Guid.NewGuid().ToString("N") + "\0");
        await stream.WriteAsync(cookie, ct);

        // 2. Read 1 byte server state (expect PARAM_EXCHANGE = 9 or TEST_START = 1)
        var stateBuf = new byte[1];
        int r = await stream.ReadAsync(stateBuf.AsMemory(0, 1), ct);
        if (r == 0) return null;

        // 3. Send test parameters JSON prefixed by 4-byte big-endian length
        string paramJson = $"{{\"tcp\":true,\"omit\":0,\"time\":{durationSec},\"parallel\":1}}";
        var jsonBytes = Encoding.UTF8.GetBytes(paramJson);
        var lenHeader = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(jsonBytes.Length));
        await stream.WriteAsync(lenHeader, ct);
        await stream.WriteAsync(jsonBytes, ct);

        // 4. Stream high-speed payload
        var payload = new byte[128 * 1024];
        long totalSent = 0;
        var sw = Stopwatch.StartNew();
        var endSec = sw.Elapsed.TotalSeconds + durationSec;

        while (sw.Elapsed.TotalSeconds < endSec && !ct.IsCancellationRequested)
        {
            await stream.WriteAsync(payload, ct);
            totalSent += payload.Length;
            onProgress(Math.Min(durationSec, sw.Elapsed.TotalSeconds));
        }

        double elapsed = sw.Elapsed.TotalSeconds;
        return elapsed > 0 && totalSent > 0 ? (totalSent * 8.0) / (1e6 * elapsed) : null;
    }
}


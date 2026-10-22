using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace ThermoSync;

public sealed record AlertNotification(
    string Category,
    string Severity,
    string Hardware,
    string Metric,
    double CurrentValue,
    double Threshold,
    string Unit,
    string? ActiveProcess = null
);

/// <summary>
/// Dispatches critical telemetry alerts to remote webhooks (Gotify, Discord, Telegram, Generic HTTP).
/// Includes cooldown tracking to prevent notification flooding.
/// </summary>
public sealed class AlertWebhookService : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly Dictionary<string, DateTime> _lastAlertSent = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public AppSettings Settings { get; set; }

    public AlertWebhookService(AppSettings settings)
    {
        Settings = settings;
    }

    /// <summary>
    /// Checks whether an alert should be sent based on configured cooldown.
    /// </summary>
    public bool ShouldAlert(string key, int cooldownMinutes = 5)
    {
        lock (_lock)
        {
            if (_lastAlertSent.TryGetValue(key, out var last))
            {
                if (DateTime.UtcNow - last < TimeSpan.FromMinutes(cooldownMinutes))
                    return false;
            }
            _lastAlertSent[key] = DateTime.UtcNow;
            return true;
        }
    }

    /// <summary>
    /// Sends an alert notification to all configured platforms.
    /// </summary>
    public async Task<(bool Gotify, bool Discord, bool Telegram)> DispatchAlertAsync(AlertNotification alert, CancellationToken ct = default)
    {
        bool gotifyOk = false;
        bool discordOk = false;
        bool telegramOk = false;

        if (Settings.AlertGotifyEnabled && !string.IsNullOrWhiteSpace(Settings.AlertGotifyUrl) && !string.IsNullOrWhiteSpace(Settings.AlertGotifyToken))
        {
            gotifyOk = await SendGotifyAsync(Settings.AlertGotifyUrl, Settings.AlertGotifyToken, alert, ct);
        }

        if (Settings.AlertDiscordEnabled && !string.IsNullOrWhiteSpace(Settings.AlertDiscordWebhookUrl))
        {
            discordOk = await SendDiscordAsync(Settings.AlertDiscordWebhookUrl, alert, ct);
        }

        if (Settings.AlertTelegramEnabled && !string.IsNullOrWhiteSpace(Settings.AlertTelegramBotToken) && !string.IsNullOrWhiteSpace(Settings.AlertTelegramChatId))
        {
            telegramOk = await SendTelegramAsync(Settings.AlertTelegramBotToken, Settings.AlertTelegramChatId, alert, ct);
        }

        return (gotifyOk, discordOk, telegramOk);
    }

    public async Task<bool> SendGotifyAsync(string url, string token, AlertNotification alert, CancellationToken ct = default)
    {
        try
        {
            string baseUrl = url.TrimEnd('/');
            string endpoint = $"{baseUrl}/message?token={Uri.EscapeDataString(token.Trim())}";

            string priorityStr = alert.Severity == "CRITICAL" ? "8" : "5";
            int priority = int.Parse(priorityStr);

            string processInfo = string.IsNullOrWhiteSpace(alert.ActiveProcess) ? "" : $"\nProcesso ativo: {alert.ActiveProcess}";
            string bodyText = $"Sensor: {alert.Hardware} - {alert.Metric}\nValor atual: {alert.CurrentValue:0.#} {alert.Unit} (Limite: {alert.Threshold:0.#} {alert.Unit}){processInfo}";

            var payload = new
            {
                title = $"ThermoSync: [{alert.Severity}] {alert.Metric} em {alert.Hardware}",
                message = bodyText,
                priority = priority,
                extras = new
                {
                    client_display = new
                    {
                        contentType = "text/plain"
                    }
                }
            };

            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync(endpoint, content, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AlertWebhookService] Gotify error: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> SendDiscordAsync(string webhookUrl, AlertNotification alert, CancellationToken ct = default)
    {
        try
        {
            int color = alert.Severity == "CRITICAL" ? 0xE74C3C : 0xE67E22; // Red or Orange
            var fields = new List<object>
            {
                new { name = "Hardware", value = alert.Hardware, @inline = true },
                new { name = "Métrica", value = $"{alert.Metric}: {alert.CurrentValue:0.#} {alert.Unit}", @inline = true },
                new { name = "Limite Seguro", value = $"{alert.Threshold:0.#} {alert.Unit}", @inline = true }
            };

            if (!string.IsNullOrWhiteSpace(alert.ActiveProcess))
            {
                fields.Add(new { name = "Processo em Primeiro Plano", value = alert.ActiveProcess, @inline = false });
            }

            var payload = new
            {
                username = "ThermoSync Monitor",
                embeds = new[]
                {
                    new
                    {
                        title = $"🚨 Alerta de Hardware: {alert.Metric} Crítico",
                        description = $"O componente **{alert.Hardware}** excedeu o patamar seguro de operação.",
                        color = color,
                        fields = fields,
                        footer = new { text = $"ThermoSync Homelab · {DateTime.Now:dd/MM/yyyy HH:mm:ss}" },
                        timestamp = DateTime.UtcNow.ToString("o")
                    }
                }
            };

            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync(webhookUrl.Trim(), content, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AlertWebhookService] Discord error: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> SendTelegramAsync(string botToken, string chatId, AlertNotification alert, CancellationToken ct = default)
    {
        try
        {
            string endpoint = $"https://api.telegram.org/bot{botToken.Trim()}/sendMessage";
            string processLine = string.IsNullOrWhiteSpace(alert.ActiveProcess) ? "" : $"\n*Processo:* `{alert.ActiveProcess}`";

            string msg = $"🚨 *ThermoSync Alerta [{alert.Severity}]*\n\n" +
                         $"*Componente:* {alert.Hardware}\n" +
                         $"*Métrica:* {alert.Metric}\n" +
                         $"*Valor Atual:* `{alert.CurrentValue:0.#} {alert.Unit}` (Limite: `{alert.Threshold:0.#} {alert.Unit}`)" +
                         processLine + $"\n*Horário:* {DateTime.Now:HH:mm:ss}";

            var payload = new
            {
                chat_id = chatId.Trim(),
                text = msg,
                parse_mode = "Markdown"
            };

            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync(endpoint, content, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AlertWebhookService] Telegram error: {ex.Message}");
            return false;
        }
    }

    public async Task<(bool Success, string Message)> TestWebhookAsync(string platform, CancellationToken ct = default)
    {
        var testAlert = new AlertNotification(
            Category: "TEST",
            Severity: "INFO",
            Hardware: "ThermoSync System Monitor",
            Metric: "Teste de Webhook",
            CurrentValue: 42.0,
            Threshold: 80.0,
            Unit: "°C",
            ActiveProcess: "ThermoSync.exe"
        );

        switch (platform.ToLowerInvariant())
        {
            case "gotify":
                if (string.IsNullOrWhiteSpace(Settings.AlertGotifyUrl) || string.IsNullOrWhiteSpace(Settings.AlertGotifyToken))
                    return (false, "URL ou Token do Gotify não foram configurados.");
                bool gOk = await SendGotifyAsync(Settings.AlertGotifyUrl, Settings.AlertGotifyToken, testAlert, ct);
                return (gOk, gOk ? "Notificação enviada com sucesso para o Gotify!" : "Falha ao enviar para o Gotify. Verifique a URL e o Token.");

            case "discord":
                if (string.IsNullOrWhiteSpace(Settings.AlertDiscordWebhookUrl))
                    return (false, "URL do Webhook do Discord não configurada.");
                bool dOk = await SendDiscordAsync(Settings.AlertDiscordWebhookUrl, testAlert, ct);
                return (dOk, dOk ? "Mensagem de teste enviada com sucesso ao Discord!" : "Falha ao enviar ao Discord. Verifique a URL do webhook.");

            case "telegram":
                if (string.IsNullOrWhiteSpace(Settings.AlertTelegramBotToken) || string.IsNullOrWhiteSpace(Settings.AlertTelegramChatId))
                    return (false, "Bot Token ou Chat ID do Telegram não configurados.");
                bool tOk = await SendTelegramAsync(Settings.AlertTelegramBotToken, Settings.AlertTelegramChatId, testAlert, ct);
                return (tOk, tOk ? "Mensagem de teste enviada com sucesso ao Telegram!" : "Falha ao enviar ao Telegram. Verifique Token e Chat ID.");

            default:
                return (false, $"Plataforma desconhecida: {platform}");
        }
    }

    public void Dispose()
    {
    }
}

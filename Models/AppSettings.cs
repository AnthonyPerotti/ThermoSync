using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ThermoSync;

public sealed record BenchRun(
    DateTime Date,
    double? Multi,
    double? Single,
    bool Sustained,
    string Cpu,
    double? PtsPerWatt = null,
    double? AvgPowerW = null,
    double? PeakTempC = null,
    double? AvgClockMhz = null
);

/// <summary>Resultado dos benchmarks de jogo (Test = "gpu", "sim" ou "mem"). Score é usado para a barra do histórico.</summary>
public sealed record BenchRecord(DateTime Date, string Test, double Score, string Summary);

/// <summary>Programa extra que abre antes do jogo (ex.: ferramenta de comando/monitoramento) e pode fechar junto com ele.</summary>
public sealed class HelperProgram
{
    public string Path { get; set; } = "";
    public string Args { get; set; } = "";
    public bool CloseWithGame { get; set; } = true;
    public string Display => System.IO.Path.GetFileName(Path) + (string.IsNullOrWhiteSpace(Args) ? "" : " " + Args);
}

/// <summary>Perfil de um jogo na aba SYNC GAMES: desempenho, argumentos de gráficos e programas auxiliares.</summary>
public sealed class GameProfile
{
    public string Priority { get; set; } = "normal";   // normal | above | high
    public bool HighPerfPlan { get; set; }
    public bool GpuHigh { get; set; }
    public bool DisableFso { get; set; }
    public string? ExePath { get; set; }
    public string WinMode { get; set; } = "default";   // default | full | borderless | window
    public string Res { get; set; } = "default";       // default | 1280x720 | 1920x1080 | 2560x1440 | 3840x2160
    public int Hz { get; set; }                        // 0 = não definir
    public string Style { get; set; } = "source";      // source | unity | unreal
    public string Extra { get; set; } = "";
    public string FanProfile { get; set; } = "default"; // default | Quiet | Balanced | Performance | Extreme
    public List<HelperProgram> Helpers { get; set; } = new();
}

/// <summary>Jogo adicionado à mão (qualquer .exe).</summary>
public sealed class CustomGame
{
    public string Name { get; set; } = "";
    public string Exe { get; set; } = "";
    public string Args { get; set; } = "";
}

/// <summary>Configurações em %AppData%\ThermoSync. A chave da API é cifrada com DPAPI (só este usuário do Windows a lê).</summary>
public sealed class AppSettings
{
    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ThermoSync");
    private static string FilePath => Path.Combine(Folder, "settings.json");

    public string? HowManyFpsKeyProtected { get; set; }

    // SYNC BENCH: últimos resultados (mais recente primeiro)
    public List<BenchRun> BenchRuns { get; set; } = new();
    public List<BenchRecord> BenchRecords { get; set; } = new();

    // SYNC OPTIMIZE: valores originais dos ajustes do Windows antes da primeira alteração ("<ausente>" = não existia)
    public Dictionary<string, string?> TweakBackup { get; set; } = new();
    /// <summary>ComDB original (reservas de números COM) antes da primeira "liberação".</summary>
    public byte[]? ComDbBackup { get; set; }
    /// <summary>Programas de inicialização removidos, para "Restaurar removidos".</summary>
    public List<StartupBackup> StartupRemoved { get; set; } = new();

    // SYNC GAMES
    public Dictionary<string, GameProfile> GameProfiles { get; set; } = new();
    public List<CustomGame> CustomGames { get; set; } = new();

    // Aparência
    public bool LightTheme { get; set; }

    // Overlay
    public string OverlayCorner { get; set; } = "TL";
    public double OverlayScale { get; set; } = 1.0;
    /// <summary>Itens escolhidos para o overlay (chaves de OverlayMetrics); null = padrão.</summary>
    public List<string>? OverlayItems { get; set; }

    // Homelab & Automação (Prometheus e MQTT)
    public bool HomelabPrometheusEnabled { get; set; }
    public int HomelabPrometheusPort { get; set; } = 9182;
    public bool HomelabMqttEnabled { get; set; }
    public string HomelabMqttBroker { get; set; } = "127.0.0.1";
    public int HomelabMqttPort { get; set; } = 1883;
    public string HomelabMqttTopic { get; set; } = "thermosync/telemetry";
    public bool HomelabMqttDiscovery { get; set; } = true;

    // Modo Segundo Plano (System Tray) e Inicializacao
    public bool StartWithWindows { get; set; } = false;
    public bool MinimizeToTray { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool StartMinimized { get; set; }

    // Webhooks de Alerta Crítico (Gotify, Discord, Telegram)
    public bool AlertGotifyEnabled { get; set; }
    public string AlertGotifyUrl { get; set; } = "";
    public string AlertGotifyToken { get; set; } = "";
    public bool AlertDiscordEnabled { get; set; }
    public string AlertDiscordWebhookUrl { get; set; } = "";
    public bool AlertTelegramEnabled { get; set; }
    public string AlertTelegramBotToken { get; set; } = "";
    public string AlertTelegramChatId { get; set; } = "";
    public double AlertThresholdCpuTemp { get; set; } = 90.0;
    public double AlertThresholdGpuTemp { get; set; } = 85.0;
    public double AlertThresholdGpuHotspot { get; set; } = 95.0;
    public double AlertThresholdRamPct { get; set; } = 95.0;
    public int AlertCooldownMinutes { get; set; } = 5;

    // SYNC FAN (Controle Ativo de Ventoinhas)
    public string FanMode { get; set; } = "Auto"; // Auto | Quiet | Balanced | Performance | Manual | Custom
    public int FanManualPct { get; set; } = 60;
    public string FanCurveSource { get; set; } = "Max"; // Max | Cpu | Gpu
    public int FanCurveP1Temp { get; set; } = 35;
    public int FanCurveP1Pct { get; set; } = 25;
    public int FanCurveP2Temp { get; set; } = 55;
    public int FanCurveP2Pct { get; set; } = 45;
    public int FanCurveP3Temp { get; set; } = 70;
    public int FanCurveP3Pct { get; set; } = 70;
    public int FanCurveP4Temp { get; set; } = 85;
    public int FanCurveP4Pct { get; set; } = 100;
    public int FanCurveHysteresis { get; set; } = 3;
    public int FanRampDownPctPerSec { get; set; } = 4;

    // SYNC NET (Teste de Rede Homelab)
    public string HomelabNetHost { get; set; } = "192.168.1.1";
    public int HomelabNetPort { get; set; } = 5201;

    // Telemetria e Logs
    public bool TelemetryLogEnabled { get; set; } = true;
    public int TelemetryLogIntervalSec { get; set; } = 5;

    // Auto-Update
    public bool AutoCheckUpdates { get; set; } = true;

    [System.Text.Json.Serialization.JsonIgnore] // nunca gravar a chave em texto puro
    public string? HowManyFpsKey
    {
        get
        {
            if (string.IsNullOrEmpty(HowManyFpsKeyProtected)) return null;
            try
            {
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(HowManyFpsKeyProtected), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch { return null; }
        }
        set
        {
            HowManyFpsKeyProtected = string.IsNullOrWhiteSpace(value)
                ? null
                : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value.Trim()), null, DataProtectionScope.CurrentUser));
        }
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void ExportToFile(string destinationPath)
    {
        string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(destinationPath, json);
    }

    public static AppSettings ImportFromFile(string sourcePath)
    {
        string json = File.ReadAllText(sourcePath);
        var settings = JsonSerializer.Deserialize<AppSettings>(json);
        if (settings == null) throw new InvalidOperationException("Arquivo de configurações inválido.");
        settings.Save();
        return settings;
    }
}

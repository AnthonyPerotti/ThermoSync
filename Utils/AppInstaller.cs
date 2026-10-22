using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace ThermoSync;

/// <summary>App do catálogo "Instalar apps" (pós-formatação), instalado pelo winget ou via GitHub Releases.</summary>
public sealed class InstallableApp : INotifyPropertyChanged
{
    public string Name { get; init; } = "";
    public string Category { get; init; } = "Geral";
    public string Description { get; init; } = "";
    public string WingetId { get; init; } = "";
    public string? GitHubRepo { get; init; }
    public string? AssetPattern { get; init; }
    /// <summary>Começo do nome em "Aplicativos instalados" (Painel de Controle), para saber se já está instalado.</summary>
    public string DisplayPrefix { get; init; } = "";
    public bool PreSelect { get; init; } = true;

    private bool _selected, _installed;
    private string _status = "";

    public bool Selected { get => _selected; set => Set(ref _selected, value); }
    public bool Installed
    {
        get => _installed;
        set { Set(ref _installed, value); OnChanged(nameof(CanSelect)); }
    }
    public bool CanSelect => !Installed;
    public string Status { get => _status; set => Set(ref _status, value); }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    private void Set<T>(ref T field, T value, [CallerMemberName] string? p = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnChanged(p);
    }
}

/// <summary>
/// Catálogo de apps essenciais e instalação silenciosa pelo winget ou download direto do GitHub Releases,
/// baixando sempre a versão mais recente oficial e instalando sem janelas.
/// </summary>
public static class AppInstaller
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(60)
    };

    static AppInstaller()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("ThermoSync-AppInstaller");
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public static List<InstallableApp> Catalog() => new()
    {
        // --- RUNTIMES ESSENCIAIS ---
        new()
        {
            Category = "Runtimes Essenciais",
            Name = "Visual C++ 2015-2022 (x64)",
            Description = "Bibliotecas C++ de 64 bits indispensáveis para jogos e softwares modernos. Evita erro de MSVCP140.dll.",
            WingetId = "Microsoft.VCRedist.2015+.x64",
            DisplayPrefix = "Microsoft Visual C++ 2015-2022 Redistributable (x64)",
            PreSelect = true
        },
        new()
        {
            Category = "Runtimes Essenciais",
            Name = "Visual C++ 2015-2022 (x86)",
            Description = "Bibliotecas C++ de 32 bits necessárias para retrocompatibilidade com jogos e ferramentas clássicas.",
            WingetId = "Microsoft.VCRedist.2015+.x86",
            DisplayPrefix = "Microsoft Visual C++ 2015-2022 Redistributable (x86)",
            PreSelect = true
        },
        new()
        {
            Category = "Runtimes Essenciais",
            Name = "DirectX End-User Runtimes",
            Description = "Componentes legados de DirectX (D3DX9, D3DX10, D3DX11 e XInput) essenciais para jogos pós-formatação.",
            WingetId = "Microsoft.DirectX",
            DisplayPrefix = "DirectX",
            PreSelect = true
        },
        new()
        {
            Category = "Runtimes Essenciais",
            Name = ".NET Desktop Runtime 8.0",
            Description = "Ambiente de execução oficial da Microsoft para utilitários modernos e aplicações desktop em .NET.",
            WingetId = "Microsoft.DotNet.DesktopRuntime.8",
            DisplayPrefix = "Microsoft Windows Desktop Runtime - 8.0",
            PreSelect = true
        },

        // --- PRODUTIVIDADE & DOCUMENTOS (GITHUB OFICIAL) ---
        new()
        {
            Category = "Produtividade & Documentos",
            Name = "PdfCraft",
            Description = "Leitor e editor de PDF moderno e ultrarrápido escrito em Rust, com processamento 100% local e sem telemetria.",
            GitHubRepo = "storytold/pdfcraft",
            AssetPattern = "windows-x64.msi",
            DisplayPrefix = "PdfCraft",
            PreSelect = true
        },
        new()
        {
            Category = "Produtividade & Documentos",
            Name = "WordCraft",
            Description = "Processador de texto para arquivos Word/DOCX nativo acelerado por GPU em Rust. Rápido, offline e leve.",
            GitHubRepo = "storytold/wordcraft",
            AssetPattern = "windows-x64.msi",
            DisplayPrefix = "WordCraft",
            PreSelect = true
        },

        // --- NAVEGADORES ---
        new()
        {
            Category = "Navegadores",
            Name = "Google Chrome",
            Description = "Navegador de internet padrão do Google com sincronização de conta e extensões.",
            WingetId = "Google.Chrome",
            DisplayPrefix = "Google Chrome",
            PreSelect = true
        },
        new()
        {
            Category = "Navegadores",
            Name = "Mozilla Firefox",
            Description = "Navegador web rápido, de código aberto e focado em privacidade e segurança do usuário.",
            WingetId = "Mozilla.Firefox",
            DisplayPrefix = "Mozilla Firefox",
            PreSelect = false
        },

        // --- COMPACTADORES ---
        new()
        {
            Category = "Compactadores",
            Name = "7-Zip",
            Description = "Compactador e descompactador gratuito e de código aberto de alta taxa de compressão (zip, 7z, rar, tar).",
            WingetId = "7zip.7zip",
            DisplayPrefix = "7-Zip",
            PreSelect = true
        },
        new()
        {
            Category = "Compactadores",
            Name = "WinRAR",
            Description = "Compactador tradicional de arquivos (pago após o período de testes). Escolha ele ou o 7-Zip.",
            WingetId = "RARLab.WinRAR",
            DisplayPrefix = "WinRAR",
            PreSelect = false
        },

        // --- JOGOS & COMUNICAÇÃO ---
        new()
        {
            Category = "Jogos & Comunicação",
            Name = "Steam",
            Description = "Loja, plataforma social e biblioteca de jogos digitais para PC da Valve.",
            WingetId = "Valve.Steam",
            DisplayPrefix = "Steam",
            PreSelect = true
        },
        new()
        {
            Category = "Jogos & Comunicação",
            Name = "Epic Games Launcher",
            Description = "Loja e inicializador de jogos da Epic Games com jogos gratuitos semanais e Unreal Engine.",
            WingetId = "EpicGames.EpicGamesLauncher",
            DisplayPrefix = "Epic Games",
            PreSelect = false
        },
        new()
        {
            Category = "Jogos & Comunicação",
            Name = "Discord",
            Description = "Chat de voz, vídeo e texto com baixa latência para conversar enquanto joga.",
            WingetId = "Discord.Discord",
            DisplayPrefix = "Discord",
            PreSelect = true
        },

        // --- MÍDIA & DRIVERS ---
        new()
        {
            Category = "Mídia & Drivers",
            Name = "VLC media player",
            Description = "Player multimídia de código aberto que reproduz praticamente qualquer formato de vídeo ou áudio sem codecs adicionais.",
            WingetId = "VideoLAN.VLC",
            DisplayPrefix = "VLC media player",
            PreSelect = true
        },
        new()
        {
            Category = "Mídia & Drivers",
            Name = "Lightshot",
            Description = "Captura de tela ágil acionada pela tecla Print Screen com ferramentas de anotação rápida.",
            WingetId = "Skillbrains.Lightshot",
            DisplayPrefix = "Lightshot",
            PreSelect = true
        },
        new()
        {
            Category = "Mídia & Drivers",
            Name = "Intel Driver & Support Assistant",
            Description = "Utilitário oficial da Intel para detectar e atualizar automaticamente drivers de rede, Wi-Fi, Bluetooth e GPU.",
            WingetId = "Intel.IntelDriverAndSupportAssistant",
            DisplayPrefix = "Intel(R) Driver & Support Assistant",
            PreSelect = false
        },
    };

    /// <summary>
    /// Procura o executável do winget em cascata: PATH do ambiente, pasta de aliases do usuário,
    /// pasta do perfil de usuário real (quando executado com elevação UAC) e pacote UWP nativo.
    /// </summary>
    public static string? FindWingetPath()
    {
        // 1. Diretórios no PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, "winget.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }

        // 2. Pasta padrão de aliases do usuário atual (%LOCALAPPDATA%\Microsoft\WindowsApps)
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
        {
            var alias = Path.Combine(localAppData, @"Microsoft\WindowsApps\winget.exe");
            if (File.Exists(alias)) return alias;
        }

        // 3. Pasta de perfil do usuário (%USERPROFILE%), caso rodando elevado como Administrador
        var userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        if (!string.IsNullOrEmpty(userProfile))
        {
            var userAlias = Path.Combine(userProfile, @"AppData\Local\Microsoft\WindowsApps\winget.exe");
            if (File.Exists(userAlias)) return userAlias;
        }

        // 4. Instalação nativa em Program Files\WindowsApps
        try
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var winApps = Path.Combine(programFiles, "WindowsApps");
            if (Directory.Exists(winApps))
            {
                var dirs = Directory.GetDirectories(winApps, "Microsoft.DesktopAppInstaller_*_x64__8wekyb3d8bbwe");
                foreach (var d in dirs.OrderByDescending(x => x))
                {
                    var exe = Path.Combine(d, "winget.exe");
                    if (File.Exists(exe)) return exe;
                }
            }
        }
        catch { }

        return null;
    }

    public static string WingetPath =>
        FindWingetPath() ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\winget.exe");

    public static bool WingetAvailable => FindWingetPath() != null;

    /// <summary>Nomes em "Aplicativos instalados" (máquina 64/32 bits e usuário atual).</summary>
    public static HashSet<string> InstalledNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const string path = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
        foreach (var (hive, view) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32),
                                             (RegistryHive.CurrentUser, RegistryView.Default) })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(path);
                if (root == null) continue;
                foreach (var sub in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(sub);
                    if (k?.GetValue("DisplayName") is string n && !string.IsNullOrWhiteSpace(n)) names.Add(n.Trim());
                }
            }
            catch { }
        }
        return names;
    }

    public static void RefreshInstalled(IEnumerable<InstallableApp> apps)
    {
        var names = InstalledNames();
        foreach (var a in apps)
        {
            a.Installed = names.Any(n => n.StartsWith(a.DisplayPrefix, StringComparison.OrdinalIgnoreCase)
                                      || n.Contains(a.Name, StringComparison.OrdinalIgnoreCase));
            if (a.Installed) a.Selected = false;
        }
    }

    /// <summary>Instala um app em silêncio (via winget ou download direto do GitHub Releases).</summary>
    public static async Task<(bool Ok, string Message)> InstallAsync(InstallableApp app, CancellationToken ct, bool upgrade = false)
    {
        if (!string.IsNullOrEmpty(app.GitHubRepo))
        {
            return await InstallFromGitHubAsync(app, ct);
        }

        var psi = new ProcessStartInfo(WingetPath,
            $"{(upgrade ? "upgrade" : "install")} --id {app.WingetId} -e --silent --accept-package-agreements --accept-source-agreements --disable-interactivity")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Não foi possível iniciar o winget.");
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
        string output = await outTask + await errTask;

        return p.ExitCode switch
        {
            0 => (true, "Instalado."),
            unchecked((int)0x8A15002B) or unchecked((int)0x8A150061) => (true, "Já estava instalado."),
            unchecked((int)0x8A150014) => (false, "Pacote não encontrado no winget."),
            unchecked((int)0x8A150008) => (false, "Falha no download: confira a internet e tente de novo."),
            _ => (false, $"Falhou (código 0x{p.ExitCode:X8}). {LastLine(output)}".Trim()),
        };
    }

    private static async Task<(bool Ok, string Message)> InstallFromGitHubAsync(InstallableApp app, CancellationToken ct)
    {
        try
        {
            string apiUrl = $"https://api.github.com/repos/{app.GitHubRepo}/releases/latest";
            using var res = await Http.GetAsync(apiUrl, ct);
            if (!res.IsSuccessStatusCode)
                return (false, $"Falha ao consultar GitHub Releases ({res.StatusCode}).");

            string json = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? downloadUrl = null;
            string? assetName = null;
            string pattern = app.AssetPattern ?? "windows-x64.msi";

            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    string name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (name.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    {
                        downloadUrl = asset.TryGetProperty("browser_download_url", out var dl) ? dl.GetString() : null;
                        assetName = name;
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(downloadUrl) || string.IsNullOrEmpty(assetName))
                return (false, "Nenhum instalador Windows x64 (.msi) encontrado na release.");

            string tempDir = Path.Combine(Path.GetTempPath(), "ThermoSync_Installs");
            Directory.CreateDirectory(tempDir);
            string targetPath = Path.Combine(tempDir, assetName);

            // Download do pacote MSI oficial
            using (var downloadRes = await Http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                downloadRes.EnsureSuccessStatusCode();
                await using var stream = await downloadRes.Content.ReadAsStreamAsync(ct);
                await using var file = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);
                await stream.CopyToAsync(file, ct);
            }

            // Execucao silenciosa via msiexec
            var psi = new ProcessStartInfo("msiexec.exe", $"/i \"{targetPath}\" /qn /norestart")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Não foi possível iniciar o msiexec.");
            await p.WaitForExitAsync(ct);

            try { File.Delete(targetPath); } catch { }

            if (p.ExitCode == 0 || p.ExitCode == 3010)
                return (true, "Instalado com sucesso via GitHub Releases.");

            return (false, $"Instalação do MSI falhou (código {p.ExitCode}).");
        }
        catch (Exception ex)
        {
            return (false, $"Erro ao baixar/instalar: {ex.Message}");
        }
    }

    /// <summary>Última linha útil da saída do winget (sem as barras de progresso).</summary>
    private static string LastLine(string output) =>
        output.Split('\n', '\r').Select(l => l.Trim())
              .LastOrDefault(l => l.Length > 3 && !l.All(c => "-\\|/█▒ ".Contains(c)) && !l.Contains("KB /") && !l.Contains("MB /")) ?? "";
}

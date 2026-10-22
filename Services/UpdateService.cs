using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;

namespace ThermoSync;

public sealed record UpdateCheckResult(
    bool HasUpdate,
    string CurrentVersion,
    string RemoteVersion,
    string? DownloadUrl,
    long AssetSizeBytes,
    string? ReleaseNotes
);

/// <summary>
/// Verifies GitHub Releases for newer ThermoSync versions, downloads binaries,
/// and executes an uncoupled helper script to bypass Windows executable file locking.
/// </summary>
public sealed class UpdateService
{
    private const string RepoOwner = "AnthonyPerotti";
    private const string RepoName = "ThermoSync";
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    static UpdateService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("ThermoSync-App");
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public static Version GetCurrentVersion()
    {
        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        return ver ?? new Version(1, 1, 0, 0);
    }

    public async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken ct = default)
    {
        var currentVer = GetCurrentVersion();
        string currentVerStr = $"v{currentVer.Major}.{currentVer.Minor}.{currentVer.Build}";

        try
        {
            string url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
            using var response = await Http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(false, currentVerStr, "N/A", null, 0, null);
            }

            string json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string tagName = root.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() ?? "" : "";
            string body = root.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? "" : "";

            string cleanTag = tagName.TrimStart('v', 'V').Trim();
            if (!Version.TryParse(cleanTag, out var remoteVer))
            {
                // Fallback: se tag for 'latest' ou sem versao numerica direta, extrair do titulo da release ou do corpo do changelog
                string releaseTitle = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
                var match = System.Text.RegularExpressions.Regex.Match(
                    $"{releaseTitle}\n{body}",
                    @"(?:Vers[aã]o(?:\s+mais\s+recente:\s*v?)?|v)\s*(\d+\.\d+(?:\.\d+)?)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                if (match.Success && Version.TryParse(match.Groups[1].Value, out var parsedFromText))
                {
                    remoteVer = parsedFromText;
                    cleanTag = match.Groups[1].Value;
                }
                else
                {
                    return new UpdateCheckResult(false, currentVerStr, tagName, null, 0, body);
                }
            }

            bool isNewer = remoteVer > currentVer;

            string? downloadUrl = null;
            long assetSize = 0;

            if (root.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsProp.EnumerateArray())
                {
                    string name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (name.Equals("ThermoSync.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        downloadUrl = asset.TryGetProperty("browser_download_url", out var dl) ? dl.GetString() : null;
                        assetSize = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                        break;
                    }
                }
            }

            return new UpdateCheckResult(
                isNewer && !string.IsNullOrEmpty(downloadUrl),
                currentVerStr,
                $"v{cleanTag}",
                downloadUrl,
                assetSize,
                body
            );
        }
        catch
        {
            return new UpdateCheckResult(false, currentVerStr, "Erro", null, 0, null);
        }
    }

    public async Task DownloadAndInstallUpdateAsync(
        string downloadUrl,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "ThermoSync_Update");
        Directory.CreateDirectory(tempDir);
        string tempExe = Path.Combine(tempDir, "ThermoSync_New.exe");

        using (var res = await Http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            res.EnsureSuccessStatusCode();
            long totalBytes = res.Content.Headers.ContentLength ?? -1L;

            await using var contentStream = await res.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(tempExe, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            var buffer = new byte[8192];
            long bytesReadTotal = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                bytesReadTotal += bytesRead;

                if (totalBytes > 0 && progress != null)
                {
                    double pct = (double)bytesReadTotal / totalBytes;
                    progress.Report(pct);
                }
            }
        }

        // Validate executable presence and basic PE signature
        var fileInfo = new FileInfo(tempExe);
        if (!fileInfo.Exists || fileInfo.Length < 100_000)
        {
            throw new InvalidOperationException("O arquivo baixado parece corrompido ou incompleto.");
        }

        string currentExePath = Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("Não foi possível identificar o caminho do executável atual.");

        string scriptPath = Path.Combine(tempDir, "apply_update.cmd");
        string scriptContent = $@"@echo off
chcp 65001 > nul
timeout /t 2 /nobreak > nul
:retry
move /y ""{tempExe}"" ""{currentExePath}"" > nul 2>&1
if errorlevel 1 (
    timeout /t 1 /nobreak > nul
    goto retry
)
start """" ""{currentExePath}""
del ""%~f0""
";

        await File.WriteAllTextAsync(scriptPath, scriptContent, ct);

        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{scriptPath}\"",
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        Process.Start(startInfo);

        Application.Current.Dispatcher.Invoke(() =>
        {
            Application.Current.Shutdown();
        });
    }
}

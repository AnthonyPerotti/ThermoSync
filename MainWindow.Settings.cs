using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Xml.Linq;
using Microsoft.Win32;

namespace ThermoSync;

/// <summary>
/// SYNC SETTINGS: Configurações do ciclo de vida da aplicação, modo segundo plano (tray),
/// inicialização com o Windows, histórico de telemetria CSV, backups e auto-update.
/// </summary>
public partial class MainWindow
{
    private const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppRunName = "ThermoSync";
    private const string TaskName = "ThermoSync_Startup";
    private string? _pendingUpdateUrl;

    private void InitSettingsUi()
    {
        ChkStartWithWindows.IsChecked = IsStartWithWindowsEnabled();
        ChkStartMinimized.IsChecked = _settings.StartMinimized;
        ChkMinimizeToTray.IsChecked = _settings.MinimizeToTray;
        ChkCloseToTray.IsChecked = _settings.CloseToTray;

        ChkTelemetryLog.IsChecked = _settings.TelemetryLogEnabled;
        UpdateTelemetryStatusUi();

        var ver = UpdateService.GetCurrentVersion();
        TxtAppVersion.Text = $"Versão instalada: v{ver.Major}.{ver.Minor}.{ver.Build}";
    }

    private static bool IsStartWithWindowsEnabled()
    {
        // Primary: check Task Scheduler (works with requireAdministrator manifest)
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", $"/query /tn \"{TaskName}\" /fo LIST")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            p?.WaitForExit(3000);
            if (p?.ExitCode == 0) return true;
        }
        catch { }

        // Fallback: legacy Run key (for migration)
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, false);
            return key?.GetValue(AppRunName) != null;
        }
        catch { return false; }
    }

    private void SetStartWithWindows(bool enable)
    {
        // Clean up legacy Run key (can't launch requireAdministrator apps at logon)
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, true);
            key?.DeleteValue(AppRunName, false);
        }
        catch { }

        if (enable)
        {
            try
            {
                string exePath = Environment.ProcessPath
                    ?? Process.GetCurrentProcess().MainModule?.FileName
                    ?? "";
                if (string.IsNullOrEmpty(exePath)) return;

                string args = _settings.StartMinimized ? "--minimized" : "";
                string ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

                // Task Scheduler XML: runs at logon with highest privileges (bypasses UAC)
                string xmlPath = Path.Combine(Path.GetTempPath(), "ThermoSync_Task.xml");
                var xml = new XDocument(
                    new XDeclaration("1.0", "UTF-16", null),
                    new XElement(XName.Get("Task", ns),
                        new XAttribute("version", "1.2"),
                        new XElement(XName.Get("RegistrationInfo", ns),
                            new XElement(XName.Get("Description", ns), "Start ThermoSync at user logon with elevated privileges")),
                        new XElement(XName.Get("Triggers", ns),
                            new XElement(XName.Get("LogonTrigger", ns),
                                new XElement(XName.Get("Enabled", ns), "true"),
                                new XElement(XName.Get("UserId", ns), Environment.UserName),
                                new XElement(XName.Get("Delay", ns), "PT5S"))),
                        new XElement(XName.Get("Principals", ns),
                            new XElement(XName.Get("Principal", ns),
                                new XAttribute("id", "Author"),
                                new XElement(XName.Get("LogonType", ns), "InteractiveToken"),
                                new XElement(XName.Get("RunLevel", ns), "HighestAvailable"))),
                        new XElement(XName.Get("Settings", ns),
                            new XElement(XName.Get("MultipleInstancesPolicy", ns), "IgnoreNew"),
                            new XElement(XName.Get("DisallowStartIfOnBatteries", ns), "false"),
                            new XElement(XName.Get("StopIfGoingOnBatteries", ns), "false"),
                            new XElement(XName.Get("ExecutionTimeLimit", ns), "PT0S"),
                            new XElement(XName.Get("AllowStartOnDemand", ns), "true")),
                        new XElement(XName.Get("Actions", ns),
                            new XElement(XName.Get("Exec", ns),
                                new XElement(XName.Get("Command", ns), exePath),
                                new XElement(XName.Get("Arguments", ns), args),
                                new XElement(XName.Get("WorkingDirectory", ns), Path.GetDirectoryName(exePath) ?? "")))));

                xml.Save(xmlPath);

                var psi = new ProcessStartInfo("schtasks.exe",
                    $"/create /tn \"{TaskName}\" /xml \"{xmlPath}\" /f")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                using var p = Process.Start(psi);
                p?.WaitForExit(5000);

                try { File.Delete(xmlPath); } catch { }
            }
            catch { }
        }
        else
        {
            try
            {
                var psi = new ProcessStartInfo("schtasks.exe", $"/delete /tn \"{TaskName}\" /f")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                using var p = Process.Start(psi);
                p?.WaitForExit(3000);
            }
            catch { }
        }
    }

    private void ChkStartWithWindows_Click(object sender, RoutedEventArgs e)
    {
        bool enable = ChkStartWithWindows.IsChecked == true;
        _settings.StartWithWindows = enable;
        SetStartWithWindows(enable);
        _settings.Save();
    }

    private void ChkStartMinimized_Click(object sender, RoutedEventArgs e)
    {
        _settings.StartMinimized = ChkStartMinimized.IsChecked == true;
        _settings.Save();
        if (_settings.StartWithWindows || IsStartWithWindowsEnabled())
        {
            SetStartWithWindows(true);
        }
    }

    private void ChkMinimizeToTray_Click(object sender, RoutedEventArgs e)
    {
        _settings.MinimizeToTray = ChkMinimizeToTray.IsChecked == true;
        _settings.Save();
    }

    private void ChkCloseToTray_Click(object sender, RoutedEventArgs e)
    {
        _settings.CloseToTray = ChkCloseToTray.IsChecked == true;
        _settings.Save();
    }

    private void ChkTelemetryLog_Click(object sender, RoutedEventArgs e)
    {
        _settings.TelemetryLogEnabled = ChkTelemetryLog.IsChecked == true;
        _telemetryLogger.IsEnabled = _settings.TelemetryLogEnabled;
        _settings.Save();
        UpdateTelemetryStatusUi();
    }

    private void UpdateTelemetryStatusUi()
    {
        if (TxtTelemetryStatus == null) return;
        if (!_settings.TelemetryLogEnabled)
        {
            TxtTelemetryStatus.Text = "Gravação contínua desativada nas configurações.";
            return;
        }

        string file = string.IsNullOrEmpty(_telemetryLogger.CurrentLogFile)
            ? "Aguardando primeira amostra"
            : Path.GetFileName(_telemetryLogger.CurrentLogFile);
        TxtTelemetryStatus.Text = $"Arquivo ativo: {file} · Amostras gravadas nesta sessão: {_telemetryLogger.TotalSamplesWritten:N0}";
    }

    private void BtnOpenLogsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string logsDir = TelemetryLogger.LogDir;
            Directory.CreateDirectory(logsDir);
            Process.Start(new ProcessStartInfo
            {
                FileName = logsDir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Não foi possível abrir a pasta de logs: {ex.Message}", "ThermoSync", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnAnalyzeLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string dateStr = DateTime.Now.ToString("yyyy-MM-dd");
            string logFile = Path.Combine(TelemetryLogger.LogDir, $"telemetry-{dateStr}.csv");

            if (!File.Exists(logFile))
            {
                if (TxtTelemetryAnalysisResult != null)
                    TxtTelemetryAnalysisResult.Text = "Nenhum arquivo de telemetria encontrado para o dia de hoje. Ative a gravação contínua acima.";
                return;
            }

            var summary = TelemetryAnalyzer.AnalyzeFile(logFile);
            if (summary == null)
            {
                if (TxtTelemetryAnalysisResult != null)
                    TxtTelemetryAnalysisResult.Text = "Arquivo de telemetria de hoje ainda não contém amostras suficientes para diagnóstico.";
                return;
            }

            string throtMsg = summary.ThrottlingCount > 0 ? $"{summary.ThrottlingCount} evento(s) de corte" : "Nenhum corte térmico/elétrico";
            string gameMsg = summary.MostActiveGame != "Nenhum" ? $" · Jogo em destaque: {summary.MostActiveGame}" : "";

            if (TxtTelemetryAnalysisResult != null)
            {
                TxtTelemetryAnalysisResult.Text =
                    $"Hoje ({summary.Duration:hh\\:mm\\:ss} monitorados, {summary.TotalSamples:N0} amostras):\n" +
                    $"• CPU: Média {summary.AvgCpuTemp:0.#} °C (Pico: {summary.MaxCpuTemp:0.#} °C) | Carga Média: {summary.AvgCpuLoad:0.#} % (Pico: {summary.MaxCpuLoad:0.#} %)\n" +
                    $"• GPU: Média {summary.AvgGpuTemp:0.#} °C (Pico: {summary.MaxGpuTemp:0.#} °C) | Carga Média: {summary.AvgGpuLoad:0.#} % (Pico: {summary.MaxGpuLoad:0.#} %)\n" +
                    $"• RAM: Média {summary.AvgRamPct:0.#} % (Pico: {summary.MaxRamPct:0.#} %) | Throttling: {throtMsg}{gameMsg}";
            }
        }
        catch (Exception ex)
        {
            if (TxtTelemetryAnalysisResult != null)
                TxtTelemetryAnalysisResult.Text = $"Erro ao analisar logs: {ex.Message}";
        }
    }

    private void BtnExportConfig_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "Arquivo de Configuração (*.json)|*.json",
            FileName = $"thermosync-backup-{DateTime.Now:yyyyMMdd}.json",
            Title = "Exportar backup de configurações do ThermoSync"
        };
        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                _settings.ExportToFile(dlg.FileName);
                if (TxtSettingsBackupStatus != null)
                    TxtSettingsBackupStatus.Text = $"Backup exportado com sucesso: {Path.GetFileName(dlg.FileName)}";
            }
            catch (Exception ex)
            {
                if (TxtSettingsBackupStatus != null)
                    TxtSettingsBackupStatus.Text = $"Erro ao exportar backup: {ex.Message}";
            }
        }
    }

    private void BtnImportConfig_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Arquivo de Configuração (*.json)|*.json",
            Title = "Restaurar backup de configurações do ThermoSync"
        };
        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                var imported = AppSettings.ImportFromFile(dlg.FileName);
                InitSettingsUi();
                InitHomelabUi();
                if (TxtSettingsBackupStatus != null)
                    TxtSettingsBackupStatus.Text = "Configurações restauradas com sucesso!";
            }
            catch (Exception ex)
            {
                if (TxtSettingsBackupStatus != null)
                    TxtSettingsBackupStatus.Text = $"Erro ao restaurar backup: {ex.Message}";
            }
        }
    }

    private async void BtnCheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        BtnCheckUpdate.IsEnabled = false;
        TxtUpdateStatus.Text = "Consultando GitHub Releases…";
        try
        {
            var res = await _updateService.CheckForUpdateAsync();
            if (res.HasUpdate && !string.IsNullOrEmpty(res.DownloadUrl))
            {
                _pendingUpdateUrl = res.DownloadUrl;
                TxtUpdateStatus.Text = $"Nova versão {res.RemoteVersion} disponível! ({res.AssetSizeBytes / (1024.0 * 1024.0):0.0} MB)";
                BtnInstallUpdate.Visibility = Visibility.Visible;
            }
            else
            {
                TxtUpdateStatus.Text = $"Você já está usando a versão mais recente ({res.CurrentVersion}).";
                BtnInstallUpdate.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            TxtUpdateStatus.Text = $"Falha ao consultar releases: {ex.Message}";
        }
        finally
        {
            BtnCheckUpdate.IsEnabled = true;
        }
    }

    private async void BtnInstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_pendingUpdateUrl)) return;

        BtnInstallUpdate.IsEnabled = false;
        BtnCheckUpdate.IsEnabled = false;
        PrgUpdate.Visibility = Visibility.Visible;
        PrgUpdate.Value = 0;
        TxtUpdateStatus.Text = "Baixando nova versão do ThermoSync…";

        var progress = new Progress<double>(p =>
        {
            PrgUpdate.Value = p * 100.0;
            TxtUpdateStatus.Text = $"Baixando: {p:P0}…";
        });

        try
        {
            await _updateService.DownloadAndInstallUpdateAsync(_pendingUpdateUrl, progress);
        }
        catch (Exception ex)
        {
            PrgUpdate.Visibility = Visibility.Collapsed;
            BtnInstallUpdate.IsEnabled = true;
            BtnCheckUpdate.IsEnabled = true;
            TxtUpdateStatus.Text = $"Erro na atualização: {ex.Message}";
        }
    }
}

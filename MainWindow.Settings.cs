using System.Diagnostics;
using System.IO;
using System.Windows;
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
    private string? _pendingUpdateUrl;

    private void InitSettingsUi()
    {
        ChkStartWithWindows.IsChecked = IsStartWithWindowsEnabled();
        ChkStartMinimized.IsChecked = _settings.StartMinimized;
        ChkMinimizeToTray.IsChecked = _settings.MinimizeToTray;
        ChkCloseToTray.IsChecked = _settings.CloseToTray;

        ChkTelemetryLog.IsChecked = _settings.TelemetryLogEnabled;

        var ver = UpdateService.GetCurrentVersion();
        TxtAppVersion.Text = $"Versão instalada: v{ver.Major}.{ver.Minor}.{ver.Build}";
    }

    private static bool IsStartWithWindowsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, false);
            return key?.GetValue(AppRunName) != null;
        }
        catch
        {
            return false;
        }
    }

    private void SetStartWithWindows(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, true);
            if (key == null) return;

            if (enable)
            {
                string exePath = Environment.ProcessPath
                    ?? Process.GetCurrentProcess().MainModule?.FileName
                    ?? "";
                if (!string.IsNullOrEmpty(exePath))
                {
                    string args = _settings.StartMinimized ? " --minimized" : "";
                    key.SetValue(AppRunName, $"\"{exePath}\"{args}");
                }
            }
            else
            {
                key.DeleteValue(AppRunName, false);
            }
        }
        catch { }
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
    }

    private void BtnOpenLogsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string logsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ThermoSync", "Logs"
            );
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

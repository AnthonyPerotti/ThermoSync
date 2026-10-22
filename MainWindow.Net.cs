using System.Windows;
using System.Windows.Threading;

namespace ThermoSync;

/// <summary>Páginas Energia (bateria e fonte) e Rede (diagnóstico de lag).</summary>
public partial class MainWindow
{
    // ================= Energia =================

    private bool _energyBusy;

    private async void RefreshEnergy()
    {
        if (_energyBusy) return;
        _energyBusy = true;
        BtnEnergyRefresh.IsEnabled = false;
        EnergyStatus.Text = "Lendo bateria e energia…";
        try
        {
            var sensors = _monitor.Rows.ToList();
            var cards = await Task.Run(() => BatteryInfo.Read(sensors));
            EnergyCards.ItemsSource = cards;
            bool hasBattery = cards.Count > 1;
            EnergyStatus.Text = (hasBattery ? "Bateria detectada" : "Nenhuma bateria (PC de mesa)") + $" · lido às {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex) { EnergyStatus.Text = "Falhou: " + ex.Message; }
        finally
        {
            _energyBusy = false;
            BtnEnergyRefresh.IsEnabled = true;
        }
    }

    private void BtnEnergyRefresh_Click(object sender, RoutedEventArgs e) => RefreshEnergy();

    // ================= Rede =================

    private CancellationTokenSource? _netCts;
    private bool _netRunning;
    private List<DiskHealthCard>? _lastNet;

    private async void BtnNetTest_Click(object sender, RoutedEventArgs e)
    {
        if (_netRunning) { _netCts?.Cancel(); return; }
        if (AnyBenchRunning || TestRunning) { NetStatus.Text = "Termine o outro teste antes."; return; }

        _netRunning = true;
        _netCts = new CancellationTokenSource();
        var ct = _netCts.Token;
        BtnNetTest.Content = "CANCELAR";
        NetCards.ItemsSource = null;
        NetProgress.ScaleX = 0;
        try
        {
            var cards = await NetworkTest.Run((text, frac) => Dispatcher.BeginInvoke(new Action(() =>
            {
                NetStatus.Text = text;
                NetProgress.ScaleX = Math.Clamp(frac, 0, 1);
            })), ct);
            _lastNet = cards;
            NetCards.ItemsSource = cards;
            NetStatus.Text = $"Concluído às {DateTime.Now:HH:mm:ss}. O resultado entra no relatório exportado.";
            NetProgress.ScaleX = 1;
        }
        catch (OperationCanceledException) { NetStatus.Text = "Teste cancelado."; NetProgress.ScaleX = 0; }
        catch (Exception ex) { NetStatus.Text = "Falhou: " + ex.Message; NetProgress.ScaleX = 0; }
        finally
        {
            _netRunning = false;
            _netCts?.Dispose();
            _netCts = null;
            BtnNetTest.Content = "TESTAR";
        }
    }

    // ================= Rede Homelab (iperf3 & Throughput LAN) =================

    private CancellationTokenSource? _homelabNetCts;
    private bool _homelabNetRunning;

    private void InitNetUi()
    {
        if (TxtHomelabNetHost != null && string.IsNullOrWhiteSpace(TxtHomelabNetHost.Text))
            TxtHomelabNetHost.Text = _settings.HomelabNetHost;
        if (TxtHomelabNetPort != null && string.IsNullOrWhiteSpace(TxtHomelabNetPort.Text))
            TxtHomelabNetPort.Text = _settings.HomelabNetPort.ToString();
    }

    private async void BtnHomelabNetTest_Click(object sender, RoutedEventArgs e)
    {
        if (_homelabNetRunning) { _homelabNetCts?.Cancel(); return; }
        if (AnyBenchRunning || TestRunning || _netRunning) { HomelabNetStatus.Text = "Termine o outro teste antes."; return; }

        string host = string.IsNullOrWhiteSpace(TxtHomelabNetHost.Text) ? "192.168.1.1" : TxtHomelabNetHost.Text.Trim();
        if (!int.TryParse(TxtHomelabNetPort.Text.Trim(), out int port) || port <= 0 || port > 65535)
            port = 5201;

        _settings.HomelabNetHost = host;
        _settings.HomelabNetPort = port;
        _settings.Save();

        _homelabNetRunning = true;
        _homelabNetCts = new CancellationTokenSource();
        var ct = _homelabNetCts.Token;

        BtnHomelabNetTest.Content = "CANCELAR";
        HomelabNetCards.ItemsSource = null;
        HomelabNetProgress.ScaleX = 0;

        try
        {
            var cards = await NetworkTest.RunHomelabNetTest(host, port, (text, frac) => Dispatcher.BeginInvoke(new Action(() =>
            {
                HomelabNetStatus.Text = text;
                HomelabNetProgress.ScaleX = Math.Clamp(frac, 0, 1);
            })), ct);

            HomelabNetCards.ItemsSource = cards;
            HomelabNetStatus.Text = $"Concluído às {DateTime.Now:HH:mm:ss}. Teste registrado com sucesso.";
            HomelabNetProgress.ScaleX = 1;
        }
        catch (OperationCanceledException) { HomelabNetStatus.Text = "Teste do Homelab cancelado."; HomelabNetProgress.ScaleX = 0; }
        catch (Exception ex) { HomelabNetStatus.Text = "Falhou: " + ex.Message; HomelabNetProgress.ScaleX = 0; }
        finally
        {
            _homelabNetRunning = false;
            _homelabNetCts?.Dispose();
            _homelabNetCts = null;
            BtnHomelabNetTest.Content = "TESTAR HOMELAB (IPERF3)";
        }
    }

    // ================= Tema (escuro / claro) =================

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        ThemeManager.Apply(!ThemeManager.IsLight);
        _settings.LightTheme = ThemeManager.IsLight;
        try { _settings.Save(); } catch { }
        UpdateThemeButton();
    }

    /// <summary>Mostra o ícone do modo para onde o clique leva (sol = ir para o claro, lua = ir para o escuro) e troca o logo.</summary>
    private void UpdateThemeButton()
    {
        bool light = ThemeManager.IsLight;
        BtnTheme.Content = light ? "\uE708" : "\uE706";
        BtnTheme.ToolTip = light ? "Modo escuro" : "Modo claro";
        TitleLogo.Source = new System.Windows.Media.Imaging.BitmapImage(
            new Uri(light ? "pack://application:,,,/Assets/logo-dark.png" : "pack://application:,,,/Assets/logo.png"));
    }
}

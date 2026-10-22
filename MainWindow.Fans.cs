using System.Windows;
using System.Windows.Controls;

namespace ThermoSync;

/// <summary>
/// SYNC FAN: Controle ativo de ventoinhas (gabinete, CPU e GPU), curvas térmicas e perfis de rotação.
/// </summary>
public partial class MainWindow
{
    private bool _fansInited;

    private void InitFansUi()
    {
        if (_fansInited) return;
        _fansInited = true;

        switch (_settings.FanMode.ToLowerInvariant())
        {
            case "quiet":
                BtnFanQuiet.IsChecked = true;
                break;
            case "balanced":
                BtnFanBalanced.IsChecked = true;
                break;
            case "performance":
                BtnFanPerformance.IsChecked = true;
                break;
            case "manual":
                BtnFanManual.IsChecked = true;
                break;
            default:
                BtnFanAuto.IsChecked = true;
                break;
        }

        SliderFanManual.Value = _settings.FanManualPct;
        TxtFanManualVal.Text = $"{_settings.FanManualPct}%";
        PanelFanManual.Visibility = BtnFanManual.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        RefreshFansList();
    }

    private void RefreshFansList()
    {
        FansList.ItemsSource = null;
        var fans = _fans.Fans;
        FansList.ItemsSource = fans;
        FansCountHint.Text = fans.Count > 0
            ? $"{fans.Count} ventoinha(s) / controle(s) detectados via LibreHardwareMonitor."
            : "Nenhuma ventoinha com controle de rotação PWM detectada nesta placa-mãe/GPU.";
    }

    private void FanMode_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string mode) return;

        _settings.FanMode = mode;
        _settings.Save();

        if (PanelFanManual != null)
            PanelFanManual.Visibility = mode == "Manual" ? Visibility.Visible : Visibility.Collapsed;

        ApplyCurrentFanProfile();
    }

    private void SliderFanManual_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int val = (int)Math.Round(SliderFanManual.Value);
        if (TxtFanManualVal != null) TxtFanManualVal.Text = $"{val}%";

        _settings.FanManualPct = val;
        _settings.Save();

        if (BtnFanManual?.IsChecked == true)
        {
            ApplyCurrentFanProfile();
        }
    }

    private void ApplyCurrentFanProfile()
    {
        float? cpuTemp = _monitor.CpuTemperature;
        float? gpuTemp = MainGpuTemp();
        _fans.ApplyProfile(_settings.FanMode, _settings.FanManualPct, cpuTemp, gpuTemp);
        UpdateFansLive();
    }

    private void UpdateFansLive()
    {
        if (!PageFans.IsVisible) return;
        _fans.UpdateValues();
        FansList.Items.Refresh();
    }
}

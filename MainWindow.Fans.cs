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
            case "custom":
                BtnFanCustom.IsChecked = true;
                break;
            default:
                BtnFanAuto.IsChecked = true;
                break;
        }

        SliderFanManual.Value = _settings.FanManualPct;
        TxtFanManualVal.Text = $"{_settings.FanManualPct}%";
        PanelFanManual.Visibility = BtnFanManual.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        if (_settings.FanCurveSource.Equals("Cpu", StringComparison.OrdinalIgnoreCase))
            BtnCurveSourceCpu.IsChecked = true;
        else if (_settings.FanCurveSource.Equals("Gpu", StringComparison.OrdinalIgnoreCase))
            BtnCurveSourceGpu.IsChecked = true;
        else
            BtnCurveSourceMax.IsChecked = true;

        SliderCurveP1.Value = _settings.FanCurveP1Pct;
        SliderCurveP2.Value = _settings.FanCurveP2Pct;
        SliderCurveP3.Value = _settings.FanCurveP3Pct;
        SliderCurveP4.Value = _settings.FanCurveP4Pct;
        SliderCurveHysteresis.Value = _settings.FanCurveHysteresis;
        UpdateCurveLabels();
        PanelFanCustom.Visibility = BtnFanCustom.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

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
            PanelFanManual.Visibility = mode.Equals("Manual", StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
        if (PanelFanCustom != null)
            PanelFanCustom.Visibility = mode.Equals("Custom", StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;

        ApplyCurrentFanProfile();
    }

    private void CurveSource_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string src) return;

        _settings.FanCurveSource = src;
        _settings.Save();

        if (BtnFanCustom?.IsChecked == true)
        {
            ApplyCurrentFanProfile();
        }
    }

    private void SliderCurve_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SliderCurveP1 == null || SliderCurveP2 == null || SliderCurveP3 == null || SliderCurveP4 == null) return;

        _settings.FanCurveP1Pct = (int)Math.Round(SliderCurveP1.Value);
        _settings.FanCurveP2Pct = (int)Math.Round(SliderCurveP2.Value);
        _settings.FanCurveP3Pct = (int)Math.Round(SliderCurveP3.Value);
        _settings.FanCurveP4Pct = (int)Math.Round(SliderCurveP4.Value);
        _settings.Save();

        UpdateCurveLabels();

        if (BtnFanCustom?.IsChecked == true)
        {
            ApplyCurrentFanProfile();
        }
    }

    private void UpdateCurveLabels()
    {
        if (TxtCurveP1 != null) TxtCurveP1.Text = $"Repouso ({_settings.FanCurveP1Temp} °C): {_settings.FanCurveP1Pct}%";
        if (TxtCurveP2 != null) TxtCurveP2.Text = $"Carga Leve ({_settings.FanCurveP2Temp} °C): {_settings.FanCurveP2Pct}%";
        if (TxtCurveP3 != null) TxtCurveP3.Text = $"Carga Alta ({_settings.FanCurveP3Temp} °C): {_settings.FanCurveP3Pct}%";
        if (TxtCurveP4 != null) TxtCurveP4.Text = $"Crítico ({_settings.FanCurveP4Temp} °C): {_settings.FanCurveP4Pct}%";
        if (TxtCurveHysteresis != null) TxtCurveHysteresis.Text = $"Histerese Acústica: {_settings.FanCurveHysteresis} °C (Evita oscilação de aceleração/desaceleração no limiar)";
    }

    private void SliderCurveHysteresis_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SliderCurveHysteresis == null) return;
        int val = (int)Math.Round(SliderCurveHysteresis.Value);
        _settings.FanCurveHysteresis = Math.Clamp(val, 1, 10);
        _settings.Save();
        UpdateCurveLabels();

        if (BtnFanCustom?.IsChecked == true)
        {
            ApplyCurrentFanProfile();
        }
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
        // Guard: during InitializeComponent the XAML parser fires Checked events
        // before the constructor has created _monitor and _fans.
        if (_monitor == null || _fans == null) return;

        float? cpuTemp = _monitor.CpuTemperature;
        float? gpuTemp = MainGpuTemp();
        var customPoints = new[]
        {
            ((float)_settings.FanCurveP1Temp, (float)_settings.FanCurveP1Pct),
            ((float)_settings.FanCurveP2Temp, (float)_settings.FanCurveP2Pct),
            ((float)_settings.FanCurveP3Temp, (float)_settings.FanCurveP3Pct),
            ((float)_settings.FanCurveP4Temp, (float)_settings.FanCurveP4Pct),
        };
        _fans.ApplyProfile(
            _settings.FanMode,
            _settings.FanManualPct,
            cpuTemp,
            gpuTemp,
            _settings.FanCurveSource,
            customPoints,
            _settings.FanCurveHysteresis,
            _settings.FanRampDownPctPerSec
        );
        UpdateFansLive();
    }

    private void UpdateFansLive()
    {
        if (!PageFans.IsVisible) return;
        _fans.UpdateValues();
        FansList.Items.Refresh();
    }
}

using LibreHardwareMonitor.Hardware;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Nova;

public partial class LiveMonitoringPanel : UserControl
{
    private readonly Computer _computer;
    private readonly DispatcherTimer _timer;

    public LiveMonitoringPanel()
    {
        InitializeComponent();
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsControllerEnabled = true
        };

        try { _computer.Open(); } catch { SensorStatus.Text = "Sensors unavailable"; }
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => UpdateSensors();
        Unloaded += (_, _) => { _timer.Stop(); _computer.Close(); };
        UpdateSensors();
        _timer.Start();
    }

    private void UpdateSensors()
    {
        try
        {
            var hardware = _computer.Hardware.ToArray();
            foreach (var item in hardware) item.Update();

            var cpu = hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
            var gpu = hardware.FirstOrDefault(h => h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel);
            var memory = hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Memory);

            var cpuLoad = FindSensor(cpu, SensorType.Load, "CPU Total") ?? FindSensor(cpu, SensorType.Load);
            var cpuTemp = FindSensor(cpu, SensorType.Temperature, "Package") ?? FindSensor(cpu, SensorType.Temperature);
            var gpuLoad = FindSensor(gpu, SensorType.Load, "GPU Core") ?? FindSensor(gpu, SensorType.Load);
            var gpuTemp = FindSensor(gpu, SensorType.Temperature, "GPU Core") ?? FindSensor(gpu, SensorType.Temperature);
            var ramLoad = FindSensor(memory, SensorType.Load);
            var fan = hardware.SelectMany(h => h.Sensors).FirstOrDefault(s => s.SensorType == SensorType.Fan);

            CpuLoadText.Text = FormatPercent(cpuLoad);
            CpuTempText.Text = $"Temperature {FormatTemperature(cpuTemp)}";
            GpuLoadText.Text = FormatPercent(gpuLoad);
            GpuTempText.Text = $"Temperature {FormatTemperature(gpuTemp)}";
            RamLoadText.Text = FormatPercent(ramLoad);
            FanText.Text = $"Fan speed {FormatRpm(fan)}";
            SensorStatus.Text = "Live • 1 sec";
        }
        catch
        {
            SensorStatus.Text = "Some sensors unavailable";
        }
    }

    private static ISensor? FindSensor(IHardware? hardware, SensorType type, string? name = null)
        => hardware?.Sensors.FirstOrDefault(s => s.SensorType == type && (name == null || s.Name.Contains(name, StringComparison.OrdinalIgnoreCase)))
           ?? (name == null ? hardware?.Sensors.FirstOrDefault(s => s.SensorType == type) : null);

    private static string FormatPercent(ISensor? sensor) => sensor?.Value is float value ? $"{value:F0}%" : "N/A";
    private static string FormatTemperature(ISensor? sensor) => sensor?.Value is float value ? $"{value:F0}°C" : "N/A";
    private static string FormatRpm(ISensor? sensor) => sensor?.Value is float value ? $"{value:F0} RPM" : "N/A";
}

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Nova;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _timer;
    private readonly string _quarantineRoot;
    private readonly string _logPath;
    private int _cleanupRunning;

    public ObservableCollection<DriveReport> DriveReports { get; } = new();
    public ObservableCollection<StartupEntry> StartupEntries { get; } = new();
    public ObservableCollection<LargeFileItem> LargeFiles { get; } = new();
    public ObservableCollection<QuarantineEntry> QuarantineEntries { get; } = new();
    public ObservableCollection<string> LogEntries { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nova");
        Directory.CreateDirectory(appData);
        _quarantineRoot = Path.Combine(appData, "Quarantine");
        _logPath = Path.Combine(appData, "nova_log.txt");
        Directory.CreateDirectory(_quarantineRoot);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => UpdateSystemMetrics();

        LoadStartupEntries();
        LoadDriveReports();
        LoadLargeFiles();
        RefreshQuarantineList();
        RefreshLogList();
        UpdateSystemMetrics();
        _timer.Start();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        LoadStartupEntries();
        LoadDriveReports();
        LoadLargeFiles();
        RefreshQuarantineList();
        RefreshLogList();
        UpdateSystemMetrics();
    }

    private void CleanupTempButton_Click(object sender, RoutedEventArgs e) => BeginCleanup("temporary files", "Temporary", GetTempFiles());
    private void CleanupLargeButton_Click(object sender, RoutedEventArgs e) => BeginCleanup("large files", "Large", LargeFiles.Select(x => new CleanupItem(x.Path, x.Name, x.Size)).ToList());

    private void CleanupRecycleButton_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Empty the Windows Recycle Bin? This cannot be undone.", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            var result = NativeMethods.SHEmptyRecycleBin(IntPtr.Zero, null, 0);
            if (result != 0) throw new InvalidOperationException($"Windows returned code {result}.");
            StatusText.Text = "Recycle Bin emptied";
            Log("Recycle Bin emptied.");
            RefreshLogList();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Recycle Bin Cleanup Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            Log(ex.ToString());
            RefreshLogList();
        }
    }

    private void BeginCleanup(string description, string kind, List<CleanupItem> candidates)
    {
        if (Interlocked.Exchange(ref _cleanupRunning, 1) != 0) return;
        Task.Run(() => CleanupWorker(description, kind, candidates));
    }

    private void CleanupWorker(string description, string kind, List<CleanupItem> candidates)
    {
        try
        {
            if (candidates.Count == 0)
            {
                Dispatcher.Invoke(() => MessageBox.Show($"No {description} were found.", "Nothing to Clean"));
                return;
            }

            var preview = string.Join(Environment.NewLine, candidates.Take(5).Select(x => $"- {x.Name} ({Bytes(x.Size)})"));
            var answer = Dispatcher.Invoke(() => MessageBox.Show($"Move {candidates.Count} {description} to Nova quarantine?\n\n{preview}\n\nFiles remain recoverable there.", "Confirm Cleanup", MessageBoxButton.YesNo, MessageBoxImage.Warning));
            if (answer != MessageBoxResult.Yes) return;

            var folder = Path.Combine(_quarantineRoot, $"{kind}_{DateTime.Now:yyyyMMdd_HHmmss}");
            Directory.CreateDirectory(folder);

            var moved = 0;
            foreach (var item in candidates)
            {
                try
                {
                    if (!File.Exists(item.Path)) continue;
                    var target = Path.Combine(folder, SafeName(item.Name + "_" + Guid.NewGuid().ToString("N")));
                    File.WriteAllText(target + ".meta", item.Path + Environment.NewLine);
                    File.Move(item.Path, target);
                    moved++;
                    Log($"Quarantined {item.Path} -> {target}");
                }
                catch (Exception ex)
                {
                    Log($"Skipped {item.Path}: {ex.Message}");
                }
            }

            Dispatcher.Invoke(() =>
            {
                MessageBox.Show($"{moved} item(s) moved to quarantine.", "Cleanup Complete");
                LoadDriveReports();
                LoadLargeFiles();
                RefreshQuarantineList();
                RefreshLogList();
            });
        }
        finally
        {
            Interlocked.Exchange(ref _cleanupRunning, 0);
        }
    }

    private void OpenQuarantineFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(_quarantineRoot);
    private void OpenStartupFolder_Click(object sender, RoutedEventArgs e)
    {
        var startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        if (string.IsNullOrWhiteSpace(startupFolder) || !Directory.Exists(startupFolder))
        {
            MessageBox.Show("Startup folder could not be found.", "Folder Not Found", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        OpenFolder(startupFolder);
    }
    private void OpenLogFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(Path.GetDirectoryName(_logPath)!);
    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private void RestoreFromQuarantine_Click(object sender, RoutedEventArgs e)
    {
        var selected = QuarantineList.SelectedItem as QuarantineEntry;
        if (selected == null)
        {
            MessageBox.Show("Select a quarantined item first.", "No Selection", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var restorePath = selected.OriginalPath;
            var dir = Path.GetDirectoryName(restorePath);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

            File.Copy(selected.QuarantinedPath, restorePath, true);
            File.Delete(selected.QuarantinedPath);
            var meta = selected.QuarantinedPath + ".meta";
            if (File.Exists(meta)) File.Delete(meta);

            Log($"Restored {restorePath} from quarantine.");
            RefreshQuarantineList();
            RefreshLogList();
            MessageBox.Show($"Restored:\n{restorePath}", "Restore Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Restore failed: {ex.Message}", "Restore Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DisableSelectedStartup_Click(object sender, RoutedEventArgs e)
    {
        var item = StartupList.SelectedItem as StartupEntry;
        if (item == null)
        {
            MessageBox.Show("Select a startup item first.", "No Selection", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            if (string.Equals(item.Source, "Run Registry", StringComparison.OrdinalIgnoreCase))
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
                using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
                if (key != null)
                {
                    key.DeleteValue(item.Name, false);
                }
                else
                {
                    using var machineKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default);
                    using var machineRun = machineKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
                    machineRun?.DeleteValue(item.Name, false);
                }
            }
            else if (!string.IsNullOrWhiteSpace(item.Path) && File.Exists(item.Path))
            {
                var target = Path.Combine(_quarantineRoot, $"StartupDisabled_{DateTime.Now:yyyyMMdd_HHmmss}_{SafeName(Path.GetFileName(item.Path))}");
                File.WriteAllText(target + ".meta", item.Path + Environment.NewLine);
                File.Move(item.Path, target);
            }

            Log($"Disabled startup item: {item.Name}");
            RefreshLogList();
            LoadStartupEntries();
            MessageBox.Show($"Disabled startup entry: {item.Name}", "Startup Item Disabled", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not disable startup item: {ex.Message}", "Disable Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // These actions delegate to Microsoft's signed Windows tools. Nova does not download or install unverified drivers.
    private void WindowsUpdate_Click(object sender, RoutedEventArgs e) => OpenUri("ms-settings:windowsupdate");
    private void OptionalUpdates_Click(object sender, RoutedEventArgs e) => OpenUri("ms-settings:windowsupdate-optionalupdates");
    private void DeviceManager_Click(object sender, RoutedEventArgs e) => StartTool("devmgmt.msc");
    private void SystemInformation_Click(object sender, RoutedEventArgs e) => StartTool("msinfo32.exe");
    private static void OpenUri(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Windows Tool Failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private static void StartTool(string file)
    {
        try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Windows Tool Failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void UpdateSystemMetrics()
    {
        var cpu = Counter("Processor Information", "% Processor Utility", "_Total");
        var memory = Counter("Memory", "% Committed Bytes In Use");
        var disk = GetDisk();

        CpuValue.Text = $"{cpu:F0}%";
        MemoryValue.Text = $"{memory:F0}%";
        DiskValue.Text = $"{disk:F0}%";
        NetworkValue.Text = "0 MB/s";

        StatusText.Text = cpu < 80 && memory < 80 && disk < 85 ? "System Healthy" : "Needs Attention";
    }

    private static double Counter(string category, string name, string instance)
    {
        try { using var c = new PerformanceCounter(category, name, instance); return c.NextValue(); }
        catch { return 0; }
    }

    private static double GetDisk()
    {
        try
        {
            var drives = DriveInfo.GetDrives().Where(x => x.IsReady && x.DriveType == DriveType.Fixed).ToList();
            var total = drives.Sum(x => x.TotalSize);
            if (total == 0) return 0;
            var used = drives.Sum(x => x.TotalSize - x.AvailableFreeSpace);
            return used * 100d / total;
        }
        catch { return 0; }
    }

    private void LoadDriveReports()
    {
        DriveReports.Clear();
        foreach (var d in DriveInfo.GetDrives().Where(x => x.IsReady && x.DriveType == DriveType.Fixed))
        {
            var used = d.TotalSize - d.AvailableFreeSpace;
            var percent = d.TotalSize == 0 ? 0 : used * 100d / d.TotalSize;
            DriveReports.Add(new DriveReport(d.Name, percent, d.AvailableFreeSpace, d.TotalSize));
        }

        DriveList.ItemsSource = DriveReports;
    }

    private void LoadStartupEntries()
    {
        StartupEntries.Clear();
        foreach (var x in RegistryEntries()) StartupEntries.Add(x);
        foreach (var file in Directory.GetFiles(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "*.*", SearchOption.TopDirectoryOnly))
        {
            StartupEntries.Add(new StartupEntry(Path.GetFileName(file), "Startup Folder", file));
        }
        StartupList.ItemsSource = StartupEntries;
    }

    private static IEnumerable<StartupEntry> RegistryEntries()
    {
        foreach (var spec in new[]
        {
            (RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
            (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run")
        })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(spec.Item1, RegistryView.Default);
                using var key = baseKey.OpenSubKey(spec.Item2);
                if (key == null) continue;
                foreach (var name in key.GetValueNames())
                {
                    var value = key.GetValue(name)?.ToString() ?? string.Empty;
                    yield return new StartupEntry(name, "Run Registry", value);
                }
            }
            catch { }
        }
    }

    private void LoadLargeFiles()
    {
        LargeFiles.Clear();
        foreach (var d in DriveInfo.GetDrives().Where(x => x.IsReady && x.DriveType == DriveType.Fixed))
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(d.RootDirectory.FullName, "*", SearchOption.TopDirectoryOnly).Take(5000))
                {
                    try
                    {
                        var info = new FileInfo(f);
                        LargeFiles.Add(new LargeFileItem(info.FullName, info.Name, info.Length, info.DirectoryName ?? string.Empty));
                    }
                    catch { }
                }
            }
            catch { }
        }

        CleanupList.ItemsSource = LargeFiles.OrderByDescending(x => x.Size).Take(10).ToList();
    }

    private static List<CleanupItem> GetTempFiles()
    {
        var result = new List<CleanupItem>();
        foreach (var root in new[] { Path.GetTempPath(), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp") }.Distinct().Where(Directory.Exists))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Take(5000))
                {
                    try
                    {
                        var info = new FileInfo(file);
                        result.Add(new CleanupItem(file, info.Name, info.Length));
                    }
                    catch { }
                }
            }
            catch { }
        }

        return result.OrderByDescending(x => x.Size).Take(20).ToList();
    }

    private void RefreshQuarantineList()
    {
        QuarantineEntries.Clear();
        foreach (var candidate in Directory.GetFiles(_quarantineRoot, "*", SearchOption.AllDirectories))
        {
            if (candidate.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
            var metadata = candidate + ".meta";
            var original = File.Exists(metadata) ? File.ReadAllText(metadata).Trim() : candidate;
            QuarantineEntries.Add(new QuarantineEntry(Path.GetFileName(candidate), candidate, original, DateTime.Now));
        }
        QuarantineList.ItemsSource = QuarantineEntries;
    }

    private void RefreshLogList()
    {
        LogEntries.Clear();
        if (File.Exists(_logPath))
        {
            foreach (var line in File.ReadAllLines(_logPath).TakeLast(20))
            {
                LogEntries.Add(line);
            }
        }
        LogList.ItemsSource = LogEntries;
    }

    private void Log(string text)
    {
        try { File.AppendAllText(_logPath, $"[{DateTime.Now:O}] {text}{Environment.NewLine}"); }
        catch { }
    }

    private static string SafeName(string text)
    {
        foreach (var ch in Path.GetInvalidFileNameChars()) text = text.Replace(ch, '_');
        return text;
    }

    private static string Bytes(long n)
    {
        string[] unit = { "B", "KB", "MB", "GB", "TB" };
        var i = 0;
        double value = n;
        while (value >= 1024 && i < unit.Length - 1)
        {
            value /= 1024;
            i++;
        }

        return $"{value:0.##} {unit[i]}";
    }

    private static class NativeMethods
    {
        [DllImport("Shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHEmptyRecycleBin(IntPtr hwnd, string? root, uint flags);
    }
}

public record DriveReport(string Name, double UsedPercent, long FreeBytes, long TotalBytes)
{
    public string UsedPercentLabel => $"{UsedPercent:F0}%";
    public string FreeSpaceLabel => Format(FreeBytes);
    public string TotalSpaceLabel => Format(TotalBytes);
    private static string Format(long value) => $"{value / 1073741824d:0.##} GB";
}

public record StartupEntry(string Name, string Source, string Path);
public record CleanupItem(string Path, string Name, long Size);
public record LargeFileItem(string Path, string Name, long Size, string DirectoryName)
{
    public string SizeLabel => $"{Size / 1048576d:0.##} MB";
}
public record QuarantineEntry(string Name, string QuarantinedPath, string OriginalPath, DateTime Timestamp);




























































































































































































































































































































































































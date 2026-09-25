using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
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
        LoadStartupEntries(); LoadDriveReports(); LoadLargeFiles(); UpdateSystemMetrics(); _timer.Start();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) { LoadStartupEntries(); LoadDriveReports(); LoadLargeFiles(); UpdateSystemMetrics(); }
    private void CleanupTempButton_Click(object sender, RoutedEventArgs e) => BeginCleanup("temporary files", "Temporary", GetTempFiles());
    private void CleanupLargeButton_Click(object sender, RoutedEventArgs e) => BeginCleanup("large files", "Large", LargeFiles.Select(x => new CleanupItem(x.Path, x.Name, x.Size)).ToList());

    private void CleanupRecycleButton_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Empty the Windows Recycle Bin? This cannot be undone.", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { var result = NativeMethods.SHEmptyRecycleBin(IntPtr.Zero, null, 0); if (result != 0) throw new InvalidOperationException($"Windows returned code {result}."); StatusText.Text = "Recycle Bin emptied"; Log("Recycle Bin emptied."); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Recycle Bin Cleanup Failed", MessageBoxButton.OK, MessageBoxImage.Error); Log(ex.ToString()); }
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
            if (candidates.Count == 0) { Dispatcher.Invoke(() => MessageBox.Show($"No {description} were found.", "Nothing to Clean")); return; }
            var preview = string.Join(Environment.NewLine, candidates.Take(5).Select(x => $"- {x.Name} ({Bytes(x.Size)})"));
            var answer = Dispatcher.Invoke(() => MessageBox.Show($"Move {candidates.Count} {description} to Nova quarantine?\n\n{preview}\n\nFiles remain recoverable there.", "Confirm Cleanup", MessageBoxButton.YesNo, MessageBoxImage.Warning));
            if (answer != MessageBoxResult.Yes) return;
            var folder = Path.Combine(_quarantineRoot, $"{kind}_{DateTime.Now:yyyyMMdd_HHmmss}"); Directory.CreateDirectory(folder);
            var moved = 0;
            foreach (var item in candidates) try { if (File.Exists(item.Path)) { File.Move(item.Path, Path.Combine(folder, SafeName(item.Name + "_" + Guid.NewGuid().ToString("N")))); moved++; } } catch (Exception ex) { Log($"Skipped {item.Path}: {ex.Message}"); }
            Log($"Quarantined {moved} {description}.");
            Dispatcher.Invoke(() => { MessageBox.Show($"{moved} item(s) moved to quarantine.", "Cleanup Complete"); LoadDriveReports(); LoadLargeFiles(); });
        }
        finally { Interlocked.Exchange(ref _cleanupRunning, 0); }
    }

    private void OpenQuarantineFolder_Click(object sender, RoutedEventArgs e) { OpenFolder(_quarantineRoot); }
    private void OpenLogFolder_Click(object sender, RoutedEventArgs e) { OpenFolder(Path.GetDirectoryName(_logPath)!); }
    private static void OpenFolder(string path) { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }

    // These actions delegate to Microsoft's signed Windows tools. Nova does not download or install unverified drivers.
    private void WindowsUpdate_Click(object sender, RoutedEventArgs e) => OpenUri("ms-settings:windowsupdate");
    private void OptionalUpdates_Click(object sender, RoutedEventArgs e) => OpenUri("ms-settings:windowsupdate-optionalupdates");
    private void DeviceManager_Click(object sender, RoutedEventArgs e) => StartTool("devmgmt.msc");
    private void SystemInformation_Click(object sender, RoutedEventArgs e) => StartTool("msinfo32.exe");
    private static void OpenUri(string uri) { try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show(ex.Message, "Windows Tool Failed", MessageBoxButton.OK, MessageBoxImage.Error); } }
    private static void StartTool(string file) { try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show(ex.Message, "Windows Tool Failed", MessageBoxButton.OK, MessageBoxImage.Error); } }

    private void UpdateSystemMetrics()
    {
        var cpu = Counter("Processor Information", "% Processor Utility", "_Total"); var memory = Counter("Memory", "% Committed Bytes In Use"); var disk = GetDisk();
        CpuValue.Text = $"{cpu:F0}%"; MemoryValue.Text = $"{memory:F0}%"; DiskValue.Text = $"{disk:F0}%"; NetworkValue.Text = "0 MB/s";
        StatusText.Text = cpu < 80 && memory < 80 && disk < 85 ? "System Healthy" : "Needs Attention";
    }
    private static double Counter(string category, string name, string instance) { try { using var c = new PerformanceCounter(category, name, instance); return c.NextValue(); } catch { return 0; } }
    private static double GetDisk() { try { var d = DriveInfo.GetDrives().Where(x => x.IsReady && x.DriveType == DriveType.Fixed).ToList(); var total = d.Sum(x => x.TotalSize); return total == 0 ? 0 : (total - d.Sum(x => x.AvailableFreeSpace)) * 100d / total; } catch { return 0; } }

    private void LoadDriveReports() { DriveReports.Clear(); foreach (var d in DriveInfo.GetDrives().Where(x => x.IsReady && x.DriveType == DriveType.Fixed)) { var used = d.TotalSize - d.AvailableFreeSpace; DriveReports.Add(new DriveReport(d.Name, d.TotalSize == 0 ? 0 : used * 100d / d.TotalSize, d.AvailableFreeSpace, d.TotalSize)); } DriveList.ItemsSource = DriveReports; }
    private void LoadStartupEntries() { StartupEntries.Clear(); foreach (var x in RegistryEntries()) StartupEntries.Add(x); StartupList.ItemsSource = StartupEntries; }
    private static IEnumerable<StartupEntry> RegistryEntries() { foreach (var spec in new[] { (RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"), (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run") }) { try { using var b = RegistryKey.OpenBaseKey(spec.Item1, RegistryView.Default); using var k = b.OpenSubKey(spec.Item2); if (k != null) foreach (var n in k.GetValueNames()) yield return new StartupEntry(n, "Run Registry", k.GetValue(n)?.ToString() ?? ""); } catch { } } }
    private void LoadLargeFiles() { LargeFiles.Clear(); foreach (var d in DriveInfo.GetDrives().Where(x => x.IsReady && x.DriveType == DriveType.Fixed)) { try { foreach (var f in Directory.EnumerateFiles(d.RootDirectory.FullName, "*", SearchOption.TopDirectoryOnly).Select(x => new FileInfo(x)).OrderByDescending(x => x.Length).Take(10)) LargeFiles.Add(new LargeFileItem(f.FullName, f.Name, f.Length, f.DirectoryName ?? "")); } catch { } } CleanupList.ItemsSource = LargeFiles.OrderByDescending(x => x.Size).Take(10).ToList(); }
    private static List<CleanupItem> GetTempFiles() { var result = new List<CleanupItem>(); foreach (var root in new[] { Path.GetTempPath(), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp") }.Distinct().Where(Directory.Exists)) try { foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Take(5000)) try { var i = new FileInfo(f); result.Add(new CleanupItem(f, i.Name, i.Length)); } catch { } } catch { } return result.OrderByDescending(x => x.Size).Take(20).ToList(); }
    private void Log(string text) { try { File.AppendAllText(_logPath, $"[{DateTime.Now:O}] {text}{Environment.NewLine}"); } catch { } }
    private static string SafeName(string text) { foreach (var c in Path.GetInvalidFileNameChars()) text = text.Replace(c, '_'); return text; }
    private static string Bytes(long n) { string[] u = { "B", "KB", "MB", "GB", "TB" }; var i = 0; double x = n; while (x >= 1024 && i < u.Length - 1) { x /= 1024; i++; } return $"{x:0.##} {u[i]}"; }
    private static class NativeMethods { [DllImport("Shell32.dll", CharSet = CharSet.Unicode)] public static extern int SHEmptyRecycleBin(IntPtr hwnd, string? root, uint flags); }
}

public record DriveReport(string Name, double UsedPercent, long FreeBytes, long TotalBytes) { public string UsedPercentLabel => $"{UsedPercent:F0}%"; public string FreeSpaceLabel => Format(FreeBytes); public string TotalSpaceLabel => Format(TotalBytes); private static string Format(long n) => $"{n / 1073741824d:0.##} GB"; }
public record StartupEntry(string Name, string Source, string Path);
public record CleanupItem(string Path, string Name, long Size);
public record LargeFileItem(string Path, string Name, long Size, string DirectoryName) { public string SizeLabel => $"{Size / 1048576d:0.##} MB"; }

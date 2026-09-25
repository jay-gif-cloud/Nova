using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Nova
{
    public partial class MainWindow : Window
    {
        private readonly DispatcherTimer _timer;
        private readonly string _quarantineRoot;
        private readonly string _logPath;
        private readonly List<PerformanceCounter> _cpuCounters = new();
        private readonly List<PerformanceCounter> _networkCounters = new();
        private bool _cleanupInProgress;

        public ObservableCollection<DriveReport> DriveReports { get; } = new();
        public ObservableCollection<StartupEntry> StartupEntries { get; } = new();
        public ObservableCollection<LargeFileItem> LargeFiles { get; } = new();

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;

            var appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nova");
            Directory.CreateDirectory(appDataPath);
            _quarantineRoot = Path.Combine(appDataPath, "Quarantine");
            _logPath = Path.Combine(appDataPath, "nova_log.txt");
            Directory.CreateDirectory(_quarantineRoot);

            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _timer.Tick += Timer_Tick;

            LoadStartupEntries();
            LoadDriveReports();
            LoadLargeFiles();
            _timer.Start();
            UpdateSystemMetrics();
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            UpdateSystemMetrics();
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            if (_cleanupInProgress)
            {
                return;
            }

            LoadDriveReports();
            LoadLargeFiles();
            LoadStartupEntries();
            UpdateSystemMetrics();
        }

        private void CleanupTempButton_Click(object sender, RoutedEventArgs e)
        {
            if (_cleanupInProgress)
            {
                return;
            }

            Task.Run(async () =>
            {
                var tempFiles = GetTempFiles();
                if (tempFiles.Count == 0)
                {
                    Dispatcher.Invoke(() =>
                    {
                        MessageBox.Show("No temporary files were found to clean.", "Nothing to Clean", MessageBoxButton.OK, MessageBoxImage.Information);
                    });
                    return;
                }

                await SafeCleanupAsync("temporary files", tempFiles, "Temporary");
            });
        }

        private void CleanupRecycleButton_Click(object sender, RoutedEventArgs e)
        {
            if (_cleanupInProgress)
            {
                return;
            }

            var result = MessageBox.Show(
                "This will empty the Recycle Bin for all drives. Files can be recovered from Windows Recycle Bin only if you restore them before final deletion. Continue?",
                "Confirm Recycle Bin Cleanup",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                var error = NativeMethods.SHEmptyRecycleBin(IntPtr.Zero, null, 0);
                if (error == 0)
                {
                    LogAction("Recycle Bin emptied successfully.");
                    StatusText.Text = "Recycle Bin emptied";
                    StatusText.Foreground = System.Windows.Media.Brushes.White;
                }
                else
                {
                    throw new InvalidOperationException($"Shell cleanup returned code {error}.");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Unable to empty Recycle Bin: {ex.Message}", "Cleanup Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                LogAction($"Recycle Bin cleanup failed: {ex.Message}");
            }
        }

        private void CleanupLargeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_cleanupInProgress)
            {
                return;
            }

            if (LargeFiles.Count == 0)
            {
                MessageBox.Show("No large files were discovered yet.", "No Cleanup Items", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var summary = string.Join(Environment.NewLine, LargeFiles.Take(5).Select(f => $"- {f.Name} ({f.SizeLabel})"));
            var result = MessageBox.Show($"Review the following large files before removal?\n\n{summary}", "Large File Review", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (result == MessageBoxResult.Yes)
            {
                Task.Run(async () =>
                {
                    var files = LargeFiles.Select(f => new CleanupItem(f.Path, f.Name, f.Size)).ToList();
                    await SafeCleanupAsync("large files", files, "Large");
                });
            }
        }

        private void OpenQuarantineFolder_Click(object sender, RoutedEventArgs e)
        {
            Directory.CreateDirectory(_quarantineRoot);
            OpenFolder(_quarantineRoot);
        }

        private void OpenStartupFolder_Click(object sender, RoutedEventArgs e)
        {
            var startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            if (string.IsNullOrWhiteSpace(startupFolder) || !Directory.Exists(startupFolder))
            {
                MessageBox.Show("The startup folder could not be found.", "Folder Not Found", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            OpenFolder(startupFolder);
        }

        private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
        {
            var logFolder = Path.GetDirectoryName(_logPath);
            if (string.IsNullOrWhiteSpace(logFolder))
            {
                MessageBox.Show("The log folder could not be resolved.", "Folder Not Found", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Directory.CreateDirectory(logFolder);
            OpenFolder(logFolder);
        }

        private static void OpenFolder(string folderPath)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{folderPath}\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Unable to open folder: {ex.Message}", "Open Folder Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UpdateSystemMetrics()
        {
            var cpu = GetCpuUsage();
            var memory = GetMemoryUsage();
            var disk = GetOverallDiskUsage();
            var network = GetNetworkUsageMbPerSecond();

            CpuValue.Text = $"{cpu:F0}%";
            MemoryValue.Text = $"{memory:F0}%";
            DiskValue.Text = $"{disk:F0}%";
            NetworkValue.Text = $"{network:F1} MB/s";

            var isHealthy = cpu < 80 && memory < 80 && disk < 85;
            StatusText.Text = isHealthy ? "System Healthy" : "Needs Attention";
            StatusText.Foreground = isHealthy ? System.Windows.Media.Brushes.LightGreen : System.Windows.Media.Brushes.Orange;
        }

        private void LoadDriveReports()
        {
            DriveReports.Clear();
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
            {
                var total = drive.TotalSize;
                var free = drive.AvailableFreeSpace;
                var used = total - free;
                var percent = total > 0 ? (double)used / total * 100 : 0;
                DriveReports.Add(new DriveReport(
                    drive.Name,
                    percent,
                    used,
                    free,
                    total));
            }
            DriveList.ItemsSource = DriveReports;
        }

        private void LoadStartupEntries()
        {
            StartupEntries.Clear();
            foreach (var entry in EnumerateStartupEntries())
            {
                StartupEntries.Add(entry);
            }
            StartupList.ItemsSource = StartupEntries;
        }

        private void LoadLargeFiles()
        {
            LargeFiles.Clear();
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
            {
                var candidates = GetLargestFilesForDrive(drive.RootDirectory.FullName, maxItems: 18, maxDepth: 4, maxFilesScanned: 1500);
                foreach (var item in candidates)
                {
                    LargeFiles.Add(item);
                }
            }

            var ordered = LargeFiles.OrderByDescending(f => f.Size).Take(10).ToList();
            LargeFiles.Clear();
            foreach (var item in ordered)
            {
                LargeFiles.Add(item);
            }
            CleanupList.ItemsSource = LargeFiles;
        }

        private List<StartupEntry> EnumerateStartupEntries()
        {
            var entries = new List<StartupEntry>();

            var runKeys = new[]
            {
                new { Root = RegistryHive.CurrentUser, Path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run" },
                new { Root = RegistryHive.LocalMachine, Path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run" },
                new { Root = RegistryHive.CurrentUser, Path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce" },
            };

            foreach (var keyInfo in runKeys)
            {
                using var reg = RegistryKey.OpenBaseKey(keyInfo.Root, RegistryView.Default);
                using var key = reg.OpenSubKey(keyInfo.Path, false);
                if (key == null)
                {
                    continue;
                }

                foreach (var valueName in key.GetValueNames())
                {
                    var value = key.GetValue(valueName, "");
                    if (value is string str && !string.IsNullOrWhiteSpace(str))
                    {
                        entries.Add(new StartupEntry(valueName, "Run Registry", str));
                    }
                }
            }

            var startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            if (!string.IsNullOrWhiteSpace(startupFolder) && Directory.Exists(startupFolder))
            {
                foreach (var file in Directory.GetFiles(startupFolder, "*.*", SearchOption.TopDirectoryOnly))
                {
                    entries.Add(new StartupEntry(Path.GetFileName(file), "Startup Folder", file));
                }
            }

            return entries.DistinctBy(e => e.Name + e.Source + e.Path).OrderBy(e => e.Name).ToList();
        }

        private List<CleanupItem> GetTempFiles()
        {
            var tempPath = Path.GetTempPath();
            var appDataTemp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp");
            var roots = new[] { tempPath, appDataTemp };
            var files = new List<CleanupItem>();

            foreach (var root in roots.Where(Directory.Exists))
            {
                foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var info = new FileInfo(file);
                        files.Add(new CleanupItem(file, info.Name, info.Length));
                    }
                    catch
                    {
                        // ignore inaccessible files
                    }
                }
            }

            return files
                .OrderByDescending(f => f.Size)
                .Take(20)
                .ToList();
        }

        private List<LargeFileItem> GetLargestFilesForDrive(string rootPath, int maxItems, int maxDepth, int maxFilesScanned)
        {
            var results = new List<LargeFileItem>();
            var queue = new Queue<(string Path, int Depth)>();
            queue.Enqueue((rootPath, 0));
            var scanned = 0;

            while (queue.Count > 0 && scanned < maxFilesScanned)
            {
                var item = queue.Dequeue();
                if (item.Depth > maxDepth)
                {
                    continue;
                }

                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(item.Path))
                    {
                        try
                        {
                            queue.Enqueue((dir, item.Depth + 1));
                        }
                        catch
                        {
                            // ignore inaccessible directories
                        }
                    }

                    foreach (var file in Directory.EnumerateFiles(item.Path))
                    {
                        scanned++;
                        try
                        {
                            var info = new FileInfo(file);
                            results.Add(new LargeFileItem(file, info.Name, info.Length, info.DirectoryName ?? string.Empty));
                        }
                        catch
                        {
                            // ignore inaccessible files
                        }
                    }
                }
                catch
                {
                    // ignore inaccessible paths
                }
            }

            return results.OrderByDescending(r => r.Size).Take(maxItems).ToList();
        }

        private async Task SafeCleanupAsync(string description, List<CleanupItem> candidates, string cleanupType)
        {
            if (_cleanupInProgress)
            {
                return;
            }

            _cleanupInProgress = true;

            try
            {
                if (candidates.Count == 0)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        MessageBox.Show($"No {description} were found to clean.", "Nothing to Clean", MessageBoxButton.OK, MessageBoxImage.Information);
                    });
                    return;
                }

                var totalSize = candidates.Sum(c => c.Size);
                var summary = string.Join(Environment.NewLine, candidates.Take(5).Select(c => $"- {c.Name} ({FormatBytes(c.Size)})"));

                var result = await Dispatcher.InvokeAsync(() => MessageBox.Show(
                    $"This action will move {candidates.Count} {description} to a safe quarantine folder before removal.\n\nEstimated size: {FormatBytes(totalSize)}\n\nPreview:\n{summary}\n\nContinue?",
                    $"Confirm {cleanupType} Cleanup",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning));

                if (result != MessageBoxResult.Yes)
                {
                    return;
                }

                var quarantineFolder = Path.Combine(_quarantineRoot, cleanupType + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                Directory.CreateDirectory(quarantineFolder);

                var moved = 0;
                foreach (var item in candidates)
                {
                    try
                    {
                        var target = Path.Combine(quarantineFolder, MakeSafeFileName(item.Name + "_" + Guid.NewGuid().ToString("N")));
                        if (File.Exists(item.Path))
                        {
                            File.Move(item.Path, target);
                        }
                        else if (Directory.Exists(item.Path))
                        {
                            Directory.Move(item.Path, target);
                        }
                        moved++;
                    }
                    catch (Exception ex)
                    {
                        LogAction($"Failed to quarantine {item.Path}: {ex.Message}");
                    }
                }

                LogAction($"Moved {moved} {description} to quarantine folder: {quarantineFolder}.");

                await Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show($"{moved} items were safely quarantined. You can restore them from the Nova quarantine directory if needed.", "Cleanup Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                    LoadLargeFiles();
                    LoadDriveReports();
                });
            }
            finally
            {
                _cleanupInProgress = false;
            }
        }

        private void LogAction(string message)
        {
            try
            {
                File.AppendAllText(_logPath, $"[{DateTime.Now:O}] {message}{Environment.NewLine}");
            }
            catch
            {
                // ignore logging errors
            }
        }

        private static string MakeSafeFileName(string value)
        {
            foreach (var invalidChar in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(invalidChar, '_');
            }

            return value.Trim();
        }

        private static string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }

            return string.Format(CultureInfo.InvariantCulture, "{0:0.##} {1}", len, sizes[order]);
        }

        private static double GetCpuUsage()
        {
            try
            {
                using var cpuCounter = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total");
                return cpuCounter.NextValue();
            }
            catch
            {
                return 0;
            }
        }

        private static double GetMemoryUsage()
        {
            try
            {
                using var memCounter = new PerformanceCounter("Memory", "% Committed Bytes In Use");
                return memCounter.NextValue();
            }
            catch
            {
                return 0;
            }
        }

        private static double GetOverallDiskUsage()
        {
            try
            {
                var drives = DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed).ToList();
                if (drives.Count == 0)
                {
                    return 0;
                }

                var total = drives.Sum(d => d.TotalSize);
                var free = drives.Sum(d => d.AvailableFreeSpace);
                var used = total - free;
                return total > 0 ? (used / (double)total) * 100 : 0;
            }
            catch
            {
                return 0;
            }
        }

        private static double GetNetworkUsageMbPerSecond()
        {
            try
            {
                var category = new PerformanceCounterCategory("Network Interface");
                var instance = category.GetInstanceNames().FirstOrDefault();
                if (instance == null)
                {
                    return 0;
                }

                using var counter = new PerformanceCounter("Network Interface", "Bytes Total/sec", instance);
                var value = counter.NextValue();
                return value / (1024.0 * 1024.0);
            }
            catch
            {
                return 0;
            }
        }

        private static class NativeMethods
        {
            [DllImport("Shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);
        }
    }

    public record DriveReport(string Name, double UsedPercent, long UsedBytes, long FreeBytes, long TotalBytes)
    {
        public string UsedPercentLabel => $"{UsedPercent:F0}%";
        public string FreeSpaceLabel => FormatBytes(FreeBytes);
        public string TotalSpaceLabel => FormatBytes(TotalBytes);

        private static string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }

            return string.Format(CultureInfo.InvariantCulture, "{0:0.##} {1}", len, sizes[order]);
        }
    }

    public record StartupEntry(string Name, string Source, string Path);

    public record CleanupItem(string Path, string Name, long Size)
    {
        public string SizeLabel => FormatBytes(Size);

        private static string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }

            return string.Format(CultureInfo.InvariantCulture, "{0:0.##} {1}", len, sizes[order]);
        }
    }

    public record LargeFileItem(string Path, string Name, long Size, string DirectoryName)
    {
        public string SizeLabel => FormatBytes(Size);

        private static string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }

            return string.Format(CultureInfo.InvariantCulture, "{0:0.##} {1}", len, sizes[order]);
        }
    }
}

using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace LanFileTransfer;

public sealed partial class MainWindow : Window
{
    private readonly LanTransferService _service = new LanTransferService();
    private readonly SystemMigrationService _migrationService = new SystemMigrationService();
    private readonly List<string> _selectedPaths = new List<string>();
    private readonly List<SelectableMigrationOption> _environmentOptions = new List<SelectableMigrationOption>();
    private readonly List<SelectableMigrationOption> _registryOptions = new List<SelectableMigrationOption>();
    private readonly List<SelectableMigrationOption> _startMenuOptions = new List<SelectableMigrationOption>();
    private readonly object _logLock = new object();
    private readonly List<string> _pendingLogs = new List<string>();

    private CancellationTokenSource? _taskCts;
    private DispatcherTimer? _uiTimer;
    private TransferProgress _latestProgress;
    private long _lastSpeedBytes;
    private DateTime _lastSpeedTime;
    private string _speedText = "0 B/s";

    public MainWindow()
    {
        InitializeComponent();
        InitDefaults();
        BindEvents();
        StartUiTimer();
        RefreshIpInfo();
        RefreshEnvironmentList();
        RefreshRegistryList();
        EnsureDirectIp();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _taskCts?.Cancel();
        base.OnClosing(e);
    }

    private void InitDefaults()
    {
        RecvOutBox.Text = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        RecvPortBox.Value = LanTransferService.DefaultPort;
        SendAllPortBox.Value = LanTransferService.DefaultPort;
        SendSelectedPortBox.Value = LanTransferService.DefaultPort;
        SendAllParallelBox.Value = LanTransferService.DefaultParallelConnections;
        SendSelectedParallelBox.Value = LanTransferService.DefaultParallelConnections;
        AppScanDirBox.Text = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    }

    private void BindEvents()
    {
        ChooseRecvOutButton.Click += async (_, _) => await ChooseFolderAsync(RecvOutBox);
        ChooseSendAllDirButton.Click += async (_, _) => await ChooseFolderAsync(SendAllDirBox);
        DiscoverSendAllButton.Click += async (_, _) => await DiscoverReceiverAsync(SendAllHostBox, GetPort(SendAllPortBox));
        DiscoverSendSelectedButton.Click += async (_, _) => await DiscoverReceiverAsync(SendSelectedHostBox, GetPort(SendSelectedPortBox));
        SpeedTestAllButton.Click += async (_, _) => await StartSpeedTestAsync(SendAllHostBox, GetPort(SendAllPortBox), GetParallel(SendAllParallelBox));
        SpeedTestSelectedButton.Click += async (_, _) => await StartSpeedTestAsync(SendSelectedHostBox, GetPort(SendSelectedPortBox), GetParallel(SendSelectedParallelBox));
        StartRecvButton.Click += async (_, _) => await StartReceiveAsync();
        StartSendAllButton.Click += async (_, _) => await StartSendAllAsync();
        StartSendSelectedButton.Click += async (_, _) => await StartSendSelectedAsync();
        AddFilesButton.Click += async (_, _) => await AddFilesAsync();
        AddFolderButton.Click += async (_, _) => await AddFolderAsync();
        RemoveSelectedButton.Click += (_, _) => RemoveSelectedItems();
        ClearSelectedButton.Click += (_, _) => ClearSelectedItems();
        ChooseMigrationSaveButton.Click += async (_, _) => await ChooseMigrationSavePathAsync();
        ChooseMigrationOpenButton.Click += async (_, _) => await ChooseMigrationOpenPathAsync();
        RefreshEnvListButton.Click += (_, _) => RefreshEnvironmentList();
        IncludeMachineEnvBox.Click += (_, _) => RefreshEnvironmentList();
        SelectAllEnvButton.Click += (_, _) => SelectAllListItems(EnvironmentListBox);
        ClearEnvButton.Click += (_, _) => SetAllOptions(_environmentOptions, false, EnvironmentListBox);
        RefreshRegistryListButton.Click += (_, _) => RefreshRegistryList();
        SelectAllRegistryButton.Click += (_, _) => SelectAllListItems(RegistrySoftwareListBox);
        ClearRegistryButton.Click += (_, _) => SetAllOptions(_registryOptions, false, RegistrySoftwareListBox);
        ExportMigrationButton.Click += async (_, _) => await ExportMigrationPackageAsync();
        ImportMigrationButton.Click += async (_, _) => await ImportMigrationPackageAsync();
        ChooseAppScanDirButton.Click += async (_, _) => await ChooseFolderAsync(AppScanDirBox);
        ScanAppsButton.Click += async (_, _) => await ScanStartMenuAppsAsync();
        SelectAllAppsButton.Click += (_, _) => SelectAllListItems(StartMenuAppListBox);
        ClearAppsButton.Click += (_, _) => SetAllOptions(_startMenuOptions, false, StartMenuAppListBox);
        AddStartMenuButton.Click += async (_, _) => await AddStartMenuShortcutsAsync();
        OpenStartMenuButton.Click += (_, _) => OpenStartMenuDirectory();
        OpenToolDirButton.Click += (_, _) => OpenToolDirectory();
        StopButton.Click += (_, _) => _taskCts?.Cancel();
    }

    private async Task StartReceiveAsync()
    {
        EnsureDirectIp();
        string outputDir = (RecvOutBox.Text ?? string.Empty).Trim();
        int port = GetPort(RecvPortBox);
        bool overwrite = RecvOverwriteBox.IsChecked == true;

        if (string.IsNullOrWhiteSpace(outputDir))
        {
            await ShowMessageAsync("请选择保存目录");
            return;
        }

        await RunTaskAsync("正在接收", () => _service.ReceiveAsync(outputDir, port, overwrite, AppendLog, UpdateProgress, _taskCts!.Token));
    }

    private async Task StartSendAllAsync()
    {
        string host = (SendAllHostBox.Text ?? string.Empty).Trim();
        string sourceDir = (SendAllDirBox.Text ?? string.Empty).Trim();
        int port = GetPort(SendAllPortBox);
        int parallelConnections = GetParallel(SendAllParallelBox);

        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(sourceDir))
        {
            await ShowMessageAsync("请填写接收端 IP 和源目录");
            return;
        }

        await RunTaskAsync("正在发送全部文件", () => _service.SendAllAsync(host, port, sourceDir, parallelConnections, AppendLog, UpdateProgress, _taskCts!.Token));
    }

    private async Task StartSendSelectedAsync()
    {
        string host = (SendSelectedHostBox.Text ?? string.Empty).Trim();
        int port = GetPort(SendSelectedPortBox);
        int parallelConnections = GetParallel(SendSelectedParallelBox);

        if (string.IsNullOrWhiteSpace(host) || _selectedPaths.Count == 0)
        {
            await ShowMessageAsync("请填写接收端 IP，并选择至少一个文件或文件夹");
            return;
        }

        await RunTaskAsync("正在发送选择的文件", () => _service.SendSelectedAsync(host, port, _selectedPaths, parallelConnections, AppendLog, UpdateProgress, _taskCts!.Token));
    }

    private async Task StartSpeedTestAsync(TextBox hostBox, int port, int parallelConnections)
    {
        string host = (hostBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            await ShowMessageAsync("请先填写接收端 IP，或点击自动识别");
            return;
        }

        const long bytesPerConnection = 128L * 1024 * 1024;
        await RunTaskAsync("正在网络测速", () => _service.RunSpeedTestAsync(host, port, parallelConnections, bytesPerConnection, AppendLog, UpdateProgress, _taskCts!.Token));
    }

    private async Task ExportMigrationPackageAsync()
    {
        string packagePath = (MigrationPackageBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            await ShowMessageAsync("请选择迁移包保存路径");
            return;
        }

        List<string> environmentKeys = GetSelectedOptionKeys(EnvironmentListBox);
        List<string> registryKeys = IncludeUserRegistryBox.IsChecked == true ? GetSelectedOptionKeys(RegistrySoftwareListBox) : new List<string>();
        await RunTaskAsync("正在生成系统迁移包", () => _migrationService.ExportPackageAsync(packagePath, environmentKeys, registryKeys, AppendLog, _taskCts!.Token));
    }

    private async Task ImportMigrationPackageAsync()
    {
        string packagePath = (MigrationPackageBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath))
        {
            await ShowMessageAsync("请选择要导入的迁移包");
            return;
        }

        bool confirmed = await ConfirmAsync("导入环境变量和注册表会修改当前电脑系统设置。建议先备份，确认继续吗？");
        if (!confirmed)
        {
            return;
        }

        bool importMachineEnvironment = IncludeMachineEnvBox.IsChecked == true;
        bool importUserSoftwareRegistry = IncludeUserRegistryBox.IsChecked == true;
        await RunTaskAsync("正在导入系统迁移包", () => _migrationService.ImportPackageAsync(packagePath, importMachineEnvironment, importUserSoftwareRegistry, AppendLog, _taskCts!.Token));
    }

    private async Task ScanStartMenuAppsAsync()
    {
        string rootDir = (AppScanDirBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(rootDir) || !Directory.Exists(rootDir))
        {
            await ShowMessageAsync("请选择已拷贝应用所在目录");
            return;
        }

        await RunTaskAsync("正在扫描应用", () =>
        {
            IReadOnlyList<MigrationOption> options = _migrationService.GetApplicationOptions(rootDir, AppendLog, _taskCts!.Token);
            Dispatcher.UIThread.Post(() =>
            {
                _startMenuOptions.Clear();
                for (int i = 0; i < options.Count; i++)
                {
                    SelectableMigrationOption option = new SelectableMigrationOption(options[i]);
                    option.IsSelected = true;
                    _startMenuOptions.Add(option);
                }

                StartMenuAppListBox.ItemsSource = null;
                StartMenuAppListBox.ItemsSource = _startMenuOptions.ToArray();
                StatusText.Text = "扫描到 " + _startMenuOptions.Count + " 个应用";
            });
            return Task.CompletedTask;
        });
    }

    private async Task AddStartMenuShortcutsAsync()
    {
        List<string> executablePaths = GetSelectedOptionKeys(StartMenuAppListBox);
        if (executablePaths.Count == 0)
        {
            await ShowMessageAsync("请先扫描并勾选要添加到开始菜单的应用");
            return;
        }

        await RunTaskAsync("正在添加开始菜单快捷方式", () => _migrationService.AddStartMenuShortcutsAsync(executablePaths, AppendLog, _taskCts!.Token));
    }

    private async Task DiscoverReceiverAsync(TextBox targetHostBox, int port)
    {
        if (_taskCts != null)
        {
            await ShowMessageAsync("当前已有任务在运行");
            return;
        }

        EnsureDirectIp();
        _taskCts = new CancellationTokenSource();
        SetBusy(true, "正在自动识别接收端");
        ResetProgress();
        try
        {
            string? host = await _service.DiscoverReceiverAsync(port, AppendLog, _taskCts.Token);
            if (!string.IsNullOrWhiteSpace(host))
            {
                targetHostBox.Text = host;
                StatusText.Text = "已识别接收端: " + host;
            }
            else
            {
                StatusText.Text = "未识别到接收端";
                await ShowMessageAsync("未识别到接收端。请先在另一台电脑点击“开始接收”，并允许防火墙访问。");
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已停止";
        }
        catch (Exception ex)
        {
            AppendLog("自动识别失败: " + ex.Message);
            StatusText.Text = "自动识别失败";
            await ShowMessageAsync(ex.Message);
        }
        finally
        {
            _taskCts.Dispose();
            _taskCts = null;
            SetBusy(false, StatusText.Text ?? "就绪");
        }
    }

    private async Task RunTaskAsync(string status, Func<Task> taskFactory)
    {
        if (_taskCts != null)
        {
            await ShowMessageAsync("当前已有任务在运行");
            return;
        }

        _taskCts = new CancellationTokenSource();
        SetBusy(true, status);
        ResetProgress();
        AppendLog("========== " + status + " ==========");

        try
        {
            await Task.Run(taskFactory);
            StatusText.Text = "完成";
            TransferProgressBar.Value = 100;
        }
        catch (OperationCanceledException)
        {
            AppendLog("任务已停止");
            StatusText.Text = "已停止";
        }
        catch (Exception ex)
        {
            AppendLog("失败: " + ex.Message);
            StatusText.Text = "失败";
            await ShowMessageAsync(ex.Message);
        }
        finally
        {
            _taskCts.Dispose();
            _taskCts = null;
            SetBusy(false, StatusText.Text ?? "就绪");
        }
    }

    private void SetBusy(bool busy, string status)
    {
        Tabs.IsEnabled = !busy;
        StopButton.IsEnabled = busy;
        StatusText.Text = status;
    }

    private void AppendLog(string message)
    {
        lock (_logLock)
        {
            if (_pendingLogs.Count < 1000)
            {
                _pendingLogs.Add("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message);
            }
        }
    }

    private void UpdateProgress(TransferProgress progress)
    {
        _latestProgress = progress;
    }

    private void ResetProgress()
    {
        _latestProgress = new TransferProgress(0, 0, "正在准备...");
        _lastSpeedBytes = 0;
        _lastSpeedTime = DateTime.UtcNow;
        _speedText = "0 B/s";
        TransferProgressBar.Value = 0;
        ProgressText.Text = "正在准备...";
    }

    private void StartUiTimer()
    {
        _lastSpeedTime = DateTime.UtcNow;
        _uiTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _uiTimer.Tick += (_, _) =>
        {
            FlushLogs();
            RefreshProgressText();
        };
        _uiTimer.Start();
    }

    private void FlushLogs()
    {
        List<string> logs = new List<string>();
        lock (_logLock)
        {
            if (_pendingLogs.Count == 0)
            {
                return;
            }

            int takeCount = Math.Min(_pendingLogs.Count, 120);
            logs.AddRange(_pendingLogs.GetRange(0, takeCount));
            _pendingLogs.RemoveRange(0, takeCount);
            if (_pendingLogs.Count > 900)
            {
                _pendingLogs.RemoveRange(0, _pendingLogs.Count - 300);
                logs.Add("[日志过多，已省略部分中间日志]");
            }
        }

        LogBox.Text = (LogBox.Text ?? string.Empty) + string.Join(Environment.NewLine, logs) + Environment.NewLine;
        if (LogBox.Text.Length > 200000)
        {
            LogBox.Text = LogBox.Text.Substring(LogBox.Text.Length - 160000);
        }

        LogBox.CaretIndex = LogBox.Text.Length;
    }

    private void RefreshProgressText()
    {
        TransferProgress progress = _latestProgress;
        DateTime now = DateTime.UtcNow;
        double seconds = (now - _lastSpeedTime).TotalSeconds;
        if (seconds >= 1)
        {
            long delta = Math.Max(0, progress.TransferredBytes - _lastSpeedBytes);
            _speedText = LanTransferService.FormatBytes((long)(delta / seconds)) + "/s";
            _lastSpeedBytes = progress.TransferredBytes;
            _lastSpeedTime = now;
        }

        if (progress.TotalBytes <= 0)
        {
            TransferProgressBar.Value = 0;
            ProgressText.Text = (string.IsNullOrWhiteSpace(progress.Message) ? "正在处理..." : progress.Message) + "    速度: " + _speedText;
            return;
        }

        double percent = Math.Min(100, Math.Max(0, progress.TransferredBytes * 100.0 / progress.TotalBytes));
        TransferProgressBar.Value = percent;
        string prefix = string.IsNullOrWhiteSpace(progress.Message) ? string.Empty : progress.Message + "  ";
        ProgressText.Text = prefix + percent.ToString("0.00") + "%  "
            + LanTransferService.FormatBytes(progress.TransferredBytes)
            + " / "
            + LanTransferService.FormatBytes(progress.TotalBytes)
            + "    速度: "
            + _speedText;
    }

    private void RefreshIpInfo()
    {
        IpInfoText.Text = "本机 IPv4: " + string.Join(", ", LanTransferService.GetLocalIPv4Addresses())
            + "    直连网段: 192.168.10.x";
        AppendLog("本机 IPv4: " + string.Join(", ", LanTransferService.GetLocalIPv4Addresses()));
        AppendLog("如果网线直连没有 192.168.10.x，本工具会尝试给无网关的有线网卡自动分配静态 IP");
    }

    private void EnsureDirectIp()
    {
        Task.Run(() =>
        {
            DirectIpConfigurator.EnsureDirectStaticIp(AppendLog);
            DirectIpConfigurator.EnsureFirewallRules(AppendLog);
            Dispatcher.UIThread.Post(RefreshIpInfo);
        });
    }

    private async Task ChooseFolderAsync(TextBox target)
    {
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择文件夹",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            target.Text = GetStoragePath(folders[0]);
        }
    }

    private async Task AddFilesAsync()
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择要发送的文件",
            AllowMultiple = true
        });

        for (int i = 0; i < files.Count; i++)
        {
            AddSelectedPath(GetStoragePath(files[i]));
        }
    }

    private async Task AddFolderAsync()
    {
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择要发送的文件夹",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            AddSelectedPath(GetStoragePath(folders[0]));
        }
    }

    private async Task ChooseMigrationSavePathAsync()
    {
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "保存迁移包",
            SuggestedFileName = "LanFileTransfer-Migration.zip",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Zip 迁移包")
                {
                    Patterns = new[] { "*.zip" }
                }
            }
        });

        if (file != null)
        {
            MigrationPackageBox.Text = GetStoragePath(file);
        }
    }

    private async Task ChooseMigrationOpenPathAsync()
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择迁移包",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Zip 迁移包")
                {
                    Patterns = new[] { "*.zip" }
                }
            }
        });

        if (files.Count > 0)
        {
            MigrationPackageBox.Text = GetStoragePath(files[0]);
        }
    }

    private void AddSelectedPath(string path)
    {
        for (int i = 0; i < _selectedPaths.Count; i++)
        {
            if (string.Equals(_selectedPaths[i], path, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        _selectedPaths.Add(path);
        RefreshSelectedList();
    }

    private void RemoveSelectedItems()
    {
        List<string> selected = new List<string>();
        foreach (object? item in SelectedListBox.SelectedItems ?? Array.Empty<object>())
        {
            if (item != null)
            {
                selected.Add(item.ToString() ?? string.Empty);
            }
        }

        for (int i = 0; i < selected.Count; i++)
        {
            _selectedPaths.Remove(selected[i]);
        }

        RefreshSelectedList();
    }

    private void ClearSelectedItems()
    {
        _selectedPaths.Clear();
        RefreshSelectedList();
    }

    private void RefreshSelectedList()
    {
        SelectedListBox.ItemsSource = null;
        SelectedListBox.ItemsSource = _selectedPaths.ToArray();
    }

    private void RefreshEnvironmentList()
    {
        _environmentOptions.Clear();
        foreach (MigrationOption option in _migrationService.GetEnvironmentOptions(IncludeMachineEnvBox.IsChecked == true))
        {
            _environmentOptions.Add(new SelectableMigrationOption(option));
        }

        EnvironmentListBox.ItemsSource = null;
        EnvironmentListBox.ItemsSource = _environmentOptions.ToArray();
    }

    private void RefreshRegistryList()
    {
        _registryOptions.Clear();
        foreach (MigrationOption option in _migrationService.GetUserSoftwareRegistryOptions())
        {
            _registryOptions.Add(new SelectableMigrationOption(option));
        }

        RegistrySoftwareListBox.ItemsSource = null;
        RegistrySoftwareListBox.ItemsSource = _registryOptions.ToArray();
    }

    private static List<string> GetSelectedOptionKeys(ListBox listBox)
    {
        List<string> keys = new List<string>();
        foreach (object? item in listBox.ItemsSource ?? Array.Empty<object>())
        {
            if (item is SelectableMigrationOption option && option.IsSelected)
            {
                keys.Add(option.Key);
            }
        }

        return keys;
    }

    private static void SelectAllListItems(ListBox listBox)
    {
        if (listBox.ItemsSource == null)
        {
            return;
        }

        foreach (object? item in listBox.ItemsSource)
        {
            if (item is SelectableMigrationOption option)
            {
                option.IsSelected = true;
            }
        }

        RefreshListSource(listBox);
    }

    private static void SetAllOptions(List<SelectableMigrationOption> options, bool selected, ListBox listBox)
    {
        for (int i = 0; i < options.Count; i++)
        {
            options[i].IsSelected = selected;
        }

        RefreshListSource(listBox);
    }

    private static void RefreshListSource(ListBox listBox)
    {
        System.Collections.IEnumerable? source = listBox.ItemsSource;
        listBox.ItemsSource = null;
        listBox.ItemsSource = source;
    }

    private async Task ShowMessageAsync(string message)
    {
        Window dialog = new Window
        {
            Title = "提示",
            Width = 360,
            Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        Button okButton = new Button
        {
            Content = "确定",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            MinWidth = 80
        };
        okButton.Click += (_, _) => dialog.Close();

        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(18),
            Spacing = 18,
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                },
                okButton
            }
        };

        await dialog.ShowDialog(this);
    }

    private async Task<bool> ConfirmAsync(string message)
    {
        Window dialog = new Window
        {
            Title = "确认",
            Width = 430,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        bool result = false;
        Button okButton = new Button
        {
            Content = "继续",
            MinWidth = 80
        };
        Button cancelButton = new Button
        {
            Content = "取消",
            MinWidth = 80
        };
        okButton.Click += (_, _) =>
        {
            result = true;
            dialog.Close();
        };
        cancelButton.Click += (_, _) => dialog.Close();

        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(18),
            Spacing = 18,
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    Spacing = 8,
                    Children = { okButton, cancelButton }
                }
            }
        };

        await dialog.ShowDialog(this);
        return result;
    }

    private static int GetPort(NumericUpDown numeric)
    {
        decimal value = numeric.Value ?? LanTransferService.DefaultPort;
        return (int)value;
    }

    private static int GetParallel(NumericUpDown numeric)
    {
        decimal value = numeric.Value ?? LanTransferService.DefaultParallelConnections;
        return Math.Clamp((int)value, 1, 32);
    }

    private static string GetStoragePath(IStorageItem item)
    {
        Uri uri = item.Path;
        if (uri.IsAbsoluteUri && uri.IsFile)
        {
            return uri.LocalPath;
        }

        string text = uri.OriginalString;
        if (string.IsNullOrWhiteSpace(text))
        {
            text = uri.ToString();
        }

        if (text.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
        {
            text = text.Substring("file:///".Length).Replace('/', Path.DirectorySeparatorChar);
        }
        else if (text.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            text = text.Substring("file://".Length).Replace('/', Path.DirectorySeparatorChar);
        }

        text = Uri.UnescapeDataString(text);
        return text.Replace('/', Path.DirectorySeparatorChar);
    }

    private static void OpenToolDirectory()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = AppContext.BaseDirectory,
            UseShellExecute = true
        });
    }

    private static void OpenStartMenuDirectory()
    {
        string startMenuDir = SystemMigrationService.GetStartMenuProgramsDirectory();
        Directory.CreateDirectory(startMenuDir);
        Process.Start(new ProcessStartInfo
        {
            FileName = startMenuDir,
            UseShellExecute = true
        });
    }
}

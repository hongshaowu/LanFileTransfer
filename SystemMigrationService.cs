using System.Collections;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace LanFileTransfer;

internal sealed class SystemMigrationService
{
    private const string UserEnvFile = "environment-user.json";
    private const string MachineEnvFile = "environment-machine.json";
    private const string RegistryDir = "registry";
    private static readonly string[] SkippedAppDirectoryNames =
    {
        "$RECYCLE.BIN",
        "System Volume Information",
        "Windows",
        "Recovery",
        "Temp",
        "tmp",
        "bin",
        "lib",
        "libs",
        "runtime",
        "runtimes",
        "redist",
        "vc_redist",
        "drivers",
        "plugins",
        "resources",
        "locales"
    };

    private static readonly string[] SkippedExecutableNameParts =
    {
        "agent",
        "bootstrap",
        "bootstrapper",
        "unins",
        "uninstall",
        "setup",
        "install",
        "update",
        "updater",
        "daemon",
        "crash",
        "crashpad",
        "report",
        "reporter",
        "repair",
        "helper",
        "service",
        "broker",
        "watcher",
        "monitor",
        "console",
        "cmd",
        "command",
        "vcredist",
        "redistributable",
        "launcher helper"
    };

    public IReadOnlyList<MigrationOption> GetEnvironmentOptions(bool includeMachine)
    {
        List<MigrationOption> options = new List<MigrationOption>();
        AddEnvironmentOptions(options, EnvironmentVariableTarget.User, "用户");
        if (includeMachine)
        {
            AddEnvironmentOptions(options, EnvironmentVariableTarget.Machine, "机器");
        }

        options.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        return options;
    }

    public IReadOnlyList<MigrationOption> GetUserSoftwareRegistryOptions()
    {
        List<MigrationOption> options = new List<MigrationOption>();
        using RegistryKey? software = Registry.CurrentUser.OpenSubKey("Software");
        if (software == null)
        {
            return options;
        }

        string[] names = software.GetSubKeyNames();
        for (int i = 0; i < names.Length; i++)
        {
            string name = names[i];
            options.Add(new MigrationOption("HKCU\\Software\\" + name, name, "HKCU\\Software\\" + name));
        }

        options.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        return options;
    }

    public IReadOnlyList<MigrationOption> GetApplicationOptions(string rootDir, Action<string>? log, Action<ApplicationScanProgress>? progress, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(rootDir))
        {
            throw new DirectoryNotFoundException("找不到应用目录: " + rootDir);
        }

        return GetApplicationOptions(new[] { rootDir }, log, progress, cancellationToken);
    }

    public IReadOnlyList<MigrationOption> GetCommonApplicationOptions(Action<string>? log, Action<ApplicationScanProgress>? progress, CancellationToken cancellationToken)
    {
        return GetApplicationOptions(GetCommonApplicationDirectories(), log, progress, cancellationToken);
    }

    private IReadOnlyList<MigrationOption> GetApplicationOptions(IReadOnlyList<string> rootDirs, Action<string>? log, Action<ApplicationScanProgress>? progress, CancellationToken cancellationToken)
    {
        List<MigrationOption> options = new List<MigrationOption>();
        List<MigrationOption> fallbackOptions = new List<MigrationOption>();
        HashSet<string> added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> fallbackAdded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Stack<string> pendingDirs = new Stack<string>();
        int scannedDirs = 0;
        int foundExeCount = 0;
        int skippedExeCount = 0;
        int discoveredDirs = 0;
        for (int i = 0; i < rootDirs.Count; i++)
        {
            string rootDir = rootDirs[i];
            if (string.IsNullOrWhiteSpace(rootDir) || !Directory.Exists(rootDir))
            {
                continue;
            }

            pendingDirs.Push(rootDir);
            discoveredDirs++;
            Log(log, "正在扫描应用目录: " + rootDir);
        }

        while (pendingDirs.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string dir = pendingDirs.Pop();
            if (ShouldSkipApplicationDirectory(dir))
            {
                continue;
            }

            try
            {
                scannedDirs++;
                string[] files = Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly);
                MigrationOption? option = TryGetBestExecutableOption(dir, files, added, fallbackAdded, ref foundExeCount, ref skippedExeCount, out bool skipped);
                if (option != null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (skipped)
                    {
                        fallbackOptions.Add(option);
                        fallbackAdded.Add(option.Key);
                    }
                    else
                    {
                        options.Add(option);
                        added.Add(option.Key);
                    }
                }

                string[] childDirs = Directory.GetDirectories(dir, "*", SearchOption.TopDirectoryOnly);
                for (int i = 0; i < childDirs.Length; i++)
                {
                    pendingDirs.Push(childDirs[i]);
                    discoveredDirs++;
                }

                ReportScanProgress(progress, scannedDirs, discoveredDirs, foundExeCount, options.Count, "正在扫描: " + dir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException || ex is PathTooLongException)
            {
                Log(log, "[跳过目录] " + dir + "，原因: " + ex.Message);
                ReportScanProgress(progress, scannedDirs, discoveredDirs, foundExeCount, options.Count, "跳过目录: " + dir);
            }
        }

        if (options.Count == 0 && fallbackOptions.Count > 0)
        {
            Log(log, "按默认过滤规则没有主程序结果，已显示被过滤的 exe 候选，请手动勾选确认");
            options.AddRange(fallbackOptions);
        }

        options.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        Log(log, "应用扫描完成，扫描目录 " + scannedDirs + " 个，发现 exe " + foundExeCount + " 个，过滤 " + skippedExeCount + " 个，显示 " + options.Count + " 个");
        ReportScanProgress(progress, scannedDirs, Math.Max(scannedDirs, discoveredDirs), foundExeCount, options.Count, "扫描完成");
        return options;
    }

    public async Task AddStartMenuShortcutsAsync(IReadOnlyList<string> executablePaths, Action<string>? log, CancellationToken cancellationToken)
    {
        string startMenuDir = GetStartMenuProgramsDirectory();
        Directory.CreateDirectory(startMenuDir);
        Log(log, "开始菜单目录: " + startMenuDir);

        for (int i = 0; i < executablePaths.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string executablePath = executablePaths[i];
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            {
                Log(log, "[跳过应用] 找不到文件: " + executablePath);
                continue;
            }

            string shortcutName = SanitizeFileName(Path.GetFileNameWithoutExtension(executablePath)) + ".lnk";
            string shortcutPath = GetAvailableShortcutPath(startMenuDir, shortcutName);
            await Task.Run(() => CreateShortcut(shortcutPath, executablePath), cancellationToken);
            Log(log, "已添加开始菜单快捷方式: " + Path.GetFileName(shortcutPath));
        }

        Log(log, "开始菜单快捷方式添加完成");
    }

    public static string GetStartMenuProgramsDirectory()
    {
        string path = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        if (!string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft",
            "Windows",
            "Start Menu",
            "Programs");
    }

    public async Task ExportPackageAsync(string packagePath, IReadOnlyList<string> environmentKeys, IReadOnlyList<string> registryKeys, Action<string>? log, CancellationToken cancellationToken)
    {
        string tempDir = CreateTempDirectory();
        try
        {
            Dictionary<string, List<string>> envGroups = SplitEnvironmentKeys(environmentKeys);
            if (envGroups["User"].Count > 0)
            {
                Log(log, "正在导出用户环境变量...");
                await WriteEnvironmentAsync(Path.Combine(tempDir, UserEnvFile), EnvironmentVariableTarget.User, envGroups["User"], cancellationToken);
            }

            if (envGroups["Machine"].Count > 0)
            {
                Log(log, "正在导出机器环境变量...");
                await WriteEnvironmentAsync(Path.Combine(tempDir, MachineEnvFile), EnvironmentVariableTarget.Machine, envGroups["Machine"], cancellationToken);
            }

            if (registryKeys.Count > 0)
            {
                string registryDir = Path.Combine(tempDir, RegistryDir);
                Directory.CreateDirectory(registryDir);
                for (int i = 0; i < registryKeys.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string key = registryKeys[i];
                    if (!key.StartsWith("HKCU\\Software\\", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string fileName = "hkcu-software-" + SanitizeFileName(key.Substring("HKCU\\Software\\".Length)) + ".reg";
                    Log(log, "正在导出注册表: " + key);
                    await RunRegAsync("export", key, Path.Combine(registryDir, fileName), log, cancellationToken);
                }
            }

            string manifestPath = Path.Combine(tempDir, "manifest.txt");
            await File.WriteAllTextAsync(manifestPath, "LanFileTransfer system migration package" + Environment.NewLine + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), cancellationToken);

            if (File.Exists(packagePath))
            {
                File.Delete(packagePath);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(packagePath) ?? ".");
            ZipFile.CreateFromDirectory(tempDir, packagePath, CompressionLevel.Optimal, false);
            Log(log, "迁移包已生成: " + packagePath);
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    public async Task ImportPackageAsync(string packagePath, bool importMachineEnvironment, bool importUserSoftwareRegistry, Action<string>? log, CancellationToken cancellationToken)
    {
        if (!File.Exists(packagePath))
        {
            throw new FileNotFoundException("找不到迁移包", packagePath);
        }

        string tempDir = CreateTempDirectory();
        try
        {
            ZipFile.ExtractToDirectory(packagePath, tempDir);

            string userEnvPath = Path.Combine(tempDir, UserEnvFile);
            if (File.Exists(userEnvPath))
            {
                Log(log, "正在导入用户环境变量...");
                await ApplyEnvironmentAsync(userEnvPath, EnvironmentVariableTarget.User, log, cancellationToken);
            }

            string machineEnvPath = Path.Combine(tempDir, MachineEnvFile);
            if (importMachineEnvironment && File.Exists(machineEnvPath))
            {
                Log(log, "正在导入机器环境变量...");
                await ApplyEnvironmentAsync(machineEnvPath, EnvironmentVariableTarget.Machine, log, cancellationToken);
            }

            string registryDir = Path.Combine(tempDir, RegistryDir);
            if (importUserSoftwareRegistry && Directory.Exists(registryDir))
            {
                string[] regFiles = Directory.GetFiles(registryDir, "*.reg", SearchOption.TopDirectoryOnly);
                for (int i = 0; i < regFiles.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Log(log, "正在导入注册表: " + Path.GetFileName(regFiles[i]));
                    await RunRegAsync("import", null, regFiles[i], log, cancellationToken);
                }
            }

            Log(log, "系统迁移导入完成。部分环境变量可能需要重新登录或重启后生效。");
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    private static void AddEnvironmentOptions(List<MigrationOption> options, EnvironmentVariableTarget target, string scopeText)
    {
        IDictionary variables = Environment.GetEnvironmentVariables(target);
        foreach (DictionaryEntry entry in variables)
        {
            string? key = entry.Key?.ToString();
            if (!string.IsNullOrWhiteSpace(key))
            {
                string scope = target == EnvironmentVariableTarget.Machine ? "Machine" : "User";
                options.Add(new MigrationOption(scope + ":" + key, scopeText + " / " + key, Environment.GetEnvironmentVariable(key, target) ?? string.Empty));
            }
        }
    }

    private static Dictionary<string, List<string>> SplitEnvironmentKeys(IReadOnlyList<string> keys)
    {
        Dictionary<string, List<string>> result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["User"] = new List<string>(),
            ["Machine"] = new List<string>()
        };

        for (int i = 0; i < keys.Count; i++)
        {
            string key = keys[i];
            int index = key.IndexOf(':');
            if (index <= 0 || index >= key.Length - 1)
            {
                continue;
            }

            string scope = key.Substring(0, index);
            string name = key.Substring(index + 1);
            if (result.TryGetValue(scope, out List<string>? list))
            {
                list.Add(name);
            }
        }

        return result;
    }

    private static async Task WriteEnvironmentAsync(string path, EnvironmentVariableTarget target, IReadOnlyList<string> selectedNames, CancellationToken cancellationToken)
    {
        Dictionary<string, string?> data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        IDictionary variables = Environment.GetEnvironmentVariables(target);
        HashSet<string> selected = new HashSet<string>(selectedNames, StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in variables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? key = entry.Key?.ToString();
            if (!string.IsNullOrWhiteSpace(key) && selected.Contains(key))
            {
                data[key] = entry.Value?.ToString();
            }
        }

        string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json, cancellationToken);
    }

    private static async Task ApplyEnvironmentAsync(string path, EnvironmentVariableTarget target, Action<string>? log, CancellationToken cancellationToken)
    {
        string json = await File.ReadAllTextAsync(path, cancellationToken);
        Dictionary<string, string?>? data = JsonSerializer.Deserialize<Dictionary<string, string?>>(json);
        if (data == null)
        {
            return;
        }

        foreach (KeyValuePair<string, string?> pair in data)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value, target);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                Log(log, "[跳过环境变量] " + pair.Key + "，原因: " + ex.Message);
            }
        }
    }

    private static async Task RunRegAsync(string mode, string? registryPath, string filePath, Action<string>? log, CancellationToken cancellationToken)
    {
        string arguments = mode == "export"
            ? "export \"" + registryPath + "\" \"" + filePath + "\" /y"
            : "import \"" + filePath + "\"";

        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = "reg.exe",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using Process process = new Process { StartInfo = startInfo };
        process.Start();
        string output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        string error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(output))
        {
            Log(log, output.Trim());
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("reg.exe " + mode + " 失败: " + error.Trim());
        }
    }

    private static void ReportScanProgress(Action<ApplicationScanProgress>? progress, int scannedDirs, int discoveredDirs, int foundExeCount, int visibleCount, string message)
    {
        if (progress == null)
        {
            return;
        }

        double percent = 0;
        if (discoveredDirs > 0)
        {
            percent = Math.Min(99, Math.Max(0, scannedDirs * 100.0 / discoveredDirs));
        }

        if (string.Equals(message, "扫描完成", StringComparison.Ordinal))
        {
            percent = 100;
        }

        progress(new ApplicationScanProgress(percent, scannedDirs, discoveredDirs, foundExeCount, visibleCount, message));
    }

    private static MigrationOption? TryGetBestExecutableOption(
        string dir,
        IReadOnlyList<string> files,
        HashSet<string> added,
        HashSet<string> fallbackAdded,
        ref int foundExeCount,
        ref int skippedExeCount,
        out bool skipped)
    {
        skipped = false;
        ExecutableCandidate? bestCandidate = null;
        for (int i = 0; i < files.Count; i++)
        {
            string path = files[i];
            if (added.Contains(path) || fallbackAdded.Contains(path))
            {
                continue;
            }

            FileInfo fileInfo = new FileInfo(path);
            if (fileInfo.Length <= 0)
            {
                continue;
            }

            foundExeCount++;
            bool shouldSkip = ShouldSkipExecutable(path);
            if (shouldSkip)
            {
                skippedExeCount++;
            }

            ExecutableCandidate candidate = new ExecutableCandidate(path, fileInfo.Length, shouldSkip, GetExecutableScore(dir, path, fileInfo.Length, shouldSkip));
            if (bestCandidate == null || CompareExecutableCandidates(candidate, bestCandidate.Value) < 0)
            {
                bestCandidate = candidate;
            }
        }

        if (bestCandidate == null)
        {
            return null;
        }

        skipped = bestCandidate.Value.ShouldSkip;
        string displayName = Path.GetFileNameWithoutExtension(bestCandidate.Value.Path);
        return new MigrationOption(bestCandidate.Value.Path, displayName, bestCandidate.Value.Path, bestCandidate.Value.Length);
    }

    private static int GetExecutableScore(string dir, string path, long length, bool shouldSkip)
    {
        int score = 0;
        string fileName = Path.GetFileNameWithoutExtension(path);
        string dirName = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.Equals(fileName, dirName, StringComparison.OrdinalIgnoreCase))
        {
            score -= 1000;
        }

        if (MainWindow.HasExecutableIcon(path))
        {
            score -= 300;
        }
        else
        {
            score += 300;
        }

        if (shouldSkip)
        {
            score += 800;
        }

        if (length > 0)
        {
            long sizeScore = Math.Min(400, length / (1024 * 1024));
            score -= (int)sizeScore;
        }

        return score;
    }

    private static int CompareExecutableCandidates(ExecutableCandidate left, ExecutableCandidate right)
    {
        int scoreCompare = left.Score.CompareTo(right.Score);
        if (scoreCompare != 0)
        {
            return scoreCompare;
        }

        int sizeCompare = right.Length.CompareTo(left.Length);
        if (sizeCompare != 0)
        {
            return sizeCompare;
        }

        return string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldSkipApplicationDirectory(string dir)
    {
        string name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        for (int i = 0; i < SkippedAppDirectoryNames.Length; i++)
        {
            if (string.Equals(name, SkippedAppDirectoryNames[i], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<string> GetCommonApplicationDirectories()
    {
        List<string> dirs = new List<string>();
        AddExistingDirectory(dirs, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        AddExistingDirectory(dirs, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
        AddExistingDirectory(dirs, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        AddExistingDirectory(dirs, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        AddExistingDirectory(dirs, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"));
        AddExistingDirectory(dirs, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Start Menu", "Programs"));

        DriveInfo[] drives = DriveInfo.GetDrives();
        for (int i = 0; i < drives.Length; i++)
        {
            DriveInfo drive = drives[i];
            if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
            {
                continue;
            }

            string root = drive.RootDirectory.FullName;
            AddExistingDirectory(dirs, Path.Combine(root, "Apps"));
            AddExistingDirectory(dirs, Path.Combine(root, "Program Files"));
            AddExistingDirectory(dirs, Path.Combine(root, "Program Files (x86)"));
            AddExistingDirectory(dirs, Path.Combine(root, "Programs"));
            AddExistingDirectory(dirs, Path.Combine(root, "Software"));
        }

        return dirs;
    }

    private static void AddExistingDirectory(List<string> dirs, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        for (int i = 0; i < dirs.Count; i++)
        {
            if (string.Equals(dirs[i], path, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        dirs.Add(path);
    }

    private static bool ShouldSkipExecutable(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        for (int i = 0; i < SkippedExecutableNameParts.Length; i++)
        {
            if (name.IndexOf(SkippedExecutableNameParts[i], StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static string GetAvailableShortcutPath(string startMenuDir, string shortcutName)
    {
        string shortcutPath = Path.Combine(startMenuDir, shortcutName);
        if (!File.Exists(shortcutPath))
        {
            return shortcutPath;
        }

        string nameWithoutExtension = Path.GetFileNameWithoutExtension(shortcutName);
        for (int i = 2; i < 1000; i++)
        {
            string candidate = Path.Combine(startMenuDir, nameWithoutExtension + " (" + i + ").lnk");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(startMenuDir, nameWithoutExtension + "-" + Guid.NewGuid().ToString("N") + ".lnk");
    }

    private static void CreateShortcut(string shortcutPath, string targetPath)
    {
        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null)
        {
            throw new InvalidOperationException("当前系统不支持 WScript.Shell，无法创建快捷方式");
        }

        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            if (shell == null)
            {
                throw new InvalidOperationException("无法启动 WScript.Shell");
            }

            shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                null,
                shell,
                new object[] { shortcutPath });

            if (shortcut == null)
            {
                throw new InvalidOperationException("无法创建快捷方式对象");
            }

            Type shortcutType = shortcut.GetType();
            shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { targetPath });
            shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { Path.GetDirectoryName(targetPath) ?? string.Empty });
            shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { Path.GetFileNameWithoutExtension(targetPath) });
            shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { targetPath + ",0" });
            shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, Array.Empty<object>());
        }
        finally
        {
            if (shortcut != null && Marshal.IsComObject(shortcut))
            {
                Marshal.FinalReleaseComObject(shortcut);
            }

            if (shell != null && Marshal.IsComObject(shell))
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "LanFileTransferMigration_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
        }
    }

    private static void Log(Action<string>? log, string message)
    {
        log?.Invoke(message);
    }

    private static string SanitizeFileName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        char[] chars = value.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0 || chars[i] == '\\' || chars[i] == '/')
            {
                chars[i] = '_';
            }
        }

        return new string(chars);
    }
}

internal sealed class MigrationOption
{
    public MigrationOption(string key, string displayName, string detail)
        : this(key, displayName, detail, 0)
    {
    }

    public MigrationOption(string key, string displayName, string detail, long length)
    {
        Key = key;
        DisplayName = displayName;
        Detail = detail;
        Length = length;
    }

    public string Key { get; }

    public string DisplayName { get; }

    public string Detail { get; }

    public long Length { get; }

    public override string ToString()
    {
        return DisplayName;
    }
}

internal readonly struct ExecutableCandidate
{
    public ExecutableCandidate(string path, long length, bool shouldSkip, int score)
    {
        Path = path;
        Length = length;
        ShouldSkip = shouldSkip;
        Score = score;
    }

    public string Path { get; }

    public long Length { get; }

    public bool ShouldSkip { get; }

    public int Score { get; }
}

internal readonly struct ApplicationScanProgress
{
    public ApplicationScanProgress(double percent, int scannedDirs, int discoveredDirs, int foundExeCount, int visibleCount, string message)
    {
        Percent = percent;
        ScannedDirs = scannedDirs;
        DiscoveredDirs = discoveredDirs;
        FoundExeCount = foundExeCount;
        VisibleCount = visibleCount;
        Message = message;
    }

    public double Percent { get; }

    public int ScannedDirs { get; }

    public int DiscoveredDirs { get; }

    public int FoundExeCount { get; }

    public int VisibleCount { get; }

    public string Message { get; }
}

internal sealed class SelectableMigrationOption
{
    public SelectableMigrationOption(MigrationOption option)
    {
        Key = option.Key;
        DisplayName = option.DisplayName;
        Detail = option.Detail;
        Length = option.Length;
        DetailWithSize = option.Length > 0 ? MainWindow.FormatFileSize(option.Length) + "    " + option.Detail : option.Detail;
        if (File.Exists(option.Key))
        {
            Icon = MainWindow.LoadExecutableIcon(option.Key);
        }
    }

    public bool IsSelected { get; set; }

    public string Key { get; }

    public string DisplayName { get; }

    public string Detail { get; }

    public string DetailWithSize { get; }

    public long Length { get; }

    public Avalonia.Media.Imaging.Bitmap? Icon { get; }
}

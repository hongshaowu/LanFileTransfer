using System.Collections;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Win32;

namespace LanFileTransfer;

internal sealed class SystemMigrationService
{
    private const string UserEnvFile = "environment-user.json";
    private const string MachineEnvFile = "environment-machine.json";
    private const string RegistryDir = "registry";

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
    {
        Key = key;
        DisplayName = displayName;
        Detail = detail;
    }

    public string Key { get; }

    public string DisplayName { get; }

    public string Detail { get; }

    public override string ToString()
    {
        return DisplayName;
    }
}

internal sealed class SelectableMigrationOption
{
    public SelectableMigrationOption(MigrationOption option)
    {
        Key = option.Key;
        DisplayName = option.DisplayName;
        Detail = option.Detail;
    }

    public bool IsSelected { get; set; }

    public string Key { get; }

    public string DisplayName { get; }

    public string Detail { get; }
}

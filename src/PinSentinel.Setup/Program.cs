using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using Microsoft.Win32;
using PinSentinel.Core;

namespace PinSentinel.Setup;

/// <summary>
/// Installer and uninstaller in one executable. The published service and tray app
/// travel inside it as an embedded zip.
///
///   PinSentinelSetup.exe              install or upgrade
///   PinSentinelSetup.exe /uninstall   remove (also what Windows "Installed apps" runs)
///   /quiet                            no dialogs
///   /extract &lt;dir&gt;                    unpack the program files only; needs no service changes
/// </summary>
static class Program
{
    private const string Name = "PinSentinel";
    private const string ServiceName = "PinSentinel";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\PinSentinel";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static readonly string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Name);
    private static readonly string Shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), Name + ".lnk");
    private static readonly string TrayExe = Path.Combine(InstallDir, "Tray", "PinSentinel.Tray.exe");
    private static readonly string Version =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    private static bool s_quiet;

    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        s_quiet = args.Contains("/quiet", StringComparer.OrdinalIgnoreCase);
        try
        {
            int extract = Array.FindIndex(args, a => a.Equals("/extract", StringComparison.OrdinalIgnoreCase));
            if (extract >= 0 && extract + 1 < args.Length)
            {
                Extract(args[extract + 1]);
                return 0;
            }
            return args.Contains("/uninstall", StringComparer.OrdinalIgnoreCase) ? Uninstall() : Install();
        }
        catch (Exception ex)
        {
            Show($"Setup could not finish.\n\n{ex.Message}", MessageBoxIcon.Error);
            return 1;
        }
    }

    private static int Install()
    {
        bool upgrade = File.Exists(Path.Combine(InstallDir, "PinSentinel.Service.exe"));
        string prompt = $"{(upgrade ? "Upgrade to" : "Install")} PinSentinel {Version}?\n\n{DescribeCard()}\n\n" +
            (upgrade ? "Your settings, armed state and logs are kept." : "The guard starts in dry run: it warns but does not throttle or shut down until you arm it.");
        if (!Confirm(prompt)) return 2;

        StopTray();
        RemoveService();

        // Keep an edited config across upgrades.
        string settings = Path.Combine(InstallDir, "appsettings.json");
        string? saved = File.Exists(settings) ? File.ReadAllText(settings) : null;
        Extract(InstallDir);
        if (saved is not null) File.WriteAllText(settings, saved);

        string setupCopy = Path.Combine(InstallDir, "PinSentinelSetup.exe");
        if (!string.Equals(Environment.ProcessPath, setupCopy, StringComparison.OrdinalIgnoreCase))
            File.Copy(Environment.ProcessPath!, setupCopy, overwrite: true);

        string serviceExe = Path.Combine(InstallDir, "PinSentinel.Service.exe");
        Sc("create", ServiceName, "binPath=", $"\"{serviceExe}\"", "start=", "auto", "DisplayName=", "PinSentinel GPU connector guard");
        Sc("description", ServiceName, "Monitors per-pin 12V-2x6 current on the ROG Astral and throttles or shuts down on a fault.");
        Sc("failure", ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/60000");
        Sc("start", ServiceName);

        CreateShortcut();
        using (var key = Registry.LocalMachine.CreateSubKey(UninstallKey))
        {
            key.SetValue("DisplayName", Name);
            key.SetValue("DisplayVersion", Version);
            key.SetValue("Publisher", "PinSentinel");
            key.SetValue("DisplayIcon", TrayExe);
            key.SetValue("InstallLocation", InstallDir);
            key.SetValue("UninstallString", $"\"{setupCopy}\" /uninstall");
            key.SetValue("QuietUninstallString", $"\"{setupCopy}\" /uninstall /quiet");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("EstimatedSize", DirectorySizeKb(InstallDir), RegistryValueKind.DWord);
        }

        // Started through Explorer so the tray app runs as the normal user, not elevated like this installer.
        Process.Start("explorer.exe", $"\"{TrayExe}\"");

        Show($"PinSentinel {Version} is installed and the service is running.\n\n" +
             "The tray icon is in the notification area. Right-click it and tick \"Start with Windows\" so it returns after a restart.",
             MessageBoxIcon.Information);
        return 0;
    }

    private static int Uninstall()
    {
        if (!Confirm("Remove PinSentinel?\n\nThe GPU connector will no longer be monitored. Logs in ProgramData\\PinSentinel are kept.")) return 2;

        StopTray();
        RemoveService();
        if (File.Exists(Shortcut)) File.Delete(Shortcut);
        Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        using (var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)) run?.DeleteValue(Name, throwOnMissingValue: false);

        // This executable lives in the folder being removed, so the folder is deleted just after it exits.
        if (Directory.Exists(InstallDir))
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{InstallDir}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = Path.GetTempPath(),
            });

        Show("PinSentinel has been removed.", MessageBoxIcon.Information);
        return 0;
    }

    private static void Extract(string directory)
    {
        using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip")
            ?? throw new InvalidOperationException("This build of setup contains no program files. Build it with scripts\\build-installer.ps1.");
        Directory.CreateDirectory(directory);
        using var zip = new ZipArchive(payload, ZipArchiveMode.Read);
        zip.ExtractToDirectory(directory, overwriteFiles: true);
    }

    private static string DescribeCard()
    {
        try
        {
            var gpus = AstralSensor.ListGpus();
            var supported = gpus.FirstOrDefault(g => AstralSensor.SupportedCards.ContainsKey(g.SubSystemId));
            if (supported is not null) return $"Found: {AstralSensor.SupportedCards[supported.SubSystemId]}.";
            string found = gpus.Count == 0 ? "no NVIDIA GPU" : string.Join(", ", gpus.Select(g => g.SubSystemText));
            return $"Warning: no supported ROG Astral card was found ({found}). PinSentinel will install but will have nothing to monitor.";
        }
        catch (Exception ex) when (ex is DllNotFoundException or InvalidOperationException or EntryPointNotFoundException)
        {
            return "Warning: the NVIDIA driver was not found, so the card could not be checked.";
        }
    }

    private static void StopTray()
    {
        foreach (var process in Process.GetProcessesByName("PinSentinel.Tray"))
        {
            try { process.Kill(); process.WaitForExit(5000); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }

    private static void RemoveService()
    {
        const int DoesNotExist = 1060;
        if (Sc("query", ServiceName) == DoesNotExist) return;
        Sc("stop", ServiceName);
        Sc("delete", ServiceName);
        // Deletion completes only once the process has exited and its handles are closed.
        for (int i = 0; i < 40 && Sc("query", ServiceName) != DoesNotExist; i++) Thread.Sleep(500);
        for (int i = 0; i < 20 && Process.GetProcessesByName("PinSentinel.Service").Length > 0; i++) Thread.Sleep(500);
    }

    private static int Sc(params string[] arguments)
    {
        var info = new ProcessStartInfo("sc.exe") { CreateNoWindow = true, UseShellExecute = false };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        process.WaitForExit();
        return process.ExitCode;
    }

    private static void CreateShortcut()
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host is unavailable.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic link = shell.CreateShortcut(Shortcut);
        link.TargetPath = TrayExe;
        link.Description = "PinSentinel GPU connector monitor";
        link.Save();
    }

    private static int DirectorySizeKb(string directory) =>
        (int)(new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) / 1024);

    private static bool Confirm(string text) =>
        s_quiet || MessageBox.Show(text, "PinSentinel Setup", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK;

    private static void Show(string text, MessageBoxIcon icon)
    {
        if (!s_quiet) MessageBox.Show(text, "PinSentinel Setup", MessageBoxButtons.OK, icon);
    }
}

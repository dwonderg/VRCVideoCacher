using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Semver;
using Serilog;
using VRCVideoCacher.Models;
using VRCVideoCacher.Utils;

namespace VRCVideoCacher;

public class Updater
{
    private const string UpdateUrl = "https://api.github.com/repos/codeyumx/VRCVideoCacherPlus/releases/latest";
    private static readonly HttpClient HttpClient = new()
    {
        DefaultRequestHeaders = { { "User-Agent", "VRCVideoCacher.Updater" } }
    };
    private static readonly ILogger Log = Program.Logger.ForContext<Updater>();
    private static readonly string FileName = OperatingSystem.IsWindows() ? "VRCVideoCacher.exe" : "VRCVideoCacher";
    private static readonly string FilePath = Path.Join(Program.CurrentProcessPath, FileName);
    private static readonly string BackupFilePath = Path.Join(Program.CurrentProcessPath, "VRCVideoCacher.bkp");
    private static readonly string TempFilePath = Path.Join(Program.CurrentProcessPath, OperatingSystem.IsWindows() ? "VRCVideoCacher.Temp.exe" : "VRCVideoCacher.Temp");
    private const string TempProcessName = "VRCVideoCacher.Temp";
    private static readonly string UpdaterLogPath = Path.Join(Program.DataPath, "Logs", "updater.log");

    public static async Task<UpdateInfo?> CheckForUpdates()
    {
        Log.Information("Checking for updates...");
        var isDebug = false;
#if DEBUG
        isDebug = true;
#endif
        if (Program.Version.Contains("-dev") || isDebug)
        {
            Log.Information("Running in dev mode. Skipping update check.");
            return null;
        }

        HttpResponseMessage response;
        string data;
        try
        {
            response = await HttpClient.GetAsync(UpdateUrl);
            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("Failed to check for updates.");
                response.Dispose();
                return null;
            }
            data = await response.Content.ReadAsStringAsync();
            response.Dispose();
        }
        catch (HttpRequestException ex)
        {
            Log.Warning(ex, "Failed to reach update server.");
            return null;
        }
        catch (TaskCanceledException ex)
        {
            Log.Warning(ex, "Update check timed out.");
            return null;
        }
        var latestRelease = JsonConvert.DeserializeObject<GitHubRelease>(data);
        if (latestRelease == null)
        {
            Console.Error.WriteLine("Failed to parse update response.");
            return null;
        }
        if (!SemVersion.TryParse(latestRelease.tag_name, SemVersionStyles.Any, out var latestVersion))
        {
            Log.Warning("Failed to parse latest release version: {Tag}", latestRelease.tag_name);
            return null;
        }
        if (!SemVersion.TryParse(Program.Version, SemVersionStyles.Any, out var currentVersion))
        {
            Log.Warning("Failed to parse current version: {Version}", Program.Version);
            return null;
        }
        Log.Information("Latest release: {Latest}, Installed Version: {Installed}", latestVersion, currentVersion);
        if (SemVersion.ComparePrecedence(currentVersion, latestVersion) >= 0)
        {
            Log.Information("No updates available.");
            return null;
        }
        Log.Information("Update available: {Version}", latestVersion);
        return new UpdateInfo(latestVersion.ToString(), latestRelease);
    }

    public static async Task<bool> ApplyUpdate(GitHubRelease release)
    {
        return await DownloadAndStageAsync(release);
    }

    public static void Cleanup()
    {
        if (File.Exists(BackupFilePath))
        {
            Log.Information("Leftover temp file found, deleting.");
            File.Delete(BackupFilePath);
        }
    }

    private static async Task<bool> DownloadAndStageAsync(GitHubRelease release)
    {
        foreach (var asset in release.assets)
        {
            if (asset.name != FileName)
                continue;

            try
            {
                if (File.Exists(TempFilePath))
                {
                    Log.Information("Temp file found from a previous update, deleting.");
                    File.Delete(TempFilePath);
                }

                await using (var stream = await HttpClient.GetStreamAsync(asset.browser_download_url))
                await using (var fileStream = new FileStream(TempFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await stream.CopyToAsync(fileStream);
                }

                if (!await HashCheck(asset.digest))
                {
                    Log.Warning("Hash check failed, aborting update.");
                    TryDeleteTempFile();
                    return false;
                }

                if (!OperatingSystem.IsWindows())
                    FileTools.MarkFileExecutable(TempFilePath);

                Log.Information("Update {Version} downloaded. Will install when the app exits.", release.tag_name);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to download update.");
                TryDeleteTempFile();
                return false;
            }
        }
        Log.Warning("No matching asset ({FileName}) found in release {Tag}.", FileName, release.tag_name);
        return false;
    }

    public static void FinalizeUpdateOnExit()
    {
        // Startup cleanup (RunUpdateHandler, OldPid==null branch) removes any stale temp file,
        // so its presence here means DownloadAndStageAsync successfully staged it this session.
        if (!File.Exists(TempFilePath))
            return;

        try
        {
            var pid = Environment.ProcessId;
            var args = LaunchArgs.BuildArgs();
            args.Add($"--old-pid={pid}");
            var argsString = string.Join(' ', args);
            Log.Information("Launching staged updater on exit. Args: {Args}", argsString);
            LogToFile("INFO", $"Launching staged updater on exit. pid={pid} argsString={argsString}");

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = TempFilePath,
                    UseShellExecute = true,
                    WorkingDirectory = Program.CurrentProcessPath,
                    Arguments = argsString
                }
            };
            var started = process.Start();
            LogToFile("INFO", $"Staged updater Process.Start returned {started}.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to launch staged updater on exit.");
            LogToFile("ERROR", $"Failed to launch staged updater on exit: {ex}");
        }
    }

    private static void LogToFile(string level, string message)
    {
        try
        {
            var dir = Path.GetDirectoryName(UpdaterLogPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.AppendAllText(UpdaterLogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}");
        }
        catch
        {
            // best-effort
        }
    }

    // If a staged updater (VRCVideoCacher.Temp.exe) is currently swapping the real exe, a user
    // who relaunches from the desktop shortcut can race it and end up running the old exe or
    // hitting a file lock. Wait (bounded) for it to finish before we proceed.
    private static void WaitForStagedUpdaterToFinish()
    {
        const int maxWaitMs = 15_000;
        var waited = 0;
        var announced = false;
        while (waited < maxWaitMs)
        {
            var temps = Process.GetProcessesByName(TempProcessName);
            if (temps.Length == 0)
                return;
            foreach (var p in temps) p.Dispose();
            if (!announced)
            {
                Console.WriteLine("An update is in progress, waiting for it to finish...");
                LogToFile("INFO", "Detected staged updater running at startup; waiting for it to finish.");
                announced = true;
            }
            Thread.Sleep(500);
            waited += 500;
        }
        Console.Error.WriteLine("Staged updater did not finish within the timeout period. Continuing anyway.");
        LogToFile("WARN", "Staged updater did not exit within 15s. Continuing startup.");
    }

    private static void TryDeleteTempFile()
    {
        // When running as the staged Temp.exe, we can't delete our own image on Windows.
        // The next clean launch of the real exe will clean it up via the cleanup branch.
        if (string.Equals(Environment.ProcessPath, TempFilePath, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            if (File.Exists(TempFilePath))
                File.Delete(TempFilePath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to delete temp update file: {Path}", TempFilePath);
        }
    }

    private static async Task<bool> HashCheck(string githubHash)
    {
        using var sha256 = SHA256.Create();
        await using var stream = File.Open(TempFilePath, FileMode.Open);
        var hashBytes = await sha256.ComputeHashAsync(stream);
        var hashString = Convert.ToHexString(hashBytes);
        githubHash = githubHash.Split(':')[1];
        var hashMatches = string.Equals(githubHash, hashString, StringComparison.OrdinalIgnoreCase);
        Log.Information("FileHash: {FileHash} GitHubHash: {GitHubHash} HashMatch: {HashMatches}", hashString, githubHash, hashMatches);
        return hashMatches;
    }

    private static bool FilesHashMatch(string pathA, string pathB)
    {
        using var sha = SHA256.Create();
        using var a = File.OpenRead(pathA);
        using var b = File.OpenRead(pathB);
        var hashA = Convert.ToHexString(sha.ComputeHash(a));
        sha.Initialize();
        var hashB = Convert.ToHexString(sha.ComputeHash(b));
        var match = string.Equals(hashA, hashB, StringComparison.OrdinalIgnoreCase);
        Log.Information("[Updater] Hash self={HashA} copy={HashB} match={Match}", hashA, hashB, match);
        return match;
    }

    public static bool RunUpdateHandler()
    {
        if (LaunchArgs.OldPid == null)
        {
            // Cleanup branch — only the post-update real exe runs this. If the user double-clicks
            // the shortcut while a staged updater is mid-swap, wait for it so we don't race the copy.
            WaitForStagedUpdaterToFinish();

            // Drop the staged temp file.
            if (Environment.ProcessPath != TempFilePath && File.Exists(TempFilePath))
            {
                Console.WriteLine("Update temp file exists. Deleting temp file.");
                try { File.Delete(TempFilePath); } catch { /* best-effort */ }
            }
            return false;
        }

        // From here on we are the staged Temp.exe. ANY failure must terminate this process
        // — never fall through to running as the app, otherwise the user ends up running
        // Temp.exe while the on-disk VRCVideoCacher.exe is still the old version.
        LogToFile("INFO", $"Staged updater started. ProcessPath={Environment.ProcessPath} TargetPath={FilePath} OldPid={LaunchArgs.OldPid}");
        try
        {
            if (Environment.ProcessPath == null)
            {
                Console.Error.WriteLine("Process path is null. Aborting update.");
                LogToFile("ERROR", "Process path is null. Aborting update.");
                AbortStagedUpdate();
            }
            if (Environment.ProcessPath == FilePath)
            {
                Console.Error.WriteLine("Process path is the same as the target file path. Aborting update to prevent self-deletion.");
                LogToFile("ERROR", $"Process path equals target path ({FilePath}). Aborting to prevent self-deletion.");
                AbortStagedUpdate();
            }

            try
            {
                using var oldProcess = Process.GetProcessById(LaunchArgs.OldPid.Value);
                if (!oldProcess.WaitForExit(10_000))
                {
                    Console.Error.WriteLine("Old process did not exit within the timeout period. Aborting update.");
                    LogToFile("ERROR", $"Old process (pid {LaunchArgs.OldPid}) did not exit within 10s. Aborting update.");
                    AbortStagedUpdate();
                }
            }
            catch
            {
                // Process already gone — that's fine
            }

            // Windows can hold the executable lock for a surprisingly long time after the owning
            // process exits (antivirus, shell extensions, etc). Retry aggressively to ride it out.
            const int maxAttempts = 20;
            Exception? lastError = null;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    File.Copy(Environment.ProcessPath, FilePath, overwrite: true);
                    lastError = null;
                    LogToFile("INFO", $"Copied new exe to {FilePath} on attempt {attempt}.");
                    break;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    Console.Error.WriteLine($"Copy attempt {attempt}/{maxAttempts} failed: {ex.Message}");
                    LogToFile("WARN", $"Copy attempt {attempt}/{maxAttempts} failed: {ex.Message}");
                    Thread.Sleep(500);
                }
            }
            if (lastError != null)
            {
                Console.Error.WriteLine($"Failed to copy new version to target path after {maxAttempts} attempts: {lastError}");
                LogToFile("ERROR", $"Failed to copy new version after {maxAttempts} attempts: {lastError}");
                AbortStagedUpdate();
            }

            if (!FilesHashMatch(Environment.ProcessPath, FilePath))
            {
                Console.Error.WriteLine("Hash check failed after copying new version. Aborting update to prevent potential corruption.");
                LogToFile("ERROR", "Hash mismatch between staged temp exe and copied target exe. Aborting.");
                AbortStagedUpdate();
            }

            var args = LaunchArgs.BuildArgs();
            var argsString = string.Join(' ', args);
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = FilePath,
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(FilePath),
                    Arguments = argsString
                }
            };
            var started = process.Start();
            LogToFile("INFO", $"Relaunched new exe at {FilePath}. Process.Start returned {started}.");
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Unexpected error during staged update: {ex}");
            LogToFile("ERROR", $"Unexpected error during staged update: {ex}");
            AbortStagedUpdate();
            return false; // unreachable, AbortStagedUpdate terminates the process
        }
    }

    [DoesNotReturn]
    private static void AbortStagedUpdate()
    {
        TryDeleteTempFile();
        Environment.Exit(1);
    }
}

public record UpdateInfo(string Version, GitHubRelease Release);
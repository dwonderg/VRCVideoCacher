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

        using var response = await HttpClient.GetAsync(UpdateUrl);
        if (!response.IsSuccessStatusCode)
        {
            Log.Warning("Failed to check for updates.");
            return null;
        }
        var data = await response.Content.ReadAsStringAsync();
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
            process.Start();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to launch staged updater on exit.");
        }
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
            // Cleanup branch — only the post-update real exe runs this. Drop the staged temp file.
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
        try
        {
            if (Environment.ProcessPath == null)
            {
                Console.Error.WriteLine("Process path is null. Aborting update.");
                AbortStagedUpdate();
            }
            if (Environment.ProcessPath == FilePath)
            {
                Console.Error.WriteLine("Process path is the same as the target file path. Aborting update to prevent self-deletion.");
                AbortStagedUpdate();
            }

            try
            {
                using var oldProcess = Process.GetProcessById(LaunchArgs.OldPid.Value);
                if (!oldProcess.WaitForExit(10_000))
                {
                    Console.Error.WriteLine("Old process did not exit within the timeout period. Aborting update.");
                    AbortStagedUpdate();
                }
            }
            catch
            {
                // Process already gone — that's fine
            }

            // Windows can hold the executable lock briefly after the owning process exits.
            // Retry the copy a few times to ride out that window.
            const int maxAttempts = 8;
            Exception? lastError = null;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    File.Copy(Environment.ProcessPath, FilePath, overwrite: true);
                    lastError = null;
                    break;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    Console.Error.WriteLine($"Copy attempt {attempt}/{maxAttempts} failed: {ex.Message}");
                    Thread.Sleep(500);
                }
            }
            if (lastError != null)
            {
                Console.Error.WriteLine($"Failed to copy new version to target path after {maxAttempts} attempts: {lastError}");
                AbortStagedUpdate();
            }

            if (!FilesHashMatch(Environment.ProcessPath, FilePath))
            {
                Console.Error.WriteLine("Hash check failed after copying new version. Aborting update to prevent potential corruption.");
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
            process.Start();
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Unexpected error during staged update: {ex}");
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
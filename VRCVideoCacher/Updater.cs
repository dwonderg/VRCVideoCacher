using System.Diagnostics;
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
#if STEAMRELEASE || DEBUG
        Log.Information("Skipping update check (Steam/dev build).");
        return null;
#else
        if (Program.Version.Contains("-dev"))
        {
            Log.Information("Running in dev mode. Skipping update check.");
            return null;
        }

        Log.Information("Checking for updates...");
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
#endif
    }

    public static async Task ApplyUpdate(GitHubRelease release)
    {
        await UpdateAsync(release);
    }

    public static void Cleanup()
    {
        if (File.Exists(BackupFilePath))
        {
            Log.Information("Leftover temp file found, deleting.");
            File.Delete(BackupFilePath);
        }
    }

    private static async Task UpdateAsync(GitHubRelease release)
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

                await using var stream = await HttpClient.GetStreamAsync(asset.browser_download_url);
                await using var fileStream = new FileStream(TempFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
                await stream.CopyToAsync(fileStream);
                fileStream.Close();

                if (!await HashCheck(asset.digest))
                {
                    Log.Information("Hash check failed, aborting update.");
                    File.Delete(TempFilePath);
                    return;
                }

                Log.Information("Hash check passed, launching updater.");

                if (!OperatingSystem.IsWindows())
                    FileTools.MarkFileExecutable(TempFilePath);

                var pid = Environment.ProcessId;
                var args = LaunchArgs.BuildArgs();
                args.Add($"--old-pid={pid}");
                var argsString = string.Join(' ', args.Select(x => x));
                Log.Information("Launching new Version: {Version} with Args: {Args}", release.tag_name, argsString);

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
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Failed to update: {Message}", ex.ToString());
                if (File.Exists(TempFilePath))
                    File.Delete(TempFilePath);
            }
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
        Log.Information($"[Updater] Hash self={hashA} copy={hashB} match={match}");
        return match;
    }

    public static bool RunUpdateHandler()
    {
        if (LaunchArgs.OldPid == null)
        {
            // clean up
            if (Environment.ProcessPath != TempFilePath && File.Exists(TempFilePath))
            {
                Console.WriteLine("Update temp file exists. Deleting temp file.");
                File.Delete(TempFilePath);
            }
            return false;
        }

        if (Environment.ProcessPath == null)
        {
            Console.Error.WriteLine("Process path is null. Aborting update to prevent potential issues.");
            return false;
        }
        if (Environment.ProcessPath == FilePath)
        {
            Console.Error.WriteLine("Process path is the same as the target file path. Aborting update to prevent self-deletion.");
            return false;
        }

        try
        {
            using var oldProcess = Process.GetProcessById(LaunchArgs.OldPid.Value);
            if (!oldProcess.WaitForExit(10_000))
            {
                Console.Error.WriteLine("Old process did not exit within the timeout period. Aborting update to prevent potential issues.");
                return false;
            }
        }
        catch
        {
            // Process already gone — that's fine
        }

        try
        {
            File.Copy(Environment.ProcessPath, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Failed to copy new version to target path: {Message}", ex.ToString());
            return false;
        }

        // Verify the copy matches self
        if (!FilesHashMatch(Environment.ProcessPath, FilePath))
        {
            Console.Error.WriteLine("Hash check failed after copying new version. Aborting update to prevent potential corruption.");
            return false;
        }

        var args = LaunchArgs.BuildArgs();
        var argsString = string.Join(' ', args.Select(x => x));
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
}

public record UpdateInfo(string Version, GitHubRelease Release);
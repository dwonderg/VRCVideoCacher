using Serilog;
using VRCVideoCacher.Database;
using VRCVideoCacher.Database.Models;

namespace VRCVideoCacher.Services;

public static class VRDancingSheetService
{
    private const string SheetCsvUrl =
        "https://docs.google.com/spreadsheets/d/14Nh9M1r__S-BHS00j6hi0otF4A63LXhoX9ISFZv7nrs/export?format=csv&gid=0";

    private static readonly TimeSpan SyncInterval = TimeSpan.FromHours(24);
    private static readonly string MarkerPath = Path.Join(Database.Database.CacheDir, "VRDancingSheet.lastsync");
    private static readonly ILogger Logger = Program.Logger.ForContext(typeof(VRDancingSheetService));
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public static void StartBackgroundSync()
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    if (IsStale())
                        await SyncOnceAsync();
                }
                catch (Exception ex)
                {
                    Logger.Warning("VRDancing sheet sync failed: {Ex}", ex.Message);
                }

                await Task.Delay(SyncInterval);
            }
            // ReSharper disable once FunctionNeverReturns
        });
    }

    private static bool IsStale()
    {
        if (!File.Exists(MarkerPath)) return true;
        return DateTime.UtcNow - File.GetLastWriteTimeUtc(MarkerPath) >= SyncInterval;
    }

    private static async Task SyncOnceAsync()
    {
        Logger.Information("Syncing VRDancing title sheet...");
        var csv = await HttpClient.GetStringAsync(SheetCsvUrl);
        var rows = ParseCsv(csv);

        // Find header row: instructor,song,artist,code
        int headerIdx = -1;
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.Count >= 4 &&
                r[0].Trim().Equals("instructor", StringComparison.OrdinalIgnoreCase) &&
                r[1].Trim().Equals("song", StringComparison.OrdinalIgnoreCase) &&
                r[2].Trim().Equals("artist", StringComparison.OrdinalIgnoreCase) &&
                r[3].Trim().Equals("code", StringComparison.OrdinalIgnoreCase))
            {
                headerIdx = i;
                break;
            }
        }
        if (headerIdx < 0)
        {
            Logger.Warning("VRDancing sheet: header row not found, skipping sync.");
            return;
        }

        var titles = new Dictionary<string, VRDancingTitle>();
        for (var i = headerIdx + 1; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.Count < 4) continue;
            var code = r[3].Trim();
            if (string.IsNullOrEmpty(code)) continue;
            titles[code] = new VRDancingTitle
            {
                Code = code,
                Instructor = r[0].Trim(),
                Song = r[1].Trim(),
                Artist = r[2].Trim()
            };
        }

        DatabaseManager.ReplaceVRDancingTitles(titles.Values);
        File.WriteAllText(MarkerPath, DateTime.UtcNow.ToString("O"));
        Logger.Information("VRDancing sheet synced: {Count} entries", titles.Count);
    }

    // RFC 4180-ish CSV parser: handles quoted fields with embedded commas, quotes, and newlines.
    private static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var current = new List<string>();
        var field = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }
            }
            else
            {
                switch (c)
                {
                    case '"':
                        inQuotes = true;
                        break;
                    case ',':
                        current.Add(field.ToString());
                        field.Clear();
                        break;
                    case '\r':
                        break;
                    case '\n':
                        current.Add(field.ToString());
                        field.Clear();
                        rows.Add(current);
                        current = new List<string>();
                        break;
                    default:
                        field.Append(c);
                        break;
                }
            }
        }

        if (field.Length > 0 || current.Count > 0)
        {
            current.Add(field.ToString());
            rows.Add(current);
        }
        return rows;
    }
}

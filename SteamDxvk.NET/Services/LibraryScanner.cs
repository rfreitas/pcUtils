using System.Collections.Concurrent;

namespace SteamDxvk;

/// <summary>Scans every installed Steam game. One implementation shared by the window and the headless flags.</summary>
internal static class LibraryScanner
{
    /// <param name="onTotal">Called once with the game count before scanning starts.</param>
    /// <param name="onResult">Called from worker threads as each game finishes.</param>
    /// <exception cref="InvalidOperationException">Steam isn't installed.</exception>
    public static List<GameScan> ScanAll(Action<int>? onTotal = null, Action<GameScan>? onResult = null, ScanCache? cache = null)
    {
        string steam = SteamLibrary.FindSteamPath() ?? throw new InvalidOperationException("Steam install not found");
        var games = SteamLibrary.ListGames(steam);
        onTotal?.Invoke(games.Count);

        cache ??= new ScanCache();
        var results = new ConcurrentBag<GameScan>();
        Parallel.ForEach(games, new ParallelOptions { MaxDegreeOfParallelism = 4 }, game =>
        {
            GameScan scan;
            try { scan = GameScanner.Scan(game, cache); }
            catch (Exception e)   // one unreadable game must not stop the rest
            {
                Logger.Log($"Scan failed for {game.Name}: {e}");
                scan = new GameScan(game) { Error = e.Message };
            }
            scan.Status = DxvkInstaller.StatusOf(scan);
            results.Add(scan);
            onResult?.Invoke(scan);
        });
        cache.Save();
        return [.. results];
    }
}

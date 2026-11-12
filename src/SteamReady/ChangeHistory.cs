using System.Text.Json;
using System.Text;
namespace SteamReady;

internal sealed record HistoryEntry(string Path, string Id, string Name, string Before, string After, string Backup, string State);
internal sealed record HistoryBatch(string Id, DateTimeOffset CreatedUtc, string Kind, string? SourceBatchId, List<HistoryEntry> Entries);
internal sealed record PlannedChange(Game Game, string Desired);

// Journal before mutation; never restore a whole historical Steam manifest.
// Every operation uses the same Steam-closed and compare-before-write checks.
internal sealed class ChangeHistory(string dataRoot, Func<bool> steamRunning)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private string Root => Path.Combine(dataRoot, "history");
    private string Legacy => Path.Combine(dataRoot, "last-batch.json");
    private string RetentionPath => Path.Combine(dataRoot, "history-retention.json");
    public int Retention
    {
        get
        {
            if (!File.Exists(RetentionPath)) return 50;
            int count = JsonSerializer.Deserialize<int>(File.ReadAllText(RetentionPath));
            if (count < 50) throw new FormatException("Invalid history retention.");
            return count;
        }
    }
    public void SetRetention(int count)
    {
        if (count < 50) throw new ArgumentOutOfRangeException(nameof(count), "Keep at least 50 batches.");
        SaveJson(RetentionPath, count);
        Prune();
    }
    private void RequireClosed()
    {
        if (steamRunning()) throw new InvalidOperationException("Exit Steam using Steam > Exit, then try again. SteamReady never kills Steam.");
    }
    private static bool Restorable(HistoryEntry e) => e.State is "applied" or "pending" or "legacy";
    public bool HasUndo
    {
        get { try { return List().Any(b => b.Entries.Any(Restorable)); } catch { return true; } }
    }
    public List<HistoryBatch> List()
    {
        var batches = new List<HistoryBatch>();
        if (Directory.Exists(Root))
        {
            foreach (var dir in Directory.EnumerateDirectories(Root))
            {
                string file = Path.Combine(dir, "batch.json");
                if (!File.Exists(file)) continue;
                var b = JsonSerializer.Deserialize<HistoryBatch>(File.ReadAllText(file))
                    ?? throw new FormatException("Invalid history: " + file);
                if (b.Id != Path.GetFileName(dir) || b.Entries == null)
                    throw new FormatException("Invalid history identity: " + file);
                batches.Add(b);
            }
        }
        // Preserve the old preview's undo journal and raw backups without rewriting them.
        if (File.Exists(Legacy))
        {
            var old = JsonSerializer.Deserialize<Batch>(File.ReadAllText(Legacy))
                ?? throw new FormatException("Invalid legacy undo journal.");
            batches.Add(new("legacy", File.GetLastWriteTimeUtc(Legacy), "legacy", null,
                old.Entries.Select(e => new HistoryEntry(e.Path, e.Id, e.Id, e.Before, e.After, e.Backup, "legacy")).ToList()));
        }
        return batches.OrderByDescending(b => b.CreatedUtc).ThenByDescending(b => b.Id, StringComparer.Ordinal).ToList();
    }
    private static void WriteDurable(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(true);
    }
    private static void SaveJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            WriteDurable(temp, JsonSerializer.SerializeToUtf8Bytes(value, Json));
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private void Save(HistoryBatch b) => SaveJson(Path.Combine(Root, b.Id, "batch.json"), b);
    private void Prune()
    {
        // Never discard pending/uncertain recovery records, or legacy backups.
        var completed = List().Where(b => b.Kind != "legacy" && b.Entries.All(e => e.State != "pending")).ToList();
        foreach (var old in completed.Skip(Retention))
        {
            if (!Guid.TryParseExact(old.Id, "N", out _)) throw new FormatException("Invalid history directory ID.");
            Directory.Delete(Path.Combine(Root, old.Id), true);
        }
    }
    public ChangeResult Apply(IEnumerable<Game> selected, string desired)
    {
        if (desired is not ("0" or "1" or "2")) throw new ArgumentException("Invalid target.");
        return Execute(selected.Where(g => g.Mode != desired).Select(g => new PlannedChange(g, desired)), "apply", null, []);
    }
    public ChangeResult UndoLatest(ScanResult scan)
    {
        var latest = List().FirstOrDefault(b => b.Entries.Any(Restorable));
        return latest == null ? new(0, []) : Restore(scan, latest.Id, null);
    }
    // Null selection restores the entire batch; otherwise select exact app IDs.
    public ChangeResult Restore(ScanResult scan, string batchId, IReadOnlySet<string>? selectedIds)
    {
        RequireClosed();
        var batch = List().SingleOrDefault(b => b.Id == batchId) ?? throw new InvalidOperationException("History batch not found.");
        var errors = new List<string>();
        var plan = new List<PlannedChange>();
        foreach (var entry in batch.Entries.Where(e => Restorable(e) && (selectedIds == null || selectedIds.Contains(e.Id))))
        {
            // Only target a manifest in the current scan, never a historical path alone.
            var game = scan.Games.SingleOrDefault(g => g.Id == entry.Id && string.Equals(g.Path, entry.Path, StringComparison.OrdinalIgnoreCase));
            if (game == null) { errors.Add(entry.Name + ": app is not in its original scanned library."); continue; }
            if (game.Mode == entry.Before) continue;
            if (game.Mode != entry.After) { errors.Add(game.Name + ": preference changed since this batch; rescan or apply manually."); continue; }
            plan.Add(new(game, entry.Before));
        }
        return Execute(plan, "restore", batchId, errors);
    }
    private ChangeResult Execute(IEnumerable<PlannedChange> plan, string kind, string? source, List<string> errors)
    {
        RequireClosed();
        // Refuse writes if existing history is unreadable; do not overwrite recovery data.
        _ = List();
        _ = Retention;
        var batch = new HistoryBatch(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, kind, source, []);
        string dir = Path.Combine(Root, batch.Id);
        int changed = 0;
        foreach (var change in plan.DistinctBy(p => p.Game.Path, StringComparer.OrdinalIgnoreCase))
        {
            var game = change.Game;
            if (game.Mode == change.Desired) continue;
            byte[]? originalBytes = null;
            int index = -1;
            try
            {
                RequireClosed();
                string expectedPath = Path.GetFullPath(Path.Combine(game.Library, "steamapps", $"appmanifest_{game.Id}.acf"));
                if (!string.Equals(Path.GetFullPath(game.Path), expectedPath, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Invalid manifest location.");
                var original = SteamFiles.Load(game.Path);
                originalBytes = original.Bytes;
                var next = SteamFiles.Encode(SteamFiles.EditPreference(original.Text, game.Id, game.Mode, change.Desired), original.Bom);
                Directory.CreateDirectory(dir);
                string backup = Path.Combine(dir, Guid.NewGuid().ToString("N") + "-" + Path.GetFileName(game.Path));
                WriteDurable(backup, originalBytes);
                index = batch.Entries.Count;
                batch.Entries.Add(new(game.Path, game.Id, game.Name, game.Mode, change.Desired, backup, "pending"));
                Save(batch); // Durable intent must exist before touching Steam's file.
                SteamFiles.AtomicReplace(game.Path, originalBytes, next, RequireClosed);
                changed++;
                batch.Entries[index] = batch.Entries[index] with { State = "applied" };
                Save(batch);
            }
            catch (Exception ex)
            {
                errors.Add(game.Name + ": " + ex.Message);
                if (index >= 0)
                {
                    // A failed write may already have reached disk. Only mark failed
                    // if the original bytes are still present; otherwise preserve intent.
                    try
                    {
                        if (batch.Entries[index].State != "applied")
                        {
                            bool unchanged = originalBytes != null && File.ReadAllBytes(game.Path).AsSpan().SequenceEqual(originalBytes);
                            batch.Entries[index] = batch.Entries[index] with { State = unchanged ? "failed" : "pending" };
                        }
                        Save(batch);
                    }
                    catch (Exception journalError)
                    {
                        errors.Add("Recovery journal: " + journalError.Message + ". Further changes stopped.");
                        break;
                    }
                }
                if (steamRunning()) break;
            }
        }
        try { if (batch.Entries.Count > 0) Prune(); }
        catch (Exception ex) { errors.Add("History retention: " + ex.Message); }
        return new(changed, errors, batch.Entries.Where(e => e.State == "applied").ToList());
    }
}

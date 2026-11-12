using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
namespace SteamReady;

// Strict subset of Valve KeyValues; unknown syntax is skipped, never rewritten.
internal sealed record Kv(string Key, string? Value, int Start, int Length, List<Kv>? Children)
{
    public Kv? Field(string key)
    {
        var matches = Children?.Where(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).ToArray() ?? [];
        if (matches.Length > 1) throw new FormatException("Duplicate key: " + key);
        return matches.SingleOrDefault();
    }
}
internal static class Vdf
{
    private static readonly Regex Tokens = new(@"\G(?:\s+|//[^\r\n]*|""(?:\\.|[^""\\])*""|[{}])", RegexOptions.CultureInvariant);
    private sealed record Token(string Text, int Start, int Length);
    public static Kv Parse(string source)
    {
        var tokens = new List<Token>();
        int offset = 0;
        while (offset < source.Length)
        {
            var m = Tokens.Match(source, offset);
            if (!m.Success || m.Index != offset) throw new FormatException("Unsupported VDF syntax at " + offset);
            if (m.Value[0] == '"' || m.Value == "{" || m.Value == "}") tokens.Add(new(m.Value, m.Index, m.Length));
            offset += m.Length;
        }
        int i = 0;
        List<Kv> Read(bool nested, int depth)
        {
            if (depth > 64) throw new FormatException("Excessive nesting");
            var result = new List<Kv>();
            while (i < tokens.Count)
            {
                var k = tokens[i++];
                if (k.Text == "}" && nested) return result;
                if (!k.Text.StartsWith('"') || i >= tokens.Count) throw new FormatException("Invalid key/value");
                var v = tokens[i++];
                if (v.Text == "{") result.Add(new(Decode(k.Text), null, 0, 0, Read(true, depth + 1)));
                else if (v.Text.StartsWith('"')) result.Add(new(Decode(k.Text), Decode(v.Text), v.Start + 1, v.Length - 2, null));
                else throw new FormatException("Invalid value");
            }
            if (nested) throw new FormatException("Unclosed VDF object");
            return result;
        }
        return new("", null, 0, 0, Read(false, 0));
    }
    private static string Decode(string quoted) =>
        Regex.Replace(quoted[1..^1], @"\\([""\\])", m => m.Groups[1].Value);
}
internal sealed record Game(string Path, string Id, string Name, string Library, string Mode);
internal sealed record ScanResult(string Root, List<string> Libraries, List<Game> Games, List<string> Warnings);
internal sealed record BackupEntry(string Path, string Id, string Before, string After, string Backup);
internal sealed record Batch(List<BackupEntry> Entries);
internal sealed record ChangeResult(int Changed, List<string> Errors);

internal static class SteamFiles
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly string Data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamReady");
    private static string Journal => Path.Combine(Data, "last-batch.json");
    public static bool HasUndo => File.Exists(Journal);
    public static bool SteamRunning()
    {
        var processes = Process.GetProcessesByName("steam");
        try { return processes.Length != 0; }
        finally { foreach (var p in processes) p.Dispose(); }
    }
    private static void RequireClosed()
    {
        if (SteamRunning()) throw new InvalidOperationException("Exit Steam using Steam > Exit, then try again. SteamReady never kills Steam.");
    }
    public static string? FindSteam()
    {
        var candidates = new List<string?>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(@"Software\Valve\Steam");
            candidates.Add(key?.GetValue("SteamPath") as string);
            candidates.Add(key?.GetValue("InstallPath") as string);
        }
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        return candidates.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Path.GetFullPath(p!))
            .FirstOrDefault(p => File.Exists(Path.Combine(p, "steam.exe")) && Directory.Exists(Path.Combine(p, "steamapps")));
    }
    private static (byte[] Bytes, string Text, bool Bom) Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        bool bom = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf });
        return (bytes, Utf8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)), bom);
    }
    private static byte[] Encode(string text, bool bom)
    {
        var bytes = Utf8.GetBytes(text);
        return bom ? new byte[] { 0xef, 0xbb, 0xbf }.Concat(bytes).ToArray() : bytes;
    }
    private static (string Id, string Name, Kv Mode) Manifest(string text)
    {
        var state = Vdf.Parse(text).Field("AppState") ?? throw new FormatException("No AppState");
        var id = state.Field("appid")?.Value ?? throw new FormatException("No appid");
        if (!uint.TryParse(id, out _)) throw new FormatException("Invalid appid");
        var name = state.Field("name")?.Value ?? id;
        var mode = state.Field("AutoUpdateBehavior") ?? throw new FormatException("Missing update preference");
        if (mode.Value is not ("0" or "1" or "2")) throw new FormatException("Unknown update preference");
        return (id, name, mode);
    }
    internal static string EditPreference(string text, string id, string expected, string desired)
    {
        if (desired is not ("0" or "1" or "2")) throw new ArgumentException("Invalid target");
        var m = Manifest(text);
        if (m.Id != id || m.Mode.Value != expected) throw new InvalidOperationException("App identity or preference changed; rescan.");
        return text.Remove(m.Mode.Start, m.Mode.Length).Insert(m.Mode.Start, desired);
    }
    public static ScanResult Scan(string root)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!File.Exists(Path.Combine(root, "steam.exe")) || !Directory.Exists(Path.Combine(root, "steamapps")))
            throw new DirectoryNotFoundException("Select the folder containing steam.exe and steamapps.");
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root };
        var warnings = new List<string>();
        var list = Path.Combine(root, "steamapps", "libraryfolders.vdf");
        if (File.Exists(list))
        {
            try
            {
                var folders = Vdf.Parse(Load(list).Text).Field("libraryfolders") ?? throw new FormatException("Missing libraryfolders");
                foreach (var f in folders.Children ?? [])
                {
                    if (!uint.TryParse(f.Key, out _)) continue;
                    var p = f.Value ?? f.Field("path")?.Value;
                    if (!string.IsNullOrWhiteSpace(p) && Path.IsPathFullyQualified(p))
                        libraries.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)));
                }
            }
            catch (Exception ex) { warnings.Add("Library list: " + ex.Message); }
        }
        else warnings.Add("Library list missing; only main Steam folder scanned.");
        var games = new List<Game>();
        foreach (var lib in libraries)
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(Path.Combine(lib, "steamapps"), "appmanifest_*.acf"))
                {
                    try
                    {
                        var m = Manifest(Load(path).Text);
                        if (!Path.GetFileName(path).Equals($"appmanifest_{m.Id}.acf", StringComparison.OrdinalIgnoreCase))
                            throw new FormatException("App ID does not match filename");
                        games.Add(new(path, m.Id, m.Name, lib, m.Mode.Value!));
                    }
                    catch (Exception ex) { warnings.Add(Path.GetFileName(path) + ": " + ex.Message); }
                }
            }
            catch (Exception ex) { warnings.Add(lib + ": " + ex.Message); }
        }
        return new(root, libraries.ToList(), games.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList(), warnings);
    }
    private static void AtomicReplace(string path, byte[] expected, byte[] next)
    {
        string temp = path + ".steamready-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, next);
            RequireClosed();
            if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(expected)) throw new IOException("File changed during operation; rescan.");
            File.Replace(temp, path, null);
            if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(next)) throw new IOException("Verification failed after replacement.");
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static void SaveJournal(Batch batch)
    {
        Directory.CreateDirectory(Data);
        string tmp = Journal + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(batch), Utf8);
            File.Move(tmp, Journal, true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
    public static ChangeResult Apply(IEnumerable<Game> selected, string desired)
    {
        RequireClosed();
        var errors = new List<string>();
        var entries = new List<BackupEntry>();
        var batch = new Batch(entries);
        var dir = Path.Combine(Data, "backups", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        int changed = 0;
        foreach (var game in selected.Where(g => g.Mode != desired))
        {
            try
            {
                RequireClosed();
                var original = Load(game.Path);
                var next = EditPreference(original.Text, game.Id, game.Mode, desired);
                Directory.CreateDirectory(dir);
                string backup = Path.Combine(dir, Guid.NewGuid().ToString("N") + "-" + Path.GetFileName(game.Path));
                File.WriteAllBytes(backup, original.Bytes);
                entries.Add(new(game.Path, game.Id, game.Mode, desired, backup));
                SaveJournal(batch);
                AtomicReplace(game.Path, original.Bytes, Encode(next, original.Bom));
                changed++;
            }
            catch (Exception ex) { errors.Add(game.Name + ": " + ex.Message); }
        }
        return new(changed, errors);
    }
    public static ChangeResult Undo(ScanResult scan)
    {
        RequireClosed();
        var batch = JsonSerializer.Deserialize<Batch>(File.ReadAllText(Journal)) ?? throw new FormatException("Invalid undo journal");
        var errors = new List<string>();
        var remaining = new List<BackupEntry>();
        int changed = 0;
        var allowed = scan.Games.GroupBy(g => g.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var entry in batch.Entries)
        {
            try
            {
                RequireClosed();
                if (!allowed.TryGetValue(entry.Path, out var game) || game.Id != entry.Id)
                    throw new IOException("App no longer in scanned libraries.");
                var original = Load(game.Path);
                var m = Manifest(original.Text);
                if (m.Mode.Value == entry.Before) continue;
                var next = EditPreference(original.Text, entry.Id, entry.After, entry.Before);
                AtomicReplace(game.Path, original.Bytes, Encode(next, original.Bom));
                changed++;
            }
            catch (Exception ex) { remaining.Add(entry); errors.Add(entry.Path + ": " + ex.Message); }
        }
        if (remaining.Count == 0) File.Delete(Journal);
        else SaveJournal(new(remaining));
        return new(changed, errors);
    }
}

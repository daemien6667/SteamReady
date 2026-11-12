using System.Text;
using SteamReady;

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
    passed++;
}
void Reject<T>(Action action, string name) where T : Exception
{
    try { action(); }
    catch (T) { Check(true, name); return; }
    throw new Exception("FAIL: expected rejection: " + name);
}
string Manifest(string id = "123", string mode = "0") =>
    "\"AppState\"\r\n{\r\n\t\"appid\"\t\"" + id + "\"\r\n\t\"name\"\t\"Fixture Game\"\r\n\t\"AutoUpdateBehavior\"\t\"" + mode +
    "\"\r\n\t\"Other\"\t\"keep me\"\r\n\t\"Nested\"\r\n\t{\r\n\t\t\"AutoUpdateBehavior\"\t\"1\"\r\n\t}\r\n}\r\n";

string original = Manifest();
string edited = SteamFiles.EditPreference(original, "123", "0", "2");
Check(edited == Manifest(mode: "2"), "Only top-level preference changes; CRLF and unrelated keys preserved");
Check(SteamFiles.EditPreference(edited, "123", "2", "0") == original, "Round trip");
Check(SteamFiles.EditPreference(original, "123", "0", "1") == Manifest(mode: "1"), "On-launch value");
Reject<InvalidOperationException>(() => SteamFiles.EditPreference(original, "999", "0", "2"), "Reject wrong app identity");
Reject<InvalidOperationException>(() => SteamFiles.EditPreference(original, "123", "1", "2"), "Reject stale preference");
Reject<ArgumentException>(() => SteamFiles.EditPreference(original, "123", "0", "3"), "Reject unsupported target");
Reject<FormatException>(() => SteamFiles.EditPreference(Manifest(mode: "9"), "123", "9", "2"), "Reject unknown stored preference");
Reject<FormatException>(() => SteamFiles.EditPreference(original.Replace("\t\"AutoUpdateBehavior\"\t\"0\"\r\n", ""), "123", "0", "2"), "Reject missing top-level preference");
Reject<FormatException>(() => SteamFiles.EditPreference(original.Replace("\t\"Other\"", "\t\"AutoUpdateBehavior\""), "123", "0", "2"), "Reject duplicate preference");
Reject<FormatException>(() => Vdf.Parse("\"AppState\" {"), "Reject unclosed object");
Reject<FormatException>(() => Vdf.Parse("\"AppState\" { bareword \"value\" }"), "Reject unsupported syntax");
Check(Vdf.Parse("// header\n\"key\" \"a\\\"b\"").Field("key")?.Value == "a\"b", "Comments and escaped quotes");

string root = Path.Combine(Path.GetTempPath(), "SteamReady-fixture-" + Guid.NewGuid().ToString("N"));
string main = Path.Combine(root, "main");
string other = Path.Combine(root, "other library");
string missing = Path.Combine(root, "offline drive");
string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
try
{
    Directory.CreateDirectory(Path.Combine(main, "steamapps"));
    Directory.CreateDirectory(Path.Combine(other, "steamapps"));
    File.WriteAllText(Path.Combine(main, "steam.exe"), "Fixture only");
    string folders = Path.Combine(main, "steamapps", "libraryfolders.vdf");
    File.WriteAllText(folders, "\"libraryfolders\" { \"0\" { \"path\" \"" + Esc(main) + "\" } \"1\" { \"path\" \"" + Esc(other) + "\" \"apps\" { \"456\" \"1\" } } \"2\" { \"path\" \"" + Esc(missing) + "\" } }");
    string app = Path.Combine(main, "steamapps", "appmanifest_123.acf");
    File.WriteAllText(app, original, new UTF8Encoding(true));
    File.WriteAllText(Path.Combine(other, "steamapps", "appmanifest_456.acf"), Manifest("456", "2"));
    File.WriteAllText(Path.Combine(other, "steamapps", "appmanifest_789.acf"), Manifest("999"));
    var scan = SteamFiles.Scan(main);
    Check(scan.Libraries.Count == 3, "Modern library discovery, main root deduplicated");
    Check(scan.Games.Count == 2 && scan.Games.Any(g => g.Id == "456" && g.Mode == "2"), "Multiple library scans and preference detection");
    Check(scan.Warnings.Count == 2, "Offline library and mismatched filename reported");
    Check(File.ReadAllBytes(app).Take(3).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }), "Scan does not change BOM");
    Check(File.ReadAllText(app) == original, "Scan does not modify manifest");
    File.WriteAllText(folders, "\"LibraryFolders\" { \"1\" \"" + Esc(other) + "\" }");
    Check(SteamFiles.Scan(main).Libraries.Count == 2, "Legacy library discovery");
    File.WriteAllText(folders, "malformed");
    var partial = SteamFiles.Scan(main);
    Check(partial.Games.Count == 1 && partial.Warnings.Count > 0, "Malformed library list reports partial scan");
}
finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
Console.WriteLine($"All {passed} fixture checks passed. No real Steam libraries accessed.");

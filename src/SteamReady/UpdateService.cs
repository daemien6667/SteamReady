using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace SteamReady;

internal sealed record UpdateSettings(int Version = 1, bool AutomaticChecks = true, DateTimeOffset? LastAttemptUtc = null, string? SkippedVersion = null);
internal sealed record SteamReadyRelease(string Tag, Version Version, string Notes, Uri Executable, Uri Checksum);
internal sealed class UpdateSettingsStore(string dataRoot)
{
    private string FilePath => Path.Combine(dataRoot, "update-settings.json");
    public UpdateSettings Load()
    {
        if (!File.Exists(FilePath)) return new();
        var settings = JsonSerializer.Deserialize<UpdateSettings>(File.ReadAllText(FilePath)) ?? throw new FormatException("Invalid update settings.");
        if (settings.Version != 1) throw new FormatException("Unsupported update settings.");
        return settings;
    }
    public void Save(UpdateSettings settings)
    {
        if (settings.Version != 1) throw new FormatException("Unsupported update settings.");
        LocalState.Save(FilePath, settings);
    }
}
internal sealed class UpdateService(HttpClient http)
{
    private const string Repository = "daemien6667/SteamReady";
    private const string Api = "https://api.github.com/repos/" + Repository + "/releases/latest";
    private const long MaxExecutableBytes = 160L * 1024 * 1024;
    public static Version CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 1, 0);
            return new(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }
    public static Version ParseVersion(string tag)
    {
        if (!Regex.IsMatch(tag, @"^v?\d+\.\d+\.\d+$", RegexOptions.CultureInvariant) ||
            !Version.TryParse(tag.TrimStart('v'), out var version))
            throw new FormatException("Unsupported release version.");
        return version;
    }
    public static bool IsDue(UpdateSettings settings, DateTimeOffset now) =>
        settings.AutomaticChecks && (settings.LastAttemptUtc == null || now - settings.LastAttemptUtc.Value >= TimeSpan.FromDays(1));
    private static Uri AssetUrl(string tag, string name, string url)
    {
        string expected = "https://github.com/" + Repository + "/releases/download/" + tag + "/" + name;
        if (!string.Equals(url, expected, StringComparison.Ordinal)) throw new FormatException("Unexpected release asset URL.");
        return new Uri(expected);
    }
    public static SteamReadyRelease? ParseRelease(string json, Version installed)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        string tag = root.GetProperty("tag_name").GetString() ?? throw new FormatException("Missing release tag.");
        Version version = ParseVersion(tag);
        if (version <= installed) return null;
        string? executable = null, checksum = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            string? name = asset.GetProperty("name").GetString();
            if (name == "SteamReady.exe")
            {
                if (executable != null) throw new FormatException("Duplicate executable asset.");
                executable = asset.GetProperty("browser_download_url").GetString();
            }
            if (name == "SHA256SUMS.txt")
            {
                if (checksum != null) throw new FormatException("Duplicate checksum asset.");
                checksum = asset.GetProperty("browser_download_url").GetString();
            }
        }
        if (executable == null || checksum == null) return null; // No compatible installable release.
        string notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        return new(tag, version, notes, AssetUrl(tag, "SteamReady.exe", executable), AssetUrl(tag, "SHA256SUMS.txt", checksum));
    }
    private static bool TrustedResponse(HttpResponseMessage response)
    {
        var uri = response.RequestMessage?.RequestUri;
        return uri?.Scheme == "https" && uri.Host is "api.github.com" or "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com";
    }
    private async Task<byte[]> ReadBounded(HttpResponseMessage response, int max, CancellationToken ct)
    {
        if (!TrustedResponse(response)) throw new IOException("Unexpected download host.");
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > max) throw new IOException("Response too large.");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        int n;
        while ((n = await input.ReadAsync(chunk, ct)) != 0)
        {
            if (buffer.Length + n > max) throw new IOException("Response too large.");
            buffer.Write(chunk, 0, n);
        }
        return buffer.ToArray();
    }
    public async Task<SteamReadyRelease?> CheckAsync(Version installed, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Api);
        request.Headers.UserAgent.ParseAdd("SteamReady/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!TrustedResponse(response)) throw new IOException("Unexpected update API host.");
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        return ParseRelease(System.Text.Encoding.UTF8.GetString(await ReadBounded(response, 2 * 1024 * 1024, ct)), installed);
    }
    public static string ParseChecksum(string contents)
    {
        var matches = contents.Split('\n').Select(line => Regex.Match(line.Trim(), @"^([A-Fa-f0-9]{64})\s+\*?SteamReady\.exe$")).Where(m => m.Success).ToArray();
        if (matches.Length != 1) throw new FormatException("Missing or ambiguous SteamReady checksum.");
        return matches[0].Groups[1].Value.ToLowerInvariant();
    }
    public static string FileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
    public static void Verify(string path, string hash)
    {
        if (!Regex.IsMatch(hash, @"^[a-fA-F0-9]{64}$") || !FileHash(path).Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Update checksum mismatch. The existing executable was not changed.");
        using var stream = File.OpenRead(path);
        if (stream.ReadByte() != 'M' || stream.ReadByte() != 'Z') throw new IOException("Update is not a Windows executable.");
    }
    // Download beside the installed executable, so replacement stays on one volume.
    // Copy the currently trusted executable as a helper; never execute downloaded
    // code until the release checksum is verified and the user has requested installation.
    public async Task<string> StageAndStartAsync(SteamReadyRelease release, string executable, CancellationToken ct = default)
    {
        if (release.Version <= CurrentVersion) throw new InvalidOperationException("No newer release.");
        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable) || !Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Portable executable not found.");
        string folder = Path.Combine(Path.GetDirectoryName(executable)!, ".steamready-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using var checksumResponse = await http.GetAsync(AssetUrl(release.Tag, "SHA256SUMS.txt", release.Checksum.AbsoluteUri), HttpCompletionOption.ResponseHeadersRead, ct);
            string hash = ParseChecksum(System.Text.Encoding.UTF8.GetString(await ReadBounded(checksumResponse, 16384, ct)));
            using var response = await http.GetAsync(AssetUrl(release.Tag, "SteamReady.exe", release.Executable.AbsoluteUri), HttpCompletionOption.ResponseHeadersRead, ct);
            if (!TrustedResponse(response)) throw new IOException("Unexpected executable download host.");
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxExecutableBytes) throw new IOException("Update too large.");
            string next = Path.Combine(folder, "next.exe");
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(next, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[65536];
                long total = 0;
                int n;
                while ((n = await input.ReadAsync(buffer, ct)) != 0)
                {
                    total += n;
                    if (total > MaxExecutableBytes) throw new IOException("Update too large.");
                    await output.WriteAsync(buffer.AsMemory(0, n), ct);
                }
                output.Flush(true);
            }
            Verify(next, hash);
            string helper = Path.Combine(folder, "helper.exe");
            File.Copy(executable, helper);
            var info = new ProcessStartInfo(helper) { UseShellExecute = false, WorkingDirectory = folder };
            foreach (string arg in new[] { "--install-update", executable, hash, release.Tag }) info.ArgumentList.Add(arg);
            using var process = Process.Start(info) ?? throw new IOException("Could not start update helper.");
            return folder;
        }
        catch
        {
            try { Directory.Delete(folder, true); } catch { }
            throw;
        }
    }
}
internal static class UpdateInstaller
{
    // Called only by the copied helper, before normal application startup.
    public static void Install(string helper, string target, string hash, string tag)
    {
        target = Path.GetFullPath(target);
        string folder = Path.GetDirectoryName(Path.GetFullPath(helper))!;
        string name = Path.GetFileName(folder);
        const string prefix = ".steamready-update-";
        if (!Path.GetFileName(helper).Equals("helper.exe", StringComparison.OrdinalIgnoreCase) ||
            !name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[prefix.Length..], "N", out _) ||
            !string.Equals(Path.GetDirectoryName(folder), Path.GetDirectoryName(target), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetExtension(target).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            UpdateService.ParseVersion(tag) <= UpdateService.CurrentVersion)
            throw new InvalidOperationException("Invalid update helper invocation.");
        string next = Path.Combine(folder, "next.exe");
        string backup = Path.Combine(folder, "previous.exe");
        UpdateService.Verify(next, hash);
        using var mutex = new Mutex(false, @"Local\SteamReady");
        bool acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(60)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("SteamReady did not close. Update not installed.");
            // Confirm that the installed file is still the exact version that started us.
            if (!File.Exists(target) || UpdateService.FileHash(target) != UpdateService.FileHash(helper))
                throw new IOException("Installed executable changed; update cancelled.");
            UpdateService.Verify(next, hash);
            File.Replace(next, target, backup);
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
        try
        {
            using var process = Process.Start(new ProcessStartInfo(target) { WorkingDirectory = Path.GetDirectoryName(target)!, UseShellExecute = false })
                ?? throw new IOException("Updated executable did not start.");
        }
        catch
        {
            // Roll back only if we can reacquire the app mutex and the replaced
            // file still matches the staged release; never overwrite a newer file.
            bool locked = false;
            try
            {
                try { locked = mutex.WaitOne(TimeSpan.FromSeconds(5)); } catch (AbandonedMutexException) { locked = true; }
                if (locked && File.Exists(backup) && UpdateService.FileHash(target).Equals(hash, StringComparison.OrdinalIgnoreCase))
                    File.Replace(backup, target, Path.Combine(folder, "failed.exe"));
            }
            finally { if (locked) mutex.ReleaseMutex(); }
            throw;
        }
    }
}

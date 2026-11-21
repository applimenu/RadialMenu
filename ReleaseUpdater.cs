using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RadialMenu1;

internal static class ReleaseUpdater
{
    internal sealed record Release(Version Version, Uri Installer, string Sha256, long Size);
    private const long MaxInstallerSize = 512 * 1024 * 1024;
    private const string RepositoryNamePattern = @"[A-Za-z0-9-]+/[A-Za-z0-9_.-]+";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly Assembly AppAssembly = typeof(ReleaseUpdater).Assembly;

    public static Version CurrentVersion { get; } = AppAssembly.GetName().Version ?? new Version(1, 0, 0);
    public static string DisplayVersion => CurrentVersion.ToString(CurrentVersion.Revision > 0 ? 4 : 3);
    private static string Repository { get; } = ReadMetadata("RadialMenuUpdateRepository");
    private static string RepositoryId { get; } = ReadMetadata("RadialMenuUpdateRepositoryId");

    private static string ReadMetadata(string key) => AppAssembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(item => item.Key == key)?.Value ?? "";

    public static async Task<Release?> CheckAsync(CancellationToken cancellation)
    {
        bool useRepositoryId = Regex.IsMatch(RepositoryId, @"\A[1-9][0-9]*\z");
        if (!useRepositoryId && !Regex.IsMatch(Repository, $@"\A{RepositoryNamePattern}\z"))
            throw new InvalidOperationException("В сборке ещё не указан репозиторий обновлений RadialMenu.");
        string endpoint = useRepositoryId ? $"repositories/{RepositoryId}" : $"repos/{Repository}";
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/{endpoint}/releases/latest");
        request.Headers.UserAgent.ParseAdd("RadialMenu/" + DisplayVersion);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Http.SendAsync(request, cancellation).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException("GitHub не нашёл опубликованный стабильный релиз RadialMenu.");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false));
        string architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        string releaseRepository = useRepositoryId ? ReadReleaseRepository(json.RootElement) : Repository;
        return ReadLatest(json.RootElement, releaseRepository, architecture, CurrentVersion);
    }

    private static string ReadReleaseRepository(JsonElement release)
    {
        // The API response from the permanent repository ID supplies its current owner and name.
        string? url = release.TryGetProperty("url", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
        var match = Regex.Match(url ?? "", $@"\Ahttps://api\.github\.com/repos/(?<repo>{RepositoryNamePattern})/releases/[0-9]+\z");
        if (!match.Success)
            throw new InvalidOperationException("GitHub вернул неизвестный адрес репозитория обновлений.");
        return match.Groups["repo"].Value;
    }

    internal static Release? ReadLatest(JsonElement json, string repository, string architecture, Version current)
    {
        if (json.GetProperty("draft").GetBoolean() || json.GetProperty("prerelease").GetBoolean()) return null;
        string tag = (json.GetProperty("tag_name").GetString() ?? "").TrimStart('v', 'V');
        if (!Version.TryParse(tag, out var version))
            throw new InvalidOperationException("Не удалось определить версию опубликованного релиза.");
        version = Normalize(version);
        if (version <= Normalize(current)) return null;

        string name = $"RadialMenu-Setup-win-{architecture}.exe";
        foreach (var asset in json.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != name) continue;

            long size = asset.GetProperty("size").GetInt64();
            string digest = asset.TryGetProperty("digest", out var hash) && hash.ValueKind == JsonValueKind.String
                ? hash.GetString()! : "";
            var url = asset.GetProperty("browser_download_url").GetString();
            if (size is > 0 and <= MaxInstallerSize &&
                Regex.IsMatch(digest, @"\Asha256:[a-fA-F0-9]{64}\z") &&
                Uri.TryCreate(url, UriKind.Absolute, out var installer) &&
                installer.Scheme == Uri.UriSchemeHttps && installer.IsDefaultPort && installer.UserInfo.Length == 0 &&
                installer.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
                installer.AbsolutePath.StartsWith($"/{repository}/releases/download/", StringComparison.OrdinalIgnoreCase))
                return new Release(version, installer, digest[7..], size);
        }

        throw new InvalidOperationException($"Версия {version.ToString(version.Revision > 0 ? 4 : 3)} доступна, но в релизе нет проверяемого установщика {name}.");
    }

    internal static async Task DownloadAndStartAsync(Release release, CancellationToken cancellation)
    {
        var directory = Directory.CreateTempSubdirectory("RadialMenu-update-");
        string installer = Path.Combine(directory.FullName, "RadialMenu-Setup.exe");
        try
        {
            using var response = await Http.GetAsync(release.Installer, HttpCompletionOption.ResponseHeadersRead, cancellation)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromMinutes(5));
            await using (var content = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false))
            await using (var output = File.Open(installer, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[128 * 1024];
                long length = 0;
                while (true)
                {
                    int count = await content.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    length += count;
                    if (length > release.Size) throw new InvalidDataException("Размер файла не совпадает с релизом.");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), deadline.Token).ConfigureAwait(false);
                }
                if (length != release.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Проверка контрольной суммы установщика не прошла.");
            }
            cancellation.ThrowIfCancellationRequested();
            using var process = Process.Start(new ProcessStartInfo(installer) { UseShellExecute = true });
            if (process == null) throw new InvalidOperationException("Не удалось запустить установщик.");
        }
        catch
        {
            try { if (File.Exists(installer)) File.Delete(installer); directory.Delete(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    private static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
}

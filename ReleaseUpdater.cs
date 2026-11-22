using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace RadialMenu1;

internal static class ReleaseUpdater
{
    internal sealed record Release(Version Version, Uri Installer, string Sha256, long Size)
    {
        public string DisplayVersion => Version.ToString(Version.Revision > 0 ? 4 : 3);
    }

    private const string InstallerName = "RadialMenu-Setup.exe";
    private const string UpdateHostName = "RadialMenu.UpdateHost.exe";
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

    internal static async Task<DownloadedInstaller> DownloadAsync(Release release, IProgress<int> progress,
        CancellationToken cancellation)
    {
        CleanCompletedUpdates();
        var directory = Directory.CreateTempSubdirectory("RadialMenu-update-");
        var downloaded = new DownloadedInstaller(directory);
        try
        {
            using var response = await Http.GetAsync(release.Installer, HttpCompletionOption.ResponseHeadersRead, cancellation)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromMinutes(5));
            await using (var content = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false))
            await using (var output = File.Open(downloaded.Path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[128 * 1024];
                long length = 0;
                int lastPercent = 0;
                while (true)
                {
                    int count = await content.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    length += count;
                    if (length > release.Size) throw new InvalidDataException("Размер файла не совпадает с релизом.");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), deadline.Token).ConfigureAwait(false);
                    int percent = (int)Math.Min(99, length * 100 / release.Size);
                    if (percent <= lastPercent) continue;
                    lastPercent = percent;
                    progress.Report(percent);
                }
                if (length != release.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Проверка контрольной суммы установщика не прошла.");
            }
            cancellation.ThrowIfCancellationRequested();
            return downloaded;
        }
        catch
        {
            downloaded.Dispose();
            throw;
        }
    }

    internal sealed class DownloadedInstaller(DirectoryInfo directory) : IDisposable
    {
        private bool _handedOff;
        public string Path { get; } = System.IO.Path.Combine(directory.FullName, InstallerName);

        public void Start(Version version, MenuPalette palette, bool animate)
        {
            string? executable = Environment.ProcessPath;
            string updateHost = System.IO.Path.Combine(AppContext.BaseDirectory, UpdateHostName);
            var start = executable is not null && File.Exists(updateHost) && IsInstalled(executable)
                ? PrepareSilentUpdate(executable, updateHost, version, palette, animate)
                : new ProcessStartInfo(Path) { UseShellExecute = true };

            using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить установщик.");
            _handedOff = true;
        }

        private ProcessStartInfo PrepareSilentUpdate(string executable, string updateHost, Version version, MenuPalette palette, bool animate)
        {
            string copy = System.IO.Path.Combine(directory.FullName, UpdateHostName);
            File.Copy(updateHost, copy);
            if (File.Exists(updateHost + ".config")) File.Copy(updateHost + ".config", copy + ".config");
            var start = new ProcessStartInfo(copy) { WorkingDirectory = directory.FullName, UseShellExecute = false };
            using var current = Process.GetCurrentProcess();
            start.ArgumentList.Add(current.Id.ToString(CultureInfo.InvariantCulture));
            start.ArgumentList.Add(current.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
            start.ArgumentList.Add(executable);
            start.ArgumentList.Add(version.ToString(4));
            start.ArgumentList.Add(palette.Center.ToString(CultureInfo.InvariantCulture));
            start.ArgumentList.Add(palette.Text.ToString(CultureInfo.InvariantCulture));
            start.ArgumentList.Add(palette.Accent.ToString(CultureInfo.InvariantCulture));
            start.ArgumentList.Add(animate ? "1" : "0");
            return start;
        }

        public void Dispose()
        {
            if (_handedOff) return;
            TryRemoveUpdateDirectory(directory);
        }
    }

    private static bool IsInstalled(string executable)
    {
        const string uninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{D2C308C1-26BB-46A3-A7D3-1E28787782F5}_is1";
        using var entry = Registry.CurrentUser.OpenSubKey(uninstallKey);
        return entry?.GetValue("InstallLocation") is string folder && Path.IsPathFullyQualified(folder) &&
            string.Equals(Path.GetFullPath(Path.Combine(folder, "RadialMenu1.exe")),
                executable, StringComparison.OrdinalIgnoreCase);
    }

    private static void CleanCompletedUpdates()
    {
        try
        {
            foreach (var directory in new DirectoryInfo(Path.GetTempPath()).EnumerateDirectories("RadialMenu-update-*"))
            {
                if ((directory.Attributes & FileAttributes.ReparsePoint) == 0 &&
                    File.Exists(Path.Combine(directory.FullName, UpdateHostName)) &&
                    !File.Exists(Path.Combine(directory.FullName, InstallerName)))
                    TryRemoveUpdateDirectory(directory);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryRemoveUpdateDirectory(DirectoryInfo directory)
    {
        try
        {
            File.Delete(Path.Combine(directory.FullName, InstallerName));
            File.Delete(Path.Combine(directory.FullName, UpdateHostName));
            File.Delete(Path.Combine(directory.FullName, UpdateHostName + ".config"));
            directory.Delete();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
}

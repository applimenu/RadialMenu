using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.RegularExpressions;

namespace RadialMenu1;

internal static class ReportDelivery
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private static readonly Uri? Endpoint = ReadEndpoint();
    private static readonly string Bot = ReadMetadata("RadialMenuReportBot");

    public static bool CanSend => Endpoint != null;
    public static bool CanReport => CanSend || Regex.IsMatch(Bot, @"\A[A-Za-z0-9_]{5,32}\z");

    public static Task SendAsync(string description, CancellationToken cancellation) =>
        SendAsync(Http, Endpoint ?? throw new InvalidOperationException("Канал отправки ещё не подключён."),
            description, cancellation);

    internal static async Task SendAsync(HttpClient http, Uri endpoint, string description, CancellationToken cancellation)
    {
        using var response = await http.PostAsJsonAsync(endpoint,
            new { description, version = ReleaseUpdater.DisplayVersion }, cancellation);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new InvalidOperationException("Слишком много отчётов. Попробуй через минуту.");
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<Receipt>(cancellationToken: cancellation);
        if (result?.Delivered != true) throw new InvalidOperationException("Сервис не подтвердил доставку.");
    }

    public static void OpenBot()
    {
        if (!CanReport || CanSend) return;
        using var browser = Process.Start(new ProcessStartInfo($"https://t.me/{Bot}") { UseShellExecute = true });
    }

    private static Uri? ReadEndpoint()
    {
        return Uri.TryCreate(ReadMetadata("RadialMenuReportEndpoint"), UriKind.Absolute, out var uri) &&
               uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 ? uri : null;
    }

    private static string ReadMetadata(string key) => typeof(ReportDelivery).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(item => item.Key == key)?.Value ?? "";

    private sealed record Receipt(bool Delivered);
}

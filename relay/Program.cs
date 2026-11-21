using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16 * 1024);
string token = builder.Configuration["Telegram:BotToken"] ?? "";
string chatId = builder.Configuration["Telegram:ChatId"] ?? "";
if (!Regex.IsMatch(token, @"\A[0-9]+:[A-Za-z0-9_-]+\z") || !Regex.IsMatch(chatId, @"\A-?[0-9]+\z"))
    throw new InvalidOperationException("Configure Telegram__BotToken and Telegram__ChatId on the server.");

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
        PartitionedRateLimiter.Create<HttpContext, string>(_ => RateLimitPartition.GetFixedWindowLimiter("all",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 15, Window = TimeSpan.FromMinutes(1) })),
        PartitionedRateLimiter.Create<HttpContext, string>(context => RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 3, Window = TimeSpan.FromMinutes(1) })));
});
builder.Services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(8) });
var app = builder.Build();
app.UseRateLimiter();
app.MapPost("/reports", async (Report report, HttpClient http, CancellationToken cancellation) =>
{
    string description = report.Description?.Trim() ?? "";
    if (description.Length is < 1 or > 1200 || !Version.TryParse(report.Version, out var version))
        return Results.BadRequest();

    try
    {
        using var response = await http.PostAsJsonAsync($"https://api.telegram.org/bot{token}/sendMessage",
            new { chat_id = chatId, text = $"Отчёт RadialMenu {version}\n\n{description}" }, cancellation);
        if (response.IsSuccessStatusCode)
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
            if (body.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
                return Results.Ok(new { delivered = true });
        }
    }
    catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
    {
        // Never log the Telegram request URI, which contains the bot token.
    }
    return Results.StatusCode(StatusCodes.Status502BadGateway);
});
app.Run();

internal sealed record Report(string? Description, string? Version);

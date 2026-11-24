using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dare.Arenas.Core;
using SS.Core.ComponentInterfaces;

namespace Dare.Arenas;

/// <summary>
/// Sends email through Resend's REST API (POST https://api.resend.com/emails, bearer key). One
/// static HttpClient, 10 s timeout, an Idempotency-Key per send; any non-2xx answer or error is a
/// failure. Never logs the key or the message body (which holds the code).
/// </summary>
internal sealed class ResendEmailSender : IEmailSender
{
    internal const string DefaultFrom = "Foundation <no-reply@mail.subspacefoundation.com>";
    private static readonly Uri Endpoint = new("https://api.resend.com/emails");
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly string _apiKey;
    private readonly string _from;
    private readonly Action<LogLevel, string> _log;

    public ResendEmailSender(string apiKey, string? from, Action<LogLevel, string> log)
    {
        _apiKey = apiKey;
        _from = string.IsNullOrWhiteSpace(from) ? DefaultFrom : from.Trim();
        _log = log;
    }

    public async Task<bool> SendAsync(string to, string subject, string text, string html, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            req.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString("N"));
            var body = JsonSerializer.Serialize(new { from = _from, to = new[] { to }, subject, text, html });
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var res = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (res.IsSuccessStatusCode) return true;
            _log(LogLevel.Warn, $"Email to {EmailMessages.Mask(to)} failed: Resend answered {(int)res.StatusCode}.");
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            _log(LogLevel.Warn, $"Email to {EmailMessages.Mask(to)} failed: {ex.GetType().Name}.");
            return false;
        }
    }
}

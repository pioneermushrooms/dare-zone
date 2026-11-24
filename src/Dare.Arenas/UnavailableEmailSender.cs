using Dare.Arenas.Core;

namespace Dare.Arenas;

/// <summary>
/// Release builds without a Resend key: every send fails, so the existing failure path cancels the
/// code and the page gives its generic answer. Never writes a code, an address or anything else to the log.
/// </summary>
internal sealed class UnavailableEmailSender : IEmailSender
{
    public Task<bool> SendAsync(string to, string subject, string text, string html, CancellationToken ct) => Task.FromResult(false);
}

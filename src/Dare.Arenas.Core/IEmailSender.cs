namespace Dare.Arenas.Core;

/// <summary>Sends one email. Returns false when it could not be sent (the caller then cancels the code).</summary>
public interface IEmailSender
{
    Task<bool> SendAsync(string to, string subject, string text, string html, CancellationToken ct);
}

using System.Text.RegularExpressions;
using Dare.Arenas.Core;
using SS.Core.ComponentInterfaces;

namespace Dare.Arenas;

/// <summary>
/// Used when no Resend key is configured (local testing): writes the code to the log instead of
/// sending mail. The module logs a warning at load that real email is off.
/// </summary>
internal sealed class LogEmailSender : IEmailSender
{
    private readonly Action<LogLevel, string> _log;

    public LogEmailSender(Action<LogLevel, string> log) => _log = log;

    public Task<bool> SendAsync(string to, string subject, string text, string html, CancellationToken ct)
    {
#if DEBUG
        // Local test switch: addresses at fail.test behave like a failed send.
        if (to.EndsWith("@fail.test", StringComparison.OrdinalIgnoreCase))
        {
            _log(LogLevel.Warn, $"(log-only email) simulated send failure to {to}.");
            return Task.FromResult(false);
        }
#endif
        var code = Regex.Match(text, @"\b\d{6}\b").Value;
        _log(LogLevel.Info, $"(log-only email) email to {to}: code {code} ({subject})");
        return Task.FromResult(true);
    }
}

using System.Net;

namespace Dare.Arenas.Core;

/// <summary>
/// The code emails: plain text plus minimal HTML with the code in large type. No link carries the
/// code (players type it on the page they started from).
/// </summary>
public static class EmailMessages
{
    public const string Site = "subspacefoundation.com";

    public static (string Subject, string Text, string Html) Code(CodePurpose purpose, string code, string? name)
    {
        var (subject, lead) = purpose switch
        {
            CodePurpose.Signup => ("Your Foundation sign-up code",
                name is null ? "Here is the code to finish creating your account." : $"Here is the code to claim the name {name}."),
            CodePurpose.Recovery => ("Your Foundation password reset code",
                name is null ? "Here is the code to set a new password." : $"Here is the code to set a new password for {name}."),
            CodePurpose.AddEmail => ("Confirm your Foundation recovery email",
                name is null ? "Here is the code to confirm this recovery email." : $"Here is the code to confirm this recovery email for {name}."),
            _ => throw new ArgumentOutOfRangeException(nameof(purpose)),
        };
        // several pilots share this email: the reset page asks which one
        if (purpose == CodePurpose.Recovery && name is not null && name.Contains(" or ", StringComparison.Ordinal))
            lead += " On the page, type the pilot name you want to reset.";
        const string tail = "It is valid for 10 minutes and works once. If you didn't ask for it, ignore this email.";

        var text = $"{lead}\n\n    {code}\n\nType it on the page where you asked for it. {tail}\n\nSubSpace Foundation · {Site}\n";
        var html =
            "<!doctype html><html><body style=\"margin:0;padding:24px;background:#05070d;color:#c6d6e6;font-family:Menlo,Consolas,monospace\">"
            + $"<p style=\"margin:0 0 16px;color:#ff8a1f;letter-spacing:2px;font-weight:bold\">SUBSPACE FOUNDATION</p>"
            + $"<p style=\"margin:0 0 16px\">{WebUtility.HtmlEncode(lead)}</p>"
            + $"<p style=\"margin:0 0 16px;font-size:32px;letter-spacing:8px;font-weight:bold;color:#22d3ee\">{WebUtility.HtmlEncode(code)}</p>"
            + $"<p style=\"margin:0 0 16px\">Type it on the page where you asked for it. {WebUtility.HtmlEncode(tail)}</p>"
            + $"<p style=\"margin:0;color:#7d8ea3\">{Site}</p>"
            + "</body></html>";
        return (subject, text, html);
    }

    /// <summary>For logs: <c>mike@x.com</c> → <c>m***@x.com</c>.</summary>
    public static string Mask(string email)
    {
        var at = (email ?? "").IndexOf('@');
        return at <= 0 ? "***" : email![0] + "***" + email[at..];
    }
}

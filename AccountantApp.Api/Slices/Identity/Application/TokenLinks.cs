using Microsoft.Extensions.Options;

namespace AccountantApp.Api.Slices.Identity.Application;

public sealed class IdentityLinkOptions
{
    /// <summary>
    /// The externally reachable origin of the front end, e.g. "https://app.example.com". No trailing
    /// slash is required -- it is trimmed. Bound from App:BaseUrl and validated at startup, because a
    /// missing value here does not break anything until the first invitation email goes out with a
    /// link to nowhere, and by then the token has been consumed by nobody and the person is stuck.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;
}

/// <summary>
/// Builds the two links that carry a raw token. Centralised so the query parameter name cannot drift
/// between the email and the page that reads it -- a mismatch there produces "invalid token" for every
/// user, with a token that is perfectly valid.
/// </summary>
public sealed class TokenLinks
{
    private readonly string _baseUrl;

    public TokenLinks(IOptions<IdentityLinkOptions> options)
    {
        _baseUrl = (options.Value.BaseUrl ?? string.Empty).TrimEnd('/');
    }

    public string AcceptInvitation(string rawToken) =>
        $"{_baseUrl}/accept-invitation?token={Uri.EscapeDataString(rawToken)}";

    public string CompletePasswordReset(string rawToken) =>
        $"{_baseUrl}/reset-password?token={Uri.EscapeDataString(rawToken)}";
}

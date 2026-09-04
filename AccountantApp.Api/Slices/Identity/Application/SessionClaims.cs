using System.Security.Claims;
using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Slices.Identity.Application.Dtos;
using AccountantApp.Api.Slices.Identity.Core;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace AccountantApp.Api.Slices.Identity.Application;

/// <summary>
/// The claims written at sign-in, in one place so the login path and the re-issue after a password
/// change cannot drift apart.
/// </summary>
public static class SessionClaims
{
    public const string Scheme = CookieAuthenticationDefaults.AuthenticationScheme;

    public const string CustomerId = "customer_id";
    public const string DisplayName = "display_name";
    public const string MustChangePassword = "must_change_password";

    public static ClaimsPrincipal Build(UserAccount account)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, account.Id.ToString()),
            new(ClaimTypes.Role, account.Role.ToString()),
            new(DisplayName, account.DisplayName),
            new(MustChangePassword, account.MustChangePassword ? "true" : "false")
        };

        // Omit entirely for Accountants; never write an empty string. CurrentUserFactory treats a
        // present-but-unparseable value as a 401, so "" would lock out every Accountant.
        //
        // Mandatory for the two Customer-side roles: the factory throws 401 without it, so such a
        // session is not degraded, it is broken -- and it fails on the NEXT request, with no useful
        // message, which looks nothing like the missing claim that caused it.
        if (account.CustomerId is { } customerId)
            claims.Add(new Claim(CustomerId, customerId.ToString()));

        // Nothing else. A claim is a snapshot taken at login and valid for up to 8 hours, so do not
        // add one for anything a handler can look up and that can change. Customer status in
        // particular must never become a claim -- it is read live on every login.
        //
        // must_change_password is the one mutable value that is a claim, and it is safe in the one
        // direction that matters: the flag only ever goes true -> false, and the handler that clears
        // it re-issues the cookie in the same request. A stale `true` costs one extra prompt; a stale
        // `false` would be the bug, and it cannot happen.
        return new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme));
    }

    public static SessionDto ToSessionDto(UserAccount account) => new(
        account.Id.ToString(),
        account.DisplayName,
        account.Role,
        account.CustomerId,
        account.MustChangePassword);
}

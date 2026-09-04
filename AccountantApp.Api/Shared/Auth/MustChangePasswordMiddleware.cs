using AccountantApp.Api.Slices.Identity.Application;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace AccountantApp.Api.Shared.Auth;

/// <summary>
/// A user whose must_change_password flag is set may do exactly one thing: change their password.
/// Everything else is 403 until they do.
///
/// Middleware rather than a check in every handler, because "every handler" is a rule that holds until
/// the next handler is written. Here it is structural: a new endpoint is covered the moment it is
/// mapped, with nobody having to remember.
/// </summary>
public sealed class MustChangePasswordMiddleware
{
    /// <summary>
    /// The allow-list, and it must stay this short.
    ///
    /// /change-password is the obvious one -- blocking it would trap the user in a loop where the only
    /// permitted action is the one they are forbidden from taking.
    ///
    /// /logout is the non-obvious one, and leaving it out is the bug that makes this middleware feel
    /// broken: a user who does not want to change their password right now would have no way out except
    /// clearing cookies by hand.
    ///
    /// /me is here because the front end calls it to discover the flag in the first place. A 403 on /me
    /// means the client cannot learn WHY it is being refused everywhere else, and shows a generic error
    /// instead of the change-password screen.
    /// </summary>
    private static readonly string[] AllowedPaths =
    [
        "/api/auth/change-password",
        "/api/auth/logout",
        "/api/auth/me"
    ];

    private readonly RequestDelegate _next;

    public MustChangePasswordMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Unauthenticated requests pass straight through. This middleware is not an authentication gate:
        // login, the reset flow and accept-invitation must all remain reachable, and rejecting anonymous
        // requests here would make the login endpoint itself unreachable.
        if (context.User?.Identity?.IsAuthenticated != true)
        {
            await _next(context);
            return;
        }

        // Reads the CLAIM, not the database. A query on every single request to answer a question that is
        // false for almost every user is the wrong trade, and the claim cannot be stale in the direction
        // that matters: the flag only ever goes true -> false, and ChangeOwnPasswordHandler re-issues the
        // cookie in the same request that clears it.
        var mustChange = string.Equals(
            context.User.FindFirst(SessionClaims.MustChangePassword)?.Value,
            "true",
            StringComparison.OrdinalIgnoreCase);

        if (!mustChange)
        {
            await _next(context);
            return;
        }

        var path = context.Request.Path.Value ?? string.Empty;
        if (AllowedPaths.Any(allowed => path.Equals(allowed, StringComparison.OrdinalIgnoreCase)))
        {
            await _next(context);
            return;
        }

        // 403, not 401. The caller IS authenticated -- re-authenticating would change nothing, and a 401
        // typically drives a client to the login screen, which is the one place that cannot help.
        //
        // ProblemDetails, written directly, because this short-circuits before any handler and therefore
        // before AppExceptionMiddleware has anything to catch.
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = StatusCodes.Status403Forbidden,
            Title = ReasonPhrases.GetReasonPhrase(StatusCodes.Status403Forbidden),
            // A distinguishable, stable message: the front end matches on it to decide to show the
            // change-password screen rather than a generic "not allowed".
            Detail = "You must change your password before continuing.",
            Extensions = { ["traceId"] = context.TraceIdentifier }
        });
    }
}

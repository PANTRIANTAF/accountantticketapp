using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Pagination;
using AccountantApp.Api.Slices.Employees.Application.Dtos;
using AccountantApp.Api.Slices.Employees.Application.Handlers;
using Microsoft.AspNetCore.Mvc;

namespace AccountantApp.Api.Slices.Employees;

public static class EmployeesEndpoints
{
    public static void MapEmployeesEndpoints(this IEndpointRouteBuilder app)
    {
        MapEmployeeRoutes(app);
        MapOnboardingRoute(app);
    }

    private static void MapEmployeeRoutes(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/employees").WithTags("Employees");

        group.MapPost("/register", async (
                RegisterEmployeeRequestDto request,
                RegisterEmployeeHandler handler,
                CurrentUser user,
                CancellationToken ct) =>
            Results.Ok(await handler.Handle(request, user, ct)))
            .WithName("RegisterEmployee")
            .WithDescription(
                "Creates an accountless Employee. No login is created and no email is sent -- use " +
                "/api/employees/invite for that.")
            .Produces<EmployeeDetailDto>()
            .Produces<ProblemDetails>(403)
            .Produces<ProblemDetails>(409)
            .Produces<ProblemDetails>(422);

        group.MapPost("/list", async (
                ListEmployeesRequestDto request,
                ListEmployeesHandler handler,
                CurrentUser user,
                CancellationToken ct) =>
            Results.Ok(await handler.Handle(request, user, ct)))
            .WithName("ListEmployees")
            .WithDescription(
                "Returns both Active and Departed Employees unless a status filter is supplied. Role is " +
                "null for an Employee who has never been invited.")
            .Produces<PaginatedResponse<EmployeeSummaryDto>>()
            .Produces<ProblemDetails>(403)
            .Produces<ProblemDetails>(422);

        group.MapPost("/get", async (
                EmployeeIdRequestDto request,
                GetEmployeeHandler handler,
                CurrentUser user,
                CancellationToken ct) =>
            Results.Ok(await handler.Handle(request, user, ct)))
            .WithName("GetEmployee")
            // Declares the detail shape and documents the narrowing rather than silently declaring one of
            // two: a caller with the Employee role receives EmployeeSelfDto, which is EmployeeDetailDto
            // without the status, the account link, the employment end date, or either personal identifying
            // number.
            .WithDescription(
                "Accountants and the owning Customer's Admins receive the full record. A caller with the " +
                "Employee role receives the narrower EmployeeSelfDto for their OWN record and a 404 for " +
                "anybody else's, including colleagues at their own customer.")
            .Produces<EmployeeDetailDto>()
            .Produces<ProblemDetails>(403)
            .Produces<ProblemDetails>(404);

        group.MapPost("/update", async (
                UpdateEmployeeRequestDto request,
                UpdateEmployeeHandler handler,
                CurrentUser user,
                CancellationToken ct) =>
            Results.Ok(await handler.Handle(request, user, ct)))
            .WithName("UpdateEmployee")
            .WithDescription(
                "Changing the work email does NOT change the address this person signs in with. The login " +
                "email lives on their account -- use /api/employees/change-login-email, which is " +
                "Accountant-only.")
            .Produces<EmployeeDetailDto>()
            .Produces<ProblemDetails>(403)
            .Produces<ProblemDetails>(404)
            .Produces<ProblemDetails>(409)
            .Produces<ProblemDetails>(422);

        group.MapPost("/update-own-contact", async (
                UpdateOwnContactRequestDto request,
                UpdateOwnContactHandler handler,
                CurrentUser user,
                CancellationToken ct) =>
            Results.Ok(await handler.Handle(request, user, ct)))
            .WithName("UpdateOwnContact")
            .WithDescription(
                "Edits the caller's OWN contact details. Takes no employee id: the record is resolved from " +
                "the session, so this endpoint cannot edit a colleague. Changing the work email here does " +
                "not change how you log in -- only the accounting office can change that address.")
            .Produces<EmployeeSelfDto>()
            .Produces<ProblemDetails>(403)
            .Produces<ProblemDetails>(404)
            .Produces<ProblemDetails>(409)
            .Produces<ProblemDetails>(422);

        group.MapPost("/invite", async (
                InviteEmployeeRequestDto request,
                InviteEmployeeHandler handler,
                CurrentUser user,
                CancellationToken ct) =>
            Results.Ok(await handler.Handle(request, user, ct)))
            .WithName("InviteEmployee")
            .WithDescription(
                "Creates the Employee's login and emails them an invitation. The token is never returned " +
                "in the response -- it goes to the invitee's mailbox and nowhere else.")
            .Produces<EmployeeDetailDto>()
            .Produces<ProblemDetails>(403)
            .Produces<ProblemDetails>(404)
            .Produces<ProblemDetails>(409)
            .Produces<ProblemDetails>(422);

        group.MapPost("/set-role", async (
                SetEmployeeRoleRequestDto request,
                SetEmployeeRoleHandler handler,
                CurrentUser user,
                CancellationToken ct) =>
            Results.Ok(await handler.Handle(request, user, ct)))
            .WithName("SetEmployeeRole")
            .WithDescription(
                "Promotes an Employee to CustomerAdmin or demotes them back. A request naming either " +
                "Accountant role is rejected. The target's existing session keeps the old role until it " +
                "expires.")
            .Produces<MarkedResultDto>()
            .Produces<ProblemDetails>(403)
            .Produces<ProblemDetails>(404)
            .Produces<ProblemDetails>(422);

        group.MapPost("/depart", async (
                DepartEmployeeRequestDto request,
                DepartEmployeeHandler handler,
                CurrentUser user,
                CancellationToken ct) =>
            Results.Ok(await handler.Handle(request, user, ct)))
            .WithName("DepartEmployee")
            .WithDescription(
                "Marks the Employee as having left and suspends their account in the same transaction. " +
                "Reversible only as a correction, through /api/employees/reinstate -- somebody who " +
                "genuinely returns after leaving is registered again as a new record.")
            .Produces<MarkedResultDto>()
            .Produces<ProblemDetails>(403)
            .Produces<ProblemDetails>(404)
            .Produces<ProblemDetails>(422);

        group.MapPost("/reinstate", async (
                EmployeeIdRequestDto request,
                ReinstateEmployeeHandler handler,
                CurrentUser user,
                CancellationToken ct) =>
            Results.Ok(await handler.Handle(request, user, ct)))
            .WithName("ReinstateEmployee")
            .WithDescription(
                "Undoes a departure entered by mistake: the Employee returns to Active and their account " +
                "is reactivated in the same transaction. Refused for an Employee who has not departed and " +
                "for a suspended Customer. Not for re-hiring -- register a returning employee again.")
            .Produces<MarkedResultDto>()
            .Produces<ProblemDetails>(403)
            .Produces<ProblemDetails>(404)
            .Produces<ProblemDetails>(422);

        group.MapPost("/change-login-email", async (
                ChangeEmployeeLoginEmailRequestDto request,
                ChangeEmployeeLoginEmailHandler handler,
                CurrentUser user,
                CancellationToken ct) =>
            Results.Ok(await handler.Handle(request, user, ct)))
            .WithName("ChangeEmployeeLoginEmail")
            .WithDescription(
                "Changes the address the Employee SIGNS IN WITH. Accountants only -- neither a Customer " +
                "Admin nor the person themselves may change it. Leaves the work email, the password and " +
                "any live session alone; the next login uses the new address.")
            .Produces<MarkedResultDto>()
            .Produces<ProblemDetails>(403)
            .Produces<ProblemDetails>(404)
            .Produces<ProblemDetails>(409)
            .Produces<ProblemDetails>(422);

        group.MapPost("/suspend-account", async (
                EmployeeIdRequestDto request,
                SuspendEmployeeAccountHandler handler,
                CurrentUser user,
                CancellationToken ct) =>
            Results.Ok(await handler.Handle(request, user, ct)))
            .WithName("SuspendEmployeeAccount")
            .WithDescription(
                "Revokes access without ending employment. Does not mark the Employee as departed.")
            .Produces<MarkedResultDto>()
            .Produces<ProblemDetails>(403)
            .Produces<ProblemDetails>(404)
            .Produces<ProblemDetails>(422);

        group.MapPost("/reactivate-account", async (
                EmployeeIdRequestDto request,
                ReactivateEmployeeAccountHandler handler,
                CurrentUser user,
                CancellationToken ct) =>
            Results.Ok(await handler.Handle(request, user, ct)))
            .WithName("ReactivateEmployeeAccount")
            .WithDescription(
                "Restores a suspended Employee's access. Refused for a departed Employee, and does not " +
                "reset a password or clear a lockout.")
            .Produces<MarkedResultDto>()
            .Produces<ProblemDetails>(403)
            .Produces<ProblemDetails>(404)
            .Produces<ProblemDetails>(422);
    }

    /// <summary>
    /// A /api/customers/* route registered from EmployeesEndpoints, on purpose and LOCKED
    /// (03-SliceInventory.md section 1: "Customer onboarding is one operation, and it lives in Employees").
    ///
    /// It is here because this slice owns two of the operation's three steps -- the first Employee and their
    /// invitation -- and therefore owns the transaction that makes all three atomic. Customers may not depend
    /// on Employees or Identity, so moving this into CustomersEndpoints would create a dependency cycle, and
    /// splitting it into two calls would let a Customer exist with nobody able to log into it.
    ///
    /// Do not "tidy" this into the Customers slice.
    /// </summary>
    private static void MapOnboardingRoute(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/customers/onboard", async (
                OnboardCustomerRequestDto request,
                OnboardCustomerHandler handler,
                CurrentUser user,
                CancellationToken ct) =>
            Results.Ok(await handler.Handle(request, user, ct)))
            .WithTags("Employees")
            .WithName("OnboardCustomer")
            .WithDescription(
                "Creates a customer, its first employee, and that employee's CustomerAdmin invitation in one " +
                "transaction. AccountantAdmin only. A failure at any step leaves nothing behind.")
            .Produces<OnboardCustomerResponseDto>()
            .Produces<ProblemDetails>(403)
            .Produces<ProblemDetails>(409)
            .Produces<ProblemDetails>(422);
    }
}

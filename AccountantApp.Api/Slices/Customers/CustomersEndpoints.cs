using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Pagination;
using AccountantApp.Api.Slices.Customers.Application.Dtos;
using AccountantApp.Api.Slices.Customers.Application.Handlers;
using Microsoft.AspNetCore.Mvc;

namespace AccountantApp.Api.Slices.Customers;

public static class CustomersEndpoints
{
    public static void MapCustomersEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/customers").WithTags("Customers");

        group.MapPost("/create", async (
                CreateCustomerRequestDto request,
                CreateCustomerHandler handler,
                CurrentUser user,
                CancellationToken ct) =>
            {
                var result = await handler.Handle(request, user, ct);
                return Results.Created($"/api/customers/detail?customerId={result.Id}", result);
            })
            .WithName("CreateCustomer")
            .Produces<CustomerDto>(201)
            .Produces<ProblemDetails>(403)
            .Produces<ProblemDetails>(409)
            .Produces<ProblemDetails>(422);

        group.MapPost("/list", async (
                ListCustomersRequestDto request,
                ListCustomersHandler handler,
                CurrentUser user,
                CancellationToken ct) =>
            Results.Ok(await handler.Handle(request, user, ct)))
            .WithName("ListCustomers")
            .Produces<PaginatedResponse<CustomerSummaryDto>>()
            .Produces<ProblemDetails>(403)
            .Produces<ProblemDetails>(422);

        group.MapGet("/detail", async (
                Guid customerId,
                GetCustomerHandler handler,
                CurrentUser user,
                CancellationToken ct) =>
            Results.Ok(await handler.Handle(
                new GetCustomerRequestDto { CustomerId = customerId }, user, ct)))
            .WithName("GetCustomer")
            .Produces<CustomerDto>()
            .Produces<ProblemDetails>(403)
            .Produces<ProblemDetails>(404);

        group.MapGet("/own", async (
                GetOwnCustomerHandler handler,
                CurrentUser user,
                CancellationToken ct) =>
            Results.Ok(await handler.Handle(user, ct)))
            .WithName("GetOwnCustomer")
            .Produces<CustomerSelfDto>()
            .Produces<ProblemDetails>(401)
            .Produces<ProblemDetails>(404);

        group.MapPost("/update-contact", async (
                UpdateCustomerContactRequestDto request,
                UpdateCustomerContactHandler handler,
                CurrentUser user,
                CancellationToken ct) =>
            Results.Ok(await handler.Handle(request, user, ct)))
            .WithName("UpdateCustomerContact")
            .Produces<CustomerDto>()
            .Produces<ProblemDetails>(403)
            .Produces<ProblemDetails>(404)
            .Produces<ProblemDetails>(422);

        group.MapPost("/update-legal", async (
                UpdateCustomerLegalRequestDto request,
                UpdateCustomerLegalHandler handler,
                CurrentUser user,
                CancellationToken ct) =>
            Results.Ok(await handler.Handle(request, user, ct)))
            .WithName("UpdateCustomerLegal")
            .Produces<CustomerDto>()
            .Produces<ProblemDetails>(403)
            .Produces<ProblemDetails>(404)
            .Produces<ProblemDetails>(409)
            .Produces<ProblemDetails>(422);

        group.MapPost("/suspend", async (
                SetCustomerStatusRequestDto request,
                SuspendCustomerHandler handler,
                CurrentUser user,
                CancellationToken ct) =>
            Results.Ok(await handler.Handle(request, user, ct)))
            .WithName("SuspendCustomer")
            .Produces<CustomerDto>()
            .Produces<ProblemDetails>(403)
            .Produces<ProblemDetails>(404)
            .Produces<ProblemDetails>(422);

        group.MapPost("/reactivate", async (
                SetCustomerStatusRequestDto request,
                ReactivateCustomerHandler handler,
                CurrentUser user,
                CancellationToken ct) =>
            Results.Ok(await handler.Handle(request, user, ct)))
            .WithName("ReactivateCustomer")
            .Produces<CustomerDto>()
            .Produces<ProblemDetails>(403)
            .Produces<ProblemDetails>(404)
            .Produces<ProblemDetails>(422);
    }
}
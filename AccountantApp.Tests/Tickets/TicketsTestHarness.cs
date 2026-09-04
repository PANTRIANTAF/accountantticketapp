using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Slices.Tickets.Core;
using AccountantApp.Api.Slices.Tickets.Infrastructure;
using AccountantApp.Api.Slices.TicketTypes.ExternalInterfaces;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Tests.Tickets;

/// <summary>
/// Fixtures for the Tickets foundation tests.
///
/// These tests cover the schema-independent half of the slice: the state machine, the visibility
/// layers, field validation and the concurrency token. Handlers and endpoints are a later piece of
/// work, so nothing here goes through HTTP -- the helpers are exercised directly, which is also the
/// only way to test them at all before the handlers exist.
/// </summary>
internal static class TicketsTestHarness
{
    public static TicketsDbContext NewDb() =>
        new(new DbContextOptionsBuilder<TicketsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>
    /// An Accountant session. No CustomerId, because an Accountant is not Customer-scoped, and the Id is
    /// an ACCOUNT id -- the same thing CurrentUserFactory puts there.
    /// </summary>
    public static CurrentUser Accountant(Guid accountId, UserRole role = UserRole.AccountantAdmin) =>
        new(accountId.ToString(), role);

    public static CurrentUser CustomerSide(Guid accountId, UserRole role, Guid customerId) =>
        new(accountId.ToString(), role, customerId);

    public static Ticket NewTicket(
        Guid customerId,
        Guid creatorAccountId,
        Guid subjectEmployeeId,
        string status = TicketStatus.Draft,
        Guid? assignee = null,
        string? reference = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            Reference = reference ?? $"TKT-2026-{Random.Shared.Next(1, 999999):D6}",
            CustomerId = customerId,
            TicketTypeId = Guid.NewGuid(),
            TicketTypeVersionId = Guid.NewGuid(),
            CreatorUserAccountId = creatorAccountId,
            SubjectEmployeeId = subjectEmployeeId,
            Status = status,
            AssigneeUserAccountId = assignee,
            Priority = TicketPriority.Normal,
            Title = "A ticket",
            Version = 1,
            CreatedAt = Now,
            LastActivityAt = Now,
        };

    public static readonly DateTimeOffset Now =
        new(2026, 9, 2, 10, 0, 0, TimeSpan.Zero);

    // --- Ticket type descriptor fixtures, for the FieldValueValidation tests ---

    public static TicketTypeDetailDto TypeWith(params FieldDescriptorDetailDto[] fields) =>
        new()
        {
            Id = Guid.NewGuid(),
            Code = "TEST",
            DisplayName = "Test type",
            Category = "Test",
            IsActive = true,
            CurrentVersionNumber = 1,
            VersionNumber = 1,
            Fields = [.. fields],
        };

    public static FieldDescriptorDetailDto Field(
        string key,
        string dataType,
        bool isRequired = false,
        bool isVisibleToCustomer = true,
        FieldValidationDto? validation = null,
        List<ChoiceOptionDto>? choices = null,
        ConditionalVisibilityDto? conditional = null) =>
        new()
        {
            Key = key,
            Label = key,
            DataType = dataType,
            DisplayOrder = 1,
            IsRequired = isRequired,
            IsVisibleToCustomer = isVisibleToCustomer,
            ChoiceOptions = choices ?? [],
            Validation = validation ?? new FieldValidationDto(),
            ConditionalVisibility = conditional,
        };
}

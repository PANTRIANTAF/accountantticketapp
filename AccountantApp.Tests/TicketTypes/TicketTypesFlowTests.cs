using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Tests.TestDoubles;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.TicketTypes;
using AccountantApp.Api.Slices.TicketTypes.Application.Dtos;
using AccountantApp.Api.Slices.TicketTypes.Application.Handlers;
using AccountantApp.Api.Slices.TicketTypes.Core;
using AccountantApp.Api.Slices.TicketTypes.Infrastructure;
using AccountantApp.Api.Slices.TicketTypes.ExternalInterfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AccountantApp.Tests.TicketTypes;

public class TicketTypesFlowTests
{
    [Fact]
    public async Task Create_and_edit_create_a_new_version_without_mutating_the_old_one()
    {
        var options = new DbContextOptionsBuilder<TicketTypesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new TicketTypesDbContext(options);
        var audit = new TestAuditApi();
        var permissions = new PermissionChecker([new TicketTypesActionCatalogue()], audit, NullLogger<PermissionChecker>.Instance);
        var createHandler = new CreateTicketTypeHandler(db, permissions, new NoOpRequestTransaction(), audit);
        var editHandler = new EditTicketTypeHandler(db, permissions, new NoOpRequestTransaction(), audit);

        var request = new CreateTicketTypeRequestDto
        {
            Code = "PAYROLL_CERTIFICATE",
            DisplayName = "Payroll Certificate",
            Description = "Payroll certificate request",
            Category = "Payroll",
            AllowEmployeeToOpen = true,
            AllowSubjectOtherThanCreator = true,
            Fields =
            [
                new CreateFieldDescriptorDto
                {
                    Key = "employee_name",
                    Label = "Employee Name",
                    DataType = "SingleLineText",
                    DisplayOrder = 1,
                    IsRequired = true,
                    Validation = new FieldValidationDto { MinLength = 2, MaxLength = 100 },
                    ChoiceOptions = []
                }
            ]
        };

        var created = await createHandler.Handle(request, new CurrentUser("user-1", UserRole.AccountantUser), CancellationToken.None);

        Assert.Equal(1, created.CurrentVersionNumber);
        Assert.Equal(1, await db.TicketTypeVersions.CountAsync());

        var originalType = await db.TicketTypes
            .Include(t => t.Versions)
            .SingleAsync();

        var editRequest = new EditTicketTypeRequestDto
        {
            TicketTypeId = originalType.Id,
            DisplayName = "Payroll Certificate Updated",
            Description = "Updated payroll certificate request",
            Category = "Payroll",
            AllowEmployeeToOpen = true,
            AllowSubjectOtherThanCreator = true,
            Fields =
            [
                new CreateFieldDescriptorDto
                {
                    Key = "employee_name",
                    Label = "Employee Name Updated",
                    DataType = "SingleLineText",
                    DisplayOrder = 1,
                    IsRequired = true,
                    Validation = new FieldValidationDto { MinLength = 2, MaxLength = 200 },
                    ChoiceOptions = []
                }
            ]
        };

        var edited = await editHandler.Handle(editRequest, new CurrentUser("user-1", UserRole.AccountantUser), CancellationToken.None);

        Assert.Equal(2, edited.CurrentVersionNumber);
        Assert.Equal(2, await db.TicketTypeVersions.CountAsync());

        var versions = await db.TicketTypeVersions
            .Where(v => v.TicketTypeId == originalType.Id)
            .OrderBy(v => v.VersionNumber)
            .ToListAsync();

        Assert.Equal(2, versions.Count);
        Assert.Equal(1, versions[0].VersionNumber);
        Assert.Equal(2, versions[1].VersionNumber);

        var v1Fields = await db.FieldDescriptors
            .Where(f => f.TicketTypeVersionId == versions[0].Id)
            .ToListAsync();

        var v2Fields = await db.FieldDescriptors
            .Where(f => f.TicketTypeVersionId == versions[1].Id)
            .ToListAsync();

        Assert.Equal("Employee Name", v1Fields[0].Label);
        Assert.Equal("Employee Name Updated", v2Fields[0].Label);
    }

    [Fact]
    public async Task Employee_list_only_shows_active_types_openable_to_employee()
    {
        var options = new DbContextOptionsBuilder<TicketTypesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new TicketTypesDbContext(options);
        var audit = new TestAuditApi();
        var permissions = new PermissionChecker([new TicketTypesActionCatalogue()], audit, NullLogger<PermissionChecker>.Instance);
        var createHandler = new CreateTicketTypeHandler(db, permissions, new NoOpRequestTransaction(), audit);

        await createHandler.Handle(new CreateTicketTypeRequestDto
        {
            Code = "ACTIVE_EMPLOYEE_OPEN",
            DisplayName = "Active Employee Open",
            Category = "Payroll",
            AllowEmployeeToOpen = true,
            AllowSubjectOtherThanCreator = true,
            Fields =
            [
                new CreateFieldDescriptorDto
                {
                    Key = "employee_name",
                    Label = "Employee Name",
                    DataType = "SingleLineText",
                    DisplayOrder = 1,
                    IsRequired = true,
                    ChoiceOptions = []
                }
            ]
        }, new CurrentUser("user-1", UserRole.AccountantUser), CancellationToken.None);

        await createHandler.Handle(new CreateTicketTypeRequestDto
        {
            Code = "INACTIVE_EMPLOYEE_OPEN",
            DisplayName = "Inactive Employee Open",
            Category = "Payroll",
            AllowEmployeeToOpen = true,
            AllowSubjectOtherThanCreator = true,
            Fields =
            [
                new CreateFieldDescriptorDto
                {
                    Key = "employee_name",
                    Label = "Employee Name",
                    DataType = "SingleLineText",
                    DisplayOrder = 1,
                    IsRequired = true,
                    ChoiceOptions = []
                }
            ]
        }, new CurrentUser("user-1", UserRole.AccountantUser), CancellationToken.None);

        var toggleHandler = new ToggleTicketTypeHandler(db, permissions, new NoOpRequestTransaction(), audit);
        var inactive = await db.TicketTypes.SingleAsync(t => t.Code == "INACTIVE_EMPLOYEE_OPEN");
        await toggleHandler.Handle(new ToggleTicketTypeRequestDto { TicketTypeId = inactive.Id, NewIsActive = false }, new CurrentUser("user-1", UserRole.AccountantUser), CancellationToken.None);

        var listHandler = new ListTicketTypesHandler(db, permissions);
        var items = await listHandler.Handle(new ListTicketTypesRequestDto { PageNumber = 1, PageSize = 20 }, new CurrentUser("user-1", UserRole.Employee), CancellationToken.None);

        Assert.Equal(1, items.TotalCount);
        Assert.Equal("ACTIVE_EMPLOYEE_OPEN", items.Items[0].Code);
    }

    [Fact]
    public async Task Customer_side_detail_strips_accountant_only_fields()
    {
        var options = new DbContextOptionsBuilder<TicketTypesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new TicketTypesDbContext(options);
        var audit = new TestAuditApi();
        var permissions = new PermissionChecker([new TicketTypesActionCatalogue()], audit, NullLogger<PermissionChecker>.Instance);
        var createHandler = new CreateTicketTypeHandler(db, permissions, new NoOpRequestTransaction(), audit);
        var created = await createHandler.Handle(new CreateTicketTypeRequestDto
        {
            Code = "PRIVATE_FIELD_TEST",
            DisplayName = "Private Field Test",
            Category = "Payroll",
            Fields =
            [
                new CreateFieldDescriptorDto { Key = "public", Label = "Public", DataType = "SingleLineText", DisplayOrder = 1 },
                new CreateFieldDescriptorDto { Key = "private", Label = "Private", DataType = "SingleLineText", DisplayOrder = 2, IsVisibleToCustomer = false }
            ]
        }, new CurrentUser("accountant", UserRole.AccountantUser), CancellationToken.None);

        var detail = await new GetTicketTypeHandler(db, permissions).Handle(
            new GetTicketTypeRequestDto { TicketTypeId = created.Id },
            new CurrentUser("employee", UserRole.Employee), CancellationToken.None);

        Assert.Single(detail.Fields);
        Assert.Equal("public", detail.Fields[0].Key);
    }

    [Fact]
    public async Task Accountant_list_includes_inactive_types_by_default()
    {
        var options = new DbContextOptionsBuilder<TicketTypesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new TicketTypesDbContext(options);
        db.TicketTypes.Add(new TicketType
        {
            Code = "INACTIVE",
            DisplayName = "Inactive",
            Category = "Payroll",
            IsActive = false
        });
        await db.SaveChangesAsync();

        var permissions = new PermissionChecker([new TicketTypesActionCatalogue()], new TestAuditApi(), NullLogger<PermissionChecker>.Instance);
        var result = await new ListTicketTypesHandler(db, permissions).Handle(
            new ListTicketTypesRequestDto(),
            new CurrentUser("accountant", UserRole.AccountantUser), CancellationToken.None);

        Assert.Single(result.Items);
        Assert.False(result.Items[0].IsActive);
    }

    [Fact]
    public async Task Invalid_field_sets_are_rejected_before_persistence()
    {
        await using var db = CreateDb();
        var audit = new TestAuditApi();
        var handler = new CreateTicketTypeHandler(db, new PermissionChecker([new TicketTypesActionCatalogue()], audit, NullLogger<PermissionChecker>.Instance), new NoOpRequestTransaction(), audit);
        var request = new CreateTicketTypeRequestDto
        {
            Code = "INVALID",
            DisplayName = "Invalid",
            Category = "Payroll",
            Fields =
            [
                new CreateFieldDescriptorDto { Key = "duplicate", Label = "One", DataType = "SingleLineText" },
                new CreateFieldDescriptorDto { Key = "DUPLICATE", Label = "Two", DataType = "SingleLineText" }
            ]
        };

        var error = await Assert.ThrowsAsync<AppException>(() => handler.Handle(
            request, new CurrentUser("accountant", UserRole.AccountantUser), CancellationToken.None));

        Assert.Equal(422, error.StatusCode);
        Assert.Empty(db.TicketTypes);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task Customer_admin_write_is_denied_and_audited_once()
    {
        await using var db = CreateDb();
        var audit = new TestAuditApi();
        var handler = new CreateTicketTypeHandler(db, new PermissionChecker([new TicketTypesActionCatalogue()], audit, NullLogger<PermissionChecker>.Instance), new NoOpRequestTransaction(), audit);

        var error = await Assert.ThrowsAsync<AppException>(() => handler.Handle(
            new CreateTicketTypeRequestDto(),
            new CurrentUser("customer", UserRole.CustomerAdmin),
            CancellationToken.None));

        Assert.Equal(403, error.StatusCode);
        Assert.Single(audit.Entries);
        Assert.Equal("PermissionDenied", audit.Entries[0].Action);
    }

    [Fact]
    public async Task Specific_version_reports_both_requested_and_current_version_numbers()
    {
        await using var db = CreateDb();
        var audit = new TestAuditApi();
        var permissions = new PermissionChecker([new TicketTypesActionCatalogue()], audit, NullLogger<PermissionChecker>.Instance);
        var create = new CreateTicketTypeHandler(db, permissions, new NoOpRequestTransaction(), audit);
        var edit = new EditTicketTypeHandler(db, permissions, new NoOpRequestTransaction(), audit);
        var created = await create.Handle(ValidRequest("VERSIONS", "Original"),
            new CurrentUser("accountant", UserRole.AccountantUser), CancellationToken.None);
        await edit.Handle(new EditTicketTypeRequestDto
        {
            TicketTypeId = created.Id,
            DisplayName = "Updated",
            Category = "Payroll",
            Fields = ValidRequest("IGNORED", "Updated").Fields
        }, new CurrentUser("accountant", UserRole.AccountantUser), CancellationToken.None);

        var result = await new GetTicketTypeVersionHandler(db, permissions).Handle(
            new GetTicketTypeVersionRequestDto { TicketTypeId = created.Id, VersionNumber = 1 },
            new CurrentUser("accountant", UserRole.AccountantAdmin), CancellationToken.None);

        Assert.Equal(2, result.CurrentVersionNumber);
        Assert.Equal(1, result.VersionNumber);
        Assert.Equal("Original", result.Fields[0].Label);
    }

    [Fact]
    public async Task External_api_strips_private_fields_for_customer_roles()
    {
        await using var db = CreateDb();
        var audit = new TestAuditApi();
        var create = new CreateTicketTypeHandler(db, new PermissionChecker([new TicketTypesActionCatalogue()], audit, NullLogger<PermissionChecker>.Instance), new NoOpRequestTransaction(), audit);
        var request = ValidRequest("EXTERNAL", "Public");
        request.Fields.Add(new CreateFieldDescriptorDto
        {
            Key = "private",
            Label = "Private",
            DataType = "SingleLineText",
            DisplayOrder = 2,
            IsVisibleToCustomer = false
        });
        var created = await create.Handle(request,
            new CurrentUser("accountant", UserRole.AccountantUser), CancellationToken.None);

        var result = await new TicketTypesApi(db).GetTicketTypeAsync(
            created.Id, UserRole.CustomerAdmin, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Single(result.Fields);
        Assert.Equal("public", result.Fields[0].Key);
    }

    [Theory]
    // Each string here is longer than the VARCHAR(n) of the column it lands in. Without
    // pre-save validation PostgreSQL raises 22001 and the caller receives a 500.
    [InlineData("DisplayName")]
    [InlineData("Category")]
    [InlineData("Code")]
    [InlineData("Label")]
    [InlineData("GroupName")]
    public async Task Over_length_strings_are_rejected_with_422(string property)
    {
        await using var db = CreateDb();
        var audit = new TestAuditApi();
        var handler = new CreateTicketTypeHandler(
            db, new PermissionChecker([new TicketTypesActionCatalogue()], audit, NullLogger<PermissionChecker>.Instance), new NoOpRequestTransaction(), audit);

        var request = ValidRequest("LENGTHS", "Lengths");
        var tooLong = new string('x', 600);
        switch (property)
        {
            case "DisplayName": request.DisplayName = tooLong; break;
            case "Category": request.Category = tooLong; break;
            case "Code": request.Code = tooLong; break;
            case "Label": request.Fields[0].Label = tooLong; break;
            case "GroupName": request.Fields[0].GroupName = tooLong; break;
        }

        var error = await Assert.ThrowsAsync<AppException>(() => handler.Handle(
            request, new CurrentUser("accountant", UserRole.AccountantUser), CancellationToken.None));

        Assert.Equal(422, error.StatusCode);
        Assert.Empty(await db.TicketTypes.ToListAsync());
    }

    [Fact]
    public async Task An_uncompilable_regex_pattern_is_rejected_with_422()
    {
        await using var db = CreateDb();
        var audit = new TestAuditApi();
        var handler = new CreateTicketTypeHandler(
            db, new PermissionChecker([new TicketTypesActionCatalogue()], audit, NullLogger<PermissionChecker>.Instance), new NoOpRequestTransaction(), audit);

        var request = ValidRequest("BAD_REGEX", "Bad Regex");
        request.Fields[0].Validation = new FieldValidationDto { RegexPattern = "([unclosed" };

        var error = await Assert.ThrowsAsync<AppException>(() => handler.Handle(
            request, new CurrentUser("accountant", UserRole.AccountantUser), CancellationToken.None));

        Assert.Equal(422, error.StatusCode);
        Assert.Empty(await db.TicketTypes.ToListAsync());
    }

    [Fact]
    public async Task A_valid_regex_pattern_is_stored()
    {
        await using var db = CreateDb();
        var audit = new TestAuditApi();
        var handler = new CreateTicketTypeHandler(
            db, new PermissionChecker([new TicketTypesActionCatalogue()], audit, NullLogger<PermissionChecker>.Instance), new NoOpRequestTransaction(), audit);

        var request = ValidRequest("GOOD_REGEX", "Good Regex");
        request.Fields[0].Validation = new FieldValidationDto { RegexPattern = @"^\d{9}$" };

        var created = await handler.Handle(
            request, new CurrentUser("accountant", UserRole.AccountantUser), CancellationToken.None);

        Assert.Equal(@"^\d{9}$", created.Fields[0].Validation.RegexPattern);
    }

    [Fact]
    public async Task A_denial_is_audited_even_when_the_audit_write_fails()
    {
        await using var db = CreateDb();
        var failing = new ThrowingAuditApi();
        var handler = new CreateTicketTypeHandler(
            db, new PermissionChecker([new TicketTypesActionCatalogue()], failing, NullLogger<PermissionChecker>.Instance), new NoOpRequestTransaction(), failing);

        // The audit write throws. The caller must still get the 403, never a 500.
        var error = await Assert.ThrowsAsync<AppException>(() => handler.Handle(
            ValidRequest("AUDIT_DOWN", "Audit Down"),
            new CurrentUser("employee", UserRole.Employee), CancellationToken.None));

        Assert.Equal(403, error.StatusCode);
        Assert.True(failing.WasCalled);
    }

    // Correction note T-4 covers the version-by-number read staying open after deactivation. It
    // was applied to GetTicketTypeVersionHandler and missed in TicketTypesApi, which is the path
    // it was about: Tickets renders a historical ticket's fields through this interface, not
    // through the HTTP endpoint.
    [Fact]
    public async Task External_api_still_serves_a_version_of_a_deactivated_type_but_not_the_type_itself()
    {
        await using var db = CreateDb();
        var audit = new TestAuditApi();
        var permissions = new PermissionChecker([new TicketTypesActionCatalogue()], audit, NullLogger<PermissionChecker>.Instance);
        var accountant = new CurrentUser("accountant", UserRole.AccountantUser);
        var created = await new CreateTicketTypeHandler(db, permissions, new NoOpRequestTransaction(), audit)
            .Handle(ValidRequest("DEACTIVATED", "Original"), accountant, CancellationToken.None);
        await new ToggleTicketTypeHandler(db, permissions, new NoOpRequestTransaction(), audit).Handle(
            new ToggleTicketTypeRequestDto { TicketTypeId = created.Id, NewIsActive = false },
            accountant, CancellationToken.None);

        var api = new TicketTypesApi(db);

        var version = await api.GetTicketTypeVersionAsync(created.Id, 1, UserRole.Employee, CancellationToken.None);
        Assert.NotNull(version);
        Assert.Equal(1, version.VersionNumber);
        Assert.False(version.IsActive);

        // Discovery is still closed: the Employee may read the version their ticket names, and
        // may not find the type in order to open a new ticket against it.
        Assert.Null(await api.GetTicketTypeAsync(created.Id, UserRole.Employee, CancellationToken.None));
        Assert.NotNull(await api.GetTicketTypeAsync(created.Id, UserRole.AccountantUser, CancellationToken.None));
    }

    [Fact]
    public async Task External_api_hides_a_version_of_an_accountant_only_type_from_an_employee()
    {
        await using var db = CreateDb();
        var audit = new TestAuditApi();
        var permissions = new PermissionChecker([new TicketTypesActionCatalogue()], audit, NullLogger<PermissionChecker>.Instance);
        var request = ValidRequest("ACCOUNTANT_ONLY", "Original");
        request.AllowEmployeeToOpen = false;
        var created = await new CreateTicketTypeHandler(db, permissions, new NoOpRequestTransaction(), audit)
            .Handle(request, new CurrentUser("accountant", UserRole.AccountantUser), CancellationToken.None);

        var api = new TicketTypesApi(db);

        Assert.Null(await api.GetTicketTypeVersionAsync(created.Id, 1, UserRole.Employee, CancellationToken.None));
        Assert.NotNull(await api.GetTicketTypeVersionAsync(created.Id, 1, UserRole.CustomerAdmin, CancellationToken.None));
    }

    // A ticket stores ticket_type_version_id, not a version number, so this is the accessor it
    // uses to resolve its frozen descriptor set (Tickets §6.1 problem 1, §13 item 1).
    [Fact]
    public async Task External_api_resolves_a_version_by_its_own_id_and_returns_the_frozen_fields()
    {
        await using var db = CreateDb();
        var audit = new TestAuditApi();
        var permissions = new PermissionChecker([new TicketTypesActionCatalogue()], audit, NullLogger<PermissionChecker>.Instance);
        var accountant = new CurrentUser("accountant", UserRole.AccountantUser);
        var created = await new CreateTicketTypeHandler(db, permissions, new NoOpRequestTransaction(), audit)
            .Handle(ValidRequest("BY_ID", "Original"), accountant, CancellationToken.None);
        await new EditTicketTypeHandler(db, permissions, new NoOpRequestTransaction(), audit).Handle(
            new EditTicketTypeRequestDto
            {
                TicketTypeId = created.Id,
                DisplayName = "Updated",
                Category = "Payroll",
                Fields = ValidRequest("IGNORED", "Updated").Fields
            }, accountant, CancellationToken.None);

        var version1Id = await db.TicketTypeVersions
            .Where(v => v.TicketTypeId == created.Id && v.VersionNumber == 1)
            .Select(v => v.Id)
            .SingleAsync();

        var result = await new TicketTypesApi(db).GetVersionByIdAsync(
            version1Id, UserRole.AccountantAdmin, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(created.Id, result.Id);
        Assert.Equal(1, result.VersionNumber);
        Assert.Equal(2, result.CurrentVersionNumber);
        // The frozen set, not the current one: version 2 relabelled this field to "Updated".
        Assert.Equal("Original", result.Fields[0].Label);
    }

    [Fact]
    public async Task External_api_returns_null_for_a_version_id_that_does_not_exist()
    {
        await using var db = CreateDb();

        Assert.Null(await new TicketTypesApi(db).GetVersionByIdAsync(
            Guid.NewGuid(), UserRole.AccountantAdmin, CancellationToken.None));
    }

    // The by-id read must strip exactly what the by-number read strips. Two accessors onto one
    // projection is the point; two projections would be two copies of this rule.
    [Fact]
    public async Task External_api_strips_private_fields_from_a_version_read_by_id_for_customer_roles()
    {
        await using var db = CreateDb();
        var audit = new TestAuditApi();
        var permissions = new PermissionChecker([new TicketTypesActionCatalogue()], audit, NullLogger<PermissionChecker>.Instance);
        var request = ValidRequest("BY_ID_PRIVATE", "Public");
        request.Fields.Add(new CreateFieldDescriptorDto
        {
            Key = "private",
            Label = "Private",
            DataType = "SingleLineText",
            DisplayOrder = 2,
            IsVisibleToCustomer = false
        });
        var created = await new CreateTicketTypeHandler(db, permissions, new NoOpRequestTransaction(), audit)
            .Handle(request, new CurrentUser("accountant", UserRole.AccountantUser), CancellationToken.None);

        var versionId = await db.TicketTypeVersions
            .Where(v => v.TicketTypeId == created.Id)
            .Select(v => v.Id)
            .SingleAsync();

        var api = new TicketTypesApi(db);

        var byId = await api.GetVersionByIdAsync(versionId, UserRole.CustomerAdmin, CancellationToken.None);
        Assert.NotNull(byId);
        Assert.Single(byId.Fields);
        Assert.Equal("public", byId.Fields[0].Key);

        var byNumber = await api.GetTicketTypeVersionAsync(created.Id, 1, UserRole.CustomerAdmin, CancellationToken.None);
        Assert.NotNull(byNumber);
        Assert.Equal(byNumber.Fields.Select(f => f.Key), byId.Fields.Select(f => f.Key));

        // Both Accountant roles still see the private field through the by-id read.
        var forAccountant = await api.GetVersionByIdAsync(versionId, UserRole.AccountantUser, CancellationToken.None);
        Assert.NotNull(forAccountant);
        Assert.Equal(2, forAccountant.Fields.Count);
    }

    // The contract has to be ROUND-TRIPPABLE, and nothing else in this file checks that. A ticket
    // stores tickets.ticket_type_version_id -- a Guid -- so whatever GetTicketTypeAsync hands back at
    // creation must name the version it projected, or the consuming slice can see which version it got
    // and still not be able to record it. TicketTypeDetailDto originally exposed only the TYPE's Id and
    // the version NUMBER, which left Tickets with no legal way to obtain that Guid: looking it up means
    // reaching into this slice's Infrastructure, and storing the number instead means a second
    // resolution path that can disagree with the first. Hence VersionId, and hence this test.
    [Fact]
    public async Task The_active_version_read_names_the_version_it_projected_so_a_ticket_can_store_it()
    {
        await using var db = CreateDb();
        var audit = new TestAuditApi();
        var permissions = new PermissionChecker([new TicketTypesActionCatalogue()], audit, NullLogger<PermissionChecker>.Instance);
        var created = await new CreateTicketTypeHandler(db, permissions, new NoOpRequestTransaction(), audit)
            .Handle(ValidRequest("ROUND_TRIP", "Round Trip"), new CurrentUser("accountant", UserRole.AccountantUser),
                    CancellationToken.None);

        var api = new TicketTypesApi(db);

        // The path creation takes.
        var active = await api.GetTicketTypeAsync(created.Id, UserRole.CustomerAdmin, CancellationToken.None);
        Assert.NotNull(active);
        Assert.NotEqual(Guid.Empty, active.VersionId);
        // The version's own id, NOT the type's -- the single most likely way to "fix" a failing
        // assertion here, and it would compile and store a Guid that GetVersionByIdAsync never finds.
        Assert.NotEqual(created.Id, active.VersionId);
        Assert.Equal(
            await db.TicketTypeVersions.Where(v => v.TicketTypeId == created.Id).Select(v => v.Id).SingleAsync(),
            active.VersionId);

        // The path every later read of that ticket takes, using only what the ticket stored.
        var resolved = await api.GetVersionByIdAsync(active.VersionId, UserRole.CustomerAdmin, CancellationToken.None);
        Assert.NotNull(resolved);
        Assert.Equal(active.VersionId, resolved.VersionId);
        Assert.Equal(active.VersionNumber, resolved.VersionNumber);
        Assert.Equal(active.Fields.Select(f => f.Key), resolved.Fields.Select(f => f.Key));
    }

    [Fact]
    public async Task Edit_trims_field_labels_and_group_names_the_way_create_does()
    {
        await using var db = CreateDb();
        var audit = new TestAuditApi();
        var permissions = new PermissionChecker([new TicketTypesActionCatalogue()], audit, NullLogger<PermissionChecker>.Instance);
        var accountant = new CurrentUser("accountant", UserRole.AccountantUser);
        var created = await new CreateTicketTypeHandler(db, permissions, new NoOpRequestTransaction(), audit)
            .Handle(ValidRequest("TRIMMED", "Original"), accountant, CancellationToken.None);

        var edited = await new EditTicketTypeHandler(db, permissions, new NoOpRequestTransaction(), audit).Handle(
            new EditTicketTypeRequestDto
            {
                TicketTypeId = created.Id,
                DisplayName = "  Updated Name  ",
                Category = "  Payroll  ",
                Fields =
                [
                    new CreateFieldDescriptorDto
                    {
                        Key = "public",
                        Label = "  Padded Label  ",
                        GroupName = "  Padded Group  ",
                        DataType = "SingleLineText",
                        DisplayOrder = 1
                    }
                ]
            }, accountant, CancellationToken.None);

        Assert.Equal("Updated Name", edited.DisplayName);
        Assert.Equal("Payroll", edited.Category);
        Assert.Equal("Padded Label", edited.Fields[0].Label);
        Assert.Equal("Padded Group", edited.Fields[0].GroupName);
    }

    private static TicketTypesDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<TicketTypesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new TicketTypesDbContext(options);
    }

    private static CreateTicketTypeRequestDto ValidRequest(string code, string label) => new()
    {
        Code = code,
        DisplayName = label,
        Category = "Payroll",
        Fields =
        [
            new CreateFieldDescriptorDto
            {
                Key = "public",
                Label = label,
                DataType = "SingleLineText",
                DisplayOrder = 1
            }
        ]
    };

    private sealed class ThrowingAuditApi : IAuditApi
    {
        public bool WasCalled { get; private set; }

        public Task LogAsync(AuditEntry entry, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            throw new InvalidOperationException("Audit store unavailable.");
        }

        public Task LogUnauthenticatedAsync(
            string actorIdentifier, AuditEntry entry, CancellationToken cancellationToken = default) =>
            LogAsync(entry, cancellationToken);
    }

    private sealed class TestAuditApi : IAuditApi
    {
        public List<AuditEntry> Entries { get; } = new();

        public Task LogAsync(AuditEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task LogUnauthenticatedAsync(
            string actorIdentifier, AuditEntry entry, CancellationToken cancellationToken = default) =>
            LogAsync(entry, cancellationToken);
    }
}

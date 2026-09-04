using System.Text;
using AccountantApp.Api.Slices.Tickets.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace AccountantApp.Tests.Tickets;

/// <summary>
/// Structural guarantees, plan section 2.0 and success criterion 38. Named by
/// TicketConfiguration's own doc comment.
///
/// Snake_case is NOT automatic in this application -- no naming convention is configured anywhere -- so
/// every one of the roughly forty columns needs an explicit HasColumnName. A missed one does not fail at
/// startup: EF happily maps Ticket.Title to a column "Title", and the failure arrives as
/// 42703 "column t.Title does not exist" on whichever request first reads that property. With six
/// entities this is the slice where one gets missed, and asserting it by reflection is the only way to
/// catch it without a database.
///
/// The model is built with UseNpgsql because column names, precision and filters are RELATIONAL
/// annotations: the in-memory provider does not have them, so a mapping test written against
/// UseInMemoryDatabase passes unconditionally and proves nothing. No connection is opened -- building a
/// model does not touch the server -- so unlike TicketsSchemaTests this one runs everywhere.
/// </summary>
public sealed class TicketsColumnMappingTests
{
    private static IModel Model()
    {
        var options = new DbContextOptionsBuilder<TicketsDbContext>()
            .UseNpgsql("Host=localhost;Database=never_connected")
            .Options;

        using var db = new TicketsDbContext(options);
        return db.Model;
    }

    [Fact]
    public void Every_mapped_property_has_an_explicit_snake_case_column_name()
    {
        var model = Model();

        // Seven, not six: ticket_due_date_reminders joined the model with the due-date scanner (plan
        // section 9a.3), added by a second migration. The count is asserted rather than left implicit so
        // that a DbSet for ANOTHER slice's table -- the mistake TicketsDbContext's doc comment warns
        // about -- still fails here.
        Assert.Equal(7, model.GetEntityTypes().Count());

        foreach (var entity in model.GetEntityTypes())
        foreach (var property in entity.GetProperties())
        {
            var columnName = property.GetColumnName();
            var expected = ToSnakeCase(property.Name);

            Assert.Equal(expected, columnName);

            // Belt and braces for the single-word properties -- Title, Body, Version, Note -- where the
            // snake_case form differs from the default only in case. PostgreSQL folds unquoted
            // identifiers to lower case, so "Title" would appear to work in hand-written SQL and fail
            // through EF, which quotes them.
            Assert.Equal(columnName, columnName.ToLowerInvariant());
        }
    }

    /// <summary>
    /// A shadow property is a column EF invented -- almost always an unconfigured foreign key, named
    /// something like TicketId1. The SQL script has no such column, so every query touching it fails.
    /// </summary>
    [Fact]
    public void The_model_has_no_shadow_properties()
    {
        foreach (var entity in Model().GetEntityTypes())
        {
            var shadow = entity.GetProperties().Where(property => property.IsShadowProperty()).ToList();

            Assert.Empty(shadow);
        }
    }

    /// <summary>
    /// Plan section 2.4. Global query filters are FORBIDDEN in this DbContext, and this is the test that
    /// keeps them out. A HasQueryFilter looks like defence in depth and is the opposite: it would silence
    /// TicketVisibility's four explicit layers into invisibility, apply to the due-date scanner and the
    /// reference allocator which have no CurrentUser at all, and -- worst -- turn a deliberate
    /// unfiltered admin query into a silently empty one. Visibility here is a decision each query makes
    /// out loud.
    /// </summary>
    [Fact]
    public void No_entity_has_a_global_query_filter()
    {
        foreach (var entity in Model().GetEntityTypes())
            Assert.Empty(entity.GetDeclaredQueryFilters());
    }

    /// <summary>
    /// Six from 20260904_001_CreateTicketsSchema.sql, plus ticket_due_date_reminders from
    /// 20260905_001_CreateDueDateReminders.sql (plan section 9a.1). ticket_reference_counters has no
    /// entity on purpose -- see TicketsDbContext.
    /// </summary>
    [Fact]
    public void The_seven_tables_are_named_as_the_migrations_name_them()
    {
        var tables = Model().GetEntityTypes()
            .Select(entity => entity.GetTableName())
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            new List<string?>
            {
                "field_values", "field_verifications", "ticket_due_date_reminders",
                "ticket_message_documents", "ticket_messages", "ticket_revisions", "tickets",
            },
            tables);
    }

    /// <summary>
    /// Plan section 9a.1, asserted on the SCRIPTS rather than on a database, because the schema test
    /// that would catch it skips wherever there is no PostgreSQL -- which is everywhere this is being
    /// built.
    ///
    /// The seventh table belongs to a SECOND, LATER migration. Folding it into the first script would
    /// look harmless and would break two things at once: success criterion 1 counts six tables plus the
    /// counter in that script, and a script already applied to a database is never re-read, so a table
    /// added to it would simply never be created anywhere it had already run. Migrations are
    /// append-only.
    /// </summary>
    [Fact]
    public void The_reminder_table_is_created_by_the_second_migration_and_not_the_first()
    {
        var migrations = Path.Combine(TicketsSliceRoot(), "Infrastructure", "Migrations");

        var first = File.ReadAllText(
            Path.Combine(migrations, "20260904_001_CreateTicketsSchema.sql"));
        var second = File.ReadAllText(
            Path.Combine(migrations, "20260905_001_CreateDueDateReminders.sql"));

        Assert.DoesNotContain("CREATE TABLE ticket_due_date_reminders", first);
        Assert.Contains("CREATE TABLE ticket_due_date_reminders", second);

        // 20260905 sorts after 20260904 on the YYYYMMDD_### prefix the runner orders by, which is what
        // guarantees tickets(id) exists before this script references it.
        Assert.Contains("REFERENCES tickets(id)", second);

        // The date prefix is what orders it. A file named without one sorts by its whole name and could
        // land before the schema it depends on.
        Assert.Equal(
            2,
            Directory.EnumerateFiles(migrations, "*.sql").Count());
    }

    /// <summary>
    /// Money is NUMERIC(18,4), never a float. A double accepts every value, answers every query, and
    /// returns 0.100000001490116 for 0.10 -- in an accounting application, where a cent of drift in a VAT
    /// figure is a filing error. Asserted on the model as well as in the schema test, because the schema
    /// test SKIPS wherever there is no PostgreSQL.
    /// </summary>
    [Fact]
    public void The_money_column_is_numeric_with_scale_four()
    {
        var property = Model()
            .FindEntityType(typeof(AccountantApp.Api.Slices.Tickets.Core.FieldValue))!
            .FindProperty(nameof(AccountantApp.Api.Slices.Tickets.Core.FieldValue.ValueNumber))!;

        Assert.Equal(18, property.GetPrecision());
        Assert.Equal(4, property.GetScale());
        Assert.Equal(typeof(decimal?), property.ClrType);
    }

    /// <summary>
    /// Plan section 0.2: this slice has NO ExternalInterfaces folder, and that is a statement about the
    /// architecture rather than an omission. Nothing in the application depends on Tickets -- Documents
    /// and Notifications are called BY it -- so an interface published here would exist only to let
    /// somebody create the dependency the direction was chosen to prevent. Once the folder exists
    /// somebody fills it.
    /// </summary>
    [Fact]
    public void The_slice_publishes_no_external_interface()
    {
        Assert.False(Directory.Exists(Path.Combine(TicketsSliceRoot(), "ExternalInterfaces")));
    }

    /// <summary>
    /// Two source-level prohibitions that no runtime assertion can express.
    ///
    /// UseXminAsConcurrencyToken: the concurrency token is a hand-maintained integer the client echoes
    /// back (section 3.2). xmin is a PostgreSQL internal that changes on any update, cannot be sent to a
    /// browser, and would silently replace the visible version with an invisible one.
    ///
    /// DELETE / Remove(: tickets, revisions, field values, verifications and messages are ALL
    /// append-only (section 1.9). A cancelled ticket stays readable; a superseded revision is how you
    /// see what the Employee originally claimed. Nothing in this slice deletes a row, ever.
    /// </summary>
    [Fact]
    public void The_slice_never_deletes_a_row_and_never_uses_xmin()
    {
        foreach (var file in Directory.EnumerateFiles(TicketsSliceRoot(), "*.cs",
                     SearchOption.AllDirectories))
        {
            // Comments are stripped first. The prohibitions are on what the slice DOES, and a doc
            // comment naming the thing it must not do is the correct way to record the decision -- the
            // configurations say "a hand-maintained integer column, NOT UseXminAsConcurrencyToken" on
            // purpose. A scan that reads comments punishes documenting the rule. (This is not
            // hypothetical: EndpointRoutingTests scans source without stripping comments, and a doc
            // comment in a neighbouring slice failed it.)
            var source = WithoutComments(File.ReadAllText(file));

            Assert.DoesNotContain("UseXminAsConcurrencyToken", source);
            Assert.DoesNotContain(".Remove(", source);
            Assert.DoesNotContain(".RemoveRange(", source);
            Assert.DoesNotContain("ExecuteDelete", source);

            // Criterion 26's second half: no float and no double anywhere in the slice. Money and every
            // numeric field answer are decimal. A double would accept 0.10, satisfy every test that
            // compares with a tolerance, and return 0.100000001490116 to an accountant.
            Assert.DoesNotContain("double", source);
            Assert.DoesNotContain("float", source);

            // Criterion 32: no reopen, in any spelling. A Closed ticket is never reopened (section 9.1,
            // LOCKED) -- a continuation is a NEW ticket carrying PrecededByTicketId. The status constant
            // is the thing that would appear first, before any endpoint did.
            Assert.DoesNotContain("Reopen", source);
        }

        // The migration too: a DROP or DELETE there is the same mistake with a longer blast radius.
        foreach (var script in Directory.EnumerateFiles(
                     Path.Combine(TicketsSliceRoot(), "Infrastructure", "Migrations"), "*.sql"))
        {
            var sql = File.ReadAllText(script).ToUpperInvariant();

            Assert.DoesNotContain("DROP TABLE", sql);
            Assert.DoesNotContain("DELETE FROM", sql);

            // Not asserted: IF NOT EXISTS guards. Idempotency here comes from schema_versions -- the
            // runner records each script by its slice-relative path and never re-runs it -- and every
            // other slice's create script is an unguarded CREATE TABLE. Guarding only this one would
            // make it the odd script out AND hide a double-apply that the version table should catch.
            Assert.Contains("CREATE TABLE", sql);
        }
    }

    /// <summary>
    /// Line comments and block comments removed; string literals are left alone, since a prohibited call
    /// spelled inside a string would still be one written down somewhere it can be reached by reflection.
    /// </summary>
    private static string WithoutComments(string source)
    {
        var kept = new StringBuilder(source.Length);
        var inBlockComment = false;

        foreach (var line in source.Split('\n'))
        {
            var trimmed = line.TrimStart();

            if (inBlockComment)
            {
                if (trimmed.Contains("*/"))
                    inBlockComment = false;
                continue;
            }

            if (trimmed.StartsWith("//", StringComparison.Ordinal))
                continue;

            if (trimmed.StartsWith("/*", StringComparison.Ordinal))
            {
                inBlockComment = !trimmed.Contains("*/");
                continue;
            }

            kept.Append(line).Append('\n');
        }

        return kept.ToString();
    }

    /// <summary>Snake_case exactly as the configurations spell it: an underscore before each capital.</summary>
    private static string ToSnakeCase(string name)
    {
        var builder = new StringBuilder(name.Length + 8);
        for (var index = 0; index < name.Length; index++)
        {
            if (index > 0 && char.IsUpper(name[index]))
                builder.Append('_');
            builder.Append(char.ToLowerInvariant(name[index]));
        }

        return builder.ToString();
    }

    private static string TicketsSliceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "AccountantApp.Api", "Slices", "Tickets");
            if (Directory.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not find AccountantApp.Api/Slices/Tickets above {AppContext.BaseDirectory}.");
    }
}

using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Slices.Documents.Core;
using AccountantApp.Api.Slices.Documents.ExternalInterfaces;
using AccountantApp.Api.Slices.Documents.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Tests.Documents;

/// <summary>
/// The shape of the slice, asserted by reflection and by reading its own source.
///
/// Every check here is for a mistake that would leave the whole behavioural suite green: an added
/// byte[] on the entity, an IAuditApi in the constructor, an IgnoreQueryFilters() in a handler, an
/// endpoint file, a CurrentUser parameter that makes the contract look as though it authorizes. None of
/// those break a single test in DocumentApiFlowTests -- most of them make it read BETTER -- so the only
/// place they can be caught is a test about structure.
///
/// The source-reading tests strip comments first. That matters: the comments in this slice discuss
/// IgnoreQueryFilters, ScanState, inline dispositions and HardDeleteAsync at length, precisely BECAUSE
/// they must not exist, and a naive grep would fail on the documentation of the rule it is enforcing.
/// </summary>
public class DocumentsContractTests
{
    [Fact]
    public void The_entity_carries_no_bytes_and_neither_does_the_summary()
    {
        // Plan section 1.1. EF materialises every mapped column, so ONE byte[] on Document turns
        // "list this ticket's ten file names" into a 250 MB read -- correct output, and the memory
        // profile of a download. The two-table schema is what makes the cheap read the default.
        Assert.Null(typeof(Document).GetProperty("Content"));
        Assert.DoesNotContain(
            typeof(Document).GetProperties(), property => property.PropertyType == typeof(byte[]));

        // And the DTO that crosses the slice boundary. Bytes come back from OpenAsync alone.
        Assert.Null(typeof(DocumentSummary).GetProperty("Content"));
        Assert.DoesNotContain(
            typeof(DocumentSummary).GetProperties(),
            property => property.PropertyType == typeof(byte[]));

        // DocumentContent is the only holder of bytes in the slice.
        Assert.Equal(typeof(byte[]), typeof(DocumentContent).GetProperty("Content")!.PropertyType);
    }

    [Fact]
    public void The_entity_declares_no_scan_state_and_no_ticket_status()
    {
        // Section 10: there is no virus scanning, and its absence is a decision. A ScanState column
        // would imply a scanner that does not exist and would leave every document Pending forever.
        // TicketStatus would be a copy of mutable state that Tickets evaluates live.
        var names = typeof(Document).GetProperties().Select(p => p.Name).ToArray();

        Assert.DoesNotContain("ScanState", names);
        Assert.DoesNotContain("IsQuarantined", names);
        Assert.DoesNotContain("TicketStatus", names);
        Assert.DoesNotContain("Ticket", names);
        Assert.DoesNotContain("Customer", names);
    }

    [Fact]
    public void No_method_on_the_contract_takes_a_caller_and_the_api_injects_no_authority()
    {
        // The contract is honest: it cannot authorize, so it does not accept the things an authorizing
        // method would need. A CurrentUser parameter that was then ignored would be worse than no
        // parameter at all, because a caller would read it as a check.
        var parameterTypes = typeof(IDocumentApi).GetMethods()
            .SelectMany(method => method.GetParameters())
            .Select(parameter => parameter.ParameterType.Name)
            .ToArray();

        Assert.DoesNotContain("CurrentUser", parameterTypes);
        Assert.DoesNotContain("IPermissionChecker", parameterTypes);
        Assert.DoesNotContain("ClaimsPrincipal", parameterTypes);
        Assert.DoesNotContain("HttpContext", parameterTypes);

        // Two dependencies, and they are these two. Not IAuditApi -- Tickets writes all three document
        // codes, and adding a call here to make the one permitted outbound edge look used would produce
        // two audit entries for one upload.
        var constructor = Assert.Single(typeof(DocumentApi).GetConstructors());
        Assert.Equal(
            new[] { typeof(DocumentsDbContext), typeof(IRequestTransaction) },
            constructor.GetParameters().Select(parameter => parameter.ParameterType).ToArray());
    }

    [Fact]
    public void The_contract_never_hands_out_the_entity_itself()
    {
        // A caller holding a tracked Document could mutate it and save it through another context,
        // which is the coupling one-DbContext-per-slice exists to prevent.
        foreach (var method in typeof(IDocumentApi).GetMethods())
        {
            var returned = method.ReturnType.IsGenericType
                ? method.ReturnType.GetGenericArguments()[0]
                : method.ReturnType;

            Assert.DoesNotContain(typeof(Document).Name, Mentioned(returned));
            Assert.DoesNotContain(typeof(DocumentContent).Name, Mentioned(returned));
            Assert.DoesNotContain(typeof(DocumentsDbContext).Name, Mentioned(returned));
        }

        static IEnumerable<string> Mentioned(Type type) =>
            new[] { type.Name }.Concat(type.IsGenericType
                ? type.GetGenericArguments().Select(argument => argument.Name)
                : []);
    }

    [Fact]
    public void There_is_no_undelete_and_no_hard_delete_not_even_internal()
    {
        // Retention is indefinite (01-DomainModel.md section 9.2), so there is nothing to purge and no
        // GDPR-erasure path in this application. A method that existed "for tests" would be the one
        // somebody calls from an admin script.
        var names = typeof(DocumentApi)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                        | BindingFlags.Instance | BindingFlags.Static)
            .Select(method => method.Name)
            .ToArray();

        Assert.DoesNotContain("UndeleteAsync", names);
        Assert.DoesNotContain("HardDeleteAsync", names);
        Assert.DoesNotContain("PurgeAsync", names);
        Assert.DoesNotContain(names, name =>
            name.Contains("Undelete", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Hard", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Purge", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_slice_registers_no_endpoints_and_no_action_catalogue()
    {
        // This slice's four routes belong to Tickets, because a document's access rules come entirely
        // from its ticket and Documents -> Tickets would be a cycle.
        Assert.False(File.Exists(Path.Combine(SliceRoot(), "DocumentsEndpoints.cs")));
        Assert.False(File.Exists(Path.Combine(SliceRoot(), "DocumentsActionCatalogue.cs")));

        // Nor by another name: nothing in the slice implements either abstraction. Reflection rather
        // than file names, because a catalogue in Application/ would satisfy the check above.
        var typesInSlice = typeof(IDocumentApi).Assembly.GetTypes()
            .Where(type => type.Namespace?.StartsWith(
                "AccountantApp.Api.Slices.Documents", StringComparison.Ordinal) == true)
            .ToArray();

        Assert.NotEmpty(typesInSlice);
        Assert.DoesNotContain(typesInSlice, type =>
            typeof(IActionCatalogue).IsAssignableFrom(type) && !type.IsInterface);

        // And no hosted service: section 10 has no background work of any kind, and one registered here
        // would run in every process including the test host.
        Assert.DoesNotContain(typesInSlice, type => type.GetInterfaces()
            .Any(contract => contract.Name is "IHostedService" or "BackgroundService"));

        // Nothing in the slice touches HTTP at all.
        foreach (var (file, code) in Code())
        {
            Assert.DoesNotContain("IEndpointRouteBuilder", code);
            Assert.DoesNotContain("MapPost", code);
            Assert.DoesNotContain("MapGet", code);
            Assert.DoesNotContain("MapGroup", code);
            Assert.DoesNotContain("MapDocumentEndpoints", code);
            Assert.DoesNotContain("HttpContext", code);
            Assert.DoesNotContain("IFormFile", code);
            Assert.False(code.Contains("Results.", StringComparison.Ordinal), file);
        }
    }

    [Fact]
    public void The_four_document_action_names_appear_nowhere_in_this_slice_code()
    {
        // All four belong to TicketsActionCatalogue -- this slice authorizes nothing (plan §0.2). Action
        // names are globally unique, so one declared here is a duplicate the permission composer fails
        // startup on, and one merely REQUIRED here is an action a slice with no handlers claims to need.
        //
        // COMMENTS ARE STRIPPED, and this test used to read the raw text instead (amended 2026-09-02).
        // Its reason for doing so was that EndpointRoutingTests scanned the raw source in both
        // directions, so a name in a comment anywhere under Slices/ could break it. That is no longer
        // true: EndpointRoutingTests gained its own StripComments, precisely so that documentation may
        // name an action freely. Keeping the raw scan here meant no comment in this slice could say
        // which slice owns these routes or why -- and the first one that tried, in
        // ExternalInterfaces/DocumentOrigin.cs, failed this test while being entirely correct.
        //
        // String literals SURVIVE stripping, so the case that actually matters -- RequireAsync("Upload-
        // Document") or an IActionCatalogue entry in this folder -- is still caught. Note the fourth
        // name: §0.3 lists three document actions but §7.2's catalogue has four, and ListTicketDocuments
        // was missing from this guard.
        foreach (var (file, code) in Code())
        {
            Assert.False(code.Contains("UploadDocument", StringComparison.Ordinal), file);
            Assert.False(code.Contains("ListTicketDocuments", StringComparison.Ordinal), file);
            Assert.False(code.Contains("DownloadDocument", StringComparison.Ordinal), file);
            Assert.False(code.Contains("DeleteDocument", StringComparison.Ordinal), file);
        }
    }

    [Fact]
    public void Nothing_in_the_slice_bypasses_the_query_filter_or_removes_a_row()
    {
        // The four tokens that would each defeat the soft delete, and none of them fails anything else:
        // IgnoreQueryFilters() serves a file its owner was told was gone; the three removal forms make
        // the "soft" delete a real one, silently and permanently, because there is no backup path back.
        foreach (var (file, code) in Code())
        {
            Assert.DoesNotContain("IgnoreQueryFilters", code);
            Assert.DoesNotContain(".Remove(", code);
            Assert.DoesNotContain("RemoveRange", code);
            Assert.DoesNotContain("ExecuteDelete", code);
            Assert.DoesNotContain("EntityState.Deleted", code);
            Assert.False(code.Contains("DELETE FROM", StringComparison.OrdinalIgnoreCase), file);
            Assert.False(code.Contains("TRUNCATE", StringComparison.OrdinalIgnoreCase), file);
            Assert.False(code.Contains("DROP TABLE", StringComparison.OrdinalIgnoreCase), file);
            Assert.False(code.Contains("ON DELETE CASCADE", StringComparison.OrdinalIgnoreCase), file);
        }
    }

    [Fact]
    public void Nothing_in_the_slice_serves_bytes_inline_or_pretends_to_scan_them()
    {
        foreach (var (file, code) in Code())
        {
            // "inline" is discussed in the comments at length and must not appear in a header value.
            Assert.False(code.Contains("inline", StringComparison.OrdinalIgnoreCase), file);
            Assert.DoesNotContain("ScanState", code);
            Assert.DoesNotContain("Quarantine", code);
            Assert.False(code.Contains("virus", StringComparison.OrdinalIgnoreCase), file);
            // No signed URLs, no object storage, no thumbnails, no generation: section 10.
            Assert.DoesNotContain("SasToken", code);
            Assert.DoesNotContain("BlobClient", code);
            Assert.DoesNotContain("S3", code);
            Assert.DoesNotContain("Thumbnail", code);
        }
    }

    [Fact]
    public void The_soft_delete_filter_is_declared_on_the_entity_rather_than_repeated_in_queries()
    {
        // Declared once, in the configuration, so the DEFAULT for every LINQ query in the slice is
        // already correct. Three WHERE clauses in three methods would pass the same behavioural tests
        // and would be one forgotten clause away from serving a deleted document.
        var configuration = Code()
            .Single(entry => entry.File.EndsWith("DocumentConfiguration.cs", StringComparison.Ordinal))
            .Code;

        Assert.Contains("HasQueryFilter(", configuration);
        Assert.Contains("DeletedAt == null", configuration);

        // No slice method repeats the predicate by hand.
        foreach (var (file, code) in Code().Where(entry =>
                     !entry.File.EndsWith("DocumentConfiguration.cs", StringComparison.Ordinal)))
        {
            Assert.False(code.Contains("DeletedAt == null", StringComparison.Ordinal), file);
            Assert.False(code.Contains("DeletedAt != null", StringComparison.Ordinal), file);
        }
    }

    [Fact]
    public async Task The_filter_is_structural_so_a_deleted_row_written_behind_the_api_is_still_hidden()
    {
        // Written straight through the context, bypassing SoftDeleteAsync entirely, so this tests the
        // MODEL rather than the method: whatever puts a deleted_at in the table, the reads exclude it.
        await using var db = DocumentsTestHarness.NewDb();
        db.Documents.Add(new Document
        {
            Id = Guid.NewGuid(),
            CustomerId = Guid.NewGuid(),
            TicketId = Guid.NewGuid(),
            Origin = DocumentOrigin.CustomerUpload,
            OriginalFileName = "hidden.pdf",
            ContentType = "application/pdf",
            SizeBytes = 10,
            ContentHash = new string('a', 64),
            UploadedByUserAccountId = Guid.NewGuid(),
            UploadedAt = DateTimeOffset.UtcNow,
            DeletedAt = DateTimeOffset.UtcNow,
            DeletedByUserAccountId = Guid.NewGuid()
        });
        await db.SaveChangesAsync();

        Assert.Empty(await db.Documents.ToListAsync());
        Assert.Equal(1, await db.Documents.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public void Only_the_open_method_reads_the_contents_table()
    {
        // document_contents has no deleted_at column and therefore NO query filter -- there is nothing
        // to filter on. So any read that starts from it, anywhere else, serves the bytes of a document
        // its owner was told was gone. StoreAsync writes it; OpenAsync reads it; nothing else names it.
        // Path.GetFileName, not EndsWith: "IDocumentApi.cs" ends with "DocumentApi.cs" too.
        var api = Code().Single(entry => Path.GetFileName(entry.File) == "DocumentApi.cs");
        var signature = new Regex(@"(?:public|private|internal)[^\n(]*?\b(\w+)\s*\(", RegexOptions.Compiled);

        var declarations = signature.Matches(api.Code)
            .Select(match => (Index: match.Index, Name: match.Groups[1].Value))
            .ToList();

        Assert.NotEmpty(declarations);

        var usages = new List<string>();
        for (var index = api.Code.IndexOf("DocumentContents", StringComparison.Ordinal);
             index >= 0;
             index = api.Code.IndexOf("DocumentContents", index + 1, StringComparison.Ordinal))
        {
            var enclosing = declarations.LastOrDefault(declaration => declaration.Index < index);
            usages.Add(enclosing.Name ?? "<none>");
        }

        // Two usages, in these two methods, and nowhere else.
        Assert.Equal(new[] { "OpenAsync", "StoreAsync" }, usages.Distinct().Order().ToArray());
        Assert.Equal(2, usages.Count);

        // And OpenAsync finds the Document through the filtered query FIRST. The order is the mechanism:
        // reversing these two lines is a one-line change with no error and no failing behavioural test
        // other than the one that deletes a document and re-opens it.
        var open = api.Code[api.Code.IndexOf("OpenAsync(", StringComparison.Ordinal)..];
        Assert.True(
            open.IndexOf("_db.Documents", StringComparison.Ordinal)
                < open.IndexOf("_db.DocumentContents", StringComparison.Ordinal),
            "OpenAsync must resolve the filtered Document before it touches document_contents");
    }

    [Fact]
    public void The_slice_names_no_other_slices_types()
    {
        // 03-SliceInventory.md section 2 permits Documents -> Audit and nothing else, and this slice does
        // not even use that edge. Documents -> Tickets in particular would be a cycle, and the compiler
        // would not object to it.
        var otherSlices = new[]
        {
            "Slices.Tickets", "Slices.TicketTypes", "Slices.Employees", "Slices.Customers",
            "Slices.Identity", "Slices.Notifications", "Slices.Audit"
        };

        foreach (var (file, code) in Code())
        {
            foreach (var slice in otherSlices)
                Assert.False(code.Contains(slice, StringComparison.Ordinal), $"{file} references {slice}");

            // Nor their types by bare name.
            Assert.DoesNotContain("IAuditApi", code);
            Assert.DoesNotContain("ITicketApi", code);
            Assert.DoesNotContain("AuditActions", code);
            Assert.DoesNotContain("CurrentUser", code);
        }
    }

    [Fact]
    public void The_size_cap_is_one_constant_and_the_migration_repeats_its_value_not_its_own_number()
    {
        // 26214400 in the CHECK constraint and 26214400 in the code. Two independently written limits
        // leave a band of file sizes that one accepts and the other rejects, and the failure surfaces as
        // a 500 from a constraint violation rather than a 422.
        var sql = Code().Single(entry => entry.File.EndsWith(".sql", StringComparison.Ordinal)).Code;

        Assert.Contains("26214400", sql);
        Assert.Contains("size_bytes > 0", sql);

        // One named constant in the code, and no second literal spelling of the same number.
        var occurrences = Code()
            .Where(entry => entry.File.EndsWith(".cs", StringComparison.Ordinal))
            .Sum(entry => Regex.Matches(entry.Code, @"26_?214_?400").Count);

        Assert.Equal(1, occurrences);
    }

    // ── Reading the slice's own source ──

    private static IEnumerable<string> Sources() =>
        Directory.EnumerateFiles(SliceRoot(), "*.*", SearchOption.AllDirectories)
            .Where(file => file.EndsWith(".cs", StringComparison.Ordinal)
                           || file.EndsWith(".sql", StringComparison.Ordinal));

    /// <summary>
    /// Every source file in the slice with its comments removed.
    ///
    /// Stripping is what makes these tests possible at all: this slice's comments discuss
    /// IgnoreQueryFilters, ScanState, inline dispositions and DELETE at length BECAUSE those must not
    /// exist. String literals are preserved, so a forbidden token hidden in one is still found.
    /// </summary>
    private static List<(string File, string Code)> Code() =>
        Sources()
            .Select(file => (file, StripComments(
                File.ReadAllText(file),
                isSql: file.EndsWith(".sql", StringComparison.Ordinal))))
            .ToList();

    /// <summary>
    /// Removes // and /* */ comments -- and -- comments in SQL, where a double hyphen is a comment and
    /// in C# it is the decrement operator.
    ///
    /// String and character literals are copied through verbatim, so a forbidden token smuggled into one
    /// is still visible to the caller, and their escapes are honoured so that a closing quote is never
    /// missed. Getting the char literal case right matters: DownloadShaping switches on '"' and on a
    /// backslash, and a scanner that treated that quote as opening a string would stop stripping
    /// comments from there on and quietly weaken every check above.
    /// </summary>
    private static string StripComments(string source, bool isSql)
    {
        var output = new StringBuilder(source.Length);

        for (var index = 0; index < source.Length; index++)
        {
            var character = source[index];
            var next = index + 1 < source.Length ? source[index + 1] : '\0';

            if (character is '"' or '\'')
            {
                var verbatim = character is '"' && index > 0 && source[index - 1] is '@';
                output.Append(character);
                index++;
                while (index < source.Length)
                {
                    if (!verbatim && source[index] is '\\' && index + 1 < source.Length)
                    {
                        output.Append(source[index]).Append(source[index + 1]);
                        index += 2;
                        continue;
                    }

                    output.Append(source[index]);
                    if (source[index] == character)
                        break;
                    index++;
                }

                continue;
            }

            if ((character is '/' && next is '/') || (isSql && character is '-' && next is '-'))
            {
                while (index < source.Length && source[index] is not '\n')
                    index++;
                output.Append('\n');
                continue;
            }

            if (character is '/' && next is '*')
            {
                var end = source.IndexOf("*/", index + 2, StringComparison.Ordinal);
                index = end < 0 ? source.Length : end + 1;
                output.Append('\n');
                continue;
            }

            output.Append(character);
        }

        return output.ToString();
    }

    /// <summary>
    /// The slice directory, found by walking up from the test binaries rather than from the working
    /// directory, which differs between `dotnet test`, the IDE runner and CI.
    /// </summary>
    private static string SliceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "AccountantApp.Api", "Slices", "Documents");
            if (Directory.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not find AccountantApp.Api/Slices/Documents above {AppContext.BaseDirectory}.");
    }
}

using AccountantApp.Api.Slices.Documents.Application;
using AccountantApp.Api.Slices.Documents.Core;
using AccountantApp.Api.Slices.Documents.ExternalInterfaces;

namespace AccountantApp.Tests.Documents;

/// <summary>
/// The download headers. Plan section 11.2's download rows and section 11.3 test 4.
///
/// These assertions look trivially small and they carry the whole weight of the .csv/.txt allow-list
/// entries: those accept arbitrary text, which includes the text of an HTML document, so an inline
/// disposition would serve script from the API's own origin with the session cookie attached. The
/// upload allow-list cannot prevent that -- only the disposition can.
/// </summary>
public class DownloadShapingTests
{
    // Written numerically rather than as escape sequences, so that a reader can see what is being
    // injected and nobody's editor silently normalises a control character out of the source.
    private const char CarriageReturn = (char)13;
    private const char LineFeed = (char)10;
    private const char Nul = (char)0;
    private const char Escape = (char)27;
    private const char Tab = (char)9;
    private const char Delete = (char)127;

    [Fact]
    public void Every_download_is_an_attachment_with_nosniff_and_the_stored_content_type()
    {
        var headers = DownloadShaping.For(Summary("statement.pdf", "application/pdf"));

        Assert.Equal("application/pdf", headers.ContentType);
        Assert.StartsWith("attachment;", headers.ContentDisposition);
        // nosniff is not optional and not per-type: without it a browser may sniff the bytes and
        // disregard the declared type, which reintroduces exactly what the attachment prevents.
        Assert.Equal("nosniff", headers.ContentTypeOptions);
        Assert.Equal("X-Content-Type-Options", DownloadHeaders.ContentTypeOptionsHeaderName);
    }

    [Fact]
    public void No_content_type_is_ever_served_inline_including_images_and_pdfs()
    {
        // The types somebody will eventually want a preview for. "inline for images because the SPA
        // wants a preview" is the change this test exists to fail: if the SPA wants a preview it builds
        // one from the downloaded blob client-side.
        var types = new[]
        {
            ("logo.png", "image/png"),
            ("photo.jpg", "image/jpeg"),
            ("scan.tiff", "image/tiff"),
            ("statement.pdf", "application/pdf"),
            ("ledger.csv", "text/csv"),
            ("notes.txt", "text/plain"),
            ("payroll.docx",
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
            ("book.xls", "application/vnd.ms-excel")
        };

        foreach (var (fileName, contentType) in types)
        {
            var headers = DownloadShaping.For(Summary(fileName, contentType));

            Assert.StartsWith("attachment;", headers.ContentDisposition);
            Assert.DoesNotContain("inline", headers.ContentDisposition);
            // The stored type is passed through untouched -- the header is honest about what the bytes
            // were proved to be, and nosniff makes the browser take it at its word.
            Assert.Equal(contentType, headers.ContentType);
            Assert.Equal("nosniff", headers.ContentTypeOptions);
        }
    }

    [Fact]
    public void A_greek_file_name_goes_out_percent_encoded_with_an_ascii_fallback()
    {
        var disposition = DownloadShaping.ContentDispositionFor("Βεβαίωση Αποδοχών.pdf");

        // RFC 5987. A raw non-ASCII byte is not legal in a header value at all, and Greek names are the
        // normal case for this application rather than an edge case -- so this path runs constantly and
        // a bug in it would not be rare.
        Assert.Contains("filename*=UTF-8''", disposition);
        Assert.Contains("%CE%92", disposition);           // the encoded capital beta
        Assert.EndsWith(".pdf", disposition);

        // And the quoted ASCII fallback for old clients, with the extension intact: non-ASCII becomes
        // '_' rather than being dropped, so the name still reads as a file name of about the right
        // length instead of an empty one.
        Assert.Contains("filename=\"", disposition);
        Assert.Contains("_.pdf\"", disposition);

        // Nothing outside ASCII survives into the header, in either parameter.
        foreach (var character in disposition)
            Assert.True(character <= 0x7F, $"non-ASCII character U+{(int)character:X4} in a header value");
    }

    /// <summary>
    /// PLAN SECTION 11.3 TEST 4. The trap is writing the raw name into the header and asserting only
    /// that the file downloads with the right name: a CR or LF in the value ends the header and starts
    /// another one, which is response splitting -- and the download still works, so the naive test passes.
    /// </summary>
    [Fact]
    public void A_file_name_containing_crlf_cannot_split_the_header()
    {
        var injected = "evil" + CarriageReturn + LineFeed + "X-Injected: 1"
            + CarriageReturn + LineFeed + "Set-Cookie: session=stolen.pdf";

        var disposition = DownloadShaping.ContentDispositionFor(injected);

        // No raw CR and no raw LF anywhere in the produced value, so this is exactly one header.
        Assert.False(disposition.Contains(CarriageReturn));
        Assert.False(disposition.Contains(LineFeed));
        Assert.Single(disposition.Split(LineFeed));
        // The injected header names may remain as ordinary text inside the quoted file name -- harmless,
        // because without a line break they are part of a value and not headers of their own.
        Assert.StartsWith("attachment; filename=\"", disposition);

        // A bare CR, a bare LF, a NUL, an ESC, a TAB and DEL, each on its own. A bare LF alone splits a
        // header on some stacks, so "the value contains no CRLF pair" would be the wrong check.
        foreach (var control in new[] { CarriageReturn, LineFeed, Nul, Escape, Tab, Delete })
        {
            var value = DownloadShaping.ContentDispositionFor("a" + control + "b.pdf");

            // The control character is gone and the rest of the name came through untouched.
            Assert.Equal("attachment; filename=\"ab.pdf\"; filename*=UTF-8''ab.pdf", value);
            // string.Contains(char) is ORDINAL. The string overload is culture-sensitive, and under ICU
            // a NUL has zero collation weight -- so Contains("\0") reports a match in any string at all,
            // which would make this assertion fail on correct output and pass on nothing.
            Assert.False(value.Contains(control), $"U+{(int)control:X4} survived into the header");
        }
    }

    [Fact]
    public void A_quote_or_backslash_in_the_file_name_is_escaped_and_cannot_end_the_value_early()
    {
        var disposition = DownloadShaping.ContentDispositionFor("a\"; x=1; y=\"b.pdf");

        // An unescaped quote would close filename= and let everything after it be read as further
        // Content-Disposition parameters.
        Assert.Contains("filename=\"a\\\"; x=1; y=\\\"b.pdf\"", disposition);

        // A lone backslash would escape the closing quote instead.
        Assert.Contains("filename=\"a\\\\b.pdf\"", DownloadShaping.ContentDispositionFor("a\\b.pdf"));
    }

    [Fact]
    public void A_file_name_that_sanitises_to_nothing_falls_back_to_a_usable_name()
    {
        // An empty header value, or filename="", is what a client with no fallback saves as. There is a
        // name here, always.
        var disposition = DownloadShaping.ContentDispositionFor(
            CarriageReturn.ToString() + LineFeed + Tab);

        Assert.Contains("filename=\"download\"", disposition);
        Assert.Contains("filename*=UTF-8''download", disposition);
    }

    [Fact]
    public void The_disposition_is_built_from_the_stored_name_not_from_a_client_supplied_one()
    {
        // For takes a DocumentSummary, so the only name it can use is the sanitised one that was
        // recorded at upload. There is no overload accepting a name from the request.
        var summary = Summary("Βεβαίωση.pdf", "application/pdf");

        Assert.Equal(
            DownloadShaping.ContentDispositionFor(summary.OriginalFileName),
            DownloadShaping.For(summary).ContentDisposition);
    }

    private static DocumentSummary Summary(string fileName, string contentType) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        DocumentOrigin.CustomerUpload,
        fileName,
        contentType,
        1024,
        Guid.NewGuid(),
        DateTimeOffset.UtcNow);
}

using System.Text;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Documents.Application;
using AccountantApp.Api.Slices.Documents.ExternalInterfaces;
using static AccountantApp.Tests.Documents.DocumentsTestHarness;

namespace AccountantApp.Tests.Documents;

/// <summary>
/// The allow-list, the size cap, and the file-name sanitiser. Plan section 11.2's upload rows.
///
/// Because there is no virus scanner anywhere in this system, this class IS the upload defence, so every
/// row of the plan's table is here rather than sampled. A file type that is accepted when it should not
/// be is stored permanently -- retention is indefinite -- and served back to somebody.
/// </summary>
public class UploadValidationTests
{
    // --- Accepted ---

    [Fact]
    public void A_pdf_is_accepted_with_the_sniffed_type()
    {
        var result = UploadValidation.Validate(Pdf(), "statement.pdf");

        Assert.Equal("application/pdf", result.ContentType);
        Assert.Equal("statement.pdf", result.OriginalFileName);
        Assert.Equal(Pdf().Length, result.SizeBytes);
    }

    [Fact]
    public void The_declared_content_type_is_never_consulted()
    {
        // A PNG declared as a PDF. The stored type is what the LEADING BYTES said, and the declared
        // header does not even reach this class -- Validate has no parameter for it. If a sniffed type
        // were stored alongside a client-declared one that the download then used, the whole exercise
        // would be defeated.
        Assert.Equal("image/png", UploadValidation.Validate(Png(), "logo.png").ContentType);
    }

    [Fact]
    public void Both_tiff_byte_orders_are_accepted()
    {
        // Little- and big-endian are both valid TIFF, and only one of them is the one a builder
        // remembers.
        Assert.Equal("image/tiff", UploadValidation.Validate(TiffLittleEndian(), "scan.tif").ContentType);
        Assert.Equal("image/tiff", UploadValidation.Validate(TiffBigEndian(), "scan.tiff").ContentType);
    }

    [Fact]
    public void A_jpeg_is_accepted_under_either_extension()
    {
        Assert.Equal("image/jpeg", UploadValidation.Validate(Jpeg(), "photo.jpg").ContentType);
        Assert.Equal("image/jpeg", UploadValidation.Validate(Jpeg(), "photo.jpeg").ContentType);
    }

    [Fact]
    public void A_real_docx_and_a_real_xlsx_are_accepted_after_the_container_is_inspected()
    {
        Assert.Equal(
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            UploadValidation.Validate(Docx(), "payroll.docx").ContentType);

        Assert.Equal(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            UploadValidation.Validate(Xlsx(), "payroll.xlsx").ContentType);
    }

    [Fact]
    public void The_container_decides_the_type_not_the_extension()
    {
        // A genuine xlsx package named .docx. The extension gate passes because .docx is allowed, and
        // the stored type is then the one the CONTAINER proved -- never the one the extension implied.
        Assert.Equal(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            UploadValidation.Validate(Xlsx(), "mislabelled.docx").ContentType);
    }

    [Fact]
    public void A_legacy_doc_and_xls_are_resolved_from_the_extension()
    {
        // The documented relaxation: one OLE2 signature covers .doc, .xls, .ppt and more, and telling
        // them apart means parsing the compound-file directory. Mislabelling one as the other is
        // possible and harmless; getting an HTML file past the OLE2 check is not.
        Assert.Equal("application/msword", UploadValidation.Validate(Ole2(), "letter.doc").ContentType);
        Assert.Equal("application/vnd.ms-excel", UploadValidation.Validate(Ole2(), "book.xls").ContentType);
    }

    [Fact]
    public void Ordinary_csv_and_txt_are_accepted_with_the_type_from_the_extension()
    {
        var csv = Encoding.UTF8.GetBytes("name,amount\nMaria,120.50\n");
        var txt = Encoding.UTF8.GetBytes("Σημειώσεις για τον πελάτη.\n");

        Assert.Equal("text/csv", UploadValidation.Validate(csv, "ledger.csv").ContentType);
        Assert.Equal("text/plain", UploadValidation.Validate(txt, "notes.txt").ContentType);
    }

    [Fact]
    public void A_txt_with_a_utf8_bom_is_accepted_and_the_bom_is_not_stripped()
    {
        var withBom = Concat([0xEF, 0xBB, 0xBF], Encoding.UTF8.GetBytes("amount\n"));

        var result = UploadValidation.Validate(withBom, "bom.txt");

        Assert.Equal("text/plain", result.ContentType);
        // The BOM is skipped FOR VALIDATION and never taken out of the content: the size is the full
        // length, and the hash is the hash of the bytes as they arrived. There is no normalisation
        // step anywhere, because section 1 requires a byte-identical round trip.
        Assert.Equal(withBom.Length, result.SizeBytes);
        Assert.Equal(UploadValidation.ComputeHash(withBom), result.ContentHash);
    }

    [Fact]
    public void A_txt_containing_tab_crlf_and_a_bare_lf_is_accepted()
    {
        // All three are permitted, and a validator that rejected CR would reject every file written on
        // Windows -- which is every file this Office produces.
        var mixed = Encoding.UTF8.GetBytes("a\tb\r\nc\nd");

        Assert.Equal("text/plain", UploadValidation.Validate(mixed, "mixed.txt").ContentType);
    }

    [Fact]
    public void A_txt_containing_a_script_tag_is_accepted_because_the_download_headers_are_the_defence()
    {
        // 200, deliberately. Rules 1-4 of section 3.3a establish that the bytes are TEXT, not that the
        // text is harmless, and no allow-list check could establish the second. What makes this safe is
        // DownloadShaping's unconditional attachment disposition plus nosniff -- see
        // DownloadShapingTests, which asserts both, and which is the other half of this test.
        var script = Encoding.UTF8.GetBytes("<script>alert(1)</script>");

        Assert.Equal("text/plain", UploadValidation.Validate(script, "note.txt").ContentType);
    }

    // --- Rejected ---

    [Fact]
    public void An_html_file_renamed_pdf_is_422()
    {
        // The extension says PDF and the leading bytes do not. This is the single most likely hostile
        // upload, because the SPA and the API share an origin.
        Assert.Equal(422, Rejects(Html(), "invoice.pdf").StatusCode);
    }

    [Fact]
    public void An_svg_is_422_under_every_extension_it_might_arrive_with()
    {
        // SVG is XML that executes script, and there is no allow-list entry for it at all.
        Assert.Equal(422, Rejects(Svg(), "logo.svg").StatusCode);
        Assert.Equal(422, Rejects(Svg(), "logo.png").StatusCode);
        // And it cannot sneak in through the text branch either: the markup prefix is treated as the
        // signature that SVG does not otherwise have.
        Assert.Equal(422, Rejects(Svg(), "logo.txt").StatusCode);
        Assert.Equal(422, Rejects(Html(), "page.txt").StatusCode);
    }

    [Fact]
    public void A_plain_zip_is_422_and_is_not_mistaken_for_ooxml()
    {
        Assert.Equal(422, Rejects(PlainZip(), "documents.zip").StatusCode);
        // Even under an Office extension: the archive has no [Content_Types].xml, so it is not a
        // package.
        Assert.Equal(422, Rejects(PlainZip(), "documents.docx").StatusCode);
        // And an archive with the Office part but no [Content_Types].xml is likewise not a package.
        Assert.Equal(422, Rejects(ZipWithoutContentTypes(), "documents.docx").StatusCode);
    }

    [Fact]
    public void A_real_docx_renamed_zip_is_422()
    {
        // DECIDED (plan section 3.2 rule 6, section 13 item 4). Every check in rules 1-5 passes -- the
        // signature and the container both say OOXML -- and it is still rejected, because .zip is not on
        // the allow-list and one rule beats a carve-out that would make the list a list of CONTENTS in
        // this branch and a list of EXTENSIONS in every other. The user renames the file back.
        Assert.Equal(422, Rejects(Docx(), "payroll.zip").StatusCode);
        Assert.Equal(422, Rejects(Xlsx(), "payroll.zip").StatusCode);
    }

    [Fact]
    public void A_zip_bomb_is_422_and_nothing_is_inflated()
    {
        // A small upload declaring 150 MB of content. The rejection comes from the DECLARED sizes in the
        // central directory, before any entry is opened, which is why this test finishes instead of
        // exhausting memory. That is the whole design: inflating is where a bomb detonates.
        var bomb = ZipBomb();
        Assert.True(bomb.Length < 1024 * 1024, "the payload must be small to be a bomb at all");

        Assert.Equal(422, Rejects(bomb, "bomb.docx").StatusCode);
    }

    [Fact]
    public void A_malformed_zip_with_an_office_extension_is_422_and_not_500()
    {
        // A truncated archive throws out of the ZIP parser. Every exception in that parser is client
        // input, so it is a 4xx: a 500 here would be an availability bug reported as a server fault.
        Assert.Equal(422, Rejects(MalformedZip(), "broken.docx").StatusCode);
        Assert.Equal(422, Rejects(MalformedZip(), "broken.xlsx").StatusCode);
    }

    [Fact]
    public void An_ole2_file_with_a_pdf_extension_is_422()
    {
        // The relaxation in section 3.3 is only that the extension picks BETWEEN .doc and .xls. Any
        // other extension over an OLE2 header is a rejection.
        Assert.Equal(422, Rejects(Ole2(), "letter.pdf").StatusCode);
        Assert.Equal(422, Rejects(Ole2(), "letter.ppt").StatusCode);
    }

    [Fact]
    public void A_real_pdf_renamed_txt_is_422()
    {
        // Section 3.3a rule 4. The signature check runs in this direction too, and this is the direction
        // that matters: text has no signature to verify, so the only thing that can be verified about a
        // .txt is that its bytes are NOT something else.
        Assert.Equal(422, Rejects(Pdf(), "invoice.txt").StatusCode);
        Assert.Equal(422, Rejects(Png(), "image.csv").StatusCode);
    }

    [Fact]
    public void A_docx_renamed_csv_is_422()
    {
        Assert.Equal(422, Rejects(Docx(), "payroll.csv").StatusCode);
    }

    [Fact]
    public void A_txt_containing_a_nul_byte_is_422()
    {
        // Text does not contain NULs; binary content reliably does. One check, and it rejects the
        // overwhelming majority of binaries mislabelled .txt.
        var withNul = Concat(Encoding.UTF8.GetBytes("amount"), [0x00, 0x01]);

        Assert.Equal(422, Rejects(withNul, "data.txt").StatusCode);
    }

    [Fact]
    public void A_txt_that_is_not_valid_utf8_is_422_rather_than_replacement_characters()
    {
        // A lone 0xFF cannot begin a UTF-8 sequence. The decode is strict, so this is a 422 -- not a 500
        // out of the decoder, and NOT a string full of U+FFFD stored as though the file had been
        // understood.
        var invalid = Concat(Encoding.UTF8.GetBytes("amount"), [0xFF, 0xFE]);

        Assert.Equal(422, Rejects(invalid, "data.txt").StatusCode);
    }

    [Fact]
    public void A_txt_containing_an_escape_character_is_422()
    {
        // 0x1B is a control character that is not TAB, CR or LF. A terminal-escape payload in a file
        // somebody will cat is exactly what rule 3 is for.
        var withEscape = Concat(Encoding.UTF8.GetBytes("amount"), [0x1B, 0x5B, 0x33, 0x31, 0x6D]);

        Assert.Equal(422, Rejects(withEscape, "data.txt").StatusCode);
    }

    [Fact]
    public void An_archive_renamed_txt_is_422()
    {
        // The excluded table's signatures, checked in the text branch: gzip, rar, 7z, bzip2. An archive
        // hides its contents from the sniffer entirely, so accepting one accepts everything inside it.
        Assert.Equal(422, Rejects(Concat([0x1F, 0x8B, 0x08], Filler()), "log.txt").StatusCode);
        Assert.Equal(422, Rejects(Concat([0x52, 0x61, 0x72, 0x21, 0x1A, 0x07], Filler()), "log.txt").StatusCode);
        Assert.Equal(422, Rejects(Concat([0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C], Filler()), "log.txt").StatusCode);
    }

    [Fact]
    public void An_unrecognised_file_is_422_because_this_is_an_allow_list()
    {
        // No signature and no text extension. An allow-list answers "no" to everything it does not
        // recognise; a block-list would answer "yes".
        Assert.Equal(422, Rejects(Filler(), "mystery.bin").StatusCode);
        Assert.Equal(422, Rejects(Filler(), "no-extension").StatusCode);
        Assert.Equal(422, Rejects(Pdf(), "").StatusCode);
    }

    [Fact]
    public void A_zero_byte_file_is_422()
    {
        // A client bug, and storing one produces a document that downloads as nothing. ck_documents_size
        // requires > 0 as the backstop.
        Assert.Equal(422, Rejects([], "empty.pdf").StatusCode);
    }

    [Fact]
    public void A_file_over_the_cap_is_422()
    {
        var oversized = new byte[DocumentLimits.MaxUploadSizeBytes + 1];
        Pdf().CopyTo(oversized, 0);

        Assert.Equal(422, Rejects(oversized, "huge.pdf").StatusCode);
    }

    [Fact]
    public void The_cap_is_exactly_25_mebibytes()
    {
        // 26214400, not 25000000. The two are different numbers, and an application limit computed one
        // way against a proxy limit configured the other leaves a band of file sizes that fail at the
        // proxy with an error the application never sees. This is the single constant all three
        // enforcement points read.
        Assert.Equal(25L * 1024 * 1024, DocumentLimits.MaxUploadSizeBytes);
        Assert.Equal(26_214_400, DocumentLimits.MaxUploadSizeBytes);
    }

    [Fact]
    public async Task An_oversized_body_is_rejected_without_being_buffered()
    {
        // 26 MB offered, 25 MiB + 1 read. The +1 byte is what proves the stream was too long, and it is
        // the only byte past the limit that is ever held.
        var stream = new CountingStream(26L * 1024 * 1024);

        var exception = await Assert.ThrowsAsync<AppException>(
            () => UploadValidation.ReadWithinLimitAsync(stream, CancellationToken.None));

        Assert.Equal(422, exception.StatusCode);
        Assert.Equal(DocumentLimits.MaxUploadSizeBytes + 1, stream.BytesRead);
        Assert.True(stream.BytesRead < stream.Length,
            "the body must not be read to the end before the size is judged");
    }

    // --- File-name sanitisation ---

    [Fact]
    public void A_path_in_the_file_name_does_not_survive()
    {
        // The stored name is never used as a filesystem path, and this is the first of two sanitisers.
        Assert.Equal("passwd", UploadValidation.SanitiseFileName("../../etc/passwd"));
        Assert.Equal("passwd", UploadValidation.SanitiseFileName("..\\..\\etc\\passwd"));
        Assert.Equal("secret.pdf", UploadValidation.SanitiseFileName("C:\\Windows\\secret.pdf"));

        // A traversal sequence with no separator left to hide behind is taken out too.
        Assert.DoesNotContain("..", UploadValidation.SanitiseFileName("....//....//evil.pdf"));

        foreach (var name in new[] { "../../etc/passwd.pdf", "..\\..\\etc\\passwd.pdf" })
        {
            var sanitised = UploadValidation.SanitiseFileName(name);
            Assert.Equal("passwd.pdf", sanitised);
            Assert.DoesNotContain("/", sanitised);
            Assert.DoesNotContain("\\", sanitised);
        }
    }

    [Fact]
    public void Control_characters_in_the_file_name_do_not_survive()
    {
        // A CR or LF that reached a header would be response splitting. It is stripped here AND again on
        // the way out, because the two sanitisers protect different things and either may be relaxed
        // later without the other noticing.
        var sanitised = UploadValidation.SanitiseFileName("evil\r\nX-Injected: 1.pdf");

        Assert.False(sanitised.Contains('\r'));
        Assert.False(sanitised.Contains('\n'));

        // "1.pdf", not "evilX-Injected: 1.pdf": the colon rule then keeps only the last segment, because
        // a ':' carries a Windows drive letter or an alternate-data-stream reference. Two independent
        // rules happen to both apply to this payload, which is what defence in depth looks like from
        // inside -- and the assertion is written to the ACTUAL output so that a later change to either
        // rule is visible here rather than silently absorbed.
        Assert.Equal("1.pdf", sanitised);
        Assert.False(sanitised.Contains(':'));
    }

    [Fact]
    public void A_very_long_file_name_is_capped_at_the_column_width_keeping_the_extension()
    {
        var sanitised = UploadValidation.SanitiseFileName(new string('a', 400) + ".pdf");

        Assert.Equal(DocumentLimits.MaxFileNameLength, sanitised.Length);
        // Truncating from the FRONT rather than the back: a name cut off at 255 characters from the
        // start loses its extension, and the extension is what the download's content type was resolved
        // from.
        Assert.EndsWith(".pdf", sanitised);
    }

    [Fact]
    public void A_greek_file_name_survives_storage_unchanged()
    {
        // Greek names are the normal case for this application, not an edge case. Nothing here
        // transliterates or percent-encodes -- that happens only in the download header.
        Assert.Equal("Βεβαίωση Αποδοχών.pdf",
            UploadValidation.SanitiseFileName("Βεβαίωση Αποδοχών.pdf"));
    }

    private static AppException Rejects(byte[] content, string fileName) =>
        Assert.Throws<AppException>(() => UploadValidation.Validate(content, fileName));

    private static byte[] Filler(int length = 64)
    {
        var filler = new byte[length];
        for (var index = 0; index < length; index++)
            filler[index] = (byte)('a' + index % 26);
        return filler;
    }
}

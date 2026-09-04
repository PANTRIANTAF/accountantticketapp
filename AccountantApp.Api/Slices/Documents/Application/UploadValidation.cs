using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Documents.ExternalInterfaces;

namespace AccountantApp.Api.Slices.Documents.Application;

/// <summary>
/// What the validator determined about an upload. The content type here is the SNIFFED one and it is
/// what gets stored; the file name is the sanitised one.
/// </summary>
public sealed record ValidatedUpload(
    string OriginalFileName,
    string ContentType,
    string ContentHash,
    long SizeBytes);

/// <summary>
/// The allow-list and the size cap. Called by DocumentApi.StoreAsync AND BY NOTHING ELSE, so the rules
/// cannot be applied inconsistently -- two call sites is two places for one of them to drift.
///
/// 01-DomainModel.md section 6: "Because there is no scanner, upload hygiene carries the whole
/// defence." There is no virus scanning, no ScanState, and no quarantine anywhere in this slice; the
/// allow-list below, the size cap, and the attachment-only download are the entire defence.
///
/// Two rules govern everything here:
///
/// 1. Never trust the declared Content-Type header. It is attacker-controlled and this class never
///    receives it.
/// 2. Never trust the file extension EITHER -- except in the two narrow, documented cases below
///    (sections 3.3 and 3.3a of the plan), each of which is commented at the code because a reader who
///    meets it cold will otherwise read it as exactly the mistake the docs warn about.
/// </summary>
public static class UploadValidation
{
    // The two limits are declared in ExternalInterfaces/DocumentLimits.cs, not here, because the Tickets
    // upload endpoint must apply the size cap as a RequestSizeLimit and may not read this folder. These
    // aliases exist so the rules below read as before; DocumentLimits holds the one declaration.
    private const long MaxUploadSizeBytes = DocumentLimits.MaxUploadSizeBytes;
    private const int MaxFileNameLength = DocumentLimits.MaxFileNameLength;

    public const string Pdf = "application/pdf";
    public const string Jpeg = "image/jpeg";
    public const string Png = "image/png";
    public const string Tiff = "image/tiff";
    public const string Docx =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    public const string Xlsx =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public const string Doc = "application/msword";
    public const string Xls = "application/vnd.ms-excel";
    public const string Csv = "text/csv";
    public const string PlainText = "text/plain";

    // Bounds on the OOXML container inspection. This is the ONE place the application voluntarily
    // parses attacker-controlled structure, so the entry count and the declared uncompressed total are
    // both checked BEFORE anything is inflated -- and nothing is ever inflated at all. A zip bomb is a
    // 25 MB upload that decompresses to gigabytes; a real Office document is a few hundred entries.
    private const int MaxZipEntries = 512;
    private const long MaxZipUncompressedBytes = 104_857_600;

    // The signatures from the plan's section 3.1 table.
    private static readonly byte[] PdfSignature = [0x25, 0x50, 0x44, 0x46];
    private static readonly byte[] JpegSignature = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] PngSignature =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] TiffLittleEndianSignature = [0x49, 0x49, 0x2A, 0x00];
    private static readonly byte[] TiffBigEndianSignature = [0x4D, 0x4D, 0x00, 0x2A];
    private static readonly byte[] ZipSignature = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] Ole2Signature =
        [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    // The EXCLUDED table's signatures, needed only by the text branch (section 3.3a rule 4). Every
    // other branch already rejects them by having no matching allow-list entry at all.
    private static readonly byte[][] ExcludedArchiveSignatures =
    [
        [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07],       // .rar
        [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C],       // .7z
        [0x1F, 0x8B],                               // .gz
        [0x42, 0x5A, 0x68]                          // .bz2
    ];

    // SVG and HTML have no binary signature -- they are text, which is the whole difficulty -- so the
    // leading markup is what stands in for one. Deliberately anchored at the start and deliberately
    // narrow: a .txt whose CONTENT contains <script>alert(1)</script> is accepted, because the
    // attachment-only download and nosniff are what make that safe, not a content inspection that
    // could never be complete.
    private static readonly string[] ExcludedMarkupPrefixes =
    [
        "<?xml", "<svg", "<html", "<!doctype html", "<!doctype svg", "<!--"
    ];

    private enum SniffedFormat
    {
        Unknown,
        Pdf,
        Jpeg,
        Png,
        Tiff,
        ZipContainer,
        Ole2
    }

    /// <summary>
    /// Reads the body into a buffer, refusing to buffer more than the cap. Each read is sized so the
    /// total can never exceed MaxUploadSizeBytes + 1: the one extra byte is what proves the stream was
    /// too long, and it is the only byte past the limit this method ever holds.
    ///
    /// A 26 MB upload is therefore rejected without 26 MB ever being in memory, which is what
    /// 04-Infrastructure.md section 7's "enforced before the body is buffered" asks for on the
    /// application side.
    /// </summary>
    public static async Task<byte[]> ReadWithinLimitAsync(Stream content, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[81_920];
        long total = 0;

        while (true)
        {
            var room = MaxUploadSizeBytes + 1 - total;
            if (room <= 0)
                throw TooLarge();

            var read = await content.ReadAsync(
                chunk.AsMemory(0, (int)Math.Min(chunk.Length, room)), ct);
            if (read == 0)
                break;

            total += read;
            if (total > MaxUploadSizeBytes)
                throw TooLarge();

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// The whole allow-list, in one pass over one buffer. Reading the leading bytes from a stream and
    /// forgetting to rewind stores zero bytes, so the caller hands this a buffer it has already read
    /// once and the hash is computed here, over the same bytes that were sniffed -- there is no second
    /// pass in which the content could differ.
    ///
    /// Throws AppException(422) for every rejection. Never a 500: an unrecognised file, a malformed
    /// ZIP, and a mislabelled extension are all client-triggerable, and
    /// App/GeneralAppArchitecture.md section 8 makes a client-triggerable value a 4xx.
    /// </summary>
    public static ValidatedUpload Validate(byte[] content, string? originalFileName)
    {
        // A zero-byte file is a client bug, and storing one produces a document that downloads as
        // nothing. ck_documents_size requires > 0 as the backstop.
        if (content.Length == 0)
            throw new AppException("The uploaded file is empty.", 422);

        if (content.Length > MaxUploadSizeBytes)
            throw TooLarge();

        var fileName = SanitiseFileName(originalFileName);
        var extension = Path.GetExtension(fileName).ToLowerInvariant();

        var contentType = ResolveContentType(content, extension);

        return new ValidatedUpload(
            fileName, contentType, ComputeHash(content), content.Length);
    }

    /// <summary>SHA-256, hex, lower case. Integrity and duplicate reporting only -- never a key.</summary>
    public static string ComputeHash(byte[] content) =>
        Convert.ToHexStringLower(SHA256.HashData(content));

    /// <summary>
    /// The stored file name is never used as a filesystem path (01-DomainModel.md section 6), and this
    /// is the first of TWO sanitisers. The second one runs on the way out, in DownloadShaping, because
    /// the name escapes into an HTTP header there -- a different context with different dangerous
    /// characters. Neither is a substitute for the other, and either may be relaxed later without the
    /// other noticing.
    /// </summary>
    public static string SanitiseFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        // Control characters first: a CR or LF here is a response-splitting payload if it ever reaches
        // a header, and they also make a name that reads as one thing and is another.
        var stripped = new StringBuilder(name.Length);
        foreach (var character in name)
            if (!char.IsControl(character))
                stripped.Append(character);

        var value = stripped.ToString();

        // Keep only the last segment. '/' and '\' are both separators regardless of the host OS, and
        // ':' carries a Windows drive or alternate-data-stream reference.
        var lastSeparator = value.LastIndexOfAny(['/', '\\', ':']);
        if (lastSeparator >= 0)
            value = value[(lastSeparator + 1)..];

        // Any remaining traversal sequence, repeatedly -- "....//" collapses to something harmless
        // only if the replacement runs to a fixed point.
        while (value.Contains(".."))
            value = value.Replace("..", string.Empty);

        value = value.Trim();

        return value.Length <= MaxFileNameLength ? value : value[^MaxFileNameLength..];
    }

    private static string ResolveContentType(byte[] content, string extension)
    {
        switch (Sniff(content))
        {
            case SniffedFormat.Pdf:
                return Require(extension, Pdf, ".pdf");

            case SniffedFormat.Jpeg:
                return Require(extension, Jpeg, ".jpg", ".jpeg");

            case SniffedFormat.Png:
                return Require(extension, Png, ".png");

            case SniffedFormat.Tiff:
                return Require(extension, Tiff, ".tif", ".tiff");

            case SniffedFormat.ZipContainer:
                // The extension is checked BEFORE the container is opened, so a plain .zip -- and a
                // real .docx renamed .zip -- never reaches the parser at all. Section 3.2 rule 6:
                // OOXML acceptance needs the container inspection to pass AND the extension to be one
                // of these two. A .docx renamed .zip is a 422 even though every byte of it is a valid
                // Office document, because .zip is not on the allow-list and one rule is worth more
                // than a carve-out that makes the list a list of contents in this branch and a list of
                // extensions in every other. The user renames it back.
                if (extension is not (".docx" or ".xlsx"))
                    throw Rejected(extension);

                // And the type stored is the one the CONTAINER proved, never the one the extension
                // implied.
                return InspectOoxmlContainer(content);

            case SniffedFormat.Ole2:
                // DOCUMENTED RELAXATION 1 (plan section 3.3). D0 CF 11 E0 A1 B1 1A E1 is the OLE2
                // compound-file header and it is IDENTICAL for .doc, .xls, .ppt and several other
                // legacy formats. Telling them apart means parsing the compound-file directory, which
                // is considerably nastier than a ZIP, so the specific type comes from the extension
                // instead.
                //
                // This is narrower than it looks: the SIGNATURE was still verified, so an attacker can
                // mislabel an .xls as a .doc -- which achieves nothing -- but cannot get an HTML file
                // past the OLE2 check. Any other extension with an OLE2 header is a 422, which is what
                // makes an OLE2 file named .pdf a rejection rather than a stored lie.
                return extension switch
                {
                    ".doc" => Doc,
                    ".xls" => Xls,
                    _ => throw Rejected(extension)
                };

            default:
                // DOCUMENTED RELAXATION 2 (plan section 3.3a). .csv and .txt have NO signature at all,
                // so signature sniffing cannot verify them and pretending otherwise would be worse
                // than admitting it. The verification is by content SHAPE instead, and only after it
                // passes does the extension get to choose between two labels for the same verified
                // bytes.
                if (extension is not (".csv" or ".txt"))
                    throw Rejected(extension);

                ValidateTextShape(content);
                return extension == ".csv" ? Csv : PlainText;
        }
    }

    private static SniffedFormat Sniff(byte[] content)
    {
        if (StartsWith(content, PdfSignature)) return SniffedFormat.Pdf;
        if (StartsWith(content, JpegSignature)) return SniffedFormat.Jpeg;
        // The full 8-byte PNG signature, not just 89 50: the trailing CR LF SUB LF is what detects a
        // PNG that has been through a text-mode transfer and corrupted.
        if (StartsWith(content, PngSignature)) return SniffedFormat.Png;
        if (StartsWith(content, TiffLittleEndianSignature)) return SniffedFormat.Tiff;
        if (StartsWith(content, TiffBigEndianSignature)) return SniffedFormat.Tiff;
        if (StartsWith(content, ZipSignature)) return SniffedFormat.ZipContainer;
        if (StartsWith(content, Ole2Signature)) return SniffedFormat.Ole2;
        return SniffedFormat.Unknown;
    }

    /// <summary>
    /// Section 3.2, the one genuinely hard case. A .docx and a .xlsx are ZIP archives whose leading
    /// bytes are byte-for-byte identical to a plain .zip, which the allow-list rejects, so the
    /// signature alone cannot tell an allowed Office file from a forbidden archive.
    ///
    /// What this does, and what it deliberately does not:
    ///   - It reads the CENTRAL DIRECTORY only. ZipArchiveMode.Read over a MemoryStream lists entries
    ///     and their declared sizes without inflating anything.
    ///   - It caps the entry count and the declared uncompressed total BEFORE touching an entry.
    ///   - IT INFLATES NOTHING. The entry NAMES are enough, and inflating is where a bomb detonates --
    ///     which is also why [Content_Types].xml is checked for PRESENCE at the archive root rather
    ///     than parsed. (The plan's section 3.2 rule 2 asks for the declared content types to be read
    ///     and its rule 5 forbids inflating any entry; rules 2 and 5 can only both hold if the entry
    ///     names carry the decision, which is what rule 2's own second sentence then says.)
    ///   - Every failure, including a malformed archive, is a 422. A malformed ZIP is entirely
    ///     client-triggerable, so a 500 here would be an availability bug reported as a server fault.
    /// </summary>
    private static string InspectOoxmlContainer(byte[] content)
    {
        try
        {
            using var stream = new MemoryStream(content, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            if (archive.Entries.Count > MaxZipEntries)
                throw new AppException(
                    "The uploaded archive has too many entries to be an Office document.", 422);

            long declaredUncompressed = 0;
            var hasContentTypes = false;
            var isWord = false;
            var isExcel = false;

            foreach (var entry in archive.Entries)
            {
                // entry.Length is the size declared in the archive's own directory. Reading it inflates
                // nothing; summing it is what makes a bomb visible before it is opened.
                declaredUncompressed += entry.Length;
                if (declaredUncompressed > MaxZipUncompressedBytes)
                    throw new AppException(
                        "The uploaded archive declares more uncompressed content than an Office "
                        + "document can plausibly contain.", 422);

                var name = entry.FullName.Replace('\\', '/');

                if (string.Equals(name, "[Content_Types].xml", StringComparison.OrdinalIgnoreCase))
                    hasContentTypes = true;
                else if (name.StartsWith("word/document.xml", StringComparison.OrdinalIgnoreCase))
                    isWord = true;
                else if (name.StartsWith("xl/workbook.xml", StringComparison.OrdinalIgnoreCase))
                    isExcel = true;
            }

            // An OOXML package without [Content_Types].xml at its root is not an OOXML package. This is
            // the check that rejects a plain .zip that happened to arrive with an Office extension.
            if (!hasContentTypes)
                throw NotOoxml();

            // Exactly one of the two part markers. Neither means a plain archive; both means something
            // that is not a single Office document, and guessing which half to believe would be
            // choosing an answer rather than proving one.
            if (isWord == isExcel)
                throw NotOoxml();

            return isWord ? Docx : Xlsx;
        }
        catch (AppException)
        {
            throw;
        }
        catch (Exception)
        {
            // InvalidDataException from a truncated or corrupt archive, and anything else the parser
            // can be provoked into. All of it is client input.
            throw new AppException("The uploaded file is not a readable Office document.", 422);
        }
    }

    /// <summary>
    /// Section 3.3a. Text has no signature, so these four rules stand in for one.
    ///
    /// Note what is NOT here: no CSV parsing, no delimiter sniffing, no line-ending normalisation and
    /// no encoding conversion. The bytes are stored exactly as they arrived, BOM included, because
    /// section 1 requires a byte-identical round trip -- the BOM is skipped for the purpose of
    /// validation and never stripped from storage.
    /// </summary>
    private static void ValidateTextShape(byte[] content)
    {
        // A UTF-8 BOM is permitted, and skipped before the checks below.
        var offset = content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF
            ? 3
            : 0;

        // Rule 4, first because it is the cheapest and the one that matters most: a PDF renamed .txt,
        // an OOXML container renamed .csv, a gzip renamed .txt. The allow-listed signatures are already
        // excluded -- Sniff returned Unknown to get here -- so this covers the EXCLUDED table.
        // "The extension-must-not-contradict-the-bytes rule still holds in this direction, which is
        // the direction that matters."
        var body = content.AsSpan(offset);
        foreach (var signature in ExcludedArchiveSignatures)
            if (StartsWith(body, signature))
                throw NotText();

        // "ustar" at offset 257 is a tar archive.
        if (body.Length >= 262 && Encoding.ASCII.GetString(body.Slice(257, 5)) == "ustar")
            throw NotText();

        // Rule 1. Text does not contain NULs; binary content reliably does. One check, and it rejects
        // the overwhelming majority of binaries mislabelled .txt.
        if (content.AsSpan().IndexOf((byte)0x00) >= 0)
            throw NotText();

        // Rule 2. Strict decode, and the exception is a 422 -- never a 500, and never a run of
        // replacement characters silently stored as though the file had been understood.
        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(body);
        }
        catch (DecoderFallbackException)
        {
            throw NotText();
        }

        // Rule 3. TAB, LF and CR are the three that appear in real text files; every other control
        // character -- an ESC at 0x1B, a BEL, a stray C1 byte -- is a sign that this is not text.
        foreach (var character in text)
            if (char.IsControl(character) && character is not ('\t' or '\n' or '\r'))
                throw NotText();

        foreach (var prefix in ExcludedMarkupPrefixes)
            if (text.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw NotText();

        // Nothing here establishes that the TEXT is harmless -- no check could. A .txt containing
        // <script>alert(1)</script> is accepted, and what makes that safe is DownloadShaping's
        // unconditional Content-Disposition: attachment plus X-Content-Type-Options: nosniff.
        //
        // IF AN INLINE DOWNLOAD PATH IS EVER ADDED, .csv AND .txt MUST LEAVE THE ALLOW-LIST IN THE
        // SAME CHANGE. For every other entry the attachment header is defence in depth; for these two
        // it is the only defence.
    }

    private static string Require(string extension, string contentType, params string[] allowed) =>
        allowed.Contains(extension, StringComparer.Ordinal)
            ? contentType
            : throw Rejected(extension);

    private static bool StartsWith(ReadOnlySpan<byte> content, ReadOnlySpan<byte> signature) =>
        content.Length >= signature.Length && content[..signature.Length].SequenceEqual(signature);

    private static AppException TooLarge() =>
        new($"The uploaded file exceeds the maximum of {MaxUploadSizeBytes} bytes.", 422);

    private static AppException NotOoxml() =>
        new("The uploaded file is an archive, not an Office document.", 422);

    private static AppException NotText() =>
        new("The uploaded file is not the plain text its extension claims.", 422);

    // Deliberately says only that the file was not accepted. It is an allow-list, not a block-list, so
    // "unrecognised" is the whole answer: an error naming which signature was found would tell an
    // attacker exactly what the sniffer saw.
    private static AppException Rejected(string extension) =>
        new(
            string.IsNullOrEmpty(extension)
                ? "The uploaded file's type is not accepted."
                : $"The uploaded file's contents do not match an accepted type for '{extension}'.",
            422);
}

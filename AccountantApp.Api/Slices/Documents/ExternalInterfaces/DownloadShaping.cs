using System.Text;

namespace AccountantApp.Api.Slices.Documents.ExternalInterfaces;

/// <summary>
/// The three headers every download carries. A record rather than four loose strings so a caller
/// cannot set two of them and forget the third.
/// </summary>
public sealed record DownloadHeaders(string ContentType, string ContentDisposition)
{
    public const string ContentTypeOptionsHeaderName = "X-Content-Type-Options";

    /// <summary>
    /// Without this a browser may sniff the content and disregard the declared type, which
    /// reintroduces exactly what the attachment disposition prevents. Always set, on every download.
    /// </summary>
    public const string ContentTypeOptionsHeaderValue = "nosniff";

    public string ContentTypeOptions => ContentTypeOptionsHeaderValue;
}

/// <summary>
/// The rules for serving bytes. They live in THIS slice, not in the Tickets endpoint that will call
/// them, because they are properties of serving bytes rather than of authorizing a ticket -- and
/// because headers that can be got right in one place and wrong in another eventually are.
///
/// This slice has no endpoints (plan section 0.2), so nothing here touches HttpResponse. Tickets asks
/// for the header values and writes them.
///
/// IN ExternalInterfaces, NOT Application (moved 2026-09-02). Tickets is the only caller and it is
/// obliged to call this -- the download route cannot write the headers without it -- so this is part of
/// the contract, and dependency rule 2 lets another slice read only this folder. It sat in Application,
/// which made the single reference the plan mandates a rule violation on paper while being the only
/// correct thing to write. For DocumentSummary, which this takes, see IDocumentApi.cs alongside.
/// </summary>
public static class DownloadShaping
{
    /// <summary>
    /// ALWAYS "attachment". Never "inline", never absent, not configurable, not a query parameter, and
    /// not "inline for images because the SPA wants a preview" -- if the SPA wants a preview it builds
    /// one from the downloaded blob client-side.
    ///
    /// 01-DomainModel.md section 6: the SPA and the API SHARE AN ORIGIN, so an HTML or SVG file served
    /// inline runs script with the session cookie available. The allow-list already excludes SVG and
    /// HTML, which makes this defence in depth for most types -- two independent controls, either of
    /// which would have been sufficient.
    ///
    /// For .csv and .txt it is NOT defence in depth. It is the only defence: those entries accept
    /// arbitrary text, which includes the text of an HTML document or a script, and no allow-list check
    /// can establish that text is harmless (see UploadValidation.ValidateTextShape). If an inline path
    /// is ever added, .csv and .txt must leave the allow-list in the same change.
    /// </summary>
    public const string Attachment = "attachment";

    /// <summary>The stored content type, the attachment disposition, and nosniff, from one place.</summary>
    public static DownloadHeaders For(DocumentSummary document) =>
        new(document.ContentType, ContentDispositionFor(document.OriginalFileName));

    /// <summary>
    /// The file name was already sanitised for STORAGE. It is sanitised again here because it now
    /// escapes into an HTTP header, which is a different context with different dangerous characters:
    ///
    ///   - A CR or LF in a header value is response splitting. The storage sanitiser already strips
    ///     control characters, and this does not rely on that: the two sanitisers protect different
    ///     things and either may be relaxed later without the other noticing.
    ///   - A raw non-ASCII byte is not valid in a header value at all, and Greek file names are the
    ///     normal case for this application rather than an edge case. So the name goes out twice: an
    ///     ASCII-only quoted filename= for old clients, and the RFC 5987 filename*=UTF-8'' form that
    ///     every current browser prefers.
    ///   - The quoted form escapes " and \, which would otherwise end the value early.
    /// </summary>
    public static string ContentDispositionFor(string fileName)
    {
        var safe = new StringBuilder(fileName.Length);
        foreach (var character in fileName)
            if (!char.IsControl(character))
                safe.Append(character);

        var cleaned = safe.ToString();
        if (cleaned.Length == 0)
            cleaned = "download";

        // The ASCII fallback. Non-ASCII becomes '_' rather than being dropped, so a name that is
        // entirely Greek still reads as a file name of about the right length instead of an empty one.
        var ascii = new StringBuilder(cleaned.Length);
        foreach (var character in cleaned)
            ascii.Append(character switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                _ => character <= 0x7F ? character.ToString() : "_"
            });

        // Uri.EscapeDataString leaves only A-Za-z0-9-._~ unescaped, all of which are legal attr-chars,
        // so the result is always a valid ext-value. Over-escaping is permitted; under-escaping is not.
        var encoded = Uri.EscapeDataString(cleaned);

        return $"{Attachment}; filename=\"{ascii}\"; filename*=UTF-8''{encoded}";
    }
}

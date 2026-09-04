namespace AccountantApp.Api.Slices.Documents.ExternalInterfaces;

/// <summary>
/// The upload limits this slice will enforce, published so the slice that owns the HTTP surface can
/// enforce them EARLIER.
///
/// IN ExternalInterfaces, NOT Application (moved 2026-09-02). MaxUploadSizeBytes was declared inside
/// UploadValidation, which Tickets may not read -- yet Tickets is obliged to apply the same number as an
/// endpoint-level RequestSizeLimit / MultipartBodyLengthLimit, because those are endpoint knobs and this
/// slice HAS NO ENDPOINTS and physically cannot set them. That left two bad options and no good one:
/// reach into Application, or write 26_214_400 a second time. A duplicated limit is worse than it looks
/// -- the two values disagree only for uploads sized between them, so the bug is invisible until a file
/// lands in the gap.
/// </summary>
public static class DocumentLimits
{
    /// <summary>
    /// THE 25 MB LIMIT, DECLARED ONCE. 26214400 is 25 * 1024 * 1024, written as the literal because
    /// 25 * 1000 * 1000 is a different number: an application limit computed one way and a proxy limit
    /// configured the other leaves a band of file sizes that fail at the proxy with an error the
    /// application never sees.
    ///
    /// Three enforcement points, all from this one constant:
    ///   - UploadValidation.ReadWithinLimitAsync, which stops reading rather than buffering an
    ///     oversized body;
    ///   - the Tickets upload endpoint's RequestSizeLimit / MultipartBodyLengthLimit;
    ///   - ck_documents_size in the migration, which is the one that cannot be bypassed.
    ///
    /// The matching proxy limit (Caddy's request_body max_size) is DEFERRED: there is no Caddyfile and
    /// no deployment layer in this repository, and inventing one to satisfy a cross-reference would be
    /// worse than recording the gap. Whoever builds it takes the number from here.
    /// </summary>
    public const long MaxUploadSizeBytes = 26_214_400;

    /// <summary>Matches original_file_name VARCHAR(255).</summary>
    public const int MaxFileNameLength = 255;
}

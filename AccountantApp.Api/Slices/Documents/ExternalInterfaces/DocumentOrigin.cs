namespace AccountantApp.Api.Slices.Documents.ExternalInterfaces;

/// <summary>
/// Derived by Tickets from the uploader's ROLE and never taken from a request body. An Accountant
/// uploading gives AccountantResponse; a Customer-side actor gives CustomerUpload.
/// 01-DomainModel.md section 6.
///
/// IN ExternalInterfaces, NOT Core, AND MOVED HERE DELIBERATELY (amended 2026-09-02). This is contract
/// VOCABULARY, not an internal detail: StoreDocumentRequest -- which lives in this folder and is the only
/// way into this slice -- validates Origin against All and throws on anything else. So the contract
/// already required one of these two exact strings while hiding them in Core, where dependency rule 2
/// forbids the calling slice from reading them. The one caller that must produce an Origin therefore had
/// no legal way to name one, and duplicated the literals in
/// Tickets/Application/Handlers/UploadDocumentHandler.cs instead. A second definition of a value matched
/// with StringComparer.Ordinal is a typo away from a throw at the boundary.
///
/// Documents.Core referencing this namespace is fine -- a slice may read its own ExternalInterfaces, and
/// Document.Origin's default comes from here.
/// </summary>
public static class DocumentOrigin
{
    public const string CustomerUpload = "CustomerUpload";
    public const string AccountantResponse = "AccountantResponse";

    /// <summary>
    /// Ordinal, matching ck_documents_origin, which is case-sensitive: accepting "customerupload" here
    /// would let a row through this check and fail in the database -- a 500 where a 422 belongs.
    /// </summary>
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { CustomerUpload, AccountantResponse };
}

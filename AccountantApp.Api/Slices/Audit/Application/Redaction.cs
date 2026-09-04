using System.Text.Json;
using System.Text.Json.Nodes;

namespace AccountantApp.Api.Slices.Audit.Application;

internal static class Redaction
{
    private const int MaximumLength = 8 * 1024;

    // Matched as substrings, case-insensitively, not as whole property names. Exact matching is
    // the wrong test for this: it redacted "PasswordHash" but let "NewPasswordHash" through, and
    // redacted "Token" but let "AccessToken" and "RefreshTokenHash" through — and the properties
    // an audit entry actually carries are named after what changed, so the qualified spelling is
    // the common one. Substring matching over-redacts (a property called "TokenCount" is
    // redacted); over-redacting an audit row costs a diagnostic, under-redacting writes a
    // credential into a table nothing ever purges.
    //
    // Terms only, never a spelling: "passwordhash" and "invitationtoken" were removed from this
    // list because "password" and "token" already cover them, and a list of spellings invites
    // the reader to add the next one instead of trusting the term.
    private static readonly string[] DeniedTerms =
    [
        "password", "hash", "salt", "token", "secret", "apikey", "sessionid", "cookie"
    ];

    public static string? ToJson(object? value, ILogger logger)
    {
        if (value is null)
            return null;

        var node = SerializeToNode(value, logger);
        Redact(node);
        var json = node?.ToJsonString() ?? "null";
        return json.Length <= MaximumLength
            ? json
            : JsonSerializer.Serialize(new { truncated = true, length = json.Length });
    }

    // Serialisation is the one step here that can throw on a value the caller chose: a cyclic
    // object graph, a type with a throwing getter, a property with an unsupported converter. An
    // audit write is a side effect of some other operation, so letting that exception out would
    // fail the operation being audited — a customer edit rejected with a 500 because the audit
    // payload happened to hold an awkward object. Record that the payload could not be captured
    // and let the operation finish; the row itself, with actor, action and target, is the part
    // that matters. Callers are expected to pass anonymous records of primitives, so this path
    // firing means a caller made a mistake, which is why it is JSON a reader will notice.
    private static JsonNode? SerializeToNode(object value, ILogger logger)
    {
        try
        {
            return JsonSerializer.SerializeToNode(value);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            logger.LogError(exception,
                "An audit payload of type {PayloadType} could not be serialised; the audit row " +
                "was written with a placeholder instead. Audit payloads must be plain data.",
                value.GetType().Name);
            return JsonSerializer.SerializeToNode(new
            {
                unserialisable = true,
                type = value.GetType().Name
            });
        }
    }

    private static void Redact(JsonNode? node)
    {
        if (node is JsonObject jsonObject)
        {
            foreach (var property in jsonObject.ToList())
            {
                if (IsDenied(property.Key))
                    jsonObject[property.Key] = "[redacted]";
                else
                    Redact(property.Value);
            }
        }
        else if (node is JsonArray jsonArray)
        {
            foreach (var item in jsonArray)
                Redact(item);
        }
    }

    private static bool IsDenied(string propertyName) =>
        DeniedTerms.Any(term => propertyName.Contains(term, StringComparison.OrdinalIgnoreCase));
}

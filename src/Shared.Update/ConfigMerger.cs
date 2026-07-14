using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace CopilotBridge.Update.Wire;

/// <summary>
/// Thrown when configuration migration cannot proceed safely — currently only
/// when an input object contains case-insensitively duplicate keys, which is
/// ambiguous under .NET configuration key semantics. The updater treats this as
/// a pre-cutover failure and never stops the old bridge.
/// </summary>
internal sealed class ConfigMergeException : Exception
{
    public ConfigMergeException(string message) : base(message) { }
}

/// <summary>
/// Migrates an operator's <c>appsettings.json</c> onto a new release's stock
/// <c>appsettings.json</c>. The new file is the complete template; the old file
/// only overlays values for keys that still exist in the template.
/// </summary>
/// <remarks>
/// The merge walks <b>only the new tree</b>, so the output's property spelling,
/// structure, and set of keys come from the template:
/// <list type="bullet">
///   <item><b>new-only</b> property → keep the new default;</item>
///   <item><b>object/object</b> match → recurse;</item>
///   <item>any other match (scalar/scalar, array/array, or a type mismatch) →
///         emit the <b>complete old value</b> atomically. Arrays are therefore
///         never element-merged, appended, sorted, or de-duplicated; the whole
///         old array replaces the whole new array. A JSON <c>null</c> on the old
///         side is a value and overrides the new default;</item>
///   <item><b>old-only</b> property → omitted — but only at an object/object
///         merge frontier. A whole old subtree copied atomically because of a
///         type mismatch keeps its nested old-only content.</item>
/// </list>
/// Key matching is case-insensitive to mirror .NET configuration semantics; the
/// output uses the template's spelling. Case-insensitively duplicate keys in
/// either input at any object level are ambiguous and fail the merge.
/// This function is pure over its two byte inputs — the caller owns reading the
/// installed bytes once into an immutable snapshot, hashing them, and using the
/// same bytes for both this merge and the private rollback copy.
/// </remarks>
internal static class ConfigMerger
{
    // Match Microsoft.Extensions.Configuration.Json's tolerance so a config the
    // running bridge accepts also parses here (it skips comments and allows a
    // trailing comma).
    private static readonly JsonDocumentOptions DocOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 128,
    };

    /// <summary>
    /// Produce the merged configuration text. <paramref name="oldJson"/> is the
    /// installed file (the immutable snapshot); <paramref name="newJson"/> is the
    /// new release's stock file (the template).
    /// </summary>
    /// <exception cref="ConfigMergeException">
    /// Either input has case-insensitively duplicate keys at some object level.
    /// </exception>
    /// <exception cref="JsonException">Either input is not valid JSON.</exception>
    public static string Merge(string oldJson, string newJson)
    {
        using var oldDoc = JsonDocument.Parse(oldJson, DocOptions);
        using var newDoc = JsonDocument.Parse(newJson, DocOptions);

        RejectCaseInsensitiveDuplicates(oldDoc.RootElement);
        RejectCaseInsensitiveDuplicates(newDoc.RootElement);

        var buffer = new ArrayBufferWriter<byte>();
        var writerOptions = new JsonWriterOptions
        {
            Indented = true,
            // Preserve values verbatim: don't \uXXXX-escape '<', '>', '&', '+',
            // or non-ASCII text an operator may have placed in a value.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        using (var writer = new Utf8JsonWriter(buffer, writerOptions))
        {
            WriteMerged(writer, newDoc.RootElement, oldDoc.RootElement, hasOld: true);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteMerged(
        Utf8JsonWriter writer, JsonElement newElem, JsonElement oldElem, bool hasOld)
    {
        // new-only: no old counterpart — emit the template value verbatim.
        if (!hasOld)
        {
            newElem.WriteTo(writer);
            return;
        }

        // object/object: the only recursion frontier, and the only place an
        // old-only key is dropped (by iterating the template, not the old side).
        if (newElem.ValueKind == JsonValueKind.Object && oldElem.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var prop in newElem.EnumerateObject())
            {
                writer.WritePropertyName(prop.Name);
                if (TryFindCaseInsensitive(oldElem, prop.Name, out var oldChild))
                {
                    WriteMerged(writer, prop.Value, oldChild, hasOld: true);
                }
                else
                {
                    WriteMerged(writer, prop.Value, default, hasOld: false);
                }
            }
            writer.WriteEndObject();
            return;
        }

        // Any non-object/object match (scalar, array, or type mismatch): the
        // entire old value replaces the new one, atomically and with all nested
        // content intact.
        oldElem.WriteTo(writer);
    }

    private static bool TryFindCaseInsensitive(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    // Recursively reject case-insensitively duplicate object keys anywhere in the
    // tree (including inside arrays that will be copied verbatim) — "any object
    // level in either input" per the spec.
    private static void RejectCaseInsensitiveDuplicates(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in element.EnumerateObject())
                {
                    if (!seen.Add(prop.Name))
                    {
                        throw new ConfigMergeException(
                            $"Ambiguous configuration: object has case-insensitively duplicate key '{prop.Name}'.");
                    }
                    RejectCaseInsensitiveDuplicates(prop.Value);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    RejectCaseInsensitiveDuplicates(item);
                }
                break;
        }
    }
}

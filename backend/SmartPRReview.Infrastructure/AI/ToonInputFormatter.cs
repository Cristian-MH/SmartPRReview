using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using SmartPRReview.Application.AI;

namespace SmartPRReview.Infrastructure.AI;

// Uniform primitive table subset of TOON 4.1, sections 7 and 9.3.
public static class ToonEncoder
{
    public static string Encode(IReadOnlyList<Dictionary<string, string?>> rows) =>
        EncodeValues(rows.Select(r => r.ToDictionary(p => p.Key, p => (object?)p.Value)).ToArray());
    public static string EncodeValues(IReadOnlyList<Dictionary<string, object?>> rows, string name = "rows")
    {
        if (!Regex.IsMatch(name, "\\A[A-Za-z_][A-Za-z0-9_.]*\\z")) throw new ArgumentException("Invalid TOON table name.");
        if (rows.Count == 0) return $"{name}[0]:";
        var keys = rows[0].Keys.ToArray();
        if (keys.Length == 0 || keys.Any(k => !Regex.IsMatch(k, "\\A[A-Za-z_][A-Za-z0-9_.]*\\z")) ||
            rows.Any(r => !r.Keys.SequenceEqual(keys))) throw new ArgumentException("TOON requiere columnas uniformes.");
        return $"{name}[{rows.Count}]{{{string.Join(',', keys)}}}:\n" +
            string.Join('\n', rows.Select(r => "  " + string.Join(',', keys.Select(k => Primitive(r[k])))));
    }
    private static string Primitive(object? value) => value switch
    {
        null => "null", string text => Quote(text), bool flag => flag ? "true" : "false",
        int or long or decimal => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!,
        _ => throw new ArgumentException("Unsupported TOON primitive.")
    };
    private static string Quote(string value)
    {
        // Reject lone surrogates instead of silently changing content.
        _ = new UTF8Encoding(false, true).GetByteCount(value);
        if (value.Length > 0 && value.Trim(' ', '\t') == value && value is not ("true" or "false" or "null") &&
            !Regex.IsMatch(value, "\\A[+-]?[0-9]+(?:\\.[0-9]+)?(?:[eE][+-]?[0-9]+)?\\z") &&
            !value.Any(c => c < 32 || ":\"\\[]{},".Contains(c)) && value[0] is not ('-' or '#')) return value;
        var output = new StringBuilder("\"");
        foreach (var c in value)
            output.Append(c switch { '\\' => "\\\\", '"' => "\\\"", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", < ' ' => "\\u" + ((int)c).ToString("x4"), _ => c.ToString() });
        return output.Append('"').ToString();
    }
}
public sealed class ToonInputFormatter : IModelInputFormatter
{
    public async Task<string> FormatAsync(IAiModelClient client, string model, IReadOnlyList<Dictionary<string, string?>> rows, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(rows);
        string toon;
        try { toon = "TOON 4.1 table: rows[N]{columns}; comma-delimited primitive cells; quoted values are strings.\n" + ToonEncoder.Encode(rows); }
        catch (Exception e) when (e is ArgumentException or EncoderFallbackException) { return json; }
        var jsonCount = await client.CountTextAsync(model, json, ct);
        if (jsonCount is null) return json;
        var toonCount = await client.CountTextAsync(model, toon, ct);
        return toonCount is not null && toonCount < jsonCount ? toon : json;
    }
}

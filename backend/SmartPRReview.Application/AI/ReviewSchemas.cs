using System.Text.Json;

namespace SmartPRReview.Application.AI;

public static class ReviewSchemas
{
    public static readonly string[] Categories = ["feat", "fix", "perf", "style", "refactor", "docs", "test", "build", "ci", "chore"];
    public static JsonElement Classification => JsonDocument.Parse("""
        {"type":"object","additionalProperties":false,"required":["category","rationale","scope","evidence","confidence"],"properties":{
        "category":{"type":["string","null"],"enum":["feat","fix","perf","style","refactor","docs","test","build","ci","chore",null]},
        "rationale":{"type":"string"},"scope":{"type":"string"},"evidence":{"type":"array","items":{"type":"string"}},"confidence":{"type":"number","minimum":0,"maximum":1}}}
        """).RootElement.Clone();
    public static JsonElement Specialist => JsonDocument.Parse("""
        {"type":"object","additionalProperties":false,"required":["summary","findings","limitations"],"properties":{
        "summary":{"type":"string"},"limitations":{"type":"array","items":{"type":"string"}},
        "findings":{"type":"array","items":{"type":"object","additionalProperties":false,
        "required":["category","message","filePath","line","severity","confidence"],"properties":{
        "category":{"type":"string"},"message":{"type":"string"},"filePath":{"type":"string"},
        "line":{"type":["integer","null"]},"severity":{"type":"string","enum":["critical","high","medium","low"]},
        "confidence":{"type":"number","minimum":0,"maximum":1}}}}}}
        """).RootElement.Clone();
    public static readonly AiTool[] Tools =
    [
        new("read_file_range", "Read a file in this review's pinned snapshot. Maximum 200 lines; base or head revision.", Parse("""
            {"type":"object","additionalProperties":false,"required":["path","revision","startLine","lineCount"],"properties":{"path":{"type":"string"},"revision":{"type":"string","enum":["base","head"]},"startLine":{"type":"integer","minimum":1},"lineCount":{"type":"integer","minimum":1,"maximum":200}}}
            """)),
        new("search_snapshot", "Literal search of collected source only. Returns at most 10 matches.", Parse("""
            {"type":"object","additionalProperties":false,"required":["query"],"properties":{"query":{"type":"string"}}}
            """)),
        new("get_execution_result", "Read bounded actual execution results. Does not execute commands.", Parse("""
            {"type":"object","additionalProperties":false,"required":[],"properties":{}}
            """))
    ];
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // Validate provider JSON independently, including providers without strict schema enforcement.
    public static void Validate(JsonElement value, JsonElement schema)
    {
        var types = schema.GetProperty("type");
        var names = types.ValueKind == JsonValueKind.Array ? types.EnumerateArray().Select(x => x.GetString()).ToArray() : [types.GetString()];
        bool matches = names.Any(t => t switch
        {
            "null" => value.ValueKind == JsonValueKind.Null,
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _),
            "number" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out _),
            _ => false
        });
        if (!matches) throw new AiRequestException("Tipo de respuesta de IA inválido.");
        if (schema.TryGetProperty("enum", out var choices) && !choices.EnumerateArray().Any(x => x.ToString() == value.ToString())) throw new AiRequestException("Valor de IA fuera del catálogo permitido.");
        if (value.ValueKind == JsonValueKind.Object)
        {
            var properties = schema.GetProperty("properties");
            foreach (var required in schema.GetProperty("required").EnumerateArray()) if (!value.TryGetProperty(required.GetString()!, out _)) throw new AiRequestException("Respuesta de IA incompleta.");
            var seen = new HashSet<string>();
            foreach (var property in value.EnumerateObject())
            {
                if (!seen.Add(property.Name) || !properties.TryGetProperty(property.Name, out var child)) throw new AiRequestException("Propiedad de IA no permitida.");
                Validate(property.Value, child);
            }
        }
        if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Validate(item, schema.GetProperty("items"));
        if (value.ValueKind == JsonValueKind.Number)
        {
            var n = value.GetDecimal();
            if (schema.TryGetProperty("minimum", out var min) && n < min.GetDecimal() || schema.TryGetProperty("maximum", out var max) && n > max.GetDecimal()) throw new AiRequestException("Número de IA fuera del rango permitido.");
        }
    }
}

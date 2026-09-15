using System.Text.Json;
using Json.Schema;

namespace bidding_service.Tests;

internal static class CanonicalEventSchema
{
    private static readonly JsonSchema Envelope = Load("event-envelope");

    public static void AssertValid(string eventSchemaName, string json)
    {
        SchemaRegistry.Global.Register(Envelope);
        var schema = Load(eventSchemaName);
        using var document = JsonDocument.Parse(json);
        var result = schema.Evaluate(document.RootElement);
        Assert.True(result.IsValid, result.ToString());
    }

    private static JsonSchema Load(string name)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "tests", "contracts", "schemas", "v1", $"{name}.schema.json");
        return JsonSchema.FromText(File.ReadAllText(Path.GetFullPath(path)));
    }
}

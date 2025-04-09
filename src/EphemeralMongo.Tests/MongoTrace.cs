using System.Text.Json;
using System.Text.Json.Serialization;

namespace EphemeralMongo.Tests;

internal sealed class MongoTrace
{
    [JsonPropertyName("s")]
    public string Severity { get; set; } = string.Empty;

    [JsonPropertyName("c")]
    public string Component { get; set; } = string.Empty;

    [JsonPropertyName("msg")]
    public string Message { get; set; } = string.Empty;

    public static void LogToOutput(string text, ITestOutputHelper output)
    {
        try
        {
            var trace = JsonSerializer.Deserialize<MongoTrace>(text);

            if (trace != null && !string.IsNullOrEmpty(trace.Message))
            {
                // https://www.mongodb.com/docs/manual/reference/log-messages/#std-label-log-severity-levels
                var logLevel = trace.Severity switch
                {
                    "F" => "CTR",
                    "E" => "ERR",
                    "W" => "WRN",
                    _ => "INF",
                };

                const int longestComponentNameLength = 8;
                output.WriteLine("{0} {1} {2}", logLevel, trace.Component.PadRight(longestComponentNameLength), trace.Message);
                return;
            }
        }
        catch (JsonException)
        {
        }

        output.WriteLine(text);
    }
}
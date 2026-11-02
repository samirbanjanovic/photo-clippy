using System.Text.Json;
using PhotoClippy.Contracts;

namespace PhotoClippy.Color.Tests;

internal static class TestJson
{
    public static string Serialize(ColorFeatures features) => JsonSerializer.Serialize(features, ContractJson.Options);
}

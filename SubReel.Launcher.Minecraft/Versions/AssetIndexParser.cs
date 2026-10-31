using System.Text.Json;
using SubReel.Launcher.Domain.Games;

namespace SubReel.Launcher.Minecraft.Versions;

public static class AssetIndexParser
{
    public static AssetIndex Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("objects", out var objects) || objects.ValueKind != JsonValueKind.Object)
                throw new VersionMetadataException("В индексе ассетов нет раздела objects.");

            var result = new Dictionary<string, AssetObject>(StringComparer.Ordinal);
            foreach (var property in objects.EnumerateObject())
            {
                var value = property.Value;
                if (value.ValueKind != JsonValueKind.Object
                    || !value.TryGetProperty("hash", out var hash) || hash.ValueKind != JsonValueKind.String
                    || !value.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out var sizeValue))
                    throw new VersionMetadataException($"Некорректная запись ассета «{property.Name}».");

                var hashText = hash.GetString()!;
                if (hashText.Length != 40 || !hashText.All(Uri.IsHexDigit))
                    throw new VersionMetadataException($"Некорректный хэш у ассета «{property.Name}».");

                result[property.Name] = new AssetObject(hashText.ToLowerInvariant(), sizeValue);
            }

            return new AssetIndex(result, Flag(root, "virtual"), Flag(root, "map_to_resources"));
        }
        catch (JsonException ex)
        {
            throw new VersionMetadataException("Индекс ассетов не является корректным JSON.", ex);
        }
    }

    private static bool Flag(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}

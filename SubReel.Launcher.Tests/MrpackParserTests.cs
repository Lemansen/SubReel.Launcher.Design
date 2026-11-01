using SubReel.Launcher.Domain.Modpacks;
using SubReel.Launcher.Domain.Versions;

namespace SubReel.Launcher.Tests;

public sealed class MrpackParserTests
{
    private const string Sha1 = "0123456789abcdef0123456789abcdef01234567";
    private static readonly string Sha512 = new('a', 128);

    internal static string FileJson(string path = "mods/sodium.jar", string url = "https://cdn.modrinth.com/data/AANobbMI/versions/x/sodium.jar",
        string? sha1 = null, string? sha512 = null, string? env = null)
    {
        var envPart = env is null ? "" : "\"env\": " + env + ",";
        var hash1 = sha1 ?? Sha1;
        var hash512 = sha512 ?? Sha512;
        return $$"""
        { "path": "{{path}}", "hashes": { "sha1": "{{hash1}}", "sha512": "{{hash512}}" },
          {{envPart}} "downloads": ["{{url}}"], "fileSize": 1234 }
        """;
    }

    internal static string PackJson(string files = "", string deps = "\"minecraft\": \"1.20.1\", \"fabric-loader\": \"0.15.0\"", string format = "1", string game = "minecraft", string name = "Моя сборка") =>
        $$"""
        { "formatVersion": {{format}}, "game": "{{game}}", "versionId": "1.0.0", "name": "{{name}}", "summary": "Описание",
          "files": [ {{files}} ], "dependencies": { {{deps}} } }
        """;

    [Fact]
    public void Parses_name_versions_loader_and_client_files()
    {
        var json = PackJson(FileJson() + "," + FileJson("mods/server-only.jar", env: "{\"client\":\"unsupported\",\"server\":\"required\"}"));

        var index = MrpackParser.Parse(json);

        Assert.Equal("Моя сборка", index.Name);
        Assert.Equal("1.20.1", index.McVersion);
        Assert.Equal(LoaderKind.Fabric, index.Loader);
        Assert.Equal("0.15.0", index.LoaderVersion);
        var file = Assert.Single(index.Files);
        Assert.Equal("mods/sodium.jar", file.Path);
        Assert.Equal(Sha1, file.Sha1);
        Assert.Equal(1234, file.Size);
    }

    [Theory]
    [InlineData("\"minecraft\": \"1.20.1\"", LoaderKind.Vanilla)]
    [InlineData("\"minecraft\": \"1.20.1\", \"quilt-loader\": \"0.26.0\"", LoaderKind.Quilt)]
    [InlineData("\"minecraft\": \"1.20.1\", \"forge\": \"47.2.0\"", LoaderKind.Forge)]
    [InlineData("\"minecraft\": \"1.21.1\", \"neoforge\": \"21.1.77\"", LoaderKind.NeoForge)]
    public void Maps_dependencies_to_loaders(string deps, LoaderKind expected)
    {
        var index = MrpackParser.Parse(PackJson(deps: deps));

        Assert.Equal(expected, index.Loader);
        Assert.Equal(expected == LoaderKind.Vanilla, index.LoaderVersion is null);
    }

    [Theory]
    [InlineData("\"fabric-loader\": \"0.15.0\"")]                                    // нет minecraft
    [InlineData("\"minecraft\": \"1.20.1\", \"fabric-loader\": \"1\", \"forge\": \"2\"")] // два загрузчика
    [InlineData("\"minecraft\": \"1.20.1\", \"optifine\": \"1\"")]                    // неизвестная зависимость
    [InlineData("\"minecraft\": \"\"")]
    public void Rejects_bad_dependencies(string deps) =>
        Assert.Throws<ModpackException>(() => MrpackParser.Parse(PackJson(deps: deps)));

    [Fact]
    public void Rejects_other_format_version_game_and_broken_json()
    {
        Assert.Throws<ModpackException>(() => MrpackParser.Parse(PackJson(format: "2")));
        Assert.Throws<ModpackException>(() => MrpackParser.Parse(PackJson(game: "terraria")));
        Assert.Throws<ModpackException>(() => MrpackParser.Parse("{ not json"));
        Assert.Throws<ModpackException>(() => MrpackParser.Parse("[]"));
    }

    [Theory]
    [InlineData("../evil.jar")]
    [InlineData("mods/../../evil.jar")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/evil.dll")]
    [InlineData("mods/a:b.jar")]
    [InlineData("mods//x.jar")]
    [InlineData("mods/x.jar/")]
    [InlineData("mods/con?.jar")]
    public void Rejects_unsafe_file_paths(string path) =>
        Assert.Throws<ModpackException>(() => MrpackParser.Parse(PackJson(FileJson(path))));

    [Theory]
    [InlineData("http://cdn.modrinth.com/data/x.jar")]
    [InlineData("https://evil.example.com/x.jar")]
    [InlineData("https://cdn.modrinth.com.evil.com/x.jar")]
    [InlineData("https://user:pass@cdn.modrinth.com/x.jar")]
    [InlineData("file:///C:/x.jar")]
    public void Rejects_untrusted_download_addresses(string url) =>
        Assert.Throws<ModpackException>(() => MrpackParser.Parse(PackJson(FileJson(url: url))));

    [Fact]
    public void Uses_the_first_trusted_address_when_there_are_several()
    {
        var file = FileJson().Replace("\"downloads\": [", "\"downloads\": [\"https://evil.example.com/a.jar\", ");

        var index = MrpackParser.Parse(PackJson(file));

        Assert.StartsWith("https://cdn.modrinth.com/", index.Files[0].Url);
    }

    [Theory]
    [InlineData("zzzz")]
    [InlineData("0123")]
    public void Rejects_bad_hashes(string sha1) =>
        Assert.Throws<ModpackException>(() => MrpackParser.Parse(PackJson(FileJson(sha1: sha1))));

    [Fact]
    public void Rejects_duplicate_paths() =>
        Assert.Throws<ModpackException>(() => MrpackParser.Parse(PackJson(FileJson() + "," + FileJson("MODS/Sodium.jar"))));

    [Theory]
    [InlineData("mods/a.jar", "mods/a.jar")]
    [InlineData("config\\sodium.json", "config/sodium.json")]
    [InlineData("options.txt", "options.txt")]
    [InlineData("", null)]
    [InlineData("..", null)]
    [InlineData("a/./b", null)]
    [InlineData("a/b.", null)]
    [InlineData("a/b ", null)]
    public void NormalizePath_accepts_only_relative_clean_paths(string raw, string? expected) =>
        Assert.Equal(expected, MrpackParser.NormalizePath(raw));
}

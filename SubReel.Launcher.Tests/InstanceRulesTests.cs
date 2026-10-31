using SubReel.Launcher.Domain.Instances;

namespace SubReel.Launcher.Tests;

public sealed class InstanceRulesTests
{
    private static Instance Make(string name = "Моя сборка", string mc = "1.21.1") => new() { Id = "x", Name = name, McVersion = mc };

    [Theory]
    [InlineData("My Pack!", "My-Pack")]
    [InlineData("  Survival 1.21  ", "Survival-1.21")]
    [InlineData("a   b", "a-b")]
    [InlineData("Привет", "instance")]
    [InlineData("...", "instance")]
    [InlineData("", "instance")]
    [InlineData("CON", "instance-CON")]
    [InlineData("con.txt", "instance-con.txt")]
    [InlineData("a/b\\c:d", "a-b-c-d")]
    public void ToFolderName_is_safe_and_never_empty(string name, string expected) =>
        Assert.Equal(expected, InstanceRules.ToFolderName(name));

    [Fact]
    public void ToFolderName_is_limited_in_length()
    {
        var folder = InstanceRules.ToFolderName(new string('a', 200));
        Assert.True(folder.Length <= InstanceRules.MaxNameLength);
    }

    [Fact]
    public void Valid_instance_passes() =>
        Assert.Equal(InstanceValidationError.None, InstanceRules.Validate(Make(), Array.Empty<Instance>()));

    [Fact]
    public void Empty_name_and_missing_version_are_rejected()
    {
        Assert.Equal(InstanceValidationError.NameEmpty, InstanceRules.Validate(Make("   "), Array.Empty<Instance>()));
        Assert.Equal(InstanceValidationError.McVersionEmpty, InstanceRules.Validate(Make(mc: " "), Array.Empty<Instance>()));
    }

    [Fact]
    public void Too_long_name_is_rejected() =>
        Assert.Equal(InstanceValidationError.NameTooLong, InstanceRules.Validate(Make(new string('a', 65)), Array.Empty<Instance>()));

    [Fact]
    public void Duplicate_name_is_rejected_ignoring_case_but_not_for_the_same_instance()
    {
        var other = new Instance { Id = "other", Name = "Моя Сборка", McVersion = "1.20.1" };
        Assert.Equal(InstanceValidationError.NameDuplicate, InstanceRules.Validate(Make(), new[] { other }));

        var same = new Instance { Id = "x", Name = "Моя сборка", McVersion = "1.20.1" };
        Assert.Equal(InstanceValidationError.None, InstanceRules.Validate(Make(), new[] { same }));
    }

    [Theory]
    [InlineData(100, null)]
    [InlineData(null, 128)]
    [InlineData(4096, 2048)]
    [InlineData(null, 5_000_000)]
    public void Invalid_memory_is_rejected(int? min, int? max)
    {
        var instance = Make();
        instance.MinMemoryMb = min;
        instance.MaxMemoryMb = max;
        Assert.Equal(InstanceValidationError.MemoryInvalid, InstanceRules.Validate(instance, Array.Empty<Instance>()));
    }

    [Fact]
    public void Reasonable_memory_is_accepted()
    {
        var instance = Make();
        instance.MinMemoryMb = 1024;
        instance.MaxMemoryMb = 4096;
        Assert.Equal(InstanceValidationError.None, InstanceRules.Validate(instance, Array.Empty<Instance>()));
    }
}

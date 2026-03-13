using HPD.Yaml.Core;

namespace HPD.Yaml.Tests;

public class TimeSpanYamlConverterTests
{
    private record TimeSpanHolder
    {
        public TimeSpan Duration { get; init; }
        public TimeSpan? OptionalDuration { get; init; }
    }

    [Theory]
    [InlineData("PT30S", 30)]         // ISO 8601: 30 seconds
    [InlineData("PT5M", 300)]         // ISO 8601: 5 minutes
    [InlineData("PT2H", 7200)]        // ISO 8601: 2 hours
    [InlineData("P1D", 86400)]        // ISO 8601: 1 day
    [InlineData("PT1H30M", 5400)]     // ISO 8601: 1 hour 30 minutes
    public void Deserialize_Iso8601Duration_ParsesCorrectly(string yamlValue, double expectedSeconds)
    {
        var yaml = $"duration: {yamlValue}";
        var deserializer = YamlDefaults.CreateDeserializerBuilder().Build();

        var result = deserializer.Deserialize<TimeSpanHolder>(yaml);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), result.Duration);
    }

    [Theory]
    [InlineData("00:00:30", 30)]
    [InlineData("00:05:00", 300)]
    [InlineData("1.00:00:00", 86400)]
    public void Deserialize_DotNetFormat_ParsesCorrectly(string yamlValue, double expectedSeconds)
    {
        var yaml = $"duration: \"{yamlValue}\"";
        var deserializer = YamlDefaults.CreateDeserializerBuilder().Build();

        var result = deserializer.Deserialize<TimeSpanHolder>(yaml);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), result.Duration);
    }

    [Theory]
    [InlineData("30s", 30)]
    [InlineData("5m", 300)]
    [InlineData("2h", 7200)]
    [InlineData("1d", 86400)]
    public void Deserialize_SuffixedFormat_ParsesCorrectly(string yamlValue, double expectedSeconds)
    {
        var yaml = $"duration: {yamlValue}";
        var deserializer = YamlDefaults.CreateDeserializerBuilder().Build();

        var result = deserializer.Deserialize<TimeSpanHolder>(yaml);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), result.Duration);
    }

    [Fact]
    public void RoundTrip_TimeSpan_PreservesValue()
    {
        var original = new TimeSpanHolder
        {
            Duration = TimeSpan.FromMinutes(5),
            OptionalDuration = TimeSpan.FromSeconds(30)
        };

        var serializer = YamlDefaults.CreateSerializerBuilder().Build();
        var deserializer = YamlDefaults.CreateDeserializerBuilder().Build();

        var yaml = serializer.Serialize(original);
        var deserialized = deserializer.Deserialize<TimeSpanHolder>(yaml);

        Assert.Equal(original.Duration, deserialized.Duration);
        Assert.Equal(original.OptionalDuration, deserialized.OptionalDuration);
    }
}

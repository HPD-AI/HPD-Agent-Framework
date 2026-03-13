using HPD.OpenApi.Core;

namespace HPD.Yaml.Tests;

public class OpenApiYamlTests
{
    [Fact]
    public void YamlToJsonConverter_SimpleSpec_ConvertsCorrectly()
    {
        var yaml = @"
openapi: '3.0.1'
info:
  title: Pet Store
  version: '1.0.0'
paths:
  /pets:
    get:
      operationId: listPets
      summary: List all pets
      responses:
        '200':
          description: A list of pets
";

        var json = YamlToJsonConverter.ConvertToJson(yaml);

        Assert.Contains("openapi", json);
        Assert.Contains("Pet Store", json);
        Assert.Contains("listPets", json);
        Assert.Contains("/pets", json);
    }

    [Fact]
    public void YamlToJsonConverter_Stream_ConvertsCorrectly()
    {
        var yaml = @"
openapi: '3.0.1'
info:
  title: Test API
  version: '1.0.0'
";

        using var yamlStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(yaml));
        using var jsonStream = YamlToJsonConverter.ConvertToJsonStream(yamlStream);

        using var reader = new StreamReader(jsonStream);
        var json = reader.ReadToEnd();

        Assert.Contains("Test API", json);
    }

    [Theory]
    [InlineData("spec.yaml", true)]
    [InlineData("spec.yml", true)]
    [InlineData("spec.YAML", true)]
    [InlineData("spec.json", false)]
    [InlineData("spec.txt", false)]
    public void IsYamlFile_DetectsExtension(string path, bool expected)
    {
        Assert.Equal(expected, YamlToJsonConverter.IsYamlFile(path));
    }
}

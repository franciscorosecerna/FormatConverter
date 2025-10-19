using FormatConverter.Bxml;
using FormatConverter.Cbor;
using FormatConverter.Interfaces;
using FormatConverter.Json;
using FormatConverter.MessagePack;
using FormatConverter.Protobuf;
using FormatConverter.Toml;
using FormatConverter.Xml;
using FormatConverter.Yaml;
using Newtonsoft.Json.Linq;

namespace FormatTest
{
    public class Tests
    {
        private readonly string complexJson = @"
{
  ""project"": {
    ""id"": ""prj_9f8b7a6c"",
    ""name"": ""Simulación multisensorial — Nodo Áureo"",
    ""description"": ""Conjunto de datos de prueba con estructuras anidadas, meta, eventos y topologías de red."",
    ""createdAt"": ""2025-10-16T19:00:00-03:00"",
    ""tags"": [""simulación"", ""sensores"", ""geoespacial"", ""localización"", ""multilenguaje""]
  },
  ""nodes"": [
    {
      ""nodeId"": ""n-01"",
      ""type"": ""sensor"",
      ""specs"": {
        ""model"": ""MX-TempHum-3000"",
        ""capabilities"": [""temperature"", ""humidity"", ""battery""],
        ""precision"": { ""temperature"": ""±0.1°C"", ""humidity"": ""±1.5% RH"" }
      },
      ""location"": {
        ""name"": ""Techo - Sala A"",
        ""coordinates"": { ""lat"": -31.63333, ""lon"": -60.7, ""alt_m"": 5.2 }
      },
      ""state"": { ""online"": true, ""lastSeen"": ""2025-10-16T18:58:12-03:00"", ""battery"": 87 },
      ""metadata"": { ""calibration"": { ""date"": ""2025-03-05"", ""offsets"": { ""temperature"": -0.02, ""humidity"": 0.4 } }, ""notes"": null }
    },
    {
      ""nodeId"": ""n-02"",
      ""type"": ""camera"",
      ""specs"": { ""model"": ""CamX-4K"", ""resolutions"": [3840, 2160, 1920, 1080], ""fpsOptions"": [24, 30, 60] },
      ""location"": { ""name"": ""Entrada Principal"", ""coordinates"": { ""lat"": -31.63412, ""lon"": -60.70145 } },
      ""state"": { ""online"": false, ""lastSeen"": ""2025-10-16T11:12:01-03:00"", ""errorCodes"": [""E102"", ""E210""] },
      ""footageSampleBase64"": ""R0lGODdhAQABAIAAAAUEBA==""
    }
  ],
  ""extras"": {
    ""exampleMixedArray"": [42, ""texto"", null, { ""nested"": [true, false, 3.1415] }, [""lista"", { ""k"": ""v"" }]],
    ""uuidSample"": ""a3f5d8b2-7c4e-11ee-9a34-0242ac120002"",
    ""checksum"": {
      ""algorithm"": ""sha256"",
      ""value"": ""e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855""
    }
  }
}";

        [Theory]
        [InlineData("xml")]
        [InlineData("bxml")]
        [InlineData("protobuf")]
        [InlineData("cbor")]
        [InlineData("toml")]
        [InlineData("yaml")]
        [InlineData("messagepack")]
        [Trait("Category", "RoundTrip")]
        public void MyTest(string format)
        {
            var jsonIn = new JsonInputStrategy();
            var jsonOut = new JsonOutputStrategy();
            var token = jsonIn.Parse(complexJson);

            var (outputStrategy, inputStrategy) = CreateStrategies(format);

            var intermediate = outputStrategy.Serialize(token);
            var reconverted = inputStrategy.Parse(intermediate);
            var jsonRoundtrip = jsonOut.Serialize(reconverted);

            var resultToken = JToken.Parse(jsonRoundtrip);

            JTokenComparer.AssertEqual(token, resultToken);
        }

        private static (BaseOutputStrategy, BaseInputStrategy) CreateStrategies(string format)
        {
            return format switch
            {
                "xml" => (new XmlOutputStrategy(), new XmlInputStrategy()),
                "bxml" => (new BxmlOutputStrategy(), new BxmlInputStrategy()),
                "protobuf" => (new ProtobufOutputStrategy(), new ProtobufInputStrategy()),
                "cbor" => (new CborOutputStrategy(), new CborInputStrategy()),
                "yaml" => (new YamlOutputStrategy(), new YamlInputStrategy()),
                "messagepack" => (new MessagePackOutputStrategy(), new MessagePackInputStrategy()),
                "toml" => (new TomlOutputStrategy(), new TomlInputStrategy()),
                _ => throw new ArgumentException($"not support format: {format}"),
            };
        }
    }
}
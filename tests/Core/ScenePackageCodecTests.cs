using System;
using System.IO;
using System.Linq;
using AppAgasCs.Core.Persistence;
using Xunit;

namespace AppAgasCs.Tests.Core;

public sealed class ScenePackageCodecTests
{
    [Fact]
    public void SerializeDeserialize_RoundTripsLayerPromptsAndReferenceFileNames()
    {
        var document = new ScenePackageDocument(
            ScenePackageCodec.CurrentSchemaVersion,
            "scene-a",
            "Export Fixture",
            "layer-1",
            new[]
            {
                new ScenePackageLayer(
                    "layer-1",
                    "Layer 1",
                    0,
                    true,
                    false,
                    new[]
                    {
                        new ScenePackagePromptFragment("p1", "ruined city", true, 0, "Environment"),
                    },
                    new[]
                    {
                        new ScenePackageReferenceImage("ref-1", "ref.png", 0, 1.5, 1.5, 0, 1),
                    }),
            });

        string json = ScenePackageCodec.Serialize(document);
        ScenePackageDocument roundTrip = ScenePackageCodec.Deserialize(json);

        Assert.Equal("Export Fixture", roundTrip.Name);
        ScenePackageLayer layer = Assert.Single(roundTrip.Layers);
        Assert.Equal("ruined city", Assert.Single(layer.PromptFragments).Text);
        ScenePackageReferenceImage reference = Assert.Single(layer.ReferenceImages);
        Assert.Equal("ref.png", reference.FileName);
        Assert.Equal(1.5, reference.ScaleX);
        Assert.DoesNotContain("map.png", json, System.StringComparison.Ordinal);
    }

    [Fact]
    public void PackageFolderName_SanitizesUnsafeCharacters()
    {
        string folder = ScenePackageCodec.PackageFolderName("scene-1", "末日 地图 / A");
        Assert.DoesNotContain("/", folder, System.StringComparison.Ordinal);
        Assert.DoesNotContain(" ", folder, System.StringComparison.Ordinal);
        Assert.Contains("scene-1", folder, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Deserialize_RejectsPathTraversalReferenceFileNames()
    {
        var document = new ScenePackageDocument(
            ScenePackageCodec.CurrentSchemaVersion,
            "scene-a",
            "Bad",
            "layer-1",
            new[]
            {
                new ScenePackageLayer(
                    "layer-1",
                    "Layer 1",
                    0,
                    true,
                    false,
                    System.Array.Empty<ScenePackagePromptFragment>(),
                    new[]
                    {
                        new ScenePackageReferenceImage("ref-1", "../secret.png", 0, 1, 1, 0, 1),
                    }),
            });

        Assert.Throws<System.IO.InvalidDataException>(() =>
            ScenePackageCodec.Deserialize(ScenePackageCodec.Serialize(document)));
    }

    [Fact]
    public void Deserialize_ReadsLegacyV1WithoutV2Fields()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "sourceSceneId": "legacy-scene",
          "name": "Legacy",
          "activeLayerId": "layer-1",
          "layers": [
            {
              "id": "layer-1",
              "name": "Layer",
              "order": 0,
              "visible": true,
              "locked": false,
              "promptFragments": [],
              "referenceImages": []
            }
          ]
        }
        """;

        ScenePackageDocument document = ScenePackageCodec.Deserialize(json);

        Assert.Equal(ScenePackageCodec.LegacySchemaVersion, document.SchemaVersion);
        ScenePackageLayer legacyLayer = Assert.Single(document.Layers);
        Assert.Null(legacyLayer.Images);
        Assert.False(legacyLayer.Shared);
    }

    [Fact]
    public void SerializeDeserialize_RoundTripsSharedFlagAndOmitsUnsharedImagesInManifest()
    {
        var document = new ScenePackageDocument(
            ScenePackageCodec.CurrentSchemaVersion,
            "scene-a",
            "Pack",
            "layer-1",
            new[]
            {
                new ScenePackageLayer(
                    "layer-1",
                    "Combined",
                    0,
                    true,
                    false,
                    new[]
                    {
                        new ScenePackagePromptFragment("p1", "grass", true, 0, "General"),
                    },
                    Array.Empty<ScenePackageReferenceImage>(),
                    new[]
                    {
                        new ScenePackageImage("image-1", "images/layer-1/image-1.png", 0, 0, 0, 1, 1, 0, 1),
                    },
                    Shared: true),
                new ScenePackageLayer(
                    "layer-2",
                    "Local",
                    1,
                    true,
                    false,
                    new[]
                    {
                        new ScenePackagePromptFragment("p2", "keep me", true, 0, "General"),
                    },
                    Array.Empty<ScenePackageReferenceImage>(),
                    Array.Empty<ScenePackageImage>(),
                    Shared: false),
            });

        string json = ScenePackageCodec.Serialize(document);
        ScenePackageDocument roundTrip = ScenePackageCodec.Deserialize(json);
        ScenePackageLayer packedShared = Assert.Single(roundTrip.Layers, item => item.Id == "layer-1");
        ScenePackageLayer packedLocal = Assert.Single(roundTrip.Layers, item => item.Id == "layer-2");

        Assert.True(packedShared.Shared);
        Assert.Equal("image-1", Assert.Single(packedShared.Images!).Id);
        Assert.False(packedLocal.Shared);
        Assert.Empty(packedLocal.Images!);
        Assert.Equal("keep me", Assert.Single(packedLocal.PromptFragments).Text);
        Assert.Contains("\"shared\": true", json, StringComparison.Ordinal);
        Assert.Contains("keep me", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SerializeDeserialize_V2RoundTripsImagesAndIntegrity()
    {
        ScenePackageDocument source = ValidV2();

        string json = ScenePackageCodec.Serialize(source);
        ScenePackageDocument document = ScenePackageCodec.Deserialize(json);

        Assert.Equal(ScenePackageCodec.CurrentSchemaVersion, document.SchemaVersion);
        ScenePackageImage image = Assert.Single(Assert.Single(document.Layers).Images!);
        Assert.Equal((12.5, -4.25, 1.5, -0.75, 33, 0.6),
            (image.X, image.Y, image.ScaleX, image.ScaleY, image.RotationDegrees, image.Opacity));
        Assert.Equal(2, document.Integrity!.Count);
        Assert.DoesNotContain("blueprint", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../escape.png")]
    [InlineData("/tmp/escape.png")]
    [InlineData("C:/escape.png")]
    [InlineData("images\\escape.png")]
    [InlineData("data:image/png;base64,AAAA")]
    public void ValidateV2_RejectsUnsafeOrInlineImagePaths(string path)
    {
        ScenePackageDocument document = WithOnlyCanvasImages(
            new ScenePackageImage("image-one", path, 0, 0, 0, 1, 1, 0, 1));

        Assert.Throws<InvalidDataException>(() => ScenePackageCodec.Validate(document));
    }

    [Fact]
    public void ValidateV2_RejectsCaseCollidingPackagePaths()
    {
        ScenePackageDocument document = WithOnlyCanvasImages(
            new ScenePackageImage("image-one", "images/layer-one/image.png", 0, 0, 0, 1, 1, 0, 1),
            new ScenePackageImage("image-two", "images/layer-one/IMAGE.png", 1, 0, 0, 1, 1, 0, 1));

        Assert.Throws<InvalidDataException>(() => ScenePackageCodec.Validate(document));
    }

    [Fact]
    public void Deserialize_V2AcceptsOnlyEmptyDemoDependencyFields()
    {
        string json = ScenePackageCodec.Serialize(ValidV2());
        string demo = json.Replace("\"schemaVersion\": 2,",
            "\"schemaVersion\": 2,\n  \"blueprintBindings\": [],\n  \"blueprints\": [],\n  \"modules\": [],",
            StringComparison.Ordinal);
        ScenePackageDocument parsed = ScenePackageCodec.Deserialize(demo);
        Assert.DoesNotContain("blueprints", ScenePackageCodec.Serialize(parsed), StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => ScenePackageCodec.Deserialize(
            demo.Replace("\"blueprints\": []", "\"blueprints\": [{}]", StringComparison.Ordinal)));
    }

    [Fact]
    public void ValidateV2_RejectsInvalidIntegrityHashSizeTypeAndPartialCoverage()
    {
        ScenePackageDocument document = ValidV2();
        ScenePackageFileIntegrity first = document.Integrity![0];

        Assert.Throws<InvalidDataException>(() => ScenePackageCodec.Validate(document with
        {
            Integrity = document.Integrity.Select((item, index) =>
                index == 0 ? item with { Sha256 = "sha256:" + item.Sha256 } : item).ToArray(),
        }));
        Assert.Throws<InvalidDataException>(() => ScenePackageCodec.Validate(document with
        {
            Integrity = document.Integrity.Select((item, index) =>
                index == 0 ? item with { Size = -1 } : item).ToArray(),
        }));
        Assert.Throws<InvalidDataException>(() => ScenePackageCodec.Validate(document with
        {
            Integrity = document.Integrity.Select((item, index) =>
                index == 0 ? item with { Type = ScenePackageFileType.Unspecified } : item).ToArray(),
        }));
        Assert.Throws<InvalidDataException>(() => ScenePackageCodec.Validate(document with
        {
            Integrity = new[] { first },
        }));
    }

    [Fact]
    public void ValidateForTransport_RequiresCompleteV2IntegrityButStructuralValidateAllowsAssembly()
    {
        ScenePackageDocument complete = ValidV2();
        ScenePackageDocument assembling = complete with { Integrity = Array.Empty<ScenePackageFileIntegrity>() };

        ScenePackageCodec.Validate(assembling);
        Assert.Throws<InvalidDataException>(() => ScenePackageCodec.ValidateForTransport(assembling));
        ScenePackageCodec.ValidateForTransport(complete);
    }

    [Fact]
    public void ScenePackageIntegrity_CreatesAndVerifiesCanonicalSha256()
    {
        byte[] contents = { 1, 2, 3, 4 };
        ScenePackageFileIntegrity integrity = ScenePackageIntegrity.Create(
            "images/layer-one/image.png",
            contents,
            ScenePackageFileType.CanvasImage);

        Assert.Equal(contents.Length, integrity.Size);
        Assert.Matches("^[0-9a-f]{64}$", integrity.Sha256);
        ScenePackageIntegrity.Verify(integrity, contents);
        Assert.Throws<InvalidDataException>(() => ScenePackageIntegrity.Verify(integrity, new byte[] { 1, 2, 3 }));
    }

    [Theory]
    [InlineData("{\"schemaVersion\":3}")]
    [InlineData("{\"schemaVersion\":\"2\"}")]
    [InlineData("{\"schemaVersion\":2,\"schemaVersion\":2}")]
    [InlineData("{\"schemaVersion\":2,\"SchemaVersion\":2}")]
    public void Deserialize_RejectsUnknownInvalidOrDuplicateSchemaVersion(string json)
    {
        Assert.Throws<InvalidDataException>(() => ScenePackageCodec.Deserialize(json));
    }

    [Fact]
    public void Deserialize_V2RejectsUnknownPropertiesAndIllegalEnumType()
    {
        string json = ScenePackageCodec.Serialize(ValidV2());
        string unknown = json.Replace(
            "\"schemaVersion\": 2,",
            "\"schemaVersion\": 2,\n  \"futureField\": true,",
            StringComparison.Ordinal);
        string illegalEnum = json.Replace(
            "\"type\": \"canvasImage\"",
            "\"type\": \"sideEffect\"",
            StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => ScenePackageCodec.Deserialize(unknown));
        Assert.Throws<InvalidDataException>(() => ScenePackageCodec.Deserialize(illegalEnum));
    }

    private static ScenePackageDocument WithOnlyCanvasImages(params ScenePackageImage[] images) => new(
        ScenePackageCodec.CurrentSchemaVersion,
        "scene-one",
        "Scene",
        "layer-one",
        new[]
        {
            new ScenePackageLayer(
                "layer-one", "Layer", 0, true, false,
                Array.Empty<ScenePackagePromptFragment>(),
                Array.Empty<ScenePackageReferenceImage>(),
                images),
        });

    private static ScenePackageDocument ValidV2()
    {
        ScenePackageFileIntegrity[] integrity =
        {
            File("references/layer-one/reference.png", ScenePackageFileType.ReferenceImage, '1'),
            File("images/layer-one/image-one.png", ScenePackageFileType.CanvasImage, '2'),
        };
        return new ScenePackageDocument(
            ScenePackageCodec.CurrentSchemaVersion,
            "scene-one",
            "Scene V2",
            "layer-one",
            new[]
            {
                new ScenePackageLayer(
                    "layer-one",
                    "Layer",
                    0,
                    true,
                    false,
                    new[] { new ScenePackagePromptFragment("prompt-one", "city", true, 0, "General") },
                    new[] { new ScenePackageReferenceImage("reference-one", "reference.png", 0, 1, 1, 0, 1) },
                    new[]
                    {
                        new ScenePackageImage(
                            "image-one", "images/layer-one/image-one.png", 1,
                            12.5, -4.25, 1.5, -0.75, 33, 0.6),
                    }),
            },
            integrity);
    }

    private static ScenePackageFileIntegrity File(string path, ScenePackageFileType type, char hash) =>
        new(path, 123, Digest(hash), type);

    private static string Digest(char value) => new(value, 64);
}

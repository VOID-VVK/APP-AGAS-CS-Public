using System.Text.Json;

namespace AppAgasCs.Tests.Core.Mcp;

internal static class AgasMcpFrozenToolSchemas
{
    public static JsonElement ProjectSnapshot => Parse("""
    {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "includeScenes": { "type": "boolean", "default": true },
        "includeLayers": { "type": "boolean", "default": true },
        "includeImages": { "type": "boolean", "default": false },
        "includePrompts": { "type": "boolean", "default": false }
      },
      "required": []
    }
    """);

    public static JsonElement AssetsList => Parse("""
    {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "query": { "type": "string", "maxLength": 200, "default": "" },
        "extensions": {
          "type": "array",
          "items": { "type": "string", "enum": ["png", "jpg", "jpeg", "webp"] },
          "uniqueItems": true,
          "minItems": 0,
          "maxItems": 4,
          "default": ["png", "jpg", "jpeg", "webp"]
        },
        "limit": { "type": "integer", "minimum": 1, "maximum": 200, "default": 50 },
        "offset": { "type": "integer", "minimum": 0, "maximum": 1000000, "default": 0 }
      },
      "required": []
    }
    """);

    public static JsonElement SceneMutate => Parse("""
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "op": { "type": "string", "enum": ["add", "rename", "delete", "setActive", "setReadme"] },
        "sceneId": { "type": "string", "minLength": 1, "maxLength": 120, "pattern": "^[A-Za-z0-9_-]+$" },
        "name": { "type": "string", "minLength": 1, "maxLength": 120 },
        "readme": { "type": "string", "minLength": 0, "maxLength": 8000 }
      },
      "required": ["op"],
      "allOf": [
        { "if": { "properties": { "op": { "const": "add" } }, "required": ["op"] }, "then": { "required": ["name"] } },
        { "if": { "properties": { "op": { "const": "rename" } }, "required": ["op"] }, "then": { "required": ["sceneId", "name"] } },
        { "if": { "properties": { "op": { "const": "delete" } }, "required": ["op"] }, "then": { "required": ["sceneId"] } },
        { "if": { "properties": { "op": { "const": "setActive" } }, "required": ["op"] }, "then": { "required": ["sceneId"] } },
        { "if": { "properties": { "op": { "const": "setReadme" } }, "required": ["op"] }, "then": { "required": ["sceneId", "readme"] } }
      ]
    }
    """);

    public static JsonElement LayerMutate => Parse("""
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "op": { "type": "string", "enum": ["add", "rename", "delete", "move", "setVisible", "setLocked", "setShared", "setCurrent"] },
        "sceneId": { "type": "string", "minLength": 1, "maxLength": 120, "pattern": "^[A-Za-z0-9_-]+$" },
        "layerId": { "type": "string", "minLength": 1, "maxLength": 120, "pattern": "^[A-Za-z0-9_-]+$" },
        "name": { "type": "string", "minLength": 1, "maxLength": 120 },
        "direction": { "type": "integer", "enum": [-1, 1] },
        "visible": { "type": "boolean" },
        "locked": { "type": "boolean" },
        "shared": { "type": "boolean" }
      },
      "required": ["op", "sceneId"],
      "allOf": [
        { "if": { "properties": { "op": { "const": "add" } }, "required": ["op"] }, "then": { "required": ["name"] } },
        { "if": { "properties": { "op": { "const": "rename" } }, "required": ["op"] }, "then": { "required": ["layerId", "name"] } },
        { "if": { "properties": { "op": { "const": "delete" } }, "required": ["op"] }, "then": { "required": ["layerId"] } },
        { "if": { "properties": { "op": { "const": "setCurrent" } }, "required": ["op"] }, "then": { "required": ["layerId"] } },
        { "if": { "properties": { "op": { "const": "move" } }, "required": ["op"] }, "then": { "required": ["layerId", "direction"] } },
        { "if": { "properties": { "op": { "const": "setVisible" } }, "required": ["op"] }, "then": { "required": ["layerId", "visible"] } },
        { "if": { "properties": { "op": { "const": "setLocked" } }, "required": ["op"] }, "then": { "required": ["layerId", "locked"] } },
        { "if": { "properties": { "op": { "const": "setShared" } }, "required": ["op"] }, "then": { "required": ["layerId", "shared"] } }
      ]
    }
    """);

    public static JsonElement ImageMutate => Parse("""
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "op": { "type": "string", "enum": ["add", "delete", "move", "update"] },
        "sceneId": { "type": "string", "minLength": 1, "maxLength": 120, "pattern": "^[A-Za-z0-9_-]+$" },
        "layerId": { "type": "string", "minLength": 1, "maxLength": 120, "pattern": "^[A-Za-z0-9_-]+$" },
        "imageId": { "type": "string", "minLength": 1, "maxLength": 120, "pattern": "^[A-Za-z0-9_-]+$" },
        "imagePath": { "type": "string", "minLength": 1, "maxLength": 500 },
        "x": { "type": "number", "minimum": -100000, "maximum": 100000 },
        "y": { "type": "number", "minimum": -100000, "maximum": 100000 },
        "scaleX": { "type": "number", "minimum": 0.01, "maximum": 100 },
        "scaleY": { "type": "number", "minimum": 0.01, "maximum": 100 },
        "rotationDegrees": { "type": "number", "minimum": -3600, "maximum": 3600 },
        "opacity": { "type": "number", "minimum": 0, "maximum": 1 }
      },
      "required": ["op", "sceneId", "layerId"],
      "allOf": [
        { "if": { "properties": { "op": { "const": "add" } }, "required": ["op"] }, "then": { "required": ["imagePath"] } },
        { "if": { "properties": { "op": { "const": "delete" } }, "required": ["op"] }, "then": { "required": ["imageId"] } },
        { "if": { "properties": { "op": { "const": "move" } }, "required": ["op"] }, "then": { "required": ["imageId"], "anyOf": [ { "required": ["x"] }, { "required": ["y"] } ] } },
        { "if": { "properties": { "op": { "const": "update" } }, "required": ["op"] }, "then": { "required": ["imageId"], "anyOf": [ { "required": ["imagePath"] }, { "required": ["x"] }, { "required": ["y"] }, { "required": ["scaleX"] }, { "required": ["scaleY"] }, { "required": ["rotationDegrees"] }, { "required": ["opacity"] } ] } }
      ]
    }
    """);

    public static JsonElement PromptMutate => Parse("""
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "op": { "type": "string", "enum": ["add", "delete", "update", "move", "setEnabled"] },
        "sceneId": { "type": "string", "minLength": 1, "maxLength": 120, "pattern": "^[A-Za-z0-9_-]+$" },
        "layerId": { "type": "string", "minLength": 1, "maxLength": 120, "pattern": "^[A-Za-z0-9_-]+$" },
        "fragmentId": { "type": "string", "minLength": 1, "maxLength": 120, "pattern": "^[A-Za-z0-9_-]+$" },
        "text": { "type": "string", "minLength": 0, "maxLength": 4000 },
        "role": { "type": "string", "enum": ["general", "environment", "negativeConstraint"] },
        "enabled": { "type": "boolean" },
        "direction": { "type": "integer", "enum": [-1, 1] }
      },
      "required": ["op", "sceneId", "layerId"],
      "allOf": [
        { "if": { "properties": { "op": { "const": "add" } }, "required": ["op"] }, "then": { "required": ["text"] } },
        { "if": { "properties": { "op": { "const": "delete" } }, "required": ["op"] }, "then": { "required": ["fragmentId"] } },
        { "if": { "properties": { "op": { "const": "update" } }, "required": ["op"] }, "then": { "required": ["fragmentId"], "anyOf": [ { "required": ["text"] }, { "required": ["role"] } ] } },
        { "if": { "properties": { "op": { "const": "move" } }, "required": ["op"] }, "then": { "required": ["fragmentId", "direction"] } },
        { "if": { "properties": { "op": { "const": "setEnabled" } }, "required": ["op"] }, "then": { "required": ["fragmentId", "enabled"] } }
      ]
    }
    """);

    public static JsonElement ViewSet => Parse("""
    {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "sceneId": { "type": "string", "minLength": 1, "maxLength": 120, "pattern": "^[A-Za-z0-9_-]+$" },
        "cameraX": { "type": "number", "minimum": -100000, "maximum": 100000 },
        "cameraY": { "type": "number", "minimum": -100000, "maximum": 100000 },
        "zoom": { "type": "number", "minimum": 0.05, "maximum": 20 }
      },
      "required": ["sceneId", "cameraX", "cameraY", "zoom"]
    }
    """);

    public static JsonElement ProjectSave => Parse("""
    {
      "type": "object",
      "additionalProperties": false,
      "properties": {},
      "required": []
    }
    """);

    public static JsonElement SelectionSet => Parse("""
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "type": "object",
      "additionalProperties": false,
      "$defs": {
        "region": {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "column": { "type": "integer", "minimum": -2147483648, "maximum": 2147483647 },
            "row": { "type": "integer", "minimum": -2147483648, "maximum": 2147483647 },
            "width": { "type": "integer", "minimum": 1, "maximum": 2147483647 },
            "height": { "type": "integer", "minimum": 1, "maximum": 2147483647 }
          },
          "required": ["column", "row", "width", "height"]
        }
      },
      "properties": {
        "op": { "type": "string", "enum": ["setRegions", "clearRegions", "selectImage", "clearImageSelection"] },
        "sceneId": { "type": "string", "minLength": 1, "maxLength": 120, "pattern": "^[A-Za-z0-9_-]+$" },
        "selection": { "$ref": "#/$defs/region" },
        "replacement": { "$ref": "#/$defs/region" },
        "imageId": { "type": "string", "minLength": 1, "maxLength": 120, "pattern": "^[A-Za-z0-9_-]+$" },
        "layerId": { "type": "string", "minLength": 1, "maxLength": 120, "pattern": "^[A-Za-z0-9_-]+$" }
      },
      "required": ["op", "sceneId"],
      "allOf": [
        { "if": { "properties": { "op": { "const": "setRegions" } }, "required": ["op"] }, "then": { "required": ["selection"] } },
        { "if": { "properties": { "op": { "const": "clearRegions" } }, "required": ["op"] }, "then": { "required": ["op"] } },
        { "if": { "properties": { "op": { "const": "selectImage" } }, "required": ["op"] }, "then": { "required": ["layerId", "imageId"] } },
        { "if": { "properties": { "op": { "const": "clearImageSelection" } }, "required": ["op"] }, "then": { "required": ["op"] } }
      ]
    }
    """);

    public static JsonElement SelectionGet => Parse("""
    {
      "type": "object",
      "additionalProperties": false,
      "properties": {},
      "required": []
    }
    """);

    public static JsonElement AiGenerate => Parse("""
    {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "sceneId": { "type": "string", "minLength": 1, "maxLength": 120, "pattern": "^[A-Za-z0-9_-]+$" },
        "layerId": { "type": "string", "minLength": 1, "maxLength": 120, "pattern": "^[A-Za-z0-9_-]+$" },
        "prompt": { "type": "string", "minLength": 1, "maxLength": 4000 },
        "profileId": { "type": "string", "minLength": 1, "maxLength": 120, "pattern": "^[A-Za-z0-9_-]+$" },
        "target": { "type": "string", "enum": ["NewImage", "ReplaceSelected"], "default": "NewImage" },
        "layerScope": { "type": "string", "enum": ["CurrentLayer", "AllVisibleLayers", "VisibleLayersExceptCurrent"], "default": "CurrentLayer" },
        "quality": { "type": "string", "enum": ["low", "medium"] }
      },
      "required": ["sceneId", "layerId"]
    }
    """);

    public static JsonElement AiStatus => Parse("""
    {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "jobId": { "type": "string", "minLength": 38, "maxLength": 38, "pattern": "^aijob-[a-f0-9]{32}$" },
        "limit": { "type": "integer", "minimum": 1, "maximum": 50, "default": 10 }
      },
      "required": []
    }
    """);

    public static JsonElement AiCancel => Parse("""
    {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "jobId": { "type": "string", "minLength": 38, "maxLength": 38, "pattern": "^aijob-[a-f0-9]{32}$" }
      },
      "required": ["jobId"]
    }
    """);

    public static JsonElement AiApply => Parse("""
    {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "jobId": { "type": "string", "minLength": 38, "maxLength": 38, "pattern": "^aijob-[a-f0-9]{32}$" }
      },
      "required": ["jobId"]
    }
    """);

    private static JsonElement Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

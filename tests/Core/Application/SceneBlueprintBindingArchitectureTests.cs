using System.Reflection;
using AppAgasCs.Application;
using AppAgasCs.Core.Resources;
using Godot;
using Xunit;

namespace AppAgasCs.Tests.Core.Application;

public sealed class SceneBlueprintBindingArchitectureTests
{
    [Fact]
    public void BindingResources_ExposeExactVersionAndImageDependencyFields()
    {
        Assert.True(typeof(Resource).IsAssignableFrom(typeof(ImageBlueprintBindingResource)));
        Assert.True(typeof(Resource).IsAssignableFrom(typeof(BlueprintInputImageBindingResource)));
        Assert.True(typeof(Resource).IsAssignableFrom(typeof(BlueprintModuleDependencyResource)));
        Assert.Equal(1, (int)BlueprintTargetImageMode.ReplaceImageContent);

        RequireExportedProperties<ImageBlueprintBindingResource>(
            "Id", "BlueprintId", "BlueprintRevision", "BlueprintDigest",
            "TargetImageId", "TargetMode", "OutputNodeId", "OutputPortName",
            "InputBindings", "ModuleDependencies");
        RequireExportedProperties<BlueprintInputImageBindingResource>(
            "InputNodeId", "InputPortName", "SourceImageId");
        RequireExportedProperties<BlueprintModuleDependencyResource>(
            "ModuleId", "Version", "Digest");
        Assert.Null(typeof(ImageInstanceResource).GetProperty("BlueprintBindings",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
    }

    [Fact]
    public void ImageMutationContract_HasNoLegacyBindingWritePort()
    {
        Assert.DoesNotContain(typeof(ImageMutationCommand).GetConstructors().Single().GetParameters(),
            parameter => parameter.Name == "BlueprintBindings" || parameter.Name == "blueprintBindings");

        string service = Read("src/Application/ProjectMutationService.cs");
        Assert.Contains("ImageSnapshot previous = ImageSnapshot.From(image);", service, StringComparison.Ordinal);
        Assert.Contains("ApplyImageUpdate(image, command)", service, StringComparison.Ordinal);
        Assert.DoesNotContain("ReplaceBlueprintBindings", service, StringComparison.Ordinal);
    }

    [Fact]
    public void CanvasReplacementAndPaste_DoNotWriteLegacyBindings()
    {
        string document = Read("src/Features/Canvas/CanvasDocumentCommands.cs");
        string workspace = Read("src/Features/Canvas/CanvasWorkspace.cs");

        Assert.Contains("ImagePath: afterPath)", document, StringComparison.Ordinal);
        Assert.Contains("ImagePath: pasted.ImagePath", document, StringComparison.Ordinal);
        Assert.DoesNotContain("BlueprintBindings", document, StringComparison.Ordinal);
        Assert.DoesNotContain("ReplaceImagePathWithBlueprintBindings", workspace, StringComparison.Ordinal);
    }

    [Fact]
    public void NewToolOutput_UsesPlainImageMutationWithoutLegacyBinding()
    {
        string controller = Read("src/UI/Controllers/CanvasToolRunController.cs");
        string main = Read("src/UI/MainWorkspace.cs");
        string workspace = Read("src/Features/Canvas/CanvasWorkspace.cs");

        Assert.DoesNotContain("new ImageBlueprintBindingResource", controller, StringComparison.Ordinal);
        Assert.Contains("_apply(frozen, output)", controller, StringComparison.Ordinal);
        Assert.Contains("_canvas?.TryAddToolOutputBelowImage(", main, StringComparison.Ordinal);
        Assert.Contains("_document.TryAddImageFromPath(\n            imagePath,\n            outputPosition,\n            selectNewImage: true,\n            undoable: true)",
            workspace, StringComparison.Ordinal);
    }

    private static void RequireExportedProperties<T>(params string[] names)
    {
        foreach (string name in names)
        {
            PropertyInfo property = typeof(T).GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"Missing {typeof(T).Name}.{name}.");
            Assert.NotNull(property.GetCustomAttribute<ExportAttribute>());
        }
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(ProjectRoot.Path, relativePath));
}

using System.Collections;
using System.Collections.Frozen;
using System.Reflection;
using AppAgasCs.Core.Resources;
using AppAgasCs.Mcp;
using AppAgasCs.Mcp.Contracts;
using Godot;
using Xunit;

namespace AppAgasCs.Tests.Core;

public sealed class PublicCollectionBoundaryTests
{
    private static readonly (Type Owner, string Name)[] ExportedArrays =
    {
        (typeof(AgasProjectResource), "Scenes"),
        (typeof(AgasSceneResource), "Layers"),
        (typeof(AgasLayerResource), "Images"),
        (typeof(AgasLayerResource), "PromptFragments"),
        (typeof(RecentProjectsResource), "Entries"),
    };

    [Fact]
    public void ExportedGodotArrays_KeepNamesButAreNotPublicMutableProperties()
    {
        foreach ((Type owner, string name) in ExportedArrays)
        {
            PropertyInfo property = owner.GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"Missing {owner.Name}.{name}.");

            Assert.NotNull(property.GetCustomAttribute<ExportAttribute>());
            Assert.False(property.GetMethod?.IsPublic);
            Assert.False(property.SetMethod?.IsPublic);
        }

        Assert.Equal(5, ExportedArrays.Length);
    }

    [Fact]
    public void McpStaticCollections_CannotBeDowncastToMutableBackingCollections()
    {
        Assert.False(AgasMcpToolNames.All is string[]);
        Assert.False(AgasMcpToolDescriptorRegistry.All is AgasMcpToolDescriptor[]);
        Assert.IsAssignableFrom<FrozenSet<string>>(AgasMcpPathValidator.DefaultAssetExtensions);

        Assert.Throws<NotSupportedException>(() =>
            ((IList)AgasMcpToolNames.All)[0] = "mutated");
        Assert.Throws<NotSupportedException>(() =>
            ((IList)AgasMcpToolDescriptorRegistry.All)[0] = AgasMcpToolDescriptorRegistry.All[0]);
    }
}

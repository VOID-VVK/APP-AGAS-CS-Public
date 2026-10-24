using System.Linq;
using System.Reflection;
using AGAS.UI;
using AGAS.UI.Controllers;
using AppAgasCs.Application;
using Godot;
using Xunit;

namespace AppAgasCs.Tests.Core.Application;

public sealed class MainWorkspaceBoundaryTests
{
    [Fact]
    public void PublicWorkspaceBoundary_DoesNotExposeMutableProjectLayoutOrSceneCacheResources()
    {
        PropertyInfo[] publicProperties = typeof(MainWorkspace).GetProperties(BindingFlags.Instance | BindingFlags.Public);

        Assert.DoesNotContain(publicProperties, property => property.Name == "Project");
        Assert.DoesNotContain(publicProperties, property => property.Name == "Layout");
        Assert.DoesNotContain(publicProperties, property => property.Name == "SceneCache");
        Assert.DoesNotContain(publicProperties, property => property.Name == "CurrentScene");
        Assert.DoesNotContain(publicProperties,
            property => property.DeclaringType == typeof(MainWorkspace)
                && typeof(Resource).IsAssignableFrom(property.PropertyType));
        Assert.Equal(typeof(ProjectSessionSnapshot),
            publicProperties.Single(property => property.Name == "SessionSnapshot").PropertyType);
        Assert.False(typeof(Resource).IsAssignableFrom(
            publicProperties.Single(property => property.Name == "ReadOnlyProject").PropertyType));
        Assert.False(typeof(Resource).IsAssignableFrom(
            publicProperties.Single(property => property.Name == "ReadOnlyLayout").PropertyType));
    }

    [Theory]
    [InlineData(300, 1000, 220, 420, 320, 300)]
    [InlineData(500, 600, 220, 420, 320, 280)]
    [InlineData(100, 200, 220, 420, 320, 0)]
    public void WorkspaceLayoutController_ClampsTargetWithoutStarvingOtherPane(
        int desired,
        int available,
        int minimum,
        int maximum,
        int otherMinimum,
        int expected)
    {
        Assert.Equal(expected, WorkspaceLayoutController.ClampPaneSize(
            desired,
            available,
            minimum,
            maximum,
            otherMinimum));
    }

}

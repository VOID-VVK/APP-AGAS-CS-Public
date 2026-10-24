using System;
using System.Collections.Generic;
using System.Linq;
using AppAgasCs.Core.Persistence;

namespace AppAgasCs.Application;

public sealed class AgasSceneDetailInvariantValidator
{
    public bool IsValid(AgasSceneDetailSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!SafeId(snapshot.SceneId)
            || string.IsNullOrWhiteSpace(snapshot.Name)
            || !Finite(snapshot.ViewState.CameraX)
            || !Finite(snapshot.ViewState.CameraY)
            || !Finite(snapshot.ViewState.Zoom)
            || snapshot.ViewState.Zoom <= 0)
        {
            return false;
        }

        var keys = new HashSet<(AgasSceneDetailItemKind Kind, string Id)>();
        var layers = new Dictionary<string, AgasSceneDetailLayerItem>(StringComparer.Ordinal);
        var imageCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var promptCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (AgasSceneDetailItem item in snapshot.Items)
        {
            if (!string.Equals(item.SceneId, snapshot.SceneId, StringComparison.Ordinal)
                || !SafeId(item.Id)
                || item.Order < 0
                || !keys.Add((item.Kind, item.Id)))
            {
                return false;
            }

            switch (item)
            {
                case AgasSceneDetailLayerItem layer:
                    if (string.IsNullOrWhiteSpace(layer.Name)
                        || layer.ImageCount < 0
                        || layer.PromptCount < 0
                        || !layers.TryAdd(layer.LayerId, layer))
                    {
                        return false;
                    }
                    break;
                case AgasSceneDetailImageItem image:
                    if (!ValidImage(image)) return false;
                    imageCounts[image.LayerId] = imageCounts.GetValueOrDefault(image.LayerId) + 1;
                    break;
                case AgasSceneDetailPromptItem prompt:
                    if (!ValidPrompt(prompt)) return false;
                    promptCounts[prompt.LayerId] = promptCounts.GetValueOrDefault(prompt.LayerId) + 1;
                    break;
                default:
                    return false;
            }
        }

        if (!string.IsNullOrEmpty(snapshot.ActiveLayerId)
            && (!SafeId(snapshot.ActiveLayerId) || !layers.ContainsKey(snapshot.ActiveLayerId)))
            return false;

        foreach (AgasSceneDetailImageItem image in snapshot.Items.OfType<AgasSceneDetailImageItem>())
        {
            if (!layers.ContainsKey(image.LayerId)) return false;
        }
        foreach (AgasSceneDetailPromptItem prompt in snapshot.Items.OfType<AgasSceneDetailPromptItem>())
        {
            if (!layers.ContainsKey(prompt.LayerId)) return false;
        }
        foreach (AgasSceneDetailLayerItem layer in layers.Values)
        {
            int images = imageCounts.GetValueOrDefault(layer.LayerId);
            int prompts = promptCounts.GetValueOrDefault(layer.LayerId);
            if (images != layer.ImageCount || prompts != layer.PromptCount) return false;
        }
        return true;
    }

    private static bool ValidImage(AgasSceneDetailImageItem image)
    {
        ProjectRelativePathSyntaxResult path = ProjectPathRules.TryNormalizeRelativePath(
            image.ImagePath,
            new ProjectRelativePathSyntaxOptions(
                RequireImageExtension: true,
                RejectUntrimmedWhitespace: true,
                RejectEmptyOrDotSegments: true));
        return SafeId(image.LayerId)
            && path.Success
            && Finite(image.X)
            && Finite(image.Y)
            && Finite(image.ScaleX)
            && Finite(image.ScaleY)
            && Finite(image.RotationDegrees)
            && Finite(image.Opacity)
            && image.Opacity is >= 0 and <= 1;
    }

    private static bool ValidPrompt(AgasSceneDetailPromptItem prompt) =>
        SafeId(prompt.LayerId)
        && prompt.Text != null
        && prompt.Role is "general" or "environment" or "negativeConstraint";

    private static bool Finite(double value) => double.IsFinite(value);

    private static bool SafeId(string value) =>
        value == value.Trim() && ProjectPathRules.IsSafeAsciiIdentifier(value);
}

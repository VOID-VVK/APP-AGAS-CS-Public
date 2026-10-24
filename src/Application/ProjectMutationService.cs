using System;
using System.Collections.Generic;
using System.Linq;
using AppAgasCs.Core.Persistence;
using AppAgasCs.Core.Resources;

namespace AppAgasCs.Application;

public sealed class ProjectMutationService
{
    private readonly IProjectMutationExecutionPolicy _executionPolicy;

    public ProjectMutationService(IProjectMutationExecutionPolicy? executionPolicy = null)
    {
        _executionPolicy = executionPolicy ?? new DirectProjectMutationExecutionPolicy();
    }

    public ProjectMutationResult MutateScene(IProjectMutationSession session, SceneMutationCommand command)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(command);
        AgasProjectResource project = session.Project;

        switch (command.Kind)
        {
            case SceneMutationKind.Add:
            {
                string name = RequireName(command.Name);
                string id = $"scene-{Guid.NewGuid():N}";
                AgasSceneResource scene = AgasProjectInitializer.CreateDefaultScene(id, name, 0);
                scene.Name = name;
                SceneIndexResource index = new()
                {
                    Id = id,
                    Name = name,
                    RelativePath = SceneRelativePath(id),
                    Order = project.Scenes.Select(item => item.Order).DefaultIfEmpty(-1).Max() + 1,
                };
                string previousActiveId = project.ActiveSceneId;
                AgasSceneResource? previousCurrent = session.CurrentScene;
                ProjectMutationEffects effects = ProjectMutationEffects.ProjectAndScene(id);
                Execute("Add scene", effects,
                    apply: () =>
                    {
                        project.Scenes.Add(index);
                        NormalizeSceneOrders(project);
                        session.CacheScene(scene);
                        project.ActiveSceneId = id;
                        session.ChangeCurrentScene(scene);
                    },
                    revert: () =>
                    {
                        project.Scenes.Remove(index);
                        NormalizeSceneOrders(project);
                        session.RemoveCachedScene(id);
                        project.ActiveSceneId = previousActiveId;
                        session.ChangeCurrentScene(previousCurrent);
                    });
                return Ok(id, null, id, effects);
            }
            case SceneMutationKind.Rename:
            {
                string id = RequireId(command.SceneId);
                SceneIndexResource? index = FindSceneIndex(project, id);
                if (index == null) return SceneMissing(id);
                string name = RequireName(command.Name);
                string previousIndexName = index.Name;
                AgasSceneResource? scene = session.LoadScene(id);
                string? previousSceneName = scene?.Name;
                ProjectMutationEffects effects = scene == null
                    ? new ProjectMutationEffects(true, false, Array.Empty<string>(), Array.Empty<string>())
                    : ProjectMutationEffects.ProjectAndScene(id);
                Execute("Rename scene", effects,
                    apply: () => { index.Name = name; if (scene != null) scene.Name = name; },
                    revert: () => { index.Name = previousIndexName; if (scene != null) scene.Name = previousSceneName ?? string.Empty; });
                return Ok(id, null, id, effects);
            }
            case SceneMutationKind.Delete:
            {
                string id = RequireId(command.SceneId);
                if (project.Scenes.Count <= 1) return Invalid("At least one scene must remain.");
                SceneIndexResource? index = FindSceneIndex(project, id);
                if (index == null) return SceneMissing(id);
                SceneIndexResource? next = project.Scenes.Where(item => item.Id != id).OrderBy(item => item.Order).FirstOrDefault();
                if (next == null) return Invalid("At least one scene must remain.");
                int previousOrder = index.Order;
                string previousActiveId = project.ActiveSceneId;
                AgasSceneResource? previousCurrent = session.CurrentScene;
                AgasSceneResource? cached = session.GetCachedScene(id);
                bool deletingActive = project.ActiveSceneId == id || previousCurrent?.Id == id;
                AgasSceneResource? nextScene = deletingActive ? session.LoadScene(next.Id) : previousCurrent;
                if (deletingActive && nextScene == null)
                    return Invalid($"Scene '{next.Id}' could not be loaded before deleting the active scene.");
                ProjectMutationEffects effects = ProjectMutationEffects.ProjectAndDeletedScene(id);
                Execute("Delete scene", effects,
                    apply: () =>
                    {
                        project.Scenes.Remove(index);
                        NormalizeSceneOrders(project);
                        session.RemoveCachedScene(id);
                        if (deletingActive)
                        {
                            project.ActiveSceneId = next.Id;
                            session.ChangeCurrentScene(nextScene);
                        }
                    },
                    revert: () =>
                    {
                        index.Order = previousOrder;
                        project.Scenes.Add(index);
                        NormalizeSceneOrders(project);
                        if (cached != null) session.CacheScene(cached);
                        project.ActiveSceneId = previousActiveId;
                        session.ChangeCurrentScene(previousCurrent);
                    });
                return Ok(id, null, id, effects);
            }
            case SceneMutationKind.SetActive:
            {
                string id = RequireId(command.SceneId);
                if (FindSceneIndex(project, id) == null) return SceneMissing(id);
                string previousActiveId = project.ActiveSceneId;
                AgasSceneResource? previousCurrent = session.CurrentScene;
                AgasSceneResource? target = session.LoadScene(id) ?? previousCurrent;
                ProjectMutationEffects effects = new(true, false, Array.Empty<string>(), Array.Empty<string>());
                Execute("Set active scene", effects,
                    apply: () => { project.ActiveSceneId = id; session.ChangeCurrentScene(target); },
                    revert: () => { project.ActiveSceneId = previousActiveId; session.ChangeCurrentScene(previousCurrent); });
                return Ok(id, null, id, effects);
            }
            case SceneMutationKind.SetReadme:
            {
                string id = RequireId(command.SceneId);
                if (FindSceneIndex(project, id) == null) return SceneMissing(id);
                AgasSceneResource? scene = session.LoadScene(id);
                if (scene == null) return SceneMissing(id);
                string previous = scene.Readme ?? string.Empty;
                string next = command.Readme ?? string.Empty;
                ProjectMutationEffects effects = ProjectMutationEffects.ProjectAndScene(id);
                Execute("Set scene readme", effects,
                    apply: () => scene.Readme = next,
                    revert: () => scene.Readme = previous);
                return Ok(id, null, id, effects);
            }
            default:
                return Invalid("Unsupported scene mutation.");
        }
    }

    public ProjectMutationResult MutateGrid(IProjectMutationSession session, GridMutationCommand command)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(command);
        GridSettingsResource? grid = session.Project.Grid;
        if (grid == null)
        {
            return Invalid("Project grid settings are unavailable.");
        }

        ProjectMutationEffects effects = ProjectMutationEffects.Project();
        switch (command.Kind)
        {
            case GridMutationKind.SetSize:
            {
                int next = Math.Max(1, command.SizePx);
                int previous = grid.SizePx;
                if (previous == next) return Ok(null, null, null, effects);
                Execute("Set grid size", effects, () => grid.SizePx = next, () => grid.SizePx = previous);
                return Ok(null, null, null, effects);
            }
            case GridMutationKind.SetSnapEnabled:
            case GridMutationKind.SetVisible:
            {
                bool previous = command.Kind == GridMutationKind.SetSnapEnabled
                    ? grid.SnapEnabled
                    : grid.Visible;
                if (previous == command.Flag) return Ok(null, null, null, effects);
                Execute(
                    command.Kind == GridMutationKind.SetSnapEnabled ? "Set grid snapping" : "Set grid visibility",
                    effects,
                    () => SetGridFlag(grid, command.Kind, command.Flag),
                    () => SetGridFlag(grid, command.Kind, previous));
                return Ok(null, null, null, effects);
            }
            default:
                return Invalid("Unsupported grid mutation.");
        }
    }

    public ProjectMutationResult MutateLayer(IProjectMutationSession session, LayerMutationCommand command)
    {
        if (!TryGetScene(session, command.SceneId, out AgasSceneResource? scene, out ProjectMutationResult? failure)) return failure!;
        ProjectMutationEffects effects = ProjectMutationEffects.Scene(scene.Id);
        switch (command.Kind)
        {
            case LayerMutationKind.Add:
            {
                AgasLayerResource layer = new()
                {
                    Id = $"layer-{Guid.NewGuid():N}",
                    Name = RequireName(command.Name),
                    Order = scene.Layers.Select(item => item.Order).DefaultIfEmpty(-1).Max() + 1,
                    Visible = true,
                    Locked = false,
                    Shared = false,
                };
                string previousActiveId = scene.ActiveLayerId;
                Execute("Add layer", effects,
                    () => { scene.Layers.Add(layer); NormalizeLayerOrders(scene); scene.ActiveLayerId = layer.Id; },
                    () => { scene.Layers.Remove(layer); NormalizeLayerOrders(scene); scene.ActiveLayerId = ResolveLayerId(scene, previousActiveId); });
                return Ok(scene.Id, layer.Id, layer.Id, effects);
            }
            case LayerMutationKind.Rename:
            {
                if (!TryGetLayer(scene, command.LayerId, out AgasLayerResource? layer, out failure)) return failure!;
                string previous = layer.Name;
                string name = RequireName(command.Name);
                Execute("Rename layer", effects, () => layer.Name = name, () => layer.Name = previous);
                return Ok(scene.Id, layer.Id, layer.Id, effects);
            }
            case LayerMutationKind.Delete:
            {
                if (!TryGetLayer(scene, command.LayerId, out AgasLayerResource? layer, out failure)) return failure!;
                if (scene.Layers.Count <= 1) return Invalid("At least one layer must remain.");
                int previousOrder = layer.Order;
                string previousActiveId = scene.ActiveLayerId;
                Execute("Delete layer", effects,
                    () => { scene.Layers.Remove(layer); NormalizeLayerOrders(scene); },
                    () => { layer.Order = previousOrder; scene.Layers.Add(layer); NormalizeLayerOrders(scene); scene.ActiveLayerId = previousActiveId; });
                return Ok(scene.Id, layer.Id, layer.Id, effects);
            }
            case LayerMutationKind.Move:
            {
                if (!TryGetLayer(scene, command.LayerId, out AgasLayerResource? layer, out failure)) return failure!;
                List<AgasLayerResource> ordered = scene.Layers.OrderBy(item => item.Order).ToList();
                int index = ordered.IndexOf(layer);
                int target = index + command.Direction;
                if (index < 0 || target < 0 || target >= ordered.Count) return Invalid("Layer cannot be moved in the requested direction.");
                int[] previousOrders = scene.Layers.Select(item => item.Order).ToArray();
                Execute("Move layer", effects,
                    () => { (ordered[index], ordered[target]) = (ordered[target], ordered[index]); ApplyOrders(ordered); },
                    () => ApplyOrders(scene.Layers, previousOrders));
                return Ok(scene.Id, layer.Id, layer.Id, effects);
            }
            case LayerMutationKind.SetVisible:
            case LayerMutationKind.SetLocked:
            case LayerMutationKind.SetShared:
            {
                if (!TryGetLayer(scene, command.LayerId, out AgasLayerResource? layer, out failure)) return failure!;
                bool previous = ReadLayerFlag(layer, command.Kind);
                Execute(LayerFlagActionName(command.Kind), effects,
                    () => SetLayerFlag(layer, command.Kind, command.Flag),
                    () => SetLayerFlag(layer, command.Kind, previous));
                return Ok(scene.Id, layer.Id, layer.Id, effects);
            }
            case LayerMutationKind.SetCurrent:
            {
                if (!TryGetLayer(scene, command.LayerId, out AgasLayerResource? layer, out failure)) return failure!;
                string previous = scene.ActiveLayerId;
                Execute("Set current layer", effects, () => scene.ActiveLayerId = layer.Id, () => scene.ActiveLayerId = previous);
                return Ok(scene.Id, layer.Id, layer.Id, effects);
            }
            default:
                return Invalid("Unsupported layer mutation.");
        }
    }

    public ProjectMutationResult MutateImage(IProjectMutationSession session, ImageMutationCommand command)
    {
        if (!TryGetScene(session, command.SceneId, out AgasSceneResource? scene, out ProjectMutationResult? failure)) return failure!;
        if (!TryGetEditableLayer(scene, command.LayerId, out AgasLayerResource? layer, out failure)) return failure!;
        ProjectMutationEffects effects = ProjectMutationEffects.Scene(scene.Id);
        switch (command.Kind)
        {
            case ImageMutationKind.Add:
            {
                ImageInstanceResource image = new()
                {
                    Id = $"image-{Guid.NewGuid():N}",
                    ImagePath = command.ImagePath ?? string.Empty,
                    Order = layer.Images.Select(item => item.Order).DefaultIfEmpty(-1).Max() + 1,
                    X = command.X ?? 0,
                    Y = command.Y ?? 0,
                    ScaleX = command.ScaleX ?? 1,
                    ScaleY = command.ScaleY ?? 1,
                    RotationDegrees = command.RotationDegrees ?? 0,
                    Opacity = command.Opacity ?? 1,
                };
                Execute("Add image", effects,
                    () =>
                    {
                        layer.Images.Add(image);
                        NormalizeImageOrders(layer);
                    },
                    () => { layer.Images.Remove(image); NormalizeImageOrders(layer); });
                return Ok(scene.Id, layer.Id, image.Id, effects);
            }
            case ImageMutationKind.Delete:
            {
                ImageInstanceResource? image = FindImage(layer, RequireId(command.ImageId));
                if (image == null) return new ProjectMutationResult(false, ProjectMutationError.ImageNotFound, "Image instance was not found.", scene.Id, layer.Id, command.ImageId);
                int previousOrder = image.Order;
                Execute("Delete image", effects,
                    () => { layer.Images.Remove(image); NormalizeImageOrders(layer); },
                    () => { image.Order = previousOrder; layer.Images.Add(image); NormalizeImageOrders(layer); });
                return Ok(scene.Id, layer.Id, image.Id, effects);
            }
            case ImageMutationKind.Update:
            {
                ImageInstanceResource? image = FindImage(layer, RequireId(command.ImageId));
                if (image == null) return new ProjectMutationResult(false, ProjectMutationError.ImageNotFound, "Image instance was not found.", scene.Id, layer.Id, command.ImageId);
                ImageSnapshot previous = ImageSnapshot.From(image);
                Execute("Update image", effects,
                    () => ApplyImageUpdate(image, command),
                    () => previous.Apply(image));
                return Ok(scene.Id, layer.Id, image.Id, effects);
            }
            default:
                return Invalid("Unsupported image mutation.");
        }
    }

    public ProjectMutationResult MutatePrompt(IProjectMutationSession session, PromptMutationCommand command)
    {
        if (!TryGetScene(session, command.SceneId, out AgasSceneResource? scene, out ProjectMutationResult? failure)) return failure!;
        if (!TryGetEditableLayer(scene, command.LayerId, out AgasLayerResource? layer, out failure)) return failure!;
        ProjectMutationEffects effects = ProjectMutationEffects.Scene(scene.Id);
        switch (command.Kind)
        {
            case PromptMutationKind.Add:
            {
                if (command.Role.HasValue && !Enum.IsDefined(typeof(PromptFragmentRole), command.Role.Value))
                    return Invalid("Prompt role is invalid.");
                PromptFragmentResource fragment = new()
                {
                    Id = $"prompt-{Guid.NewGuid():N}",
                    Text = command.Text ?? string.Empty,
                    Enabled = command.Enabled,
                    Role = command.Role.HasValue
                        ? (PromptFragmentRole)command.Role.Value
                        : PromptFragmentRole.General,
                    Order = layer.PromptFragments.Select(item => item.Order).DefaultIfEmpty(-1).Max() + 1,
                };
                Execute("Add prompt fragment", effects,
                    () => { layer.PromptFragments.Add(fragment); NormalizePromptOrders(layer); },
                    () => { layer.PromptFragments.Remove(fragment); NormalizePromptOrders(layer); });
                return Ok(scene.Id, layer.Id, fragment.Id, effects);
            }
            case PromptMutationKind.Delete:
            {
                PromptFragmentResource? fragment = FindPrompt(layer, RequireId(command.FragmentId));
                if (fragment == null) return new ProjectMutationResult(false, ProjectMutationError.PromptNotFound, "Prompt fragment was not found.", scene.Id, layer.Id, command.FragmentId);
                int previousOrder = fragment.Order;
                Execute("Delete prompt fragment", effects,
                    () => { layer.PromptFragments.Remove(fragment); NormalizePromptOrders(layer); },
                    () => { fragment.Order = previousOrder; layer.PromptFragments.Add(fragment); NormalizePromptOrders(layer); });
                return Ok(scene.Id, layer.Id, fragment.Id, effects);
            }
            case PromptMutationKind.Update:
            {
                PromptFragmentResource? fragment = FindPrompt(layer, RequireId(command.FragmentId));
                if (fragment == null) return new ProjectMutationResult(false, ProjectMutationError.PromptNotFound, "Prompt fragment was not found.", scene.Id, layer.Id, command.FragmentId);
                if (command.Role.HasValue && !Enum.IsDefined(typeof(PromptFragmentRole), command.Role.Value))
                    return Invalid("Prompt role is invalid.");
                PromptSnapshot previous = PromptSnapshot.From(fragment);
                Execute("Update prompt fragment", effects,
                    () =>
                    {
                        if (command.Text != null) fragment.Text = command.Text;
                        if (command.Role.HasValue) fragment.Role = (PromptFragmentRole)command.Role.Value;
                        if (command.UpdatedEnabled.HasValue) fragment.Enabled = command.UpdatedEnabled.Value;
                    },
                    () => previous.Apply(fragment));
                return Ok(scene.Id, layer.Id, fragment.Id, effects);
            }
            case PromptMutationKind.Move:
            {
                PromptFragmentResource? fragment = FindPrompt(layer, RequireId(command.FragmentId));
                if (fragment == null) return new ProjectMutationResult(false, ProjectMutationError.PromptNotFound, "Prompt fragment was not found.", scene.Id, layer.Id, command.FragmentId);
                List<PromptFragmentResource> ordered = layer.PromptFragments.OrderBy(item => item.Order).ToList();
                int index = ordered.IndexOf(fragment);
                int target = command.TargetIndex ?? index + command.Direction;
                if (index < 0 || target < 0 || target >= ordered.Count) return Invalid("Prompt cannot be moved in the requested direction.");
                int[] previousOrders = layer.PromptFragments.Select(item => item.Order).ToArray();
                Execute("Move prompt fragment", effects,
                    () => { (ordered[index], ordered[target]) = (ordered[target], ordered[index]); ApplyOrders(ordered); },
                    () => ApplyOrders(layer.PromptFragments, previousOrders));
                return Ok(scene.Id, layer.Id, fragment.Id, effects);
            }
            case PromptMutationKind.SetEnabled:
            {
                PromptFragmentResource? fragment = FindPrompt(layer, RequireId(command.FragmentId));
                if (fragment == null) return new ProjectMutationResult(false, ProjectMutationError.PromptNotFound, "Prompt fragment was not found.", scene.Id, layer.Id, command.FragmentId);
                bool previous = fragment.Enabled;
                Execute("Set prompt enabled", effects, () => fragment.Enabled = command.Enabled, () => fragment.Enabled = previous);
                return Ok(scene.Id, layer.Id, fragment.Id, effects);
            }
            default:
                return Invalid("Unsupported prompt mutation.");
        }
    }

    public ProjectMutationResult MutateView(IProjectMutationSession session, ViewMutationCommand command)
    {
        if (!TryGetScene(session, command.SceneId, out AgasSceneResource? scene, out ProjectMutationResult? failure)) return failure!;
        SceneViewStateResource? previousView = scene.ViewState;
        SceneViewStateResource targetView = previousView ?? new SceneViewStateResource();
        SceneViewSnapshot previous = SceneViewSnapshot.From(targetView);
        ProjectMutationEffects effects = ProjectMutationEffects.Scene(scene.Id);
        Execute("Update view", effects,
            () =>
            {
                scene.ViewState = targetView;
                targetView.CameraX = command.CameraX;
                targetView.CameraY = command.CameraY;
                targetView.Zoom = command.Zoom;
            },
            () =>
            {
                if (previousView == null) scene.ViewState = null!;
                else previous.Apply(previousView);
            });
        return Ok(scene.Id, null, null, effects);
    }

    private void Execute(string description, ProjectMutationEffects effects, Action apply, Action revert) =>
        _executionPolicy.Execute(new DelegateOperation(description, effects, apply, revert));

    private static bool TryGetScene(IProjectMutationSession session, string sceneId, out AgasSceneResource scene, out ProjectMutationResult? failure)
    {
        scene = null!;
        failure = null;
        AgasSceneResource? found = session.CurrentScene?.Id == sceneId
            ? session.CurrentScene
            : session.LoadScene(sceneId);
        if (found == null)
        {
            failure = SceneMissing(sceneId);
            return false;
        }
        scene = found;
        return true;
    }

    private static bool TryGetLayer(AgasSceneResource scene, string? layerId, out AgasLayerResource layer, out ProjectMutationResult? failure)
    {
        layer = null!;
        failure = null;
        string id = RequireId(layerId);
        AgasLayerResource? found = scene.Layers.FirstOrDefault(item => item.Id == id);
        if (found == null)
        {
            failure = ProjectMutationResult.Failure(ProjectMutationError.LayerNotFound, "Layer was not found.");
            return false;
        }
        layer = found;
        return true;
    }

    private static bool TryGetEditableLayer(AgasSceneResource scene, string? layerId, out AgasLayerResource layer, out ProjectMutationResult? failure)
    {
        if (!TryGetLayer(scene, layerId, out layer, out failure)) return false;
        if (!layer.Visible)
        {
            failure = ProjectMutationResult.Failure(ProjectMutationError.LayerHidden, "Target layer is hidden and cannot be modified.");
            return false;
        }
        if (layer.Locked)
        {
            failure = ProjectMutationResult.Failure(ProjectMutationError.LayerLocked, "Target layer is locked and cannot be modified.");
            return false;
        }
        return true;
    }

    private static ProjectMutationResult Ok(string? sceneId, string? layerId, string? itemId, ProjectMutationEffects effects) =>
        new(true, ProjectMutationError.None, string.Empty, sceneId, layerId, itemId, effects);
    private static ProjectMutationResult Invalid(string message) => ProjectMutationResult.Failure(ProjectMutationError.InvalidOperation, message);
    private static ProjectMutationResult SceneMissing(string id) => new(false, ProjectMutationError.SceneNotFound, $"Scene '{id}' was not found or could not be loaded.", id);
    private static string RequireId(string? value) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Required id is missing.") : value.Trim();
    private static string RequireName(string? value) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Required name is missing.") : value.Trim();
    private static string SceneRelativePath(string id) =>
        $"{AgasPaths.AgasDirectoryName}/{AgasPaths.ScenesDirectoryName}/{id}.tres";
    private static SceneIndexResource? FindSceneIndex(AgasProjectResource project, string id) => project.Scenes.FirstOrDefault(item => item.Id == id);
    private static ImageInstanceResource? FindImage(AgasLayerResource layer, string id) => layer.Images.FirstOrDefault(item => item.Id == id);
    private static PromptFragmentResource? FindPrompt(AgasLayerResource layer, string id) => layer.PromptFragments.FirstOrDefault(item => item.Id == id);
    private static void NormalizeSceneOrders(AgasProjectResource project) => ApplyOrders(project.Scenes.OrderBy(item => item.Order).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList());
    private static void NormalizeLayerOrders(AgasSceneResource scene) { ApplyOrders(scene.Layers.OrderBy(item => item.Order).ToList()); scene.ActiveLayerId = ResolveLayerId(scene, scene.ActiveLayerId); }
    private static void NormalizeImageOrders(AgasLayerResource layer) => ApplyOrders(layer.Images.OrderBy(item => item.Order).ToList());
    private static void NormalizePromptOrders(AgasLayerResource layer) => ApplyOrders(layer.PromptFragments.OrderBy(item => item.Order).ToList());
    private static string ResolveLayerId(AgasSceneResource scene, string preferred) => scene.Layers.Any(item => item.Id == preferred) ? preferred : scene.Layers.OrderBy(item => item.Order).FirstOrDefault()?.Id ?? string.Empty;
    private static bool ReadLayerFlag(AgasLayerResource layer, LayerMutationKind kind) => kind switch
    {
        LayerMutationKind.SetVisible => layer.Visible,
        LayerMutationKind.SetLocked => layer.Locked,
        LayerMutationKind.SetShared => layer.Shared,
        _ => false,
    };

    private static string LayerFlagActionName(LayerMutationKind kind) => kind switch
    {
        LayerMutationKind.SetVisible => "Set layer visibility",
        LayerMutationKind.SetLocked => "Set layer lock",
        LayerMutationKind.SetShared => "Set layer share",
        _ => "Set layer flag",
    };

    private static void SetLayerFlag(AgasLayerResource layer, LayerMutationKind kind, bool value)
    {
        switch (kind)
        {
            case LayerMutationKind.SetVisible:
                layer.Visible = value;
                break;
            case LayerMutationKind.SetLocked:
                layer.Locked = value;
                break;
            case LayerMutationKind.SetShared:
                layer.Shared = value;
                break;
        }
    }
    private static void SetGridFlag(GridSettingsResource grid, GridMutationKind kind, bool value)
    {
        if (kind == GridMutationKind.SetSnapEnabled) grid.SnapEnabled = value;
        else grid.Visible = value;
    }
    private static void ApplyOrders<T>(IReadOnlyList<T> items) where T : class
    {
        for (int i = 0; i < items.Count; i++)
        {
            switch (items[i])
            {
                case SceneIndexResource scene: scene.Order = i; break;
                case AgasLayerResource layer: layer.Order = i; break;
                case ImageInstanceResource image: image.Order = i; break;
                case PromptFragmentResource prompt: prompt.Order = i; break;
            }
        }
    }
    private static void ApplyOrders<T>(IReadOnlyList<T> items, IReadOnlyList<int> orders) where T : class
    {
        for (int i = 0; i < items.Count && i < orders.Count; i++)
        {
            switch (items[i])
            {
                case SceneIndexResource scene: scene.Order = orders[i]; break;
                case AgasLayerResource layer: layer.Order = orders[i]; break;
                case ImageInstanceResource image: image.Order = orders[i]; break;
                case PromptFragmentResource prompt: prompt.Order = orders[i]; break;
            }
        }
    }

    private static void ApplyImageUpdate(
        ImageInstanceResource image,
        ImageMutationCommand command)
    {
        if (!string.IsNullOrWhiteSpace(command.ImagePath)) image.ImagePath = command.ImagePath;
        if (command.X.HasValue) image.X = command.X.Value;
        if (command.Y.HasValue) image.Y = command.Y.Value;
        if (command.ScaleX.HasValue) image.ScaleX = command.ScaleX.Value;
        if (command.ScaleY.HasValue) image.ScaleY = command.ScaleY.Value;
        if (command.RotationDegrees.HasValue) image.RotationDegrees = command.RotationDegrees.Value;
        if (command.Opacity.HasValue) image.Opacity = command.Opacity.Value;
    }

    private sealed record DelegateOperation(string Description, ProjectMutationEffects Effects, Action ApplyAction, Action RevertAction) : IProjectMutationOperation
    {
        public void Apply() => ApplyAction();
        public void Revert() => RevertAction();
    }

    private sealed record ImageSnapshot(
        string Path,
        double X,
        double Y,
        double ScaleX,
        double ScaleY,
        double Rotation,
        double Opacity)
    {
        public static ImageSnapshot From(ImageInstanceResource image) => new(
            image.ImagePath,
            image.X,
            image.Y,
            image.ScaleX,
            image.ScaleY,
            image.RotationDegrees,
            image.Opacity);

        public void Apply(ImageInstanceResource image)
        {
            image.ImagePath = Path;
            image.X = X;
            image.Y = Y;
            image.ScaleX = ScaleX;
            image.ScaleY = ScaleY;
            image.RotationDegrees = Rotation;
            image.Opacity = Opacity;
        }
    }

    private readonly record struct SceneViewSnapshot(double X, double Y, double Zoom)
    {
        public static SceneViewSnapshot From(SceneViewStateResource view) => new(view.CameraX, view.CameraY, view.Zoom);
        public void Apply(SceneViewStateResource view) { view.CameraX = X; view.CameraY = Y; view.Zoom = Zoom; }
    }

    private readonly record struct PromptSnapshot(string Text, bool Enabled, int Order, PromptFragmentRole Role)
    {
        public static PromptSnapshot From(PromptFragmentResource prompt) =>
            new(prompt.Text, prompt.Enabled, prompt.Order, prompt.Role);
        public void Apply(PromptFragmentResource prompt)
        {
            prompt.Text = Text;
            prompt.Enabled = Enabled;
            prompt.Order = Order;
            prompt.Role = Role;
        }
    }
}

#nullable enable

using System.Collections.Generic;

namespace AppAgasCs.Features.Tools.Model;

/// <summary>Portable definitions, including complete custom script source and ordered chain steps.</summary>
public sealed record ToolCatalogSnapshot(
    IReadOnlyList<ToolDefinition> Tools,
    IReadOnlyList<ToolChainDefinition> Chains);

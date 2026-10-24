#nullable enable

using System;
using AGAS.UI.Dialogs;

namespace AGAS.UI;

public partial class Main
{
    internal SettingsDialog EnsureAiSettingsDialogForMcp()
    {
        EnsureSettingsDialog();
        return _settingsDialog
            ?? throw new InvalidOperationException("AI settings dialog could not be initialized.");
    }

    internal string CurrentMcpWorkspaceId()
    {
        return _workspace != null
            && _workspace.TryGetCurrentProjectIdentity(out _, out string projectId)
                ? projectId
                : string.Empty;
    }
}

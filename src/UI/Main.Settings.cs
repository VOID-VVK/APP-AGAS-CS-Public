using AGAS.UI.Dialogs;
using AppAgasCs.UI.Shared;

namespace AGAS.UI;

public partial class Main
{
    private string FormatProjectOpenError(string template, params object[] arguments)
    {
        _projectOpenErrorTemplate = template;
        _projectOpenErrorArguments = arguments;
        return AppLocale.Format(template, arguments);
    }

    private void RefreshProjectOpenErrorTranslation()
    {
        if (_projectOpenErrorTemplate != null && _projectPicker != null && IsInstanceValid(_projectPicker))
        {
            _projectPicker.ShowProjectOpenFailure(AppLocale.Format(_projectOpenErrorTemplate, _projectOpenErrorArguments));
        }
    }

    private void RefreshAppMenuTranslation()
    {
        if (_appMenuBar != null && IsInstanceValid(_appMenuBar) && _appMenuBar.GetMenuCount() > 0)
        {
            _appMenuBar.SetMenuTitle(0, AppLocale.Translate("设置"));
        }
    }

    internal void BindMcpSettingsPage(SettingsDialog dialog)
    {
        dialog.McpEnabledChangeRequested -= OnMcpEnabledChangeRequestedAsync;
        dialog.McpEnabledChangeRequested += OnMcpEnabledChangeRequestedAsync;
    }
}

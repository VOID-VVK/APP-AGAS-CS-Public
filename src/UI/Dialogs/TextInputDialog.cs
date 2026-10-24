using System;
using Godot;

namespace AGAS.UI.Dialogs;

public partial class TextInputDialog : AcceptDialog
{
    private readonly LineEdit _input = new()
    {
        AutoTranslateMode = AutoTranslateModeEnum.Disabled,
        Name = "TextInput",
        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
    };

    private Action<string>? _confirmed;
    private string _placeholder = string.Empty;

    public override void _Notification(int what)
    {
        if (what == NotificationTranslationChanged && IsNodeReady())
            _input.PlaceholderText = Tr(_placeholder);
    }

    public TextInputDialog()
    {
        MinSize = new Vector2I(360, 110);
        AddChild(_input);
        Confirmed += OnConfirmed;
    }

    public void Open(Node parent, string title, string initialText, string placeholder, Action<string> confirmed)
    {
        if (GetParent() == null)
        {
            parent.AddChild(this);
        }

        Title = title;
        _confirmed = confirmed;
        _input.Text = initialText;
        _placeholder = placeholder;
        _input.PlaceholderText = Tr(placeholder);
        PopupCentered(new Vector2I(420, 120));
        _input.GrabFocus();
        _input.SelectAll();
    }

    private void OnConfirmed()
    {
        _confirmed?.Invoke(_input.Text.Trim());
    }
}

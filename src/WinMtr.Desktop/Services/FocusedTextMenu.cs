using Avalonia.Controls;
using Avalonia.Input;
using CommunityToolkit.Mvvm.Input;

namespace WinMtr.Desktop.Services;

// Avalonia 12.1.2 exposes focus lookup and TextBox editing methods. Native
// menus do not take keyboard focus, so the same editor owns menu and key edits.
internal static class FocusedTextMenu
{
    internal static NativeMenu Create(Window window)
    {
        var menu = new NativeMenu();
        Add("Cut", Key.X, box => box.CanCut, box => box.Cut());
        Add("Copy", Key.C, box => box.CanCopy, box => box.Copy());
        Add("Paste", Key.V, box => box.CanPaste, box => box.Paste());
        Add("Select All", Key.A, box => !string.IsNullOrEmpty(box.Text), box => box.SelectAll());
        return menu;

        void Add(string header, Key key, Func<TextBox, bool> canExecute, Action<TextBox> execute)
        {
            TextBox? FocusedEditor() => window.FocusManager?.GetFocusedElement() is TextBox { IsEffectivelyEnabled: true } box
                && TopLevel.GetTopLevel(box) == window ? box : null;

            bool CanExecute() => FocusedEditor() is { } box && canExecute(box);

            var command = new RelayCommand(() =>
            {
                // Focus and editability may change after the menu was drawn.
                if (FocusedEditor() is { } box && canExecute(box))
                    execute(box);
            }, CanExecute);
            menu.Add(new NativeMenuItem(header)
            {
                Gesture = new KeyGesture(key, KeyModifiers.Meta),
                Command = command,
            });
            // Native exporters request this both before showing the menu and
            // before dispatching a gesture. No polling or synthetic keys.
            menu.NeedsUpdate += (_, _) => command.NotifyCanExecuteChanged();
        }
    }
}

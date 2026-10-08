using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace LocalAudiobookStudio.App.Controls;

/// <summary>
/// Attached properties used from XAML. An attached property is set on any control
/// (e.g. <c>ctl:Behaviors.FocusWhenVisible="True"</c>) and runs code when it changes.
/// </summary>
public static class Behaviors
{
    /// <summary>Focuses a control (and selects a TextBox's text end) whenever it becomes visible.</summary>
    public static readonly AttachedProperty<bool> FocusWhenVisibleProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("FocusWhenVisible", typeof(Behaviors));

    public static bool GetFocusWhenVisible(Control c) => c.GetValue(FocusWhenVisibleProperty);
    public static void SetFocusWhenVisible(Control c, bool value) => c.SetValue(FocusWhenVisibleProperty, value);

    static Behaviors()
    {
        FocusWhenVisibleProperty.Changed.AddClassHandler<Control>((c, e) =>
        {
            if (e.NewValue is not true) return;
            c.PropertyChanged += (_, pe) =>
            {
                if (pe.Property == Visual.IsVisibleProperty && c.IsVisible) FocusSoon(c);
            };
            if (c.IsVisible) FocusSoon(c);
        });
    }

    private static void FocusSoon(Control c) => Dispatcher.UIThread.Post(() =>
    {
        if (!c.IsVisible) return;
        c.Focus();
        if (c is TextBox tb) tb.CaretIndex = tb.Text?.Length ?? 0;
    }, DispatcherPriority.Input);
}

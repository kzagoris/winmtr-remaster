using Avalonia;
using Avalonia.Controls;

namespace WinMtr.Desktop.Views;

// The platform theme chooses wrapping without duplicating the toolbar controls.
internal sealed class ToolbarPanel : WrapPanel
{
    public static readonly StyledProperty<bool> CanWrapProperty =
        AvaloniaProperty.Register<ToolbarPanel, bool>(nameof(CanWrap), true);

    private double unwrappedWidth;

    static ToolbarPanel() => AffectsMeasure<ToolbarPanel>(CanWrapProperty);

    public bool CanWrap
    {
        get => GetValue(CanWrapProperty);
        set => SetValue(CanWrapProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // The unwrapped width is cached because the framework constrains the
        // returned DesiredSize to the measure constraint, which would otherwise
        // let the arrange pass fall back to wrapping.
        Size size = base.MeasureOverride(CanWrap
            ? availableSize
            : new Size(double.PositiveInfinity, availableSize.Height));
        unwrappedWidth = size.Width;
        return CanWrap ? size : new Size(Math.Min(size.Width, availableSize.Width), size.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        base.ArrangeOverride(CanWrap ? finalSize : new Size(Math.Max(finalSize.Width, unwrappedWidth), finalSize.Height));
        return finalSize;
    }
}

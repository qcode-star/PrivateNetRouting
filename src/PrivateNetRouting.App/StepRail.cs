using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using PrivateNetRouting.Core.Chain;
using ShapePath = System.Windows.Shapes.Path;

namespace PrivateNetRouting.App;

/// <summary>
/// One stage of a rail (chain bring-up on the splash screen, update phases in the update window):
/// a marker sitting on a link, the stage name and a right-aligned detail. The marker vocabulary is
/// shared with the chain card of the main window and distinguishes the states by shape as well as
/// by colour — pending is hollow, done carries a check, failed carries a cross:
/// colour is never the only carrier of a state.
/// </summary>
internal sealed class StepRailRow
{
    private readonly Ellipse _marker;
    private readonly Rectangle _link;
    private readonly ShapePath _check;
    private readonly ShapePath _cross;

    /// <param name="text">Stage name; the caller re-localizes it through <see cref="Label"/>.</param>
    public StepRailRow(string text, Style labelStyle, Style detailStyle)
    {
        Grid.SetColumn(Label = new TextBlock
        {
            Style = labelStyle,
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 12, 0),
        }, 1);
        Grid.SetColumn(Detail = new TextBlock
        {
            Style = detailStyle,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
            TextTrimming = TextTrimming.CharacterEllipsis,
        }, 2);

        var stack = new Grid();
        stack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(9) });
        stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _link = new Rectangle { Width = 2, Fill = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Center };
        Grid.SetRow(_link, 0);
        _marker = new Ellipse { Width = 13, Height = 13, StrokeThickness = 2, Fill = Brushes.Transparent };
        _check = Glyph("Mark.Check");
        _cross = Glyph("Mark.Cross");
        var face = new Grid { Width = 13, Height = 13, VerticalAlignment = VerticalAlignment.Center };
        face.Children.Add(_marker);
        face.Children.Add(_check);
        face.Children.Add(_cross);
        Grid.SetRow(face, 1);
        stack.Children.Add(_link);
        stack.Children.Add(face);
        Grid.SetColumn(stack, 0);

        Element = new Grid();
        Element.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        Element.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Element.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Element.Children.Add(stack);
        Element.Children.Add(Label);
        Element.Children.Add(Detail);
    }

    /// <summary>The row to put into the steps panel.</summary>
    public Grid Element { get; }

    public TextBlock Label { get; }

    public TextBlock Detail { get; }

    /// <summary>Stage name and detail; <see cref="ChainStepStatus.Skipped"/> is drawn as done.</summary>
    public void Apply(ChainStepStatus status, string detail)
    {
        status = status == ChainStepStatus.Skipped ? ChainStepStatus.Done : status;
        var brush = status switch
        {
            ChainStepStatus.Active => Theme.Accent,
            ChainStepStatus.Done => Theme.Ok,
            ChainStepStatus.Failed => Theme.Bad,
            _ => Theme.Idle,
        };
        _marker.Stroke = brush;
        _marker.Fill = status == ChainStepStatus.Pending ? Brushes.Transparent : brush;
        _check.Visibility = status == ChainStepStatus.Done ? Visibility.Visible : Visibility.Collapsed;
        _cross.Visibility = status == ChainStepStatus.Failed ? Visibility.Visible : Visibility.Collapsed;
        Label.Foreground = status switch
        {
            ChainStepStatus.Failed => Theme.Bad,
            ChainStepStatus.Pending => Theme.TextMuted,
            _ => Theme.Text,
        };
        Label.FontWeight = status is ChainStepStatus.Done or ChainStepStatus.Failed ? FontWeights.SemiBold : FontWeights.Normal;
        Detail.Text = detail;
    }

    /// <summary>The link takes the colour of the stage it leaves, so a rail shows where the flow stops.</summary>
    public void SetLinkFrom(ChainStepStatus upstream) => _link.Fill = upstream switch
    {
        ChainStepStatus.Skipped or ChainStepStatus.Done => Theme.Ok,
        ChainStepStatus.Active => Theme.Accent,
        ChainStepStatus.Failed => Theme.Bad,
        _ => Theme.Idle,
    };

    private static ShapePath Glyph(string geometryKey) => new()
    {
        Data = (Geometry)Application.Current.FindResource(geometryKey),
        Stroke = Theme.Of("Brush.Card"),
        StrokeThickness = 1.7,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Visibility = Visibility.Collapsed,
    };
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PrivateNetRouting.App;

/// <summary>
/// One option of the language selector: the config code, the short label and the full name
/// for the tooltip.
/// </summary>
/// <summary>A language selector option; Label is the short code shown next to the flag, Hint the
/// full name in the tooltip. ToString returns the label: it becomes the combo item's UIA name,
/// which assistive tech reads.</summary>
public sealed record LanguageOption(string Code, string Label, string Hint)
{
    public override string ToString() => Label;
}

/// <summary>
/// The mark of a language selector option: a small drawn flag — the Union Jack for en, three
/// stripes for ru — chosen by <see cref="Code"/>. Drawn in code from fixed geometry: no image
/// assets, no emoji (Windows fonts render flag emoji as two-letter codes). The colours are the
/// flags' own identity colours from App.xaml, not theme tokens; the frame around the mark is the
/// item template's business.
/// </summary>
public sealed class LanguageFlag : Canvas
{
    public static readonly DependencyProperty CodeProperty = DependencyProperty.Register(
        nameof(Code), typeof(string), typeof(LanguageFlag), new PropertyMetadata("", OnCodeChanged));

    private const double MarkWidth = 21;
    private const double MarkHeight = 14;

    public LanguageFlag()
    {
        Width = MarkWidth;
        Height = MarkHeight;
        Clip = new RectangleGeometry(new Rect(0, 0, MarkWidth, MarkHeight), 1.5, 1.5);
    }

    /// <summary>The language code the mark stands for: "en" or "ru".</summary>
    public string Code
    {
        get => (string)GetValue(CodeProperty);
        set => SetValue(CodeProperty, value);
    }

    private static void OnCodeChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
        ((LanguageFlag)sender).Draw((string)e.NewValue);

    private void Draw(string code)
    {
        Children.Clear();
        if (code == "ru")
        {
            DrawStripes();
        }
        else
        {
            DrawUnionJack();
        }
    }

    /// <summary>Union Jack, simplified for the mark size: diagonals and the cross without the
    /// counterchanged detail — at 21×14 the silhouette carries the recognition.</summary>
    private void DrawUnionJack()
    {
        Children.Add(new Rectangle { Width = MarkWidth, Height = MarkHeight, Fill = Resource("Brush.Flag.UnionBlue") });
        AddLine(0, 0, MarkWidth, MarkHeight, "Brush.Flag.White", 3.4);
        AddLine(MarkWidth, 0, 0, MarkHeight, "Brush.Flag.White", 3.4);
        AddLine(0, 0, MarkWidth, MarkHeight, "Brush.Flag.UnionRed", 1.2);
        AddLine(MarkWidth, 0, 0, MarkHeight, "Brush.Flag.UnionRed", 1.2);
        AddLine(MarkWidth / 2, 0, MarkWidth / 2, MarkHeight, "Brush.Flag.White", 5.2);
        AddLine(0, MarkHeight / 2, MarkWidth, MarkHeight / 2, "Brush.Flag.White", 5.2);
        AddLine(MarkWidth / 2, 0, MarkWidth / 2, MarkHeight, "Brush.Flag.UnionRed", 3);
        AddLine(0, MarkHeight / 2, MarkWidth, MarkHeight / 2, "Brush.Flag.UnionRed", 3);
    }

    /// <summary>Three horizontal stripes: white, blue, red.</summary>
    private void DrawStripes()
    {
        var grid = new Grid { Width = MarkWidth, Height = MarkHeight };
        var fills = new[] { "Brush.Flag.White", "Brush.Flag.RussiaBlue", "Brush.Flag.RussiaRed" };
        for (var i = 0; i < fills.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var stripe = new Rectangle { Fill = Resource(fills[i]) };
            Grid.SetRow(stripe, i);
            grid.Children.Add(stripe);
        }

        Children.Add(grid);
    }

    private void AddLine(double x1, double y1, double x2, double y2, string brushKey, double thickness)
    {
        Children.Add(new Line
        {
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
            Stroke = Resource(brushKey),
            StrokeThickness = thickness,
        });
    }

    private static Brush Resource(string key) => (Brush)Application.Current.FindResource(key);
}

using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PrivateNetRouting.App;

/// <summary>
/// A text block for file paths: bind the full value to <see cref="Source"/>; when it does not
/// fit the column, the text shows the head of the path, an ellipsis, and the separator with the
/// complete file name at the end ("C:\dir…\file.ext") — the leading separator makes it obvious
/// the file name is whole, not cut. A tooltip still carries the full value. Falls back to the
/// ordinary end ellipsis when even the file name does not fit. Recomputes on value changes and
/// on resize.
/// </summary>
public class PathTextBlock : TextBlock
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(string), typeof(PathTextBlock), new PropertyMetadata("", OnSourceChanged));

    /// <summary>The full, untruncated value; Text carries only the display form.</summary>
    public string Source
    {
        get => (string)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    private string _source = "";

    // the window renders in Display formatting mode, whose snapped glyphs are slightly wider
    // than the Ideal-mode FormattedText measure — keep a margin so the composed text really fits
    private const double FitMargin = 8;

    public PathTextBlock()
    {
        TextTrimming = TextTrimming.CharacterEllipsis; // the fallback when the file name does not fit either
        SizeChanged += (_, _) => Retrim();
    }

    private static void OnSourceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var block = (PathTextBlock)sender;
        block._source = e.NewValue as string ?? "";
        block.Retrim();
    }

    private void Retrim()
    {
        // until the width is known (and whenever the path is not shortened) the full value is the
        // text: the built-in end ellipsis trims it, so the cell is never empty
        Text = _source;
        var available = ActualWidth - FitMargin;
        if (available <= 0 || _source.Length == 0)
        {
            return;
        }

        var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double Measure(string text) =>
            new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection, typeface, FontSize, Foreground, pixelsPerDip).Width;

        if (Measure(_source) <= available)
        {
            return;
        }

        // the separator rides with the file name: "head…\name" reads as a whole name
        var tail = Path.DirectorySeparatorChar + Path.GetFileName(_source);
        // no directory part to cut from — the built-in end ellipsis stays in charge
        if (tail.Length <= 1 || tail.Length >= _source.Length || Measure("…" + tail) > available)
        {
            return;
        }

        // the longest head that still fits together with the ellipsis and the file name
        int low = 0, high = _source.Length - tail.Length;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (Measure(_source[..middle] + "…" + tail) <= available)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        Text = _source[..low] + "…" + tail;
    }
}

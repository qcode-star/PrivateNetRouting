using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using PrivateNetRouting.App.Localization;
using ShapePath = System.Windows.Shapes.Path;

namespace PrivateNetRouting.App;

/// <summary>Mark a stage carries besides its colour, so a state never rests on colour alone.</summary>
internal enum RailMark
{
    None,
    Check,
    Cross,
}

/// <summary>One stage of the chain rail: the word, the colour of the marker and its mark.</summary>
internal sealed record RailStage(string Text, Brush Brush, RailMark Mark = RailMark.None)
{
    /// <summary>Nothing is known about the stage yet.</summary>
    public static RailStage Unknown => new("…", Theme.Idle);
}

/// <summary>
/// The chain rail: the stages of the chain in bring-up order, drawn as markers sitting on a link.
/// The stage list follows the chain mode (tailscale: daemon → keeper → node → proxy → health;
/// external upstream: egress → proxy → health) and comes in through the constructor. A marker lights up when
/// its stage comes up, and a link carries the colour of the stage it leads to — so the rail shows
/// where the flow has got to and where it stops. It runs live from the bring-up steps and, the
/// rest of the time, from the chain snapshot. Colours are animated, so a stage lights up instead
/// of jumping, and a stage that completes pops its marker once.
/// </summary>
internal sealed class ChainRail
{
    private readonly string[] _labelKeys;

    private readonly int _stageCount;

    private readonly Ellipse[] _markers;
    private readonly ShapePath[] _checks;
    private readonly ShapePath[] _crosses;
    private readonly Rectangle[] _links;
    private readonly TextBlock[] _labels;
    private readonly TextBlock[] _states;
    private readonly ScaleTransform[] _scales;
    private readonly SolidColorBrush[] _markerBrushes;
    private readonly SolidColorBrush[] _linkBrushes;
    private readonly RailMark[] _marks;

    /// <param name="host">Empty grid of the chain card the rail is drawn into.</param>
    /// <param name="labelKeys">Localization keys of the stage labels, in bring-up order.</param>
    public ChainRail(Grid host, IReadOnlyList<string> labelKeys)
    {
        _labelKeys = [.. labelKeys];
        _stageCount = _labelKeys.Length;
        _markers = new Ellipse[_stageCount];
        _checks = new ShapePath[_stageCount];
        _crosses = new ShapePath[_stageCount];
        _links = new Rectangle[_stageCount - 1];
        _labels = new TextBlock[_stageCount];
        _states = new TextBlock[_stageCount];
        _scales = new ScaleTransform[_stageCount];
        _markerBrushes = new SolidColorBrush[_stageCount];
        _linkBrushes = new SolidColorBrush[_stageCount - 1];
        _marks = new RailMark[_stageCount];

        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.5, GridUnitType.Star) });
        for (var i = 0; i < _stageCount; i++)
        {
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (i < _stageCount - 1)
            {
                host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }
        }

        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.5, GridUnitType.Star) });
        for (var i = 0; i < 3; i++)
        {
            host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        var glyph = (Brush)Application.Current.FindResource("Brush.Card");
        for (var i = 0; i < _stageCount; i++)
        {
            var column = 1 + (i * 2);
            _markerBrushes[i] = new SolidColorBrush(Colors.Transparent);
            _scales[i] = new ScaleTransform(1, 1);
            var face = new Grid
            {
                Width = 13,
                Height = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = _scales[i],
            };
            _markers[i] = new Ellipse
            {
                Width = 13,
                Height = 13,
                StrokeThickness = 2,
                Fill = Brushes.Transparent,
                Stroke = _markerBrushes[i],
            };
            _checks[i] = Glyph("Mark.Check", glyph);
            _crosses[i] = Glyph("Mark.Cross", glyph);
            face.Children.Add(_markers[i]);
            face.Children.Add(_checks[i]);
            face.Children.Add(_crosses[i]);
            Grid.SetRow(face, 0);
            Grid.SetColumn(face, column);
            host.Children.Add(face);

            _labels[i] = new TextBlock
            {
                Style = (Style)Application.Current.FindResource("Text.Caption"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 10, 0, 0),
                Text = Loc.S(_labelKeys[i]),
            };
            Grid.SetRow(_labels[i], 1);
            Grid.SetColumn(_labels[i], column);
            host.Children.Add(_labels[i]);

            _states[i] = new TextBlock
            {
                Style = (Style)Application.Current.FindResource("Text.Caption"),
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 3, 0, 0),
                Foreground = Theme.TextMuted,
                Text = "",
            };
            Grid.SetRow(_states[i], 2);
            Grid.SetColumn(_states[i], column);
            host.Children.Add(_states[i]);

            if (i >= _stageCount - 1)
            {
                continue;
            }

            _linkBrushes[i] = new SolidColorBrush(Colors.Transparent);
            _links[i] = new Rectangle
            {
                Height = 2,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = _linkBrushes[i],
            };
            Grid.SetRow(_links[i], 0);
            Grid.SetColumn(_links[i], column + 1);
            host.Children.Add(_links[i]);
        }
    }

    /// <summary>Stage names after a language change.</summary>
    public void Relabel()
    {
        for (var i = 0; i < _stageCount; i++)
        {
            _labels[i].Text = Loc.S(_labelKeys[i]);
        }
    }

    /// <summary>Puts every stage back to "nothing is known yet" — the start of a bring-up.</summary>
    public void Reset(RailStage pending)
    {
        for (var i = 0; i < _stageCount; i++)
        {
            SetStage(i, pending, animate: false);
        }
    }

    /// <summary>
    /// Shows one stage. The colour fades in, a stage that completes pops its marker once, and an
    /// active stage breathes while the work is in flight; <paramref name="animate"/> is off for the
    /// first paint, where a fade would only delay the picture.
    /// </summary>
    public void SetStage(int index, RailStage stage, bool animate = true)
    {
        if (index < 0 || index >= _stageCount)
        {
            return;
        }

        var idle = ((SolidColorBrush)Theme.Idle).Color;
        var target = ((SolidColorBrush)stage.Brush).Color;
        var brush = _markerBrushes[index];
        SetColor(brush, target, animate);

        _markers[index].Fill = target == idle ? Brushes.Transparent : brush;
        _checks[index].Visibility = stage.Mark == RailMark.Check ? Visibility.Visible : Visibility.Collapsed;
        _crosses[index].Visibility = stage.Mark == RailMark.Cross ? Visibility.Visible : Visibility.Collapsed;

        // an idle marker is a neutral grey that is too faint for a word, so the word stays readable
        _states[index].Foreground = target == idle ? Theme.TextMuted : brush;
        _states[index].Text = stage.Text;

        if (animate && stage.Mark != RailMark.None && _marks[index] != stage.Mark)
        {
            Pop(_scales[index]);
        }

        _marks[index] = stage.Mark;
        if (animate && target == ((SolidColorBrush)Theme.Accent).Color)
        {
            Breathe(_markers[index]);
        }
        else
        {
            _markers[index].BeginAnimation(UIElement.OpacityProperty, null);
            _markers[index].Opacity = 1;
        }

        // the link leading into a stage carries the state of that stage
        if (index > 0)
        {
            SetColor(_linkBrushes[index - 1], target, animate);
        }
    }

    private static void SetColor(SolidColorBrush brush, Color target, bool animate)
    {
        if (!animate || brush.Color == target)
        {
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            brush.Color = target;
            return;
        }

        brush.BeginAnimation(
            SolidColorBrush.ColorProperty,
            new ColorAnimation(target, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
    }

    private static void Pop(ScaleTransform scale)
    {
        var pop = new DoubleAnimationUsingKeyFrames();
        pop.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(1.45, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(130))));
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(330))));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    private static void Breathe(UIElement marker)
    {
        var breathe = new DoubleAnimation(1, 0.45, TimeSpan.FromMilliseconds(700))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        };
        marker.BeginAnimation(UIElement.OpacityProperty, breathe);
    }

    private static ShapePath Glyph(string geometryKey, Brush brush) => new()
    {
        Data = (Geometry)Application.Current.FindResource(geometryKey),
        Stroke = brush,
        StrokeThickness = 1.7,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Visibility = Visibility.Collapsed,
    };
}

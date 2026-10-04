using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PrivateNetRouting.App.Localization;
using PrivateNetRouting.Core.Chain;

namespace PrivateNetRouting.App;

/// <summary>
/// Заставка подъёма цепочки при запуске Claude (порт claude-splash.ps1): карточка в правом
/// нижнем углу рабочей области, шаги демона→кипера→узла→прокси→здоровья, счётчик времени,
/// подсказка про UAC; в конце «Готово» либо текст ошибки с кнопками «Открыть лог»/«Закрыть»
/// (автозакрытие через минуту). Показывается только там, где есть реальное ожидание:
/// на живой цепочке окна нет вовсе. Все методы вызываются в UI-потоке (прогресс приходит
/// через Progress&lt;T&gt;, продолжения — в контексте диспетчера).
/// </summary>
public partial class SplashWindow : Window
{
    private static readonly string[] StepOrder = ["Daemon", "Keeper", "Node", "Proxy", "Health"];

    private readonly TextBlock[] _glyphs;
    private readonly TextBlock[] _labels;
    private readonly TextBlock[] _details;
    private readonly ChainStepStatus[] _states;
    private readonly DispatcherTimer _clock;
    private DispatcherTimer? _failAutoClose;
    private readonly string _logPath;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _finished;

    public SplashWindow(string logPath)
    {
        InitializeComponent();
        Icon = AppIcons.WindowIcon;
        _logPath = logPath;

        TitleText.Text = Loc.S("Splash.Title");
        SubText.Text = Loc.S("Splash.Subtitle");
        DetailText.Text = "";
        BtnLog.Content = Loc.S("Splash.Button.OpenLog");
        BtnClose.Content = Loc.S("Splash.Button.Close");

        _glyphs = new TextBlock[StepOrder.Length];
        _labels = new TextBlock[StepOrder.Length];
        _details = new TextBlock[StepOrder.Length];
        _states = new ChainStepStatus[StepOrder.Length];
        for (var i = 0; i < StepOrder.Length; i++)
        {
            _states[i] = ChainStepStatus.Pending;
            var row = new Grid { Height = 32 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _glyphs[i] = new TextBlock { FontSize = 13, Foreground = FindResource("Splash.Pending") as Brush };
            Grid.SetColumn(_glyphs[i], 0);
            _labels[i] = new TextBlock
            {
                FontSize = 13,
                Foreground = FindResource("Splash.Pending") as Brush,
                VerticalAlignment = VerticalAlignment.Center,
                Text = Loc.S($"Splash.Step.{StepOrder[i]}"),
            };
            Grid.SetColumn(_labels[i], 1);
            _details[i] = new TextBlock
            {
                FontSize = 11,
                Foreground = FindResource("Splash.Hint") as Brush,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(_details[i], 2);
            row.Children.Add(_glyphs[i]);
            row.Children.Add(_labels[i]);
            row.Children.Add(_details[i]);
            StepsPanel.Children.Add(row);
        }

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.5) };
        _clock.Tick += (_, _) => ElapsedText.Text = FormatElapsed(_watch.Elapsed);
        _clock.Start();

        Loaded += (_, _) =>
        {
            var intro = (Storyboard)FindResource("Intro");
            intro.Begin(this, true);
            PositionBottomRight();
        };
        SizeChanged += (_, _) => PositionBottomRight();
        Closed += (_, _) =>
        {
            _clock.Stop();
            _failAutoClose?.Stop();
            _closed.TrySetResult();
        };
    }

    /// <summary>Завершается, когда окно закрылось (кнопкой, автозакрытием или программно).</summary>
    public Task ClosedAsync => _closed.Task;

    public void Update(ChainStep step)
    {
        var index = Array.IndexOf(StepOrder, step.Component);
        if (index < 0)
        {
            return;
        }

        // Skipped = звено уже было живо — для заставки это выполненный шаг
        var status = step.Status == ChainStepStatus.Skipped ? ChainStepStatus.Done : step.Status;
        _states[index] = status;
        _glyphs[index].Text = status switch
        {
            ChainStepStatus.Done => "●",
            ChainStepStatus.Active => "●",
            ChainStepStatus.Failed => "✕",
            _ => "○",
        };
        _glyphs[index].Foreground = FindResource($"Splash.{status}") as Brush;
        _labels[index].Foreground = FindResource(status == ChainStepStatus.Pending ? "Splash.Pending" : $"Splash.{status}") as Brush;
        _details[index].Text = status == ChainStepStatus.Failed ? step.Detail : (step.Status == ChainStepStatus.Skipped ? Loc.S("Splash.SkipNote") : step.Detail);

        if (step.Component == "Daemon" && status == ChainStepStatus.Active)
        {
            HintText.Text = Loc.S("Splash.Hint.Uac");
        }

        if (!string.IsNullOrEmpty(step.Detail) && status is ChainStepStatus.Active or ChainStepStatus.Failed)
        {
            DetailText.Text = step.Detail;
        }
    }

    public void Complete(string text)
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        for (var i = 0; i < StepOrder.Length; i++)
        {
            if (_states[i] != ChainStepStatus.Done)
            {
                Update(new ChainStep(StepOrder[i], ChainStepStatus.Done, ""));
            }
        }

        SpinnerArc.Visibility = Visibility.Collapsed;
        SpinnerRing.Stroke = FindResource("Splash.Done") as Brush;
        _clock.Stop();
        DetailText.Foreground = FindResource("Splash.Done") as Brush;
        DetailText.Text = text;
    }

    public void Fail(string text, string hint)
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        for (var i = 0; i < StepOrder.Length; i++)
        {
            if (_states[i] == ChainStepStatus.Active)
            {
                Update(new ChainStep(StepOrder[i], ChainStepStatus.Failed, ""));
            }
        }

        SubText.Text = Loc.S("Splash.Subtitle.Fail");
        SpinnerArc.Visibility = Visibility.Collapsed;
        SpinnerRing.Stroke = FindResource("Splash.Failed") as Brush;
        _clock.Stop();
        DetailText.Foreground = FindResource("Splash.Failed") as Brush;
        DetailText.Text = text;
        if (!string.IsNullOrEmpty(hint))
        {
            HintText.Text = hint;
        }

        Actions.Visibility = Visibility.Visible;
        _failAutoClose = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _failAutoClose.Tick += (_, _) => Close();
        _failAutoClose.Start();
    }

    /// <summary>Закрытие с коротким затуханием (успех мелькать не должен).</summary>
    public void CloseSplash(int fadeMs = 120)
    {
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(fadeMs));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    private void OnOpenLog(object sender, RoutedEventArgs e)
    {
        try
        {
            if (File.Exists(_logPath))
            {
                Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_logPath}\"")
                {
                    UseShellExecute = true,
                });
            }
        }
        catch (Exception ex)
        {
            DetailText.Text = ex.Message;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>Правый нижний угол рабочей области: не поверх окна Claude и не по центру.</summary>
    private void PositionBottomRight()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 24;
        Top = area.Bottom - ActualHeight - 24;
    }

    private static string FormatElapsed(TimeSpan elapsed) =>
        $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
}

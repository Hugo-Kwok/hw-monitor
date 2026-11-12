using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace BetterHwMonitor;

public partial class MainWindow : Window
{
    private static readonly (string Label, double Seconds)[] WindowOptions =
    {
        ("1 min", 60), ("5 min", 300), ("15 min", 900), ("30 min", 1800), ("1 hour", 3600),
        ("6 hours", 21600), ("12 hours", 43200), ("24 hours", 86400), ("All (3 days)", 0),
    };

    private static readonly (string Label, int Milliseconds)[] IntervalOptions =
    {
        ("250 ms", 250), ("500 ms", 500), ("1 s", 1000), ("2 s", 2000), ("5 s", 5000),
    };

    private readonly DataStore _store = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly List<GraphCard> _graphs = new();
    private readonly TrayIcon _tray;
    private readonly DispatcherTimer _saveTimer;
    private GraphCard? _activeGraph;
    private Border? _dropZone;
    private volatile int _intervalMs = 1000;
    private double _windowSeconds = 300;
    private int _graphCounter;
    private bool _firstSample = true;
    private string _warning = "";
    private DateTime _messageUntil = DateTime.MinValue;
    private Point _dragStart;
    private bool _dragArmed;
    private bool _loadingLayout;
    private bool _exiting;
    private bool _trayHintShown;
    private HistoryLog? _log;
    private bool _sensorsHidden;
    private double _sensorPanelWidth = 520;
    private readonly HashSet<string> _collapsedGroups = new();
    private bool _syncingGroupToggle;

    public MainWindow()
    {
        InitializeComponent();

        _tray = new TrayIcon();
        _tray.OpenRequested += ShowFromTray;
        _tray.ExitRequested += ExitApp;
        Icon = Imaging.CreateBitmapSourceFromHIcon(_tray.Icon.Handle, Int32Rect.Empty,
            BitmapSizeOptions.FromEmptyOptions());

        // Save shortly after the last change instead of on every keystroke
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveLayout();
        };

        var view = (ListCollectionView)CollectionViewSource.GetDefaultView(_store.Sensors);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SensorRecord.Hardware)));
        view.Filter = FilterSensor;
        SensorList.ItemsSource = _store.Sensors;

        BuildDropZone();

        WindowCombo.ItemsSource = WindowOptions.Select(o => o.Label).ToList();
        IntervalCombo.ItemsSource = IntervalOptions.Select(o => o.Label).ToList();

        LoadLayout();
        UpdateThemeButton();
        StatusText.Text = "Starting sensors...";

        Task.Run(() => PollLoop(_cts.Token));
    }

    // ---------- Polling ----------

    private async Task PollLoop(CancellationToken ct)
    {
        SensorReader? reader = null;
        try
        {
            // Load the recorded history first, so graphs show data from before the app was closed
            await Dispatcher.InvokeAsync(() => StatusText.Text = "Loading history...");
            try
            {
                var history = HistoryLog.Load(_store.KeepSeconds);
                await Dispatcher.InvokeAsync(() =>
                {
                    _store.Import(history);
                    foreach (var g in _graphs)
                    {
                        if (g.HasPending) g.ResolvePending();
                        g.Chart.InvalidateVisual();
                    }
                    StatusText.Text = "Starting sensors...";
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() => ShowMessage("Couldn't load history: " + ex.Message));
            }
            if (ct.IsCancellationRequested) return;
            _log = new HistoryLog(_store.KeepSeconds);

            try
            {
                reader = new SensorReader();
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() => StatusText.Text = "Failed to open sensors: " + ex.Message);
                return;
            }

            var sw = new Stopwatch();
            while (!ct.IsCancellationRequested)
            {
                sw.Restart();
                List<SensorSample> samples;
                try
                {
                    samples = reader.Read();
                }
                catch (Exception ex)
                {
                    samples = new List<SensorSample>();
                    await Dispatcher.InvokeAsync(() => ShowMessage("Read error: " + ex.Message));
                }

                double readMs = sw.Elapsed.TotalMilliseconds;
                if (samples.Count > 0) _log?.Write(DateTime.UtcNow, samples);
                double t = (DateTime.Now - _store.StartTime).TotalSeconds;
                if (ct.IsCancellationRequested) break;
                await Dispatcher.InvokeAsync(() => OnSamples(t, samples, readMs));

                int wait = _intervalMs - (int)sw.ElapsedMilliseconds;
                if (wait > 0) await Task.Delay(wait, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // app exiting
        }
        finally
        {
            reader?.Dispose();
            _log?.Dispose();
        }
    }

    private void OnSamples(double t, List<SensorSample> samples, double readMs)
    {
        _store.Append(t, samples);

        foreach (var g in _graphs)
        {
            if (g.HasPending) g.ResolvePending();
            g.Chart.InvalidateVisual();
        }

        if (_firstSample && samples.Count > 0)
        {
            _firstSample = false;
            bool cpuTemp = _store.Sensors.Any(s =>
                s.HardwareType == "Cpu" && s.Type == "Temperature" && s.Current.HasValue);
            if (!cpuTemp)
                _warning = "  ·  No CPU temperature found: install PawnIO (see README)";
        }

        if (DateTime.Now >= _messageUntil)
        {
            StatusText.SetResourceReference(TextBlock.ForegroundProperty, "SubtleTextBrush");
            StatusText.Text = $"{_store.Sensors.Count} sensors  ·  read took {readMs:F0} ms{_warning}";
        }
    }

    private void ShowMessage(string message)
    {
        _messageUntil = DateTime.Now.AddSeconds(5);
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, "ErrorBrush");
        StatusText.Text = message;
    }

    // ---------- Window, tray and exit ----------

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        Theme.SetTitleBar(this, Theme.IsDark);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_exiting)
        {
            // The X button only hides the window; monitoring and graph history keep running.
            e.Cancel = true;
            SaveLayout();
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray.ShowHint("Better HW Monitor is still running",
                    "Click the tray icon to open it again. Right-click it and choose Exit to quit.");
            }
            return;
        }

        _cts.Cancel();
    }

    /// <summary>Brings the window back (tray click, or the exe was started again).</summary>
    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        // Make sure it comes to the front even if another app has focus
        Topmost = true;
        Topmost = false;
        Focus();
    }

    /// <summary>Saves and marks the app as exiting (tray Exit, Windows sign-out or shutdown).</summary>
    public void PrepareForExit()
    {
        if (_exiting) return;
        _exiting = true;
        _saveTimer.Stop();
        SaveLayout();
        _cts.Cancel();
        _log?.Dispose(); // flush the last readings to disk
        _tray.Dispose();
    }

    private void ExitApp()
    {
        PrepareForExit();
        Application.Current.Shutdown();
    }

    // ---------- Layout persistence ----------

    private void LoadLayout()
    {
        _loadingLayout = true;
        try
        {
            var layout = LayoutStore.Load();
            int windowIndex = 1, intervalIndex = 2;

            if (layout != null)
            {
                int wi = Array.FindIndex(WindowOptions, o => o.Seconds == layout.WindowSeconds);
                if (wi >= 0) windowIndex = wi;
                int ii = Array.FindIndex(IntervalOptions, o => o.Milliseconds == layout.IntervalMs);
                if (ii >= 0) intervalIndex = ii;
                ApplySavedBounds(layout);
                foreach (var g in layout.CollapsedGroups) _collapsedGroups.Add(g);
                GraphCard.SetLegendWidth(layout.LegendWidth);
                if (layout.SensorPanelWidth >= 260) _sensorPanelWidth = layout.SensorPanelWidth;
                SensorColumn.Width = new GridLength(_sensorPanelWidth);
                if (layout.SensorsHidden) SetSensorPanelVisible(false);
            }

            WindowCombo.SelectedIndex = windowIndex;
            IntervalCombo.SelectedIndex = intervalIndex;

            if (layout != null)
            {
                foreach (var g in layout.Graphs)
                {
                    var card = AddGraph(string.IsNullOrWhiteSpace(g.Title) ? null : g.Title);
                    card.LoadLayout(g);
                }
                _graphCounter = Math.Max(_graphCounter, _graphs.Count);
            }

            if (_graphs.Count == 0) AddGraph();
            SetActiveGraph(_graphs[0]);
        }
        finally
        {
            _loadingLayout = false;
        }
    }

    private void ApplySavedBounds(LayoutFile layout)
    {
        if (double.IsNaN(layout.Width) || double.IsNaN(layout.Height) ||
            double.IsNaN(layout.Left) || double.IsNaN(layout.Top) ||
            layout.Width < 400 || layout.Height < 300)
            return;

        // Only restore the position if the window would still be visible on the current monitors
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                              SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var bounds = new Rect(layout.Left, layout.Top, layout.Width, layout.Height);
        if (!screen.IntersectsWith(bounds)) return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = layout.Left;
        Top = layout.Top;
        Width = layout.Width;
        Height = layout.Height;
        if (layout.Maximized) WindowState = WindowState.Maximized;
    }

    private void ScheduleSave()
    {
        if (_loadingLayout || _exiting) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveLayout()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        var layout = new LayoutFile
        {
            WindowSeconds = _windowSeconds,
            IntervalMs = _intervalMs,
            Maximized = WindowState == WindowState.Maximized,
            Graphs = _graphs.Select(g => g.ToLayout()).ToList(),
            SensorsHidden = _sensorsHidden,
            SensorPanelWidth = _sensorsHidden ? _sensorPanelWidth : SensorColumn.ActualWidth > 0 ? SensorColumn.ActualWidth : _sensorPanelWidth,
            CollapsedGroups = _collapsedGroups.ToList(),
            LegendWidth = GraphCard.LegendWidth,
        };
        if (!bounds.IsEmpty && !double.IsNaN(bounds.Width))
        {
            layout.Left = bounds.Left;
            layout.Top = bounds.Top;
            layout.Width = bounds.Width;
            layout.Height = bounds.Height;
        }
        LayoutStore.Save(layout);
    }

    // ---------- Theme ----------

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        Theme.Apply(!Theme.IsDark, save: true);
        UpdateThemeButton();
        foreach (var g in _graphs) g.Chart.InvalidateVisual();
    }

    private void UpdateThemeButton()
    {
        ThemeButton.Content = Theme.IsDark ? "Light mode" : "Dark mode";
        ThemeButton.ToolTip = Theme.IsDark ? "Switch to the light theme" : "Switch to the dark theme";
    }

    // ---------- Graphs ----------

    private GraphCard AddGraph(string? title = null)
    {
        var card = new GraphCard(_store, title ?? $"Graph {++_graphCounter}");
        card.Chart.WindowSeconds = _windowSeconds;
        card.RemoveRequested += RemoveGraph;
        card.Activated += SetActiveGraph;
        card.Message += ShowMessage;
        card.Changed += ScheduleSave;
        card.MoveRequested += MoveGraph;

        _graphs.Add(card);
        GraphsHost.Children.Insert(Math.Max(0, GraphsHost.Children.Count - 1), card); // keep the drop zone last
        SetActiveGraph(card);
        card.BringIntoView();
        ScheduleSave();
        return card;
    }

    private void RemoveGraph(GraphCard card)
    {
        _graphs.Remove(card);
        GraphsHost.Children.Remove(card);
        if (_activeGraph == card)
        {
            _activeGraph = null;
            if (_graphs.Count > 0) SetActiveGraph(_graphs[^1]);
        }
        ScheduleSave();
    }

    private void MoveGraph(GraphCard card, int delta)
    {
        int from = _graphs.IndexOf(card);
        int to = from + delta;
        if (from < 0 || to < 0 || to >= _graphs.Count) return;

        _graphs.RemoveAt(from);
        _graphs.Insert(to, card);
        // GraphsHost holds the graphs in the same order, followed by the drop zone
        GraphsHost.Children.Remove(card);
        GraphsHost.Children.Insert(to, card);
        SetActiveGraph(card);
        card.BringIntoView();
        ScheduleSave();
    }

    private void SetActiveGraph(GraphCard card)
    {
        if (_activeGraph == card) return;
        if (_activeGraph != null) _activeGraph.IsActive = false;
        _activeGraph = card;
        card.IsActive = true;
    }

    private void AddToGraph(GraphCard card, IEnumerable<SensorRecord> sensors)
    {
        foreach (var rec in sensors)
        {
            var error = card.TryAddSensor(rec);
            if (error != null) ShowMessage(error);
        }
    }

    private void BuildDropZone()
    {
        var text = new TextBlock
        {
            Text = "+  Drop sensors here to make a new graph (or click)",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _dropZone = new Border
        {
            Height = 70,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(4),
            Child = text,
            AllowDrop = true,
            Cursor = Cursors.Hand,
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "SubtleTextBrush");
        _dropZone.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        _dropZone.SetResourceReference(Border.BackgroundProperty, "DropZoneBrush");
        _dropZone.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(GraphCard.DragFormat) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        _dropZone.Drop += (_, e) =>
        {
            if (e.Data.GetData(GraphCard.DragFormat) is string ids)
            {
                var card = AddGraph();
                AddToGraph(card, ids.Split('\n').Select(id => _store.Find(id)).OfType<SensorRecord>());
            }
            e.Handled = true;
        };
        _dropZone.MouseLeftButtonUp += (_, _) => AddGraph();
        GraphsHost.Children.Add(_dropZone);
    }

    private void NewGraph_Click(object sender, RoutedEventArgs e) => AddGraph();

    private void WindowCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WindowCombo.SelectedIndex < 0) return;
        _windowSeconds = WindowOptions[WindowCombo.SelectedIndex].Seconds;
        foreach (var g in _graphs)
        {
            g.Chart.WindowSeconds = _windowSeconds;
            g.Chart.InvalidateVisual();
        }
        ScheduleSave();
    }

    private void IntervalCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IntervalCombo.SelectedIndex < 0) return;
        _intervalMs = IntervalOptions[IntervalCombo.SelectedIndex].Milliseconds;
        ScheduleSave();
    }

    // ---------- Sensor list ----------

    private void HideSensors_Click(object sender, RoutedEventArgs e) => SetSensorPanelVisible(false);
    private void ShowSensors_Click(object sender, RoutedEventArgs e) => SetSensorPanelVisible(true);

    /// <summary>Hides or shows the whole sensor list; the graphs take the freed width.</summary>
    private void SetSensorPanelVisible(bool visible)
    {
        if (!visible)
        {
            if (SensorColumn.ActualWidth >= 260) _sensorPanelWidth = SensorColumn.ActualWidth;
            SensorColumn.MinWidth = 0;
            SensorColumn.Width = new GridLength(0);
            SplitterColumn.Width = new GridLength(0);
            SensorPanel.Visibility = Visibility.Collapsed;
            Splitter.Visibility = Visibility.Collapsed;
            ShowSensorsButton.Visibility = Visibility.Visible;
        }
        else
        {
            SensorColumn.MinWidth = 260;
            SensorColumn.Width = new GridLength(_sensorPanelWidth);
            SplitterColumn.Width = new GridLength(5);
            SensorPanel.Visibility = Visibility.Visible;
            Splitter.Visibility = Visibility.Visible;
            ShowSensorsButton.Visibility = Visibility.Collapsed;
        }
        _sensorsHidden = !visible;
        ScheduleSave();
    }

    // Category headers: remember which ones are collapsed (by hardware name) across restarts.
    private static string? GroupName(object? dataContext) => (dataContext as CollectionViewGroup)?.Name?.ToString();

    private void GroupToggle_Loaded(object sender, RoutedEventArgs e) => SyncGroupToggle((ToggleButton)sender);

    private void GroupToggle_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        SyncGroupToggle((ToggleButton)sender);

    private void SyncGroupToggle(ToggleButton toggle)
    {
        var name = GroupName(toggle.DataContext);
        if (name == null) return;
        _syncingGroupToggle = true;
        toggle.IsChecked = !_collapsedGroups.Contains(name);
        _syncingGroupToggle = false;
    }

    private void GroupToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingGroupToggle) return;
        var toggle = (ToggleButton)sender;
        var name = GroupName(toggle.DataContext);
        if (name == null) return;
        if (toggle.IsChecked == true) _collapsedGroups.Remove(name);
        else _collapsedGroups.Add(name);
        ScheduleSave();
    }

    private bool FilterSensor(object item)
    {
        var query = SearchBox?.Text?.Trim();
        if (string.IsNullOrEmpty(query)) return true;
        var s = (SensorRecord)item;
        return s.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || s.Hardware.Contains(query, StringComparison.OrdinalIgnoreCase)
            || s.Type.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        CollectionViewSource.GetDefaultView(_store.Sensors).Refresh();
    }

    private void ResetMinMax_Click(object sender, RoutedEventArgs e)
    {
        foreach (var s in _store.Sensors) s.ResetMinMax();
    }

    private List<SensorRecord> SelectedSensors() => SensorList.SelectedItems.Cast<SensorRecord>().ToList();

    private void SensorList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d
            && FindAncestor<ListViewItem>(d) is ListViewItem item
            && item.DataContext is SensorRecord rec)
        {
            AddToGraph(_activeGraph ?? AddGraph(), new[] { rec });
        }
    }

    private void AddToActive_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedSensors();
        if (selected.Count == 0) return;
        AddToGraph(_activeGraph ?? AddGraph(), selected);
    }

    private void AddToNew_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedSensors();
        if (selected.Count == 0) return;
        AddToGraph(AddGraph(), selected);
    }

    private void SensorList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragArmed = e.OriginalSource is DependencyObject d && FindAncestor<ListViewItem>(d) != null;
    }

    private void SensorList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed) return;

        var p = e.GetPosition(null);
        if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _dragArmed = false;
        var ids = SelectedSensors().Select(s => s.Id).ToList();
        if (ids.Count == 0) return;

        var data = new DataObject(GraphCard.DragFormat, string.Join("\n", ids));
        DragDrop.DoDragDrop(SensorList, data, DragDropEffects.Copy);
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T found) return found;
            d = d is Visual || d is Visual3D
                ? VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }
}

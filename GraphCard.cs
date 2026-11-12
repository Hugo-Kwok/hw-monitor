using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace BetterHwMonitor;

/// <summary>A graph panel: header, the chart, and the legend on the right of the chart.</summary>
public sealed class GraphCard : Border
{
    public const string DragFormat = "BetterHwMonitor.SensorIds";

    /// <summary>
    /// Legend width, shared by every graph so all plot areas line up. Dragging the divider between a chart
    /// and its legend changes it for all graphs at once. Long names are cut with "...".
    /// </summary>
    public static double LegendWidth { get; private set; } = DefaultLegendWidth;
    public const double DefaultLegendWidth = 250, MinLegendWidth = 140, MaxLegendWidth = 700;
    private static event Action? LegendWidthChanged;

    public static void SetLegendWidth(double width)
    {
        if (double.IsNaN(width)) return;
        width = Math.Round(Math.Clamp(width, MinLegendWidth, MaxLegendWidth));
        if (width == LegendWidth) return;
        LegendWidth = width;
        LegendWidthChanged?.Invoke();
    }

    // Curve colors chosen to stay readable on both the light and the dark chart background
    private static readonly Color[] Palette =
    {
        Color.FromRgb(0x3B, 0x8E, 0xEA), Color.FromRgb(0xE5, 0x53, 0x4B), Color.FromRgb(0x3F, 0xB9, 0x50),
        Color.FromRgb(0xF0, 0x88, 0x3E), Color.FromRgb(0xA3, 0x71, 0xF7), Color.FromRgb(0xDB, 0x61, 0xA2),
        Color.FromRgb(0x2D, 0xBC, 0xD2), Color.FromRgb(0xD4, 0xA7, 0x2C), Color.FromRgb(0x8B, 0x94, 0x9E),
        Color.FromRgb(0xA5, 0x73, 0x4E),
    };

    // Extra choices offered in the color picker (after the palette)
    private static readonly Color[] ExtraPickerColors =
    {
        Color.FromRgb(0x00, 0x5F, 0xCC), Color.FromRgb(0xB0, 0x1E, 0x1E), Color.FromRgb(0x1E, 0x7D, 0x32),
        Color.FromRgb(0xFF, 0xC1, 0x07), Color.FromRgb(0xFF, 0x52, 0x52), Color.FromRgb(0x00, 0xE6, 0x76),
        Color.FromRgb(0x18, 0xFF, 0xFF), Color.FromRgb(0xE0, 0x40, 0xFB), Color.FromRgb(0xFF, 0xFF, 0xFF),
        Color.FromRgb(0x00, 0x00, 0x00), Color.FromRgb(0x60, 0x7D, 0x8B), Color.FromRgb(0xC6, 0xFF, 0x00),
        Color.FromRgb(0xFF, 0x6D, 0x00), Color.FromRgb(0x7C, 0x4D, 0xFF),
    };

    private readonly DataStore _store;
    private readonly StackPanel _legend = new() { Margin = new Thickness(6, 6, 2, 2) };
    private readonly List<SeriesLayout> _pending = new();
    private readonly Button _axisButton;
    private readonly DockPanel _body;

    public const double DefaultHeight = 260;
    private const double MinHeight_ = 120;
    private const double MaxHeight_ = 1200;
    private bool _active;

    public event Action<GraphCard>? RemoveRequested;
    public event Action<GraphCard>? Activated;
    public event Action<GraphCard, int>? MoveRequested;
    public event Action<string>? Message;

    /// <summary>Raised when anything worth saving changes (title, curves, names, colors, axis limits).</summary>
    public event Action? Changed;

    public GraphCard(DataStore store, string title)
    {
        _store = store;
        SetResourceReference(BackgroundProperty, "PanelBrush");
        BorderThickness = new Thickness(2);
        CornerRadius = new CornerRadius(4);
        Margin = new Thickness(0, 0, 0, 3);
        Padding = new Thickness(2);

        Chart = new MultiChart(store);

        TitleBox = new TextBox
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Click to rename",
        };
        TitleBox.TextChanged += (_, _) => Changed?.Invoke();

        _axisButton = HeaderButton("Y axis...", "Set the minimum and maximum of the Y axes");
        _axisButton.Width = 118; // same width for "Y axis..." and "Y axis (manual)..."
        _axisButton.Click += (_, _) => OpenAxisEditor();
        var upButton = HeaderButton("▲", "Move this graph up");
        upButton.Click += (_, _) => MoveRequested?.Invoke(this, -1);
        var downButton = HeaderButton("▼", "Move this graph down");
        downButton.Click += (_, _) => MoveRequested?.Invoke(this, +1);
        var removeButton = HeaderButton("Remove graph", "Delete this graph");
        removeButton.Click += (_, _) => RemoveRequested?.Invoke(this);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(_axisButton);
        buttons.Children.Add(upButton);
        buttons.Children.Add(downButton);
        buttons.Children.Add(removeButton);

        var header = new DockPanel { Margin = new Thickness(4, 1, 4, 1) };
        DockPanel.SetDock(buttons, Dock.Right);
        header.Children.Add(buttons);
        header.Children.Add(TitleBox);

        // Legend sits on the right, right next to the right Y-axis labels
        var legendScroll = new ScrollViewer
        {
            Content = _legend,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Width = LegendWidth,
        };

        _body = new DockPanel { Height = DefaultHeight };
        DockPanel.SetDock(legendScroll, Dock.Right);
        _body.Children.Add(legendScroll);
        var legendGrip = MakeLegendGrip();
        DockPanel.SetDock(legendGrip, Dock.Right);
        _body.Children.Add(legendGrip);
        _body.Children.Add(Chart);

        // Follow width changes made on any graph (only while this card is on screen)
        void OnLegendWidthChanged() => legendScroll.Width = LegendWidth;
        Loaded += (_, _) =>
        {
            legendScroll.Width = LegendWidth;
            LegendWidthChanged -= OnLegendWidthChanged;
            LegendWidthChanged += OnLegendWidthChanged;
        };
        Unloaded += (_, _) => LegendWidthChanged -= OnLegendWidthChanged;

        var resizeGrip = MakeResizeGrip();

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(resizeGrip, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(resizeGrip);
        root.Children.Add(_body);
        Child = root;

        AllowDrop = true;
        DragOver += OnDragOver;
        Drop += OnDrop;
        PreviewMouseLeftButtonDown += (_, _) => Activated?.Invoke(this);

        IsActive = false;
    }

    private static Button HeaderButton(string text, string tooltip) => new()
    {
        Content = text,
        ToolTip = tooltip,
        Padding = new Thickness(8, 1, 8, 1),
        Margin = new Thickness(4, 0, 0, 0),
    };

    public MultiChart Chart { get; }
    public TextBox TitleBox { get; }

    public bool IsActive
    {
        get => _active;
        set
        {
            _active = value;
            SetResourceReference(BorderBrushProperty, value ? "AccentBrush" : "LineBrush");
        }
    }

    // ---------- Height ----------

    /// <summary>Height of the chart area (legend + plot) in pixels.</summary>
    public double GraphHeight
    {
        get => _body.Height;
        set => _body.Height = double.IsNaN(value) || value <= 0
            ? DefaultHeight
            : Math.Clamp(value, MinHeight_, MaxHeight_);
    }

    /// <summary>Thin bar under the graph: drag to resize, double-click to reset.</summary>
    /// <summary>Vertical divider between the chart and the legend; drag to resize the legend on every graph.</summary>
    private FrameworkElement MakeLegendGrip()
    {
        var gripTemplate = new ControlTemplate(typeof(Thumb));
        var outer = new FrameworkElementFactory(typeof(Border));
        outer.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var line = new FrameworkElementFactory(typeof(Border));
        line.SetValue(FrameworkElement.WidthProperty, 3.0);
        line.SetValue(FrameworkElement.HeightProperty, 40.0);
        line.SetValue(Border.CornerRadiusProperty, new CornerRadius(1.5));
        line.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        line.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        line.SetResourceReference(Border.BackgroundProperty, "ScrollThumbBrush");
        outer.AppendChild(line);
        gripTemplate.VisualTree = outer;

        var grip = new Thumb
        {
            Width = 8,
            Cursor = Cursors.SizeWE,
            Template = gripTemplate,
            ToolTip = "Drag to change the legend width on all graphs. Double-click to reset.",
        };
        // The legend is on the right, so dragging left makes it wider
        grip.DragDelta += (_, e) => SetLegendWidth(LegendWidth - e.HorizontalChange);
        grip.DragCompleted += (_, _) => Changed?.Invoke();
        grip.MouseDoubleClick += (_, _) =>
        {
            SetLegendWidth(DefaultLegendWidth);
            Changed?.Invoke();
        };
        return grip;
    }

    private FrameworkElement MakeResizeGrip()
    {
        var gripTemplate = new ControlTemplate(typeof(Thumb));
        var outer = new FrameworkElementFactory(typeof(Border));
        outer.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var line = new FrameworkElementFactory(typeof(Border));
        line.SetValue(FrameworkElement.WidthProperty, 40.0);
        line.SetValue(FrameworkElement.HeightProperty, 3.0);
        line.SetValue(Border.CornerRadiusProperty, new CornerRadius(1.5));
        line.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        line.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        line.SetResourceReference(Border.BackgroundProperty, "ScrollThumbBrush");
        outer.AppendChild(line);
        gripTemplate.VisualTree = outer;

        var grip = new Thumb
        {
            Height = 8,
            Cursor = Cursors.SizeNS,
            Template = gripTemplate,
            ToolTip = "Drag to change the graph height. Double-click to reset.",
        };
        grip.DragDelta += (_, e) => GraphHeight = _body.Height + e.VerticalChange;
        grip.DragCompleted += (_, _) => Changed?.Invoke();
        grip.MouseDoubleClick += (_, _) =>
        {
            GraphHeight = DefaultHeight;
            Changed?.Invoke();
        };
        return grip;
    }

    // ---------- Saving / restoring ----------

    public bool HasPending => _pending.Count > 0;

    /// <summary>Applies a saved graph: axis limits now, curves once their sensors have been read.</summary>
    public void LoadLayout(GraphLayout layout)
    {
        Chart.LeftMin = layout.LeftMin;
        Chart.LeftMax = layout.LeftMax;
        Chart.RightMin = layout.RightMin;
        Chart.RightMax = layout.RightMax;
        GraphHeight = layout.Height;
        UpdateAxisButton();
        _pending.AddRange(layout.Series.Where(s => !string.IsNullOrEmpty(s.SensorId)));
        ResolvePending();
    }

    /// <summary>Adds saved curves whose sensors exist now.</summary>
    public void ResolvePending()
    {
        for (int i = 0; i < _pending.Count; i++)
        {
            var saved = _pending[i];
            var rec = _store.Find(saved.SensorId);
            if (rec == null) continue;

            _pending.RemoveAt(i--);
            if (Chart.Series.Any(s => s.Sensor == rec)) continue;
            AddSeries(rec, ParseColor(saved.Color) ?? NextColor(), saved.RightAxis, saved.Name, notify: false);
        }
    }

    public GraphLayout ToLayout()
    {
        var layout = new GraphLayout
        {
            Title = TitleBox.Text,
            LeftMin = Chart.LeftMin,
            LeftMax = Chart.LeftMax,
            RightMin = Chart.RightMin,
            RightMax = Chart.RightMax,
            Height = GraphHeight,
        };
        foreach (var s in Chart.Series)
        {
            layout.Series.Add(new SeriesLayout
            {
                SensorId = s.Sensor.Id,
                Color = ToHex(s.Color),
                RightAxis = s.RightAxis,
                Name = s.CustomName,
            });
        }
        // Keep curves whose sensors were not found (yet), so they are not lost from the saved layout.
        layout.Series.AddRange(_pending);
        return layout;
    }

    // ---------- Curves ----------

    /// <summary>Adds a sensor. Returns an error message, or null on success.</summary>
    public string? TryAddSensor(SensorRecord rec)
    {
        if (Chart.Series.Any(s => s.Sensor == rec))
            return $"'{rec.Name}' is already in {TitleBox.Text}.";

        string? leftUnit = Chart.Series.FirstOrDefault(s => !s.RightAxis)?.Sensor.Unit;
        string? rightUnit = Chart.Series.FirstOrDefault(s => s.RightAxis)?.Sensor.Unit;

        bool right;
        if (leftUnit == null && rightUnit == null) right = false;
        else if (rec.Unit == leftUnit) right = false;
        else if (rec.Unit == rightUnit) right = true;
        else if (leftUnit == null) right = false;
        else if (rightUnit == null) right = true;
        else
            return $"{TitleBox.Text} already uses two units ({ShowUnit(leftUnit)} and {ShowUnit(rightUnit)}). " +
                   $"Put '{rec.Name}' ({ShowUnit(rec.Unit)}) in another graph.";

        AddSeries(rec, NextColor(), right, null, notify: true);
        return null;
    }

    private void AddSeries(SensorRecord rec, Color color, bool right, string? customName, bool notify)
    {
        var series = new ChartSeries(rec, color, right) { CustomName = string.IsNullOrWhiteSpace(customName) ? null : customName };
        Chart.Series.Add(series);
        _legend.Children.Add(MakeLegendItem(series));
        Chart.InvalidateVisual();
        UpdateAxisButton();
        if (notify) Changed?.Invoke();
    }

    private void RemoveSeries(ChartSeries series, FrameworkElement legendItem)
    {
        Chart.Series.Remove(series);
        _legend.Children.Remove(legendItem);
        Chart.InvalidateVisual();
        Changed?.Invoke();
    }

    private Color NextColor()
    {
        foreach (var c in Palette)
        {
            if (Chart.Series.All(s => s.Color != c)) return c;
        }
        return Palette[Chart.Series.Count % Palette.Length];
    }

    private static string ShowUnit(string unit) => unit.Length == 0 ? "no unit" : unit;

    private static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static Color? ParseColor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (!text.StartsWith('#')) text = "#" + text;
        try
        {
            return ColorConverter.ConvertFromString(text) is Color c ? c : null;
        }
        catch
        {
            return null;
        }
    }

    // ---------- Legend ----------

    /// <summary>One legend row: [color] [name, click to rename] [reset] [value] [R] [x]</summary>
    private FrameworkElement MakeLegendItem(ChartSeries s)
    {
        var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1), LastChildFill = true };

        var swatch = new Border
        {
            Width = 16,
            Height = 10,
            CornerRadius = new CornerRadius(2),
            Background = s.Brush,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            Cursor = Cursors.Hand,
            ToolTip = "Click to change the color",
        };
        swatch.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        swatch.MouseLeftButtonUp += (_, e) =>
        {
            OpenColorPicker(s, swatch);
            e.Handled = true;
        };
        DockPanel.SetDock(swatch, Dock.Left);
        row.Children.Add(swatch);

        var removeButton = SmallButton("✕", "Remove from graph");
        DockPanel.SetDock(removeButton, Dock.Right);
        row.Children.Add(removeButton);

        if (s.RightAxis)
        {
            var axisNote = new TextBlock
            {
                Text = "R",
                FontSize = 10,
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Uses the right Y axis",
            };
            axisNote.SetResourceReference(TextBlock.ForegroundProperty, "SubtleTextBrush");
            DockPanel.SetDock(axisNote, Dock.Right);
            row.Children.Add(axisNote);
        }

        var value = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            Width = 66, // fixed so every legend row has the same layout
            TextAlignment = TextAlignment.Right,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        value.SetBinding(TextBlock.TextProperty, new Binding(nameof(SensorRecord.ValueText)) { Source = s.Sensor });
        DockPanel.SetDock(value, Dock.Right);
        row.Children.Add(value);

        var resetButton = SmallButton("↺", "");
        DockPanel.SetDock(resetButton, Dock.Right);
        row.Children.Add(resetButton);

        // Name: a label that turns into a text box when clicked
        var nameText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Cursor = Cursors.IBeam,
        };
        var nameEdit = new TextBox { Visibility = Visibility.Collapsed };
        var nameHost = new Grid();
        nameHost.Children.Add(nameText);
        nameHost.Children.Add(nameEdit);
        row.Children.Add(nameHost);

        void RefreshName()
        {
            nameText.Text = s.Label;
            nameText.ToolTip = s.CustomName == null
                ? $"{s.Label}\nClick to rename"
                : $"{s.Label}\nOriginal name: {s.Sensor.DisplayName}\nClick to rename";
            resetButton.Visibility = s.CustomName == null ? Visibility.Collapsed : Visibility.Visible;
            resetButton.ToolTip = $"Change back to the original name ({s.Sensor.DisplayName})";
            Chart.InvalidateVisual();
        }

        void SetName(string? name)
        {
            name = name?.Trim();
            s.CustomName = string.IsNullOrEmpty(name) || name == s.Sensor.DisplayName ? null : name;
            RefreshName();
            Changed?.Invoke();
        }

        bool editing = false;
        void BeginRename()
        {
            if (editing) return;
            editing = true;
            nameEdit.Text = s.Label;
            nameText.Visibility = Visibility.Collapsed;
            nameEdit.Visibility = Visibility.Visible;
            nameEdit.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                nameEdit.Focus();
                Keyboard.Focus(nameEdit);
                nameEdit.SelectAll();
            }));
        }

        void EndRename(bool commit)
        {
            if (!editing) return;
            editing = false;
            nameEdit.Visibility = Visibility.Collapsed;
            nameText.Visibility = Visibility.Visible;
            if (commit) SetName(nameEdit.Text);
        }

        nameText.MouseLeftButtonUp += (_, e) =>
        {
            BeginRename();
            e.Handled = true;
        };
        nameEdit.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { EndRename(commit: true); e.Handled = true; }
            else if (e.Key == Key.Escape) { EndRename(commit: false); e.Handled = true; }
        };
        nameEdit.LostKeyboardFocus += (_, _) => EndRename(commit: true);
        resetButton.Click += (_, _) => SetName(null);
        removeButton.Click += (_, _) => RemoveSeries(s, row);

        // Right-click menu with the same actions
        var renameItem = new MenuItem { Header = "Rename" };
        renameItem.Click += (_, _) => BeginRename();
        var resetItem = new MenuItem { Header = "Reset to original name" };
        resetItem.Click += (_, _) => SetName(null);
        var colorItem = new MenuItem { Header = "Change color" };
        colorItem.Click += (_, _) => OpenColorPicker(s, swatch);
        var removeItem = new MenuItem { Header = "Remove from graph" };
        removeItem.Click += (_, _) => RemoveSeries(s, row);
        var menu = new ContextMenu();
        menu.Items.Add(renameItem);
        menu.Items.Add(resetItem);
        menu.Items.Add(colorItem);
        menu.Items.Add(removeItem);
        menu.Opened += (_, _) => resetItem.IsEnabled = s.CustomName != null;
        row.ContextMenu = menu;
        row.Background = Brushes.Transparent; // so the whole row reacts to right-click

        RefreshName();
        return row;
    }

    private static Button SmallButton(string text, string tooltip) => new()
    {
        Content = text,
        ToolTip = tooltip,
        Padding = new Thickness(4, 0, 4, 0),
        Margin = new Thickness(2, 0, 0, 0),
        BorderThickness = new Thickness(0),
        Background = Brushes.Transparent,
        VerticalAlignment = VerticalAlignment.Center,
    };

    // ---------- Y axis limits ----------

    private void UpdateAxisButton()
    {
        bool manual = Chart.LeftMin.HasValue || Chart.LeftMax.HasValue ||
                      Chart.RightMin.HasValue || Chart.RightMax.HasValue;
        _axisButton.Content = manual ? "Y axis (manual)..." : "Y axis...";
    }

    private void OpenAxisEditor()
    {
        var popup = new Popup
        {
            PlacementTarget = _axisButton,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
        };

        string leftUnit = Chart.Series.FirstOrDefault(s => !s.RightAxis)?.Sensor.Unit ?? "";
        string? rightUnit = Chart.Series.FirstOrDefault(s => s.RightAxis)?.Sensor.Unit;

        static string Format(double? v) => v?.ToString("G", CultureInfo.CurrentCulture) ?? "";

        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });

        void AddCell(UIElement e, int row, int col)
        {
            Grid.SetRow(e, row);
            Grid.SetColumn(e, col);
            grid.Children.Add(e);
        }

        for (int i = 0; i < 3; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddCell(new TextBlock { Text = "Min", Margin = new Thickness(4, 0, 4, 2) }, 0, 1);
        AddCell(new TextBlock { Text = "Max", Margin = new Thickness(4, 0, 4, 2) }, 0, 2);

        TextBox Box(double? v) => new() { Text = Format(v), Margin = new Thickness(4, 2, 4, 2) };

        var leftMin = Box(Chart.LeftMin);
        var leftMax = Box(Chart.LeftMax);
        var rightMin = Box(Chart.RightMin);
        var rightMax = Box(Chart.RightMax);

        string AxisName(string side, string? unit) =>
            unit == null ? $"{side} axis (unused)" : unit.Length == 0 ? $"{side} axis" : $"{side} axis ({unit})";

        AddCell(new TextBlock { Text = AxisName("Left", leftUnit), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) }, 1, 0);
        AddCell(leftMin, 1, 1);
        AddCell(leftMax, 1, 2);
        AddCell(new TextBlock { Text = AxisName("Right", rightUnit), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) }, 2, 0);
        AddCell(rightMin, 2, 1);
        AddCell(rightMax, 2, 2);

        var hint = new TextBlock
        {
            Text = "Leave a box empty for automatic.",
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 6),
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "SubtleTextBrush");

        bool TryRead(TextBox box, out double? value)
        {
            value = null;
            var text = box.Text.Trim();
            if (text.Length == 0) return true;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var v) ||
                double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
            {
                value = v;
                return true;
            }
            Message?.Invoke($"'{text}' is not a number.");
            return false;
        }

        void Apply()
        {
            if (!TryRead(leftMin, out var lMin) || !TryRead(leftMax, out var lMax) ||
                !TryRead(rightMin, out var rMin) || !TryRead(rightMax, out var rMax))
                return;
            if ((lMin.HasValue && lMax.HasValue && lMin >= lMax) || (rMin.HasValue && rMax.HasValue && rMin >= rMax))
            {
                Message?.Invoke("Max must be larger than Min.");
                return;
            }
            Chart.LeftMin = lMin;
            Chart.LeftMax = lMax;
            Chart.RightMin = rMin;
            Chart.RightMax = rMax;
            UpdateAxisButton();
            Chart.InvalidateVisual();
            Changed?.Invoke();
            popup.IsOpen = false;
        }

        foreach (var box in new[] { leftMin, leftMax, rightMin, rightMax })
        {
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) Apply();
            };
        }

        var applyButton = new Button { Content = "Apply", Margin = new Thickness(4, 0, 0, 0) };
        applyButton.Click += (_, _) => Apply();
        var autoButton = new Button { Content = "All automatic", Margin = new Thickness(4, 0, 0, 0) };
        autoButton.Click += (_, _) =>
        {
            leftMin.Text = leftMax.Text = rightMin.Text = rightMax.Text = "";
            Apply();
        };
        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttonRow.Children.Add(autoButton);
        buttonRow.Children.Add(applyButton);

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = "Y axis range", FontWeight = FontWeights.SemiBold });
        stack.Children.Add(grid);
        stack.Children.Add(hint);
        stack.Children.Add(buttonRow);

        var border = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10),
            Child = stack,
        };
        border.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        border.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");

        popup.Child = border;
        popup.IsOpen = true;
    }

    // ---------- Color picker ----------

    private void OpenColorPicker(ChartSeries series, Border swatch)
    {
        var popup = new Popup
        {
            PlacementTarget = swatch,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
        };

        void Apply(Color c)
        {
            series.SetColor(c);
            swatch.Background = series.Brush;
            Chart.InvalidateVisual();
            Changed?.Invoke();
        }

        var colors = new WrapPanel { Width = 8 * 26 };
        foreach (var c in Palette.Concat(ExtraPickerColors))
        {
            var chip = new Border
            {
                Width = 22,
                Height = 22,
                Margin = new Thickness(2),
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(c),
                BorderThickness = new Thickness(2),
                BorderBrush = c == series.Color ? Brushes.White : Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = ToHex(c),
            };
            var color = c;
            chip.MouseEnter += (_, _) => chip.BorderBrush = Brushes.Gray;
            chip.MouseLeave += (_, _) => chip.BorderBrush = color == series.Color ? Brushes.White : Brushes.Transparent;
            chip.MouseLeftButtonUp += (_, _) =>
            {
                Apply(color);
                popup.IsOpen = false;
            };
            colors.Children.Add(chip);
        }

        var hexBox = new TextBox { Text = ToHex(series.Color), Width = 90, VerticalContentAlignment = VerticalAlignment.Center };
        var applyButton = new Button { Content = "Apply", Margin = new Thickness(6, 0, 0, 0) };
        void ApplyHex()
        {
            if (ParseColor(hexBox.Text) is Color c)
            {
                Apply(c);
                popup.IsOpen = false;
            }
            else
            {
                Message?.Invoke($"'{hexBox.Text}' is not a color. Use a hex code like #FF8800.");
            }
        }
        applyButton.Click += (_, _) => ApplyHex();
        hexBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) ApplyHex();
        };

        var hexLabel = new TextBlock { Text = "Hex:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        var hexRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 6, 2, 2) };
        hexRow.Children.Add(hexLabel);
        hexRow.Children.Add(hexBox);
        hexRow.Children.Add(applyButton);

        var title = new TextBlock
        {
            Text = series.Label,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(2, 0, 2, 6),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 8 * 26,
        };

        var stack = new StackPanel();
        stack.Children.Add(title);
        stack.Children.Add(colors);
        stack.Children.Add(hexRow);

        var border = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8),
            Child = stack,
        };
        border.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        border.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");

        popup.Child = border;
        popup.IsOpen = true;
    }


    // ---------- Drag and drop ----------

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DragFormat) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DragFormat) is string ids)
        {
            Activated?.Invoke(this);
            foreach (var id in ids.Split('\n'))
            {
                var rec = _store.Find(id);
                if (rec == null) continue;
                var error = TryAddSensor(rec);
                if (error != null) Message?.Invoke(error);
            }
        }
        e.Handled = true;
    }
}

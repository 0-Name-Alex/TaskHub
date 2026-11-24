using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using TaskHub.App.ViewModels;

namespace TaskHub.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Activated += (_, _) => _viewModel.Diagnostics.RefreshAutostart(); // the tray menu may have changed it
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private TasksViewModel Tasks => _viewModel.Tasks;

    public void ShowTasksTab() => Tabs.SelectedIndex = 0;

    public void ShowNotificationsTab() => Tabs.SelectedItem = NotificationsTab;

    public void ShowAutomationTab() => Tabs.SelectedItem = AutomationTab;

    /// <summary>Called by the quick-add hotkey.</summary>
    public void FocusQuickAdd()
    {
        ShowTasksTab();
        QuickAddBox.Focus();
        Keyboard.Focus(QuickAddBox);
        QuickAddBox.SelectAll();
    }

    /// <summary>
    /// Keyboard-first:
    /// Ctrl+N / Ctrl+L quick add · Ctrl+F search · Esc clear search / leave field ·
    /// Ctrl+Z undo (outside text boxes) · Ctrl+S save editor ·
    /// in the task list: Space complete, Delete trash, Enter edit title.
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        var inTextBox = Keyboard.FocusedElement is TextBoxBase or DatePicker || Keyboard.FocusedElement is DependencyObject d && IsInside<DatePicker>(d);

        if (ctrl && e.Key is Key.N or Key.L)
        {
            FocusQuickAdd();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.F)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.S)
        {
            Tasks.SaveEditorCommand.Execute(null);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.Z && !inTextBox)
        {
            Tasks.UndoCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && Keyboard.FocusedElement == SearchBox)
        {
            Tasks.SearchText = "";
            TaskList.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && Keyboard.FocusedElement == NewListBox)
        {
            Tasks.CancelNewListCommand.Execute(null);
            TaskList.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && inTextBox)
        {
            TaskList.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && Tasks.HasSelection && Tabs.SelectedIndex == 0)
        {
            Tasks.CloseEditorCommand.Execute(null);
            e.Handled = true;
        }
        else if (!inTextBox && IsInside<ListBox>(Keyboard.FocusedElement as DependencyObject, TaskList))
        {
            switch (e.Key)
            {
                case Key.Space:
                    Tasks.ToggleCompleteCommand.Execute(Tasks.SelectedTask);
                    e.Handled = true;
                    break;
                case Key.Delete:
                    if (Tasks.SelectedTask?.IsDeleted == false) Tasks.DeleteSelectedCommand.Execute(null);
                    e.Handled = true;
                    break;
            }
        }
    }

    // ------------------------------------------------------------------ lists: new list box, colour menu

    private void OnNewListBoxVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (NewListBox.IsVisible)
            _ = Dispatcher.InvokeAsync(() => Keyboard.Focus(NewListBox), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnNewListBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Tasks.NewListName)) Tasks.CancelNewListCommand.Execute(null);
    }

    /// <summary>Right-click on a list in the sidebar: colour menu.</summary>
    private void OnNavRightClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement element || element.DataContext is not NavItem { Kind: NavKind.List } item) return;
        // Anchor on the whole row, not the element under the mouse (that may be the icon-font glyph).
        UIElement target = NavList.ContainerFromElement(element) as ListBoxItem ?? (UIElement)NavList;
        OpenListMenu(item, target, PlacementMode.MousePoint);
        e.Handled = true;
    }

    private void OnListColorButtonClick(object sender, RoutedEventArgs e)
    {
        if (Tasks.SelectedList is { } item) OpenListMenu(item, ListColorButton, PlacementMode.Bottom);
    }

    /// <summary>The list menu: Colour ▸ and Icon ▸ submenus.</summary>
    private void OpenListMenu(NavItem item, UIElement target, PlacementMode placement)
    {
        // A context menu inherits fonts from its target; pin the window's text font so labels never
        // come out in the icon font (squares).
        var menu = new ContextMenu { PlacementTarget = target, Placement = placement, FontFamily = FontFamily, FontSize = 14 };
        menu.Items.Add(new MenuItem { Header = item.Title, IsEnabled = false, FontWeight = FontWeights.SemiBold });
        menu.Items.Add(new Separator());

        var colourMenu = new MenuItem { Header = "Colour", Icon = Swatch(item.Color) };
        foreach (var choice in item.ColorChoices)
        {
            colourMenu.Items.Add(new MenuItem
            {
                Header = choice.Name,
                Icon = Swatch(choice.Hex),
                FontWeight = choice.IsCurrent ? FontWeights.SemiBold : FontWeights.Normal,
                Command = Tasks.SetListColorCommand,
                CommandParameter = choice,
            });
        }
        menu.Items.Add(colourMenu);

        var iconMenu = new MenuItem { Header = "Icon", Icon = Glyph(item.Glyph, item.Color) };
        foreach (var choice in item.IconChoices)
        {
            iconMenu.Items.Add(new MenuItem
            {
                Header = choice.Name,
                Icon = Glyph(choice.Glyph, item.Color),
                FontWeight = choice.IsCurrent ? FontWeights.SemiBold : FontWeights.Normal,
                Command = Tasks.SetListIconCommand,
                CommandParameter = choice,
            });
        }
        menu.Items.Add(iconMenu);
        menu.IsOpen = true;
    }

    private static System.Windows.Media.Brush? BrushFor(string? hex) =>
        hex is null ? null
            : new ColorBrushConverter().Convert(hex, typeof(System.Windows.Media.Brush), null, System.Globalization.CultureInfo.InvariantCulture)
                as System.Windows.Media.Brush;

    /// <summary>A colour dot; an empty ring for "no colour".</summary>
    private static UIElement Swatch(string? hex)
    {
        var swatch = new System.Windows.Shapes.Ellipse { Width = 12, Height = 12 };
        if (BrushFor(hex) is { } brush) swatch.Fill = brush;
        else
        {
            swatch.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextFillColorSecondaryBrush");
            swatch.StrokeThickness = 1.2;
        }
        return swatch;
    }

    /// <summary>An icon-font glyph (only this element uses the icon font), tinted with the list colour.</summary>
    private UIElement Glyph(string glyph, string? hex)
    {
        var text = new TextBlock
        {
            Text = glyph,
            FontFamily = (System.Windows.Media.FontFamily)FindResource("IconFont"),
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        if (BrushFor(hex) is { } brush) text.Foreground = brush;
        return text;
    }

    private static bool IsInside<T>(DependencyObject? element, T? specific = null) where T : DependencyObject
    {
        for (var current = element; current is not null; current = ParentOf(current))
        {
            if (current is T match && (specific is null || ReferenceEquals(match, specific))) return true;
        }
        return false;
    }

    private static DependencyObject? ParentOf(DependencyObject d) =>
        d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
            ? System.Windows.Media.VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d)
            : LogicalTreeHelper.GetParent(d);

    /// <summary>Minimizing to tray flushes working set memory.</summary>
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized)
        {
            Hide();
            TaskHub.Platform.Windows.Memory.MemoryOptimizer.TrimWorkingSet();
        }
    }

    /// <summary>Closing the window hides it to the tray; only Quit exits the app.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!App.Current.IsQuitting)
        {
            e.Cancel = true;
            Hide();
            TaskHub.Platform.Windows.Memory.MemoryOptimizer.TrimWorkingSet();
        }
        base.OnClosing(e);
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace ScOutfitter.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _filtering;

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        vm.SelectStart("Stanton", "Hurston", "Everus Harbor");
        Loaded += (_, _) =>
        {
            if (vm.ErkulMode && vm.ErkulLink.Length > 0 && vm.LoadErkulCommand.CanExecute(null))
            {
                vm.LoadErkulCommand.Execute(null);
            }
        };

        // filter the ship dropdown while typing, without touching the typed text
        ShipBox.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(OnShipTextChanged));
        ShipBox.PreviewKeyDown += OnShipKeyDown;
    }

    private void OnShipTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_filtering || !ShipBox.IsKeyboardFocusWithin)
        {
            return;
        }

        string typed = ShipBox.Text.Trim();
        if (ShipBox.SelectedItem is string sel && sel == typed)
        {
            return; // a pick from the list, not typing
        }

        _filtering = true;
        try
        {
            List<string> hits = typed.Length == 0
                ? _vm.ShipNames.ToList()
                : _vm.ShipNames.Where(n => n.Contains(typed, StringComparison.OrdinalIgnoreCase)).ToList();
            ShipBox.ItemsSource = hits.Count > 0 ? hits : _vm.ShipNames;
            ShipBox.IsDropDownOpen = typed.Length > 0 && hits.Count > 0;
            // reopening the popup steals the caret; put it back at the end
            if (ShipBox.Template.FindName("PART_EditableTextBox", ShipBox) is TextBox tb)
            {
                tb.CaretIndex = tb.Text.Length;
            }
        }
        finally
        {
            _filtering = false;
        }
    }

    private void OnShipKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _vm.PlanCommand.CanExecute(null))
        {
            ShipBox.IsDropDownOpen = false;
            _vm.PlanCommand.Execute(null);
            e.Handled = true;
        }
    }
}

using System.Linq;
using Avalonia.Controls;
using Vulcano.App.ViewModels;

namespace Vulcano.App.Views;

public partial class LogView : UserControl
{
    private LogViewModel? _viewModel;

    public LogView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null) _viewModel.EntriesAppended -= OnEntriesAppended;

            _viewModel = DataContext as LogViewModel;
            if (_viewModel is not null) _viewModel.EntriesAppended += OnEntriesAppended;
        };
    }

    /// <summary>
    /// Keeps the newest line in view. The table reads oldest-first, like the exported file, which
    /// means the interesting end is the bottom one.
    /// </summary>
    private void OnEntriesAppended(object? sender, System.EventArgs e)
    {
        if (_viewModel?.Entries.LastOrDefault() is { } last)
        {
            Lines.ScrollIntoView(last);
        }
    }
}

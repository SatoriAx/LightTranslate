using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace LightTranslate;

public partial class HistoryWindow : Window
{
    private readonly ObservableCollection<TranslationHistoryEntry> _entries = [];
    private readonly DispatcherTimer _clearResetTimer;
    private bool _clearArmed;

    public event Action<TranslationHistoryEntry>? EntrySelected;

    public HistoryWindow()
    {
        InitializeComponent();
        _clearResetTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(4)
        };
        _clearResetTimer.Tick += (_, _) => ResetClearConfirmation();
        HistoryList.ItemsSource = _entries;
        Reload();
    }

    private void Reload()
    {
        _entries.Clear();
        foreach (var entry in HistoryStore.Load())
            _entries.Add(entry);

        EmptyState.Visibility = _entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_entries.Count > 0)
            HistoryList.SelectedIndex = 0;
    }

    private void Load_Click(object sender, RoutedEventArgs e) => LoadSelected();

    private void HistoryList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => LoadSelected();

    private void LoadSelected()
    {
        if (HistoryList.SelectedItem is not TranslationHistoryEntry entry)
            return;

        EntrySelected?.Invoke(entry);
        Close();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_entries.Count == 0)
            return;

        if (!_clearArmed)
        {
            _clearArmed = true;
            ClearHistoryLabel.Text = "再次确认清空";
            _clearResetTimer.Stop();
            _clearResetTimer.Start();
            return;
        }

        HistoryStore.Clear();
        ResetClearConfirmation();
        Reload();
    }

    private void ResetClearConfirmation()
    {
        _clearResetTimer.Stop();
        _clearArmed = false;
        ClearHistoryLabel.Text = "清空记录";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        WindowDragHelper.BeginDrag(this, e);
    }
}

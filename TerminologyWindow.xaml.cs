using System.Windows;
using System.Windows.Input;

namespace LightTranslate;

public partial class TerminologyWindow : Window
{
    public TerminologyWindow()
    {
        InitializeComponent();
        TermsTextBox.Text = TerminologyStore.ToEditableText(TerminologyStore.Load());
        TermsTextBox.CaretIndex = TermsTextBox.Text.Length;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = TerminologyStore.ParseEditableTextWithDiagnostics(TermsTextBox.Text);
            TerminologyStore.Save(result.Entries);
            var savedCount = result.Entries
                .DistinctBy(entry => entry.Source, StringComparer.OrdinalIgnoreCase)
                .Take(200)
                .Count();

            if (result.InvalidLineNumbers.Count == 0)
            {
                StateText.Text = $"已安全保存 {savedCount} 条术语";
                return;
            }

            var linePreview = string.Join("、", result.InvalidLineNumbers.Take(6));
            if (result.InvalidLineNumbers.Count > 6)
                linePreview += "…";
            StateText.Text = $"已保存 {savedCount} 条 · 第 {linePreview} 行格式错误";
        }
        catch (Exception ex)
        {
            StateText.Text = $"保存失败：{ex.Message}";
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        WindowDragHelper.BeginDrag(this, e);
    }
}

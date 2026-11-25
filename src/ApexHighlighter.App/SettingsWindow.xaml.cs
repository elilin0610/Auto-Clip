using System.Windows;
using System.Windows.Controls;
using ApexHighlighter.Core;

namespace ApexHighlighter.App;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        UiLanguageBox.ItemsSource = Loc.Languages;
        UiLanguageBox.SelectedItem = Loc.Languages.First(l => l.Code == Loc.Current);
    }

    private void UiLanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (UiLanguageBox.SelectedItem is not Loc.Language lang || lang.Code == Loc.Current) return;
        Loc.Set(lang.Code);
        App.Settings.UiLanguage = lang.Code;
        App.Settings.Save();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

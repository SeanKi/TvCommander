using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MPCommander.Model;

namespace MPCommander.Views;

/// <summary>원격 연결 목록: 연결 / 추가 / 편집 / 삭제</summary>
public partial class ConnectionsWindow : Window
{
    private readonly MainWindow _host;

    public ConnectionsWindow(MainWindow host)
    {
        InitializeComponent();
        _host = host;
        Reload(null);
        Loaded += (_, _) =>
        {
            if (List.Items.Count > 0) { List.SelectedIndex = 0; List.Focus(); }
        };
    }

    private RemoteConnection? Selected => List.SelectedItem as RemoteConnection;

    private void Reload(string? select)
    {
        var all = RemoteConnections.All;
        List.ItemsSource = all;
        if (select != null) List.SelectedItem = all.FirstOrDefault(c => c.Name.Equals(select, StringComparison.OrdinalIgnoreCase));
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool has = Selected != null;
        ConnectButton.IsEnabled = EditButton.IsEnabled = DeleteButton.IsEnabled = has;
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } c) return;
        Close();
        _host.OpenPathInActivePanel(c.RootPath);
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (ConnectionEditDialog.Show(this, null) is not { } c) return;
        RemoteConnections.Save(c, null);
        Reload(c.Name);
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } old) return;
        if (ConnectionEditDialog.Show(this, old.Clone()) is not { } c) return;
        RemoteConnections.Save(c, old.Name);
        Reload(c.Name);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } c) return;
        if (MessageBox.Show(this, $"'{c.Name}' ({c.KindText} {c.AddressText}) 연결을 삭제할까요?\n서버의 파일은 지워지지 않습니다.",
                "연결 삭제", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        RemoteConnections.Delete(c.Name);
        Reload(null);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Connect_Click(sender, e);

    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter: Connect_Click(sender, e); e.Handled = true; break;
            case Key.Delete: Delete_Click(sender, e); e.Handled = true; break;
            case Key.Insert: Add_Click(sender, e); e.Handled = true; break;
            case Key.F2 or Key.F4: Edit_Click(sender, e); e.Handled = true; break;
        }
    }
}

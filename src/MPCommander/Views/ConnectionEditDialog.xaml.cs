using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MPCommander.IO;
using MPCommander.Model;

namespace MPCommander.Views;

/// <summary>FTP / FTPS / WebDAV 연결 추가·편집</summary>
public partial class ConnectionEditDialog : Window
{
    private readonly RemoteConnection? _original;

    private ConnectionEditDialog(RemoteConnection? original)
    {
        InitializeComponent();
        _original = original;
        Title = original == null ? "연결 추가" : $"연결 편집 - {original.Name}";

        var c = original ?? new RemoteConnection();
        NameBox.Text = c.Name;
        KindBox.SelectedIndex = (int)c.Kind;
        AddressBox.Text = c.Address;
        PortBox.Text = c.Port > 0 ? c.Port.ToString() : "";
        StartBox.Text = c.StartPath;
        UserBox.Text = c.User;
        PassiveCheck.IsChecked = c.Passive;
        IgnoreCertCheck.IsChecked = c.IgnoreCertErrors;
        PassHint.Visibility = original != null && original.Password.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateKindUi();
        Loaded += (_, _) => (original == null ? NameBox : AddressBox).Focus();
    }

    /// <summary>저장되면 그 연결, 취소하면 null</summary>
    public static RemoteConnection? Show(Window owner, RemoteConnection? existing)
    {
        var dlg = new ConnectionEditDialog(existing) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg.Build() : null;
    }

    private RemoteKind Kind => (RemoteKind)Math.Max(0, KindBox.SelectedIndex);

    private void KindBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateKindUi();

    private void UpdateKindUi()
    {
        if (AddressLabel == null) return;   // InitializeComponent 도중
        bool dav = Kind == RemoteKind.WebDav;
        AddressLabel.Text = dav ? "URL" : "서버";
        AddressHint.Text = dav
            ? "전체 주소 (예: https://app.koofr.net/dav/Koofr, https://nas.local:5006/home)"
            : "서버 이름 또는 IP (예: ftp.example.com, 192.168.0.10)";
        var ftpOnly = dav ? Visibility.Collapsed : Visibility.Visible;
        PortLabel.Visibility = PortBox.Visibility = ftpOnly;
        StartLabel.Visibility = StartBox.Visibility = ftpOnly;
        PassiveCheck.Visibility = ftpOnly;
        IgnoreCertCheck.Visibility = Kind == RemoteKind.Ftp ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>입력값으로 연결을 만든다. 비밀번호를 비우면 기존 비밀번호 유지.</summary>
    private RemoteConnection Build() => new()
    {
        Name = NameBox.Text.Trim(),
        Kind = Kind,
        Address = AddressBox.Text.Trim(),
        Port = int.TryParse(PortBox.Text.Trim(), out var port) ? port : 0,
        StartPath = StartBox.Text.Trim().Trim('/'),
        User = UserBox.Text.Trim(),
        Password = PassBox.Password.Length > 0 ? PassBox.Password : _original?.Password ?? "",
        Passive = PassiveCheck.IsChecked == true,
        IgnoreCertErrors = IgnoreCertCheck.IsChecked == true,
    };

    /// <summary>입력 검사. 문제가 있으면 메시지.</summary>
    private string? Validate(RemoteConnection c, bool checkName)
    {
        if (checkName && RemoteConnections.ValidateName(c.Name, _original?.Name) is { } nameError) return nameError;
        if (c.Address.Length == 0) return c.Kind == RemoteKind.WebDav ? "URL 을 입력하세요." : "서버 주소를 입력하세요.";
        if (c.Kind == RemoteKind.WebDav)
        {
            if (!Uri.TryCreate(c.Address, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
                return "URL 은 http:// 또는 https:// 로 시작해야 합니다.";
        }
        else
        {
            if (c.Address.Contains("://")) return "서버에는 ftp:// 없이 이름이나 IP 만 입력하세요.";
            if (PortBox.Text.Trim().Length > 0 && (c.Port < 1 || c.Port > 65535)) return "포트는 1~65535 입니다.";
        }
        return null;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var c = Build();
        if (Validate(c, checkName: true) is { } error)
        {
            ShowResult(error, ok: false);
            return;
        }
        DialogResult = true;
    }

    /// <summary>저장하지 않고 루트 목록을 읽어 본다 (타임아웃 있음)</summary>
    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        var c = Build();
        if (Validate(c, checkName: false) is { } error)
        {
            ShowResult(error, ok: false);
            return;
        }
        c.Name = "~test";   // 저장된 연결·캐시와 섞이지 않게
        var root = c.RootPath;
        var fs = VirtualFs.For(root);

        TestButton.IsEnabled = false;
        ShowResult("연결하는 중...", ok: true);
        RemoteSupport.InvalidateProbes();
        try
        {
            using (RemoteConnections.Temporary(c))
            {
                var count = await GuardedIo.RunAsync(ctx => fs.List(root, ctx).Count, fs.IdleTimeoutMs, root, onTimeout: () => fs.Abandon(root));
                var space = c.Kind == RemoteKind.WebDav
                    ? await GuardedIo.RunAsync(_ => fs.SpaceInfo(root), fs.IdleTimeoutMs, root)
                    : null;
                var spaceText = space is { } s ? $", 여유 {PathUtil.FormatSize((long)s.Free)} / {PathUtil.FormatSize((long)s.Total)}" : "";
                ShowResult($"연결 성공: 항목 {count}개{spaceText}", ok: true);
            }
        }
        catch (Exception ex)
        {
            ShowResult("연결 실패: " + ex.Message, ok: false);
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void ShowResult(string text, bool ok)
    {
        ResultText.Text = text;
        ResultText.Foreground = ok ? Brushes.DarkGreen : Brushes.Firebrick;
    }
}

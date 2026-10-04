using System.Text;
using System.Windows;
using TvCommander.Native;
using TvCommander.Views;

namespace TvCommander;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "TvCommander 오류", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();

        try
        {
            NativeMethods.FmGetVersion();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            MessageBox.Show("FmCore.dll 을 불러올 수 없습니다. 실행 파일과 같은 폴더에 있는지 확인하세요.\n\n" + ex.Message,
                "TvCommander", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var main = new MainWindow();
        MainWindow = main;
        main.Show();
    }
}

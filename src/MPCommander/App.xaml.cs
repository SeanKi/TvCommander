using System.Text;
using System.Windows;
using MPCommander.Native;
using MPCommander.Views;

namespace MPCommander;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "MP-Commander 오류", MessageBoxButton.OK, MessageBoxImage.Error);
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
                "MP-Commander", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var main = new MainWindow();
        MainWindow = main;
        main.Show();
    }
}

using System.Text;
using System.Windows;
using MPCommander.Native;
using MPCommander.Views;

namespace MPCommander;

public partial class App : Application
{
    // 실행 파일 안에 넣은 라이브러리(AvalonEdit)를 처음 쓸 때 리소스에서 불러온다 — 배포 파일을 늘리지 않으려고
    static App() => AppDomain.CurrentDomain.AssemblyResolve += ResolveEmbedded;

    private static readonly Dictionary<string, System.Reflection.Assembly> Embedded = new(StringComparer.OrdinalIgnoreCase);

    private static System.Reflection.Assembly? ResolveEmbedded(object? sender, ResolveEventArgs args)
    {
        var name = new System.Reflection.AssemblyName(args.Name).Name;
        lock (Embedded)
        {
            if (Embedded.TryGetValue(name, out var loaded)) return loaded;
            using var s = typeof(App).Assembly.GetManifestResourceStream($"Embedded.{name}.dll");
            if (s == null) return null;
            var bytes = new byte[s.Length];
            int read = 0;
            while (read < bytes.Length) read += s.Read(bytes, read, bytes.Length - read);
            var asm = System.Reflection.Assembly.Load(bytes);
            Embedded[name] = asm;
            return asm;
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // 언어: 설정(auto = 운영체제 언어) → lang\<코드>.txt. 창을 만들기 전에 정해야 한다
        Loc.Init(AppSettings.Load().Language);

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, Loc.T("MP-Commander 오류"), MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();

        try
        {
            NativeMethods.FmGetVersion();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            MessageBox.Show(Loc.T("FmCore.dll 을 불러올 수 없습니다. 실행 파일과 같은 폴더에 있는지 확인하세요.\n\n") + ex.Message,
                "MP-Commander", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var main = new MainWindow();
        MainWindow = main;
        main.Show();
    }
}

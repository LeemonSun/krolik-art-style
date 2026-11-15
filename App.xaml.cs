using System.IO;
using System.Windows;

namespace Bestiary;

public partial class App : Application
{
    private FileStream? catalogLock;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            string data = Path.Combine(AppContext.BaseDirectory, "Data");
            Directory.CreateDirectory(data);
            try
            {
                catalogLock = new FileStream(Path.Combine(data, "catalog.lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException error)
            {
                throw new IOException("Каталог занят. Закрой другое окно Bestiary и попробуй снова.", error);
            }
            var library = new Library(AppContext.BaseDirectory);
            if (e.Args.Length == 2 && e.Args[0] == "--verify")
            {
                await library.Verify(e.Args[1]);
                Shutdown();
                return;
            }
            var window = new Gallery(library);
            MainWindow = window;
            window.Show();
            if (e.Args.Length == 2 && e.Args[0] == "--capture")
                await window.Capture(e.Args[1]);
        }
        catch (Exception error)
        {
            if (e.Args.Length == 2)
                File.WriteAllText(e.Args[1] + ".error", error.ToString());
            else
                MessageBox.Show(error.Message, "Bestiary", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        catalogLock?.Dispose();
        base.OnExit(e);
    }
}

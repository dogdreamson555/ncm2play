using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;
using WinRT.Interop;

namespace Ncm.App;

public partial class App : Application
{
    private Window? _window;
    private bool _activationPending;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppStorage.Initialize();
        _window = new MainWindow();
        _window.Activate();

        if (_activationPending)
        {
            _activationPending = false;
            ActivateMainWindow();
        }
    }

    internal void ActivateMainWindow()
    {
        if (_window is null)
        {
            _activationPending = true;
            return;
        }

        WindowActivation.RestoreAndBringToFront(_window);
    }

    private static class WindowActivation
    {
        private const int SwRestore = 9;

        public static void RestoreAndBringToFront(Window window)
        {
            var handle = WindowNative.GetWindowHandle(window);
            if (IsIconic(handle))
            {
                ShowWindow(handle, SwRestore);
            }

            window.Activate();
            SetForegroundWindow(handle);
        }

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);
    }
}

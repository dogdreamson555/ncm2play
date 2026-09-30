using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using WinRT;

namespace Ncm.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ComWrappersSupport.InitializeComWrappers();

        var activatedArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
        var appInstance = AppInstance.FindOrRegisterForKey(SingleInstanceManager.InstanceKey);
        if (!appInstance.IsCurrent)
        {
            AllowSetForegroundWindow(appInstance.ProcessId);
            RedirectActivation(appInstance, activatedArgs);
            return 0;
        }

        using var singleInstance = new SingleInstanceManager(appInstance);
        Application.Start(_ =>
        {
            var dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(dispatcherQueue));
            var app = new App();
            singleInstance.SetActivationHandler(dispatcherQueue, app.ActivateMainWindow);
        });

        return 0;
    }

    private static void RedirectActivation(AppInstance appInstance, AppActivationArguments activatedArgs)
    {
        using var redirectCompleted = new ManualResetEvent(false);
        Exception? redirectException = null;
        var redirectTask = Task.Run(async () =>
        {
            try
            {
                await appInstance.RedirectActivationToAsync(activatedArgs);
            }
            catch (Exception exception)
            {
                redirectException = exception;
            }
            finally
            {
                redirectCompleted.Set();
            }
        });

        var handles = new[] { redirectCompleted.SafeWaitHandle.DangerousGetHandle() };
        var waitResult = CoWaitForMultipleObjects(0, uint.MaxValue, 1, handles, out _);
        redirectTask.GetAwaiter().GetResult();
        Marshal.ThrowExceptionForHR(waitResult);

        if (redirectException is not null)
        {
            ExceptionDispatchInfo.Capture(redirectException).Throw();
        }
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("ole32.dll")]
    private static extern int CoWaitForMultipleObjects(
        uint flags,
        uint timeout,
        uint handleCount,
        [In] IntPtr[] handles,
        out uint index);
}

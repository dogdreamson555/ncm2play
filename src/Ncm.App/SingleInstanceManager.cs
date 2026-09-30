using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;

namespace Ncm.App;

internal sealed class SingleInstanceManager : IDisposable
{
    public const string InstanceKey = "NcmConverter.Main";

    private readonly AppInstance _appInstance;
    private readonly object _sync = new();
    private DispatcherQueue? _dispatcherQueue;
    private Action? _activationHandler;
    private int _pendingActivations;
    private bool _disposed;

    public SingleInstanceManager(AppInstance appInstance)
    {
        _appInstance = appInstance;
        _appInstance.Activated += OnActivated;
    }

    public void SetActivationHandler(DispatcherQueue dispatcherQueue, Action activationHandler)
    {
        int pendingActivations;
        lock (_sync)
        {
            _dispatcherQueue = dispatcherQueue;
            _activationHandler = activationHandler;
            pendingActivations = _pendingActivations;
            _pendingActivations = 0;
        }

        for (var i = 0; i < pendingActivations; i++)
        {
            EnqueueActivation(dispatcherQueue, activationHandler);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _appInstance.Activated -= OnActivated;
        _appInstance.UnregisterKey();
    }

    private void OnActivated(object? sender, AppActivationArguments args)
    {
        DispatcherQueue? dispatcherQueue;
        Action? activationHandler;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            dispatcherQueue = _dispatcherQueue;
            activationHandler = _activationHandler;
            if (dispatcherQueue is null || activationHandler is null)
            {
                _pendingActivations++;
                return;
            }
        }

        EnqueueActivation(dispatcherQueue, activationHandler);
    }

    private static void EnqueueActivation(DispatcherQueue dispatcherQueue, Action activationHandler)
    {
        dispatcherQueue.TryEnqueue(() => activationHandler());
    }
}

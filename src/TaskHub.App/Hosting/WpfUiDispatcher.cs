using System.Windows.Threading;
using TaskHub.Core.Hosting;

namespace TaskHub.App;

internal sealed class WpfUiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public bool CheckAccess() => dispatcher.CheckAccess();

    public void Post(Action action)
    {
        if (dispatcher.HasShutdownStarted) return;
        dispatcher.BeginInvoke(action);
    }

    public T Invoke<T>(Func<T> func)
    {
        if (dispatcher.CheckAccess()) return func();
        if (dispatcher.HasShutdownStarted)
            throw new InvalidOperationException("The UI thread is shutting down.");
        return dispatcher.Invoke(func);
    }
}

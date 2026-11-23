namespace TaskHub.Core.Hosting;

/// <summary>
/// The only way background services touch the UI thread. Implemented over the WPF
/// Dispatcher in the app; tests use an inline implementation.
/// </summary>
public interface IUiDispatcher
{
    bool CheckAccess();

    /// <summary>Queue work on the UI thread and return immediately.</summary>
    void Post(Action action);

    /// <summary>Run on the UI thread and wait for the result (runs inline if already on it).</summary>
    T Invoke<T>(Func<T> func);
}

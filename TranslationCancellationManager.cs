namespace LightTranslate;

public sealed class TranslationCancellationManager : IDisposable
{
    private CancellationTokenSource? _current;
    private bool _disposed;

    public CancellationTokenSource BeginNewRequest()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(TranslationCancellationManager));

        var next = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _current, next);
        CancelSafely(previous);
        return next;
    }

    public void CompleteRequest(CancellationTokenSource request)
    {
        Interlocked.CompareExchange(ref _current, null, request);
        request.Dispose();
    }

    public bool CancelCurrent()
    {
        var current = Volatile.Read(ref _current);
        if (current is null)
            return false;

        return CancelSafely(current);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        var current = Interlocked.Exchange(ref _current, null);
        CancelSafely(current);
        current?.Dispose();
    }

    private static bool CancelSafely(CancellationTokenSource? source)
    {
        if (source is null)
            return false;

        try
        {
            source.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }
}

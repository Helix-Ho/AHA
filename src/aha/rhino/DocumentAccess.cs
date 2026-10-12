using System.Collections.Concurrent;
using Rhino;

namespace Aha.Runtime;

public sealed class DocumentBusyException() : InvalidOperationException("이 문서에서 AHA 변경 작업이 진행 중입니다. 완료 후 다시 실행하세요.");

/// <summary>One mutation per live document. No persistent identifiers or model registry.</summary>
public static class DocumentAccess
{
    private static readonly ConcurrentDictionary<uint, byte> Busy = new();

    public static RhinoDoc Get(uint serial) => RhinoDoc.FromRuntimeSerialNumber(serial)
        ?? throw new InvalidOperationException("대상 Rhino 문서가 닫혔습니다. 문서 목록을 다시 조회하세요.");

    public static IDisposable Enter(RhinoDoc doc)
    {
        var serial = doc.RuntimeSerialNumber;
        Get(serial);
        if (!Busy.TryAdd(serial, 0))
            throw new DocumentBusyException();
        return new Lease(serial);
    }

    private sealed class Lease(uint serial) : IDisposable
    {
        private int disposed;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) Busy.TryRemove(serial, out _); }
    }
}

public static class RhinoDispatch
{
    public static Task<T> InvokeAsync<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        RhinoApp.InvokeOnUiThread((Action)(() =>
        {
            try { completion.TrySetResult(action()); }
            catch (Exception error) { completion.TrySetException(error); }
        }));
        return completion.Task;
    }
}

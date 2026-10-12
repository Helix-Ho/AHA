using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Aha.Runtime;

/// <summary>Owns a detached result until explicit creation or disposal.</summary>
internal abstract class PreparedJob : IDisposable
{
    public abstract JsonNode Summary { get; }
    public abstract Task<JsonNode?> CreateAsync();
    public abstract void Dispose();
}

/// <summary>A temporary creation conflict: retain the prepared result for retry.</summary>
internal sealed class CreateDeferredException(string message) : InvalidOperationException(message);

/// <summary>Process-local execution records; prepared geometry is never serialized.</summary>
internal static class Jobs
{
    private sealed class Job(string id, uint doc, string operation, bool prepared, JsonNode? input, TimeProvider timeProvider)
    {
        public readonly object Sync = new();
        public readonly string Id = id;
        public readonly uint Document = doc;
        public readonly string Operation = operation;
        public readonly bool IsPrepared = prepared;
        public readonly JsonNode? Input = input?.DeepClone();
        public readonly CancellationTokenSource Cancellation = prepared ? new() : new(TimeSpan.FromMinutes(10), timeProvider);
        public string? StopReason;
        public DateTimeOffset? Finished;
        public string State = "running";
        public string Progress = "시작";
        public JsonNode? Result;
        public JsonNode? Preparation;
        public PreparedJob? Owned;
        public string? Error;
        public bool OwnerClosed;
        public bool CancellationRequested;
    }

    private static readonly ConcurrentDictionary<string, Job> Records = new();
    private static readonly object Registration = new();
    private static bool shuttingDown;
    public static event Action<uint>? Changed;

    public static JsonObject Start(uint document, string operation, Func<CancellationToken, IProgress<string>, Task<JsonNode?>> action, TimeProvider? timeProvider = null)
    {
        var job = Register(document, operation, false, null, timeProvider);
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await action(job.Cancellation.Token, new JobProgress(job)).ConfigureAwait(false);
                // A write that returned successfully is not rewritten by a late cancellation.
                lock (job.Sync) { job.Result = result?.DeepClone(); Finish(job, "completed", "완료"); }
            }
            catch (OperationCanceledException) { lock (job.Sync) Finish(job, "cancelled", StopMessage(job)); }
            catch (Exception error) { lock (job.Sync) { job.Error = UserError.Message(error); Finish(job, "failed", "실패"); } }
            finally { job.Cancellation.Dispose(); Publish(job); }
        });
        Notify(document);
        return Status(job.Id);
    }

    public static JsonObject StartPrepared(uint document, string operation,
        Func<CancellationToken, IProgress<string>, Task<PreparedJob>> action, JsonNode? input = null, TimeProvider? timeProvider = null)
    {
        var job = Register(document, operation, true, input, timeProvider);
        _ = Task.Run(async () =>
        {
            PreparedJob? result = null;
            try
            {
                result = await action(job.Cancellation.Token, new JobProgress(job)).ConfigureAwait(false);
                var summary = result.Summary.DeepClone();
                lock (job.Sync)
                {
                    // Native calls may return after cancellation. Never publish that geometry.
                    if (job.CancellationRequested) throw new OperationCanceledException(job.Cancellation.Token);
                    job.Cancellation.Token.ThrowIfCancellationRequested();
                    job.Owned = result; result = null;
                    job.Preparation = summary;
                    job.State = "ready"; job.Progress = "준비 완료 · 생성 가능";
                }
            }
            catch (OperationCanceledException) { lock (job.Sync) Finish(job, "cancelled", StopMessage(job)); }
            catch (Exception error) { lock (job.Sync) { job.Error = UserError.Message(error); Finish(job, "failed", "실패"); } }
            finally
            {
                result?.Dispose();
                job.Cancellation.Dispose();
                Publish(job);
            }
        });
        Notify(document);
        return Status(job.Id);
    }

    public static JsonObject Create(string id)
    {
        var job = Find(id);
        PreparedJob prepared;
        lock (job.Sync)
        {
            if (!job.IsPrepared) throw new ArgumentException("생성 가능한 Topo 준비 작업이 아닙니다.");
            if (job.State is "creating" or "completed") return Snapshot(job);
            if (job.State != "ready" || job.Owned is null)
                throw new InvalidOperationException("준비 완료된 작업만 생성할 수 있습니다.");
            prepared = job.Owned;
            job.State = "creating"; job.Progress = "Rhino 모델 추가"; job.Error = null;
        }
        _ = Task.Run(async () =>
        {
            bool release = true;
            try
            {
                var result = await prepared.CreateAsync().ConfigureAwait(false);
                lock (job.Sync) { job.Result = result?.DeepClone(); Finish(job, "completed", "생성 완료"); }
            }
            catch (CreateDeferredException error)
            {
                lock (job.Sync)
                {
                    if (job.OwnerClosed) Finish(job, "cancelled", "대상 문서 또는 AHA가 종료되었습니다");
                    else { job.State = "ready"; job.Progress = error.Message; release = false; }
                }
            }
            catch (Exception error)
            {
                lock (job.Sync) { job.Error = UserError.Message(error); Finish(job, "failed", "생성 실패"); }
            }
            finally
            {
                if (release)
                {
                    lock (job.Sync) job.Owned = null;
                    prepared.Dispose();
                }
                Publish(job);
            }
        });
        Notify(job.Document);
        return Status(id);
    }

    public static JsonObject Status(string id)
    {
        var job = Find(id);
        lock (job.Sync) return Snapshot(job);
    }

    public static JsonObject? CurrentPrepared(uint document)
    {
        lock (Registration)
            foreach (var job in Records.Values.Where(value => value.IsPrepared && value.Document == document))
                lock (job.Sync) if (!Terminal(job)) return Snapshot(job);
        return null;
    }

    public static JsonObject Cancel(string id)
    {
        var job = Find(id);
        Cancel(job);
        return Status(id);
    }

    public static void CancelDocument(uint document)
    {
        foreach (var job in Records.Values.Where(value => value.Document == document)) Cancel(job, true);
    }

    // Stopping a transport does not own or cancel a document's Topo preparation.
    public static void CancelTransient()
    {
        foreach (var job in Records.Values.Where(value => !value.IsPrepared)) Cancel(job);
    }

    public static void Shutdown()
    {
        lock (Registration) shuttingDown = true;
        foreach (var job in Records.Values) Cancel(job, true);
    }

    private static Job Register(uint document, string operation, bool prepared, JsonNode? input, TimeProvider? timeProvider = null)
    {
        lock (Registration)
        {
            if (shuttingDown) throw new InvalidOperationException("AHA가 종료 중입니다.");
            TrimHistory();
            int active = 0;
            foreach (var existing in Records.Values)
                lock (existing.Sync)
                {
                    if (Terminal(existing)) continue;
                    active++;
                    if (prepared && existing.IsPrepared && existing.Document == document)
                        throw new InvalidOperationException($"이 문서에 Topo 작업이 있습니다: {existing.Id}. 생성하거나 취소·폐기한 뒤 다시 연산하세요.");
                }
            if (active >= 128) throw new InvalidOperationException("실행·준비 작업 한도에 도달했습니다. 기존 작업을 완료하거나 취소하세요.");
            var job = new Job(Guid.NewGuid().ToString("N"), document, operation, prepared, input, timeProvider ?? TimeProvider.System);
            job.Cancellation.Token.Register(() =>
            {
                lock(job.Sync) job.StopReason ??= job.IsPrepared ? "연산이 취소되었습니다." : "연산 시간 제한을 초과했습니다.";
            });
            Records[job.Id] = job;
            return job;
        }
    }

    private static void Cancel(Job job, bool ownerClosed = false)
    {
        PreparedJob? released = null;
        CancellationTokenSource? cancellation = null;
        lock (job.Sync)
        {
            job.OwnerClosed |= ownerClosed;
            if (job.State == "running")
            {
                job.StopReason ??= !job.IsPrepared && job.Cancellation.IsCancellationRequested ? "연산 시간 제한을 초과했습니다." :
                    ownerClosed ? "대상 문서 또는 AHA가 종료되었습니다." : "사용자 요청으로 연산을 취소했습니다.";
                job.CancellationRequested = true;
                cancellation = job.Cancellation;
                job.Progress = "취소 요청됨 · 안전한 처리 지점에서 중단합니다";
            }
            else if (job.State == "ready")
            {
                released = job.Owned; job.Owned = null;
                Finish(job, "cancelled", ownerClosed ? "대상 문서 또는 AHA가 종료되었습니다." : "준비 결과를 폐기했습니다.");
            }
            // Once creating starts, finish the atomic commit or its rollback.
        }
        // Cancellation invokes external callbacks. Do not hold job/registration locks.
        if (cancellation is not null)
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { /* Work finished after the request was recorded. */ }
        released?.Dispose();
        Publish(job);
    }

    private static void Finish(Job job, string state, string progress)
    {
        job.State = state; job.Progress = progress; job.Finished = DateTimeOffset.UtcNow;
    }

    private static string StopMessage(Job job) => job.StopReason ?? (!job.IsPrepared && job.Cancellation.IsCancellationRequested
        ? "연산 시간 제한을 초과했습니다." : "연산이 취소되었습니다.");
    private static bool Terminal(Job job) => job.State is "completed" or "failed" or "cancelled";
    private static Job Find(string id) => Records.TryGetValue(id, out var job) ? job
        : throw new ArgumentException("작업이 없거나 종료 기록이 정리되었습니다. 현재 작업을 다시 조회하세요.");

    private static JsonObject Snapshot(Job job) => new()
    {
        ["job_id"] = job.Id, ["document_id"] = job.Document, ["operation"] = job.Operation,
        ["state"] = job.State, ["progress"] = job.Progress,
        ["input"] = job.Input?.DeepClone(), ["summary"] = job.Preparation?.DeepClone(),
        ["result"] = job.Result?.DeepClone(), ["error"] = job.Error
    };

    private static void Publish(Job job)
    {
        lock (Registration) TrimHistory();
        Notify(job.Document);
    }

    private static void TrimHistory()
    {
        var terminal = new List<(Job Job, DateTimeOffset Finished)>();
        foreach (var job in Records.Values)
            lock (job.Sync) if (Terminal(job) && job.Finished is { } finished) terminal.Add((job, finished));
        int retained = 0;
        foreach (var item in terminal.OrderByDescending(value => value.Finished))
            if (++retained > 128 || DateTimeOffset.UtcNow - item.Finished > TimeSpan.FromHours(1))
                Records.TryRemove(item.Job.Id, out _);
    }

    private static void Notify(uint document)
    {
        if (Changed is not { } handlers) return;
        foreach (Action<uint> handler in handlers.GetInvocationList())
            try { handler(document); }
            catch { /* A detached panel observer must not change the job outcome. */ }
    }

    private sealed class JobProgress(Job job) : IProgress<string>
    {
        public void Report(string value)
        {
            lock (job.Sync) if (job.State == "running" && job.StopReason is null) job.Progress = value;
            Notify(job.Document);
        }
    }
}

internal static class UserError
{
    public static string Message(Exception error) => error is ArgumentException or InvalidOperationException or InvalidDataException or NotSupportedException
        ? error.Message : "작업 실패: " + error.GetType().Name + ". 연결과 입력을 확인하세요.";
}

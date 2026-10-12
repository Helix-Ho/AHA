using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Aha.Runtime;

internal static class JobChecks
{
    private static string Id(JsonObject job) => job["job_id"]!.GetValue<string>();
    private static string State(string id) => Jobs.Status(id)["state"]!.GetValue<string>();
    private static void Require(bool value, string name) { if (!value) throw new Exception(name); }
    private static async Task WaitFor(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(5, deadline.Token);
    }
    private static async Task<JsonObject> Finish(string id)
    {
        await WaitFor(() => State(id) is not ("running" or "creating"));
        return Jobs.Status(id);
    }
    private static TaskCompletionSource<bool> Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Result(Func<Task<JsonNode?>>? create = null) : PreparedJob
    {
        public readonly JsonObject Data = new() { ["prepared_objects"] = 4 };
        public int Creates;
        public int Disposes;
        public override JsonNode Summary => Data;
        public override async Task<JsonNode?> CreateAsync()
        {
            Interlocked.Increment(ref Creates);
            return create is null ? new JsonObject { ["object_ids"] = new JsonArray("created") } : await create();
        }
        public override void Dispose() => Interlocked.Increment(ref Disposes);
    }

    // Only the job deadline boundary uses this deterministic clock.
    private sealed class ManualClock : TimeProvider
    {
        private TimeSpan elapsed;
        private readonly List<ClockTimer> timers = new();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ClockTimer(this, callback, state);
            timers.Add(timer); timer.Change(dueTime, period); return timer;
        }
        public void Advance(TimeSpan delta)
        {
            elapsed += delta;
            foreach (var timer in timers.ToArray()) timer.Fire(elapsed);
        }
        private sealed class ClockTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            private TimeSpan? due;
            private TimeSpan period;
            private bool disposed;
            public bool Change(TimeSpan dueTime, TimeSpan repeat)
            {
                if (disposed) return false;
                due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.elapsed + dueTime;
                period = repeat; return true;
            }
            public void Fire(TimeSpan now)
            {
                if (disposed || due is not { } at || at > now) return;
                due = period == Timeout.InfiniteTimeSpan ? null : now + period;
                callback(state);
            }
            public void Dispose() { disposed = true; due = null; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    internal static async Task RunAsync()
    {
        var done = Id(Jobs.Start(10, "test", (_, progress) =>
        { progress.Report("processing"); return Task.FromResult<JsonNode?>(new JsonObject { ["ok"] = true }); }));
        var completed = await Finish(done);
        Require(completed["state"]!.GetValue<string>() == "completed" && completed["result"]!["ok"]!.GetValue<bool>(), "completed job result");
        completed["result"]!["ok"] = false;
        Require(Jobs.Status(done)["result"]!["ok"]!.GetValue<bool>(), "job snapshots detach mutable JSON");
        Require(Jobs.Cancel(done)["state"]!.GetValue<string>() == "completed", "late cancellation does not rewrite a committed result");
        var pending = Id(Jobs.Start(11, "test", async (ct, _) => { await Task.Delay(Timeout.Infinite, ct); return null; }));
        Jobs.Cancel(pending);
        Require((await Finish(pending))["state"]!.GetValue<string>() == "cancelled", "cancelled lookup terminates");
        Require(Jobs.Status(pending)["progress"]!.GetValue<string>().Contains("사용자 요청"), "explicit cancellation reports its cause");
        var clock = new ManualClock();
        var lookupEntered = Gate(); var prepareEntered = Gate(); var prepareRelease = Gate();
        var timed = Id(Jobs.Start(13, "eum-crawl", async (ct, _) =>
        { lookupEntered.SetResult(true); await Task.Delay(Timeout.Infinite, ct); return null; }, clock));
        var retainedBeyondDeadline = new Result();
        var noDeadline = Id(Jobs.StartPrepared(14, "topo", async (ct, _) =>
        { prepareEntered.SetResult(true); await prepareRelease.Task; ct.ThrowIfCancellationRequested(); return retainedBeyondDeadline; }, timeProvider: clock));
        await Task.WhenAll(lookupEntered.Task, prepareEntered.Task);
        clock.Advance(TimeSpan.FromMinutes(11));
        var timedStatus = await Finish(timed);
        Require(timedStatus["state"]!.GetValue<string>() == "cancelled" &&
            timedStatus["progress"]!.GetValue<string>().Contains("시간 제한") && State(noDeadline) == "running",
            "lookup deadline does not cancel Topo preparation at the same elapsed time");
        prepareRelease.SetResult(true); await Finish(noDeadline);
        clock.Advance(TimeSpan.FromDays(1));
        Require(State(noDeadline) == "ready" && retainedBeyondDeadline.Disposes == 0,
            "ready preparation has no timer or automatic expiry");
        Jobs.Cancel(noDeadline);
        Require(retainedBeyondDeadline.Disposes == 1 && Jobs.Status(noDeadline)["progress"]!.GetValue<string>().Contains("폐기"),
            "explicit discard after a long preparation preserves its cause");
        var failed = Id(Jobs.Start(12, "test", (_, _) => throw new HttpRequestException("private credential")));
        var failure = await Finish(failed);
        Require(failure["state"]!.GetValue<string>() == "failed" && !failure.ToJsonString().Contains("private credential"), "unexpected failures sanitized");

        var prepareGate = Gate(); var createGate = Gate();
        var prepared = new Result(async () => { await createGate.Task; return new JsonObject { ["object_ids"] = new JsonArray("created") }; });
        var input = new JsonObject { ["width_m"] = 400 };
        var main = Id(Jobs.StartPrepared(30, "topo", async (_, _) => { await prepareGate.Task; return prepared; }, input));
        input["width_m"] = 900;
        Require(Jobs.CurrentPrepared(30)?["job_id"]?.GetValue<string>() == main && Jobs.Status(main)["input"]!["width_m"]!.GetValue<int>() == 400,
            "document reservation and input are fixed before preparation");
        bool earlyRejected = false;
        try { Jobs.Create(main); } catch (InvalidOperationException) { earlyRejected = true; }
        Require(earlyRejected && prepared.Creates == 0, "creation before ready changes nothing");
        bool overlapRejected = false;
        try { Jobs.StartPrepared(30, "topo", (_, _) => Task.FromResult<PreparedJob>(new Result())); }
        catch (InvalidOperationException error) { overlapRejected = error.Message.Contains(main, StringComparison.Ordinal); }
        Require(overlapRejected, "a second preparation is rejected with the current job ID");
        prepareGate.SetResult(true);
        var ready = await Finish(main);
        Require(ready["state"]!.GetValue<string>() == "ready" && ready["result"] is null && prepared.Creates == 0 && prepared.Disposes == 0,
            "ready retains geometry without creation or object result");
        ready["summary"]!["prepared_objects"] = 0; prepared.Data["prepared_objects"] = 99;
        Require(Jobs.Status(main)["summary"]!["prepared_objects"]!.GetValue<int>() == 4, "ready summary is an immutable JSON snapshot");
        var transient = Id(Jobs.Start(30, "eum-crawl", async (ct, _) => { await Task.Delay(Timeout.Infinite, ct); return null; }));
        Jobs.CancelTransient();
        Require((await Finish(transient))["state"]!.GetValue<string>() == "cancelled" && State(main) == "ready" && prepared.Disposes == 0,
            "transport stop cancels lookups but preserves prepared Topo");
        Parallel.For(0, 24, _ => Jobs.Create(main));
        await WaitFor(() => prepared.Creates == 1);
        Require(State(main) == "creating" && Jobs.Cancel(main)["state"]!.GetValue<string>() == "creating", "creation is single and noncancellable");
        createGate.SetResult(true); await Finish(main); await WaitFor(() => prepared.Disposes == 1);
        Require(Jobs.Create(main)["state"]!.GetValue<string>() == "completed" && prepared.Creates == 1 && Jobs.CurrentPrepared(30) is null,
            "completed creation is idempotent and releases the document reservation");

        var lateGate = Gate(); var late = new Result();
        var lateId = Id(Jobs.StartPrepared(31, "topo", async (_, _) => { await lateGate.Task; return late; }));
        Jobs.Cancel(lateId); lateGate.SetResult(true); await Finish(lateId); await WaitFor(() => late.Disposes == 1);
        Require(State(lateId) == "cancelled" && late.Creates == 0 && Jobs.CurrentPrepared(31) is null,
            "late native result is disposed after accepted cancellation");
        int attempts = 0;
        var deferred = new Result(() => ++attempts == 1 ? throw new CreateDeferredException("busy") : Task.FromResult<JsonNode?>(new JsonObject()));
        var deferredId = Id(Jobs.StartPrepared(32, "topo", (_, _) => Task.FromResult<PreparedJob>(deferred)));
        await Finish(deferredId); Jobs.Create(deferredId); await Finish(deferredId);
        Require(State(deferredId) == "ready" && deferred.Disposes == 0, "busy document preserves ready geometry");
        Jobs.Create(deferredId); await Finish(deferredId); await WaitFor(() => deferred.Disposes == 1);
        Require(State(deferredId) == "completed" && deferred.Creates == 2, "deferred creation can be retried once available");
        var badCreate = new Result(() => throw new HttpRequestException("private credential"));
        var badCreateId = Id(Jobs.StartPrepared(33, "topo", (_, _) => Task.FromResult<PreparedJob>(badCreate)));
        await Finish(badCreateId); Jobs.Create(badCreateId); var bad = await Finish(badCreateId); await WaitFor(() => badCreate.Disposes == 1);
        Require(State(badCreateId) == "failed" && !bad.ToJsonString().Contains("private credential") && Jobs.CurrentPrepared(33) is null,
            "creation failure releases owned geometry and sanitizes unexpected errors");
        var discarded = new Result();
        var discardId = Id(Jobs.StartPrepared(34, "topo", (_, _) => Task.FromResult<PreparedJob>(discarded)));
        await Finish(discardId); Jobs.Cancel(discardId); Jobs.Cancel(discardId);
        Require(State(discardId) == "cancelled" && discarded.Disposes == 1, "ready discard disposes exactly once");
        Require(Jobs.Status(discardId)["progress"]!.GetValue<string>().Contains("폐기"), "ready discard keeps its distinct cause");
        var closed = new Result();
        var closeId = Id(Jobs.StartPrepared(35, "topo", (_, _) => Task.FromResult<PreparedJob>(closed)));
        await Finish(closeId); Jobs.CancelDocument(35);
        Require(State(closeId) == "cancelled" && closed.Disposes == 1, "document close releases ready geometry");
        Require(Jobs.Status(closeId)["progress"]!.GetValue<string>().Contains("종료"), "document closure reports its cause");
        var closingGate = Gate();
        var closing = new Result(async () => { await closingGate.Task; throw new CreateDeferredException("busy"); });
        var closingId = Id(Jobs.StartPrepared(36, "topo", (_, _) => Task.FromResult<PreparedJob>(closing)));
        await Finish(closingId); Jobs.Create(closingId); await WaitFor(() => closing.Creates == 1); Jobs.CancelDocument(36); closingGate.SetResult(true);
        await Finish(closingId); await WaitFor(() => closing.Disposes == 1);
        Require(State(closingId) == "cancelled", "closed document cannot regain ready after a deferred creation");

        var raceGate = Gate(); var raced = new ConcurrentBag<string>(); var refusal = new ConcurrentBag<string>();
        Parallel.For(0, 24, _ =>
        {
            try { raced.Add(Id(Jobs.StartPrepared(37, "topo", async (ct, _) => { await raceGate.Task.WaitAsync(ct); return new Result(); }))); }
            catch (InvalidOperationException error) { refusal.Add(error.Message); }
        });
        Require(raced.Count == 1 && refusal.All(message => message.Contains(raced.Single(), StringComparison.Ordinal)), "document reservation is atomic across concurrent starts");
        Jobs.Cancel(raced.Single()); await Finish(raced.Single());
        var callbackRegistered = Gate(); var callbackRead = Gate();
        var callbackId = Id(Jobs.StartPrepared(39, "topo", async (ct, _) =>
        {
            using var registration = ct.Register(() => callbackRead.TrySetResult(
                Task.Run(() => Jobs.CurrentPrepared(39)).Wait(TimeSpan.FromSeconds(2))));
            callbackRegistered.SetResult(true);
            await Task.Delay(Timeout.Infinite, ct);
            return new Result();
        }));
        await callbackRegistered.Task; Jobs.Cancel(callbackId); await Finish(callbackId);
        Require(await callbackRead.Task, "cancellation callbacks can query work without waiting on a held job lock");
        var retained = new Result();
        var retainedId = Id(Jobs.StartPrepared(38, "topo", (_, _) => Task.FromResult<PreparedJob>(retained)));
        await Finish(retainedId);
        for (int i = 0; i < 140; i++) await Finish(Id(Jobs.Start(40, "history", (_, _) => Task.FromResult<JsonNode?>(null))));
        Require(State(retainedId) == "ready" && retained.Disposes == 0, "terminal JSON retention does not evict prepared geometry");
        Jobs.Cancel(retainedId);

        var accepted = new ConcurrentBag<string>();
        Parallel.For(0, 256, new ParallelOptions { MaxDegreeOfParallelism = 32 }, i =>
        {
            try { accepted.Add(Id(Jobs.Start((uint)(100 + i), "capacity", async (ct, _) => { await Task.Delay(Timeout.Infinite, ct); return null; }))); }
            catch (InvalidOperationException) { }
        });
        Require(accepted.Count == 128, "concurrent registration enforces active-work budget independently of history");
        foreach (var id in accepted) { Jobs.Cancel(id); await Finish(id); }
        var shutdownReady = new Result();
        var shutdownId = Id(Jobs.StartPrepared(500, "topo", (_, _) => Task.FromResult<PreparedJob>(shutdownReady)));
        await Finish(shutdownId);
        var shutdownRunning = Id(Jobs.StartPrepared(501, "topo", async (ct, _) => { await Task.Delay(Timeout.Infinite, ct); return new Result(); }));
        Jobs.Shutdown(); await Finish(shutdownRunning);
        Require(State(shutdownId) == "cancelled" && shutdownReady.Disposes == 1 && State(shutdownRunning) == "cancelled", "plugin shutdown releases running and ready work");
        Console.WriteLine("Job checks passed: lookup lifecycle; prepared ownership, isolation, cancellation, retry, idempotent creation, history and shutdown.");
    }
}

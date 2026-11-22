using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NeoRadar.PluginSdk.Logger;

namespace Viff.NeoRadar.Common;

/// <summary>
/// Runs periodic and one-off work on the thread pool, driven by the host's once-a-second timer.
/// </summary>
/// <remarks>
/// A periodic job is due again one period after its work finishes, so it never overlaps itself. Nothing starts after
/// <see cref="Stop"/>, which waits a bounded time for work in flight.
/// </remarks>
internal sealed class JobScheduler
{
    private readonly TimeProvider _clock;
    private readonly ILoggerApi _logger;
    private readonly CancellationTokenSource _stopping = new();

    // Guards _jobs, _inFlight and _stopped; taken by the timer thread, the pool and event threads.
    private readonly object _lock = new();
    private readonly List<PeriodicJob> _jobs = new();
    private readonly HashSet<Task> _inFlight = new();
    private bool _stopped;

    public JobScheduler(TimeProvider clock, ILoggerApi logger)
    {
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Registers work that runs every <paramref name="period"/>, first on the next tick.
    /// </summary>
    public PeriodicJob Every(string name, TimeSpan period, Func<CancellationToken, Task> work)
    {
        PeriodicJob job = new(name, period, work, _clock.GetUtcNow());
        lock (_lock)
        {
            _jobs.Add(job);
        }

        return job;
    }

    /// <summary>
    /// Makes a periodic job due on the next tick.
    /// </summary>
    public void TriggerSoon(PeriodicJob job)
    {
        lock (_lock)
        {
            job.DueAt = _clock.GetUtcNow();
        }
    }

    /// <summary>
    /// Runs work once on the thread pool now, unless the scheduler has stopped.
    /// </summary>
    public void RunOnce(string name, Func<CancellationToken, Task> work)
    {
        lock (_lock)
        {
            if (_stopped)
            {
                return;
            }

            Start(name, work, onFinished: null);
        }
    }

    /// <summary>
    /// Starts every periodic job that is due and not already running. Called from the host timer.
    /// </summary>
    public void Tick()
    {
        DateTimeOffset now = _clock.GetUtcNow();
        lock (_lock)
        {
            if (_stopped)
            {
                return;
            }

            foreach (PeriodicJob job in _jobs)
            {
                if (job.IsRunning || job.DueAt > now)
                {
                    continue;
                }

                job.IsRunning = true;
                PeriodicJob started = job;
                Start(job.Name, job.Work, () =>
                {
                    started.IsRunning = false;
                    started.DueAt = _clock.GetUtcNow() + started.Period;
                });
            }
        }
    }

    /// <summary>
    /// Waits up to <paramref name="timeout"/> until no work is in flight, and returns whether it got there.
    /// </summary>
    public bool WaitForIdle(TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            Task[] running;
            lock (_lock)
            {
                running = _inFlight.ToArray();
            }

            TimeSpan left = deadline - DateTimeOffset.UtcNow;
            if (running.Length == 0)
            {
                return true;
            }

            if (left <= TimeSpan.Zero)
            {
                return false;
            }

            try
            {
                Task.WaitAll(running, left);
            }
            catch (AggregateException)
            {
                // Each job logs its own failure.
            }
        }
    }

    /// <summary>
    /// Stops starting work, cancels work in flight and waits up to <paramref name="wait"/> for it to finish.
    /// </summary>
    public void Stop(TimeSpan wait)
    {
        Task[] running;
        lock (_lock)
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            running = _inFlight.ToArray();
        }

        _stopping.Cancel();
        try
        {
            if (!Task.WaitAll(running, wait))
            {
                _logger.Warning($"{running.Count(t => !t.IsCompleted)} background job(s) were still running at shutdown");
            }
        }
        catch (AggregateException)
        {
            // Each job logs its own failure; a cancelled job is expected here.
        }
    }

    // Must be called under _lock.
    private void Start(string name, Func<CancellationToken, Task> work, Action? onFinished)
    {
        CancellationToken token = _stopping.Token;
        Task task = Task.Run(async () =>
        {
            try
            {
                await work(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.Error($"[{name}] {ex}");
            }
            finally
            {
                lock (_lock)
                {
                    onFinished?.Invoke();
                }
            }
        });
        _inFlight.Add(task);
        task.ContinueWith(t =>
        {
            lock (_lock)
            {
                _inFlight.Remove(t);
            }
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Represents one periodic job registered with <see cref="Every"/>.
    /// </summary>
    internal sealed class PeriodicJob
    {
        public PeriodicJob(string name, TimeSpan period, Func<CancellationToken, Task> work, DateTimeOffset dueAt)
        {
            Name = name;
            Period = period;
            Work = work;
            DueAt = dueAt;
        }

        public string Name { get; }

        public TimeSpan Period { get; }

        public Func<CancellationToken, Task> Work { get; }

        public DateTimeOffset DueAt { get; set; }

        public bool IsRunning { get; set; }
    }
}

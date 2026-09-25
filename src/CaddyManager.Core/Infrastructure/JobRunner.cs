using System.Collections.Concurrent;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Models;
using Microsoft.Extensions.Logging;

namespace CaddyManager.Core.Infrastructure;

public sealed class JobRunner(ILogger<JobRunner> logger) : IJobRunner
{
    private sealed class Job
    {
        public required string Id;
        public required string Kind;
        public required string Title;
        public JobState State = JobState.Running;
        public DateTime StartedAt = DateTime.UtcNow;
        public DateTime? FinishedAt;
        public readonly List<string> Log = new();
        public string? Error;

        public JobInfo Snapshot()
        {
            lock (Log)
                return new JobInfo
                {
                    Id = Id, Kind = Kind, Title = Title, State = State, StartedAt = StartedAt,
                    FinishedAt = FinishedAt, Log = Log.ToList(), Error = Error,
                };
        }
    }

    private readonly ConcurrentDictionary<string, Job> _jobs = new();

    public JobInfo Start(string kind, string title, Func<Action<string>, CancellationToken, Task> work)
    {
        var job = new Job { Id = Entity.NewId(), Kind = kind, Title = title };
        _jobs[job.Id] = job;
        void Log(string line)
        {
            lock (job.Log) job.Log.Add($"{DateTime.UtcNow:HH:mm:ss} {line}");
            logger.LogInformation("[job {Kind}] {Line}", kind, line);
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await work(Log, CancellationToken.None);
                job.State = JobState.Succeeded;
            }
            catch (Exception ex)
            {
                job.Error = ex.Message;
                Log("ERROR: " + ex.Message);
                job.State = JobState.Failed;
                logger.LogError(ex, "Job {Kind} failed", kind);
            }
            finally
            {
                job.FinishedAt = DateTime.UtcNow;
                Prune();
            }
        });
        return job.Snapshot();
    }

    public JobInfo? Get(string id) => _jobs.TryGetValue(id, out var j) ? j.Snapshot() : null;

    public IReadOnlyList<JobInfo> Recent(int take = 20) =>
        _jobs.Values.OrderByDescending(j => j.StartedAt).Take(take).Select(j => j.Snapshot()).ToList();

    public bool IsRunning(string kind) => _jobs.Values.Any(j => j.Kind == kind && j.State == JobState.Running);

    private void Prune()
    {
        foreach (var old in _jobs.Values.OrderByDescending(j => j.StartedAt).Skip(50))
            _jobs.TryRemove(old.Id, out _);
    }
}

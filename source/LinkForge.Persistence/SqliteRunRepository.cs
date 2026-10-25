using Microsoft.EntityFrameworkCore;

namespace LinkForge;

public interface IRunRepository
{
    Task<IReadOnlyList<string>> LoadRunsAsync(CancellationToken cancellationToken = default);

    /// <summary>Atomically persists the run snapshot and appends its audit event.</summary>
    Task SaveRunAndAuditAsync(string runId, string scenarioId, string status, int requirementVersion,
        DateTimeOffset startedAt, DateTimeOffset updatedAt, string payload, WorkflowAuditEntity auditEvent,
        CancellationToken cancellationToken = default);

    Task AddAgentCallAsync(AgentCallEntity call, CancellationToken cancellationToken = default);
    Task AddArtifactAsync(ArtifactEntity artifact, CancellationToken cancellationToken = default);
    Task AddValidationsAsync(IEnumerable<ValidationEntity> validations, CancellationToken cancellationToken = default);
    Task AddApprovalAsync(ApprovalEntity approval, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkflowAuditEntity>> GetAuditAsync(string runId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AgentCallEntity>> GetAgentCallsAsync(string runId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ArtifactEntity>> GetArtifactsAsync(string runId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ValidationEntity>> GetValidationsAsync(string runId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ApprovalEntity>> GetApprovalsAsync(string runId, CancellationToken cancellationToken = default);
}

public sealed class SqliteRunRepository(IDbContextFactory<LinkForgeDbContext> dbFactory)
    : IRunRepository
{
    public async Task<IReadOnlyList<string>> LoadRunsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.WorkflowRuns.AsNoTracking().OrderBy(run => run.StartedAt)
            .Select(run => run.Payload).ToArrayAsync(cancellationToken);
    }

    public async Task SaveRunAndAuditAsync(
        string runId, string scenarioId, string status, int requirementVersion,
        DateTimeOffset startedAt, DateTimeOffset updatedAt, string payload, WorkflowAuditEntity auditEvent,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var run = await db.WorkflowRuns.SingleOrDefaultAsync(value => value.Id == runId, cancellationToken);
        if (run is null)
        {
            db.WorkflowRuns.Add(new WorkflowRunEntity
            {
                Id = runId, ScenarioId = scenarioId, Status = status, RequirementVersion = requirementVersion,
                StartedAt = startedAt, UpdatedAt = updatedAt, Payload = payload
            });
        }
        else
        {
            run.Status = status;
            run.RequirementVersion = requirementVersion;
            run.UpdatedAt = updatedAt;
            run.Payload = payload;
        }
        auditEvent.Id = 0;
        db.WorkflowAudit.Add(auditEvent);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public Task AddAgentCallAsync(AgentCallEntity call, CancellationToken cancellationToken = default) =>
        AddAsync([call], cancellationToken);

    public Task AddArtifactAsync(ArtifactEntity artifact, CancellationToken cancellationToken = default) =>
        AddAsync([artifact], cancellationToken);

    public Task AddValidationsAsync(IEnumerable<ValidationEntity> validations, CancellationToken cancellationToken = default) =>
        AddAsync(validations.ToArray(), cancellationToken);

    public Task AddApprovalAsync(ApprovalEntity approval, CancellationToken cancellationToken = default) =>
        AddAsync([approval], cancellationToken);

    public async Task<IReadOnlyList<WorkflowAuditEntity>> GetAuditAsync(string runId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.WorkflowAudit.AsNoTracking().Where(item => item.RunId == runId)
            .OrderBy(item => item.Sequence).ToArrayAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AgentCallEntity>> GetAgentCallsAsync(string runId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.AgentCalls.AsNoTracking().Where(item => item.RunId == runId)
            .OrderBy(item => item.Id).ToArrayAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ArtifactEntity>> GetArtifactsAsync(string runId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Artifacts.AsNoTracking().Where(item => item.RunId == runId)
            .OrderBy(item => item.Id).ToArrayAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ValidationEntity>> GetValidationsAsync(string runId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Validations.AsNoTracking().Where(item => item.RunId == runId)
            .OrderBy(item => item.Id).ToArrayAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ApprovalEntity>> GetApprovalsAsync(string runId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Approvals.AsNoTracking().Where(item => item.RunId == runId)
            .OrderBy(item => item.Id).ToArrayAsync(cancellationToken);
    }

    private async Task AddAsync<T>(IReadOnlyCollection<T> items, CancellationToken cancellationToken) where T : class
    {
        if (items.Count == 0) return;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.AddRange(items);
        await db.SaveChangesAsync(cancellationToken);
    }
}

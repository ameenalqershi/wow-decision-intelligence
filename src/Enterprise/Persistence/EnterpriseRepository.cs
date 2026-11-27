using System.Collections.Immutable;
using System.Data;
using Microsoft.EntityFrameworkCore;
using WowDi.DecisionGraph;
using WowDi.Enterprise.Identity;
using WowDi.Udir;

namespace WowDi.Enterprise.Persistence;

public sealed record StoredJob(string TenantId, string JobId, string ModelId, string RevisionId,
    string RequestHash, ImmutableArray<byte> Request, string Issuer, string Subject, DateTimeOffset CreatedAt,
    string State);

/// <summary>Scoped application repository. Authorize before opening a database connection.</summary>
public sealed class EnterpriseRepository(DbContextOptions<EnterpriseDb> options, TenantAccess access, TimeProvider clock)
{
    public async Task<GraphRevision?> SaveRevisionAsync(UdirDocument document, DecisionGraphSnapshot graph,
        IReadOnlyDictionary<string, ReadOnlyMemory<byte>> inputs, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(inputs);
        var key = RevisionCodec.Key(document);
        var actor = await access.AuthorizeAsync(TenantPermission.Write, new(key.TenantId, key.ModelId), ct);
        if (actor is null) return null;
        ValidateKey(key);
        if (graph.Revision != key) throw RevisionCodec.Error("scope");
        var owned = inputs.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal);
        RevisionCodec.Validate(document, owned);
        var modelBytes = document.Write();
        var graphBytes = GraphData.Encode(graph);
        var mh = RevisionCodec.Hash(modelBytes);
        var gh = RevisionCodec.Hash(graphBytes);

        await using var db = new EnterpriseDb(options);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        // A conflicting insert waits for the first transaction. The subsequent query observes
        // its committed revision; no client-side read/insert race and no overwrite.
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO wowdi.revisions (tenant,model_id,revision_id,model,graph,model_hash,graph_hash)
            VALUES ({key.TenantId},{key.ModelId},{key.RevisionId},{modelBytes},{graphBytes},{mh},{gh})
            ON CONFLICT (tenant,model_id,revision_id) DO NOTHING
            """, ct);
        if (inserted == 0)
        {
            var previous = await LoadAsync(db, key, ct) ?? throw RevisionCodec.Error("integrity");
            if (!modelBytes.AsSpan().SequenceEqual(previous.Document.Write()) ||
                !graphBytes.AsSpan().SequenceEqual(GraphData.Encode(previous.Graph)) ||
                owned.Count != previous.Inputs.Count ||
                owned.Any(p => !previous.Inputs.TryGetValue(p.Key, out var stored) ||
                    !p.Value.AsSpan().SequenceEqual(stored.AsSpan())))
                throw RevisionCodec.Error("revisionConflict");
        }
        else
        {
            // Stable ordering avoids lock-order inversions when distinct revisions share blobs.
            foreach (var (hash, bytes) in owned.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var added = await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO wowdi.content (tenant,hash,data) VALUES ({key.TenantId},{hash},{bytes})
                    ON CONFLICT (tenant,hash) DO NOTHING
                    """, ct);
                if (added == 0)
                {
                    var existing = await db.Content.AsNoTracking().SingleAsync(x => x.Tenant == key.TenantId && x.Hash == hash, ct);
                    if (!bytes.AsSpan().SequenceEqual(existing.Data)) throw RevisionCodec.Error("digest");
                }
                db.Inputs.Add(new() { Tenant = key.TenantId, ModelId = key.ModelId, RevisionId = key.RevisionId, Hash = hash });
            }
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        return key;
    }

    public async Task<EnterpriseRevision?> FindRevisionAsync(GraphRevision key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var actor = await access.AuthorizeAsync(TenantPermission.View, new(key.TenantId, key.ModelId), ct);
        if (actor is null) return null;
        ValidateKey(key);
        await using var db = new EnterpriseDb(options);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var revision = await LoadAsync(db, key, ct);
        await tx.CommitAsync(ct);
        return revision;
    }

    private static async Task<EnterpriseRevision?> LoadAsync(EnterpriseDb db, GraphRevision key, CancellationToken ct)
    {
        var row = await db.Revisions.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Tenant == key.TenantId && x.ModelId == key.ModelId && x.RevisionId == key.RevisionId, ct);
        if (row is null) return null;
        if (RevisionCodec.Hash(row.Model) != row.ModelHash || RevisionCodec.Hash(row.Graph) != row.GraphHash)
            throw RevisionCodec.Error("integrity");
        var model = UdirDocument.Read(row.Model);
        if (RevisionCodec.Key(model) != key) throw RevisionCodec.Error("scope");
        var graph = GraphData.Decode(row.Graph, key);
        var linked = await (from i in db.Inputs.AsNoTracking()
                            where i.Tenant == key.TenantId && i.ModelId == key.ModelId && i.RevisionId == key.RevisionId
                            join c in db.Content.AsNoTracking() on new { i.Tenant, i.Hash } equals new { c.Tenant, c.Hash }
                            select new { i.Hash, c.Data }).ToListAsync(ct);
        var inputs = linked.ToDictionary(x => x.Hash, x => x.Data, StringComparer.Ordinal);
        RevisionCodec.Validate(model, inputs);
        return new(model, graph, inputs.ToImmutableDictionary(x => x.Key, x => ImmutableArray.CreateRange(x.Value), StringComparer.Ordinal));
    }

    // The host validates the frozen request against the solver/admission contract before calling.
    // Admission is durable, but leases and state transitions belong to S12/E03.
    public async Task<StoredJob?> AdmitJobAsync(GraphRevision key, string idempotencyKey,
        ReadOnlyMemory<byte> frozenRequest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var binding = await access.BindJobAsync(new(key.TenantId, key.ModelId), ct);
        if (binding is null) return null;
        ValidateKey(key);
        RevisionCodec.ValidateKey(idempotencyKey);
        if (frozenRequest.Length is < 1 or > 1024 * 1024) throw RevisionCodec.Error("jobRequest");
        var request = frozenRequest.ToArray();
        var hash = RevisionCodec.Hash(request);
        var id = Guid.NewGuid().ToString("N");
        var now = clock.GetUtcNow().ToUniversalTime();
        var actor = binding.Actor;
        await using var db = new EnterpriseDb(options);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        if (!await db.Revisions.AnyAsync(x => x.Tenant == key.TenantId && x.ModelId == key.ModelId && x.RevisionId == key.RevisionId, ct))
            return null;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO wowdi.jobs (tenant,job_id,model_id,revision_id,idempotency_key,request_hash,request,issuer,subject,created_at,state)
            VALUES ({actor.TenantId},{id},{key.ModelId},{key.RevisionId},{idempotencyKey},{hash},{request},{actor.Issuer},{actor.Subject},{now},{"Queued"})
            ON CONFLICT (tenant,idempotency_key) DO NOTHING
            """, ct);
        var row = await db.Jobs.AsNoTracking().SingleAsync(x => x.Tenant == actor.TenantId && x.IdempotencyKey == idempotencyKey, ct);
        if (row.ModelId != key.ModelId || row.RevisionId != key.RevisionId || row.Issuer != actor.Issuer ||
            row.Subject != actor.Subject || row.RequestHash != hash || !row.Request.AsSpan().SequenceEqual(request))
            throw RevisionCodec.Error("idempotencyConflict");
        var result = Materialize(row);
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<StoredJob?> FindJobAsync(TenantResource scope, string jobId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var actor = await access.AuthorizeAsync(TenantPermission.View, scope, ct);
        if (actor is null) return null;
        RevisionCodec.ValidateKey(jobId);
        await using var db = new EnterpriseDb(options);
        var row = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Tenant == actor.TenantId && x.ModelId == scope.ModelId && x.JobId == jobId, ct);
        return row is null ? null : Materialize(row);
    }

    private static StoredJob Materialize(JobRow row)
    {
        if (RevisionCodec.Hash(row.Request) != row.RequestHash) throw RevisionCodec.Error("integrity");
        return new(row.Tenant, row.JobId, row.ModelId, row.RevisionId, row.RequestHash,
            ImmutableArray.CreateRange(row.Request), row.Issuer, row.Subject, row.CreatedAt, row.State);
    }

    private static void ValidateKey(GraphRevision key)
    {
        RevisionCodec.ValidateKey(key.TenantId);
        RevisionCodec.ValidateKey(key.ModelId);
        RevisionCodec.ValidateKey(key.RevisionId);
    }
}

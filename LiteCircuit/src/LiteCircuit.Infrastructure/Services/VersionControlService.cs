using LiteCircuit.Core.Models;
using LiteCircuit.Core.Pcb;
using LiteCircuit.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace LiteCircuit.Infrastructure.Services;

public record DesignDiff(int ComponentsAdded, int ComponentsRemoved, int TracksAdded, int TracksRemoved, string Summary);

/// <summary>Git-like snapshots for PCB projects: commit, log, restore, diff.</summary>
public class VersionControlService(IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task<DesignCommit> CommitAsync(Guid projectId, string message, string author)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var p = await db.Projects.FirstAsync(x => x.Id == projectId);
        var commit = new DesignCommit
        {
            ProjectId = projectId, Message = message, Author = author,
            SchematicJson = p.SchematicJson, BoardJson = p.BoardJson,
        };
        db.Commits.Add(commit);
        await db.SaveChangesAsync();
        return commit;
    }

    public async Task<List<DesignCommit>> LogAsync(Guid projectId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Commits.Where(c => c.ProjectId == projectId)
            .OrderByDescending(c => c.CreatedAt).AsNoTracking().ToListAsync();
    }

    public async Task RestoreAsync(Guid commitId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var commit = await db.Commits.FirstAsync(c => c.Id == commitId);
        var p = await db.Projects.FirstAsync(x => x.Id == commit.ProjectId);
        p.SchematicJson = commit.SchematicJson;
        p.BoardJson = commit.BoardJson;
        p.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    public DesignDiff Diff(string boardJsonA, string boardJsonB)
    {
        var a = PcbJson.ParseBoard(boardJsonA);
        var b = PcbJson.ParseBoard(boardJsonB);
        var refsA = a.Components.Select(c => c.Ref).ToHashSet();
        var refsB = b.Components.Select(c => c.Ref).ToHashSet();
        var added = refsB.Except(refsA).Count();
        var removed = refsA.Except(refsB).Count();
        var tAdded = Math.Max(0, b.Tracks.Count - a.Tracks.Count);
        var tRemoved = Math.Max(0, a.Tracks.Count - b.Tracks.Count);
        return new DesignDiff(added, removed, tAdded, tRemoved,
            $"+{added}/-{removed} components, +{tAdded}/-{tRemoved} tracks");
    }
}

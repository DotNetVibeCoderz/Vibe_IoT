using System.Runtime.CompilerServices;
using System.Text.Json;
using LiteCircuit.Core.Models;
using LiteCircuit.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace LiteCircuit.Infrastructure.Ai;

/// <summary>Electra: multi-session chat with attachments, streaming and kernel functions.</summary>
public class ElectraChatService(ElectraKernelFactory kernelFactory, IDbContextFactory<AppDbContext> dbFactory)
{
    // ---- session management -----------------------------------------------

    public async Task<List<ChatSession>> GetSessionsAsync(string userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.ChatSessions.Where(s => s.UserId == userId)
            .OrderByDescending(s => s.CreatedAt).AsNoTracking().ToListAsync();
    }

    public async Task<ChatSession> CreateSessionAsync(string userId, string title = "New chat")
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var s = new ChatSession { UserId = userId, Title = title };
        db.ChatSessions.Add(s);
        await db.SaveChangesAsync();
        return s;
    }

    public async Task DeleteSessionAsync(Guid sessionId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.ChatSessions.Where(s => s.Id == sessionId).ExecuteDeleteAsync();
    }

    /// <summary>Reset = clear all messages but keep the session.</summary>
    public async Task ResetSessionAsync(Guid sessionId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.ChatMessages.Where(m => m.SessionId == sessionId).ExecuteDeleteAsync();
    }

    public async Task<List<ChatMessage>> GetMessagesAsync(Guid sessionId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.ChatMessages.Where(m => m.SessionId == sessionId)
            .OrderBy(m => m.CreatedAt).AsNoTracking().ToListAsync();
    }

    // ---- chat ----------------------------------------------------------------

    /// <summary>
    /// Sends a user message (with optional attachments) and streams Electra's reply.
    /// Image attachments are passed as image content; documents are linked in the text.
    /// </summary>
    public async IAsyncEnumerable<string> StreamReplyAsync(
        Guid sessionId, string userText, List<ChatAttachment> attachments, string publicBaseUrl,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Persist the user message first
        var userContent = userText;
        var docs = attachments.Where(a => !a.IsImage).ToList();
        if (docs.Count > 0)
            userContent += "\n\n" + string.Join("\n", docs.Select(d => $"[Attached document: {d.Name}]({Absolute(d.Url, publicBaseUrl)})"));

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.ChatMessages.Add(new ChatMessage
            {
                SessionId = sessionId, Role = "user", Content = userContent,
                AttachmentsJson = JsonSerializer.Serialize(attachments),
            });
            // First user message becomes the session title
            var session = await db.ChatSessions.FirstAsync(s => s.Id == sessionId, ct);
            if (session.Title == "New chat" && userText.Length > 0)
                session.Title = userText.Length > 48 ? userText[..48] + "…" : userText;
            await db.SaveChangesAsync(ct);
        }

        Kernel? kernel = null;
        string? configError = null;
        try { kernel = kernelFactory.Create(); }
        catch (InvalidOperationException e) { configError = e.Message; }
        if (kernel is null)
        {
            yield return configError ?? "AI provider is not configured.";
            yield break;
        }

        var o = kernelFactory.Options;
        var history = new ChatHistory(o.SystemPrompt);
        foreach (var m in (await GetMessagesAsync(sessionId)).TakeLast(24))
        {
            if (m.Role == "assistant") { history.AddAssistantMessage(m.Content); continue; }
            var atts = JsonSerializer.Deserialize<List<ChatAttachment>>(m.AttachmentsJson) ?? new();
            var images = atts.Where(a => a.IsImage).ToList();
            if (images.Count == 0) { history.AddUserMessage(m.Content); continue; }
            var items = new ChatMessageContentItemCollection { new TextContent(m.Content) };
            foreach (var img in images)
                items.Add(new ImageContent(new Uri(Absolute(img.Url, publicBaseUrl))));
            history.AddUserMessage(items);
        }

        var settings = new OpenAIPromptExecutionSettings
        {
            Temperature = o.Temperature,
            MaxTokens = o.MaxTokens,
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(),
        };

        var chat = kernel.GetRequiredService<IChatCompletionService>();
        var full = new System.Text.StringBuilder();
        var stream = chat.GetStreamingChatMessageContentsAsync(history, settings, kernel, ct);

        await foreach (var chunk in Guarded(stream, ct))
        {
            if (string.IsNullOrEmpty(chunk)) continue;
            full.Append(chunk);
            yield return chunk;
        }

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.ChatMessages.Add(new ChatMessage
            {
                SessionId = sessionId, Role = "assistant",
                Content = full.Length > 0 ? full.ToString() : "(no reply)",
            });
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    /// <summary>Wraps the provider stream so API failures surface as a readable chat message.</summary>
    private static async IAsyncEnumerable<string> Guarded(
        IAsyncEnumerable<StreamingChatMessageContent> stream,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var e = stream.GetAsyncEnumerator(ct);
        while (true)
        {
            string? chunk = null;
            string? error = null;
            var done = false;
            try
            {
                if (await e.MoveNextAsync()) chunk = e.Current?.Content;
                else done = true;
            }
            catch (Exception ex) { error = $"\n\n> ⚠ Provider error: {ex.Message}"; }

            if (error is not null) { yield return error; break; }
            if (done) break;
            if (chunk is not null) yield return chunk;
        }
        await e.DisposeAsync();
    }

    private static string Absolute(string url, string baseUrl) =>
        url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? url
            : $"{baseUrl.TrimEnd('/')}{(url.StartsWith('/') ? "" : "/")}{url}";
}

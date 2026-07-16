using Markdig;

namespace LiteCircuit.Infrastructure.Ai;

/// <summary>Renders chat markdown (tables, code, media, task lists) to HTML.</summary>
public class MarkdownService
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()   // tables, task lists, autolinks, media links...
        .UseMediaLinks()           // youtube/audio/video embeds
        .DisableHtml()             // never pass raw HTML from the model through
        .Build();

    public string ToHtml(string markdown) =>
        string.IsNullOrWhiteSpace(markdown) ? "" : Markdown.ToHtml(markdown, Pipeline);
}

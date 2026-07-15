using Ganss.Xss;
using Markdig;
using Microsoft.AspNetCore.Html;

namespace Highbyte.Wrighty.Web.Markdown;

public sealed class MarkdownRenderer
{
    private const int MaximumBodyLength = 1_000_000;
    private readonly MarkdownPipeline pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();
    private readonly HtmlSanitizer sanitizer = CreateSanitizer();

    public IHtmlContent Render(string markdown)
    {
        if (markdown.Length > MaximumBodyLength)
        {
            return new HtmlString("<p>Markdown is too large to render safely.</p>");
        }

        var html = Markdig.Markdown.ToHtml(markdown, pipeline);
        return new HtmlString(sanitizer.Sanitize(html));
    }

    private static HtmlSanitizer CreateSanitizer()
    {
        var value = new HtmlSanitizer();
        value.AllowedTags.Remove("img");
        value.AllowedSchemes.Clear();
        value.AllowedSchemes.Add("http");
        value.AllowedSchemes.Add("https");
        value.AllowedSchemes.Add("mailto");
        value.PostProcessNode += (_, args) =>
        {
            if (args.Node is AngleSharp.Dom.IElement element &&
                string.Equals(element.TagName, "A", StringComparison.OrdinalIgnoreCase))
            {
                element.SetAttribute("rel", "noopener noreferrer");
                element.SetAttribute("target", "_blank");
            }
        };
        return value;
    }
}

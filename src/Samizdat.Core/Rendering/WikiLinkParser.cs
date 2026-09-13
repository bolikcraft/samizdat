using System.Text;
using Markdig;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Renderers;

namespace Samizdat.Core.Rendering;

public sealed class WikiLinkParser : InlineParser
{
    public WikiLinkParser() => OpeningCharacters = ['[', '!'];

    public override bool Match(InlineProcessor processor, ref StringSlice slice)
    {
        var start = slice.Start;
        var embed = slice.CurrentChar == '!';
        if (embed && slice.PeekChar() != '[') return false;
        if (embed) slice.NextChar();

        if (slice.CurrentChar != '[' || slice.PeekChar() != '[')
        {
            slice.Start = start;
            return false;
        }

        slice.NextChar();
        slice.NextChar();

        var content = new StringBuilder();
        while (slice.CurrentChar != '\0')
        {
            if (slice.CurrentChar == ']' && slice.PeekChar() == ']')
            {
                slice.NextChar();
                slice.NextChar();

                var text = content.ToString();
                var bar = text.IndexOf('|');
                processor.Inline = new WikiLink
                {
                    Target = (bar < 0 ? text : text[..bar]).Trim(),
                    Label = bar < 0 ? null : text[(bar + 1)..].Trim(),
                    IsEmbed = embed,
                    Span = { Start = processor.GetSourcePosition(start, out var line, out var column) },
                    Line = line,
                    Column = column,
                };
                processor.Inline.Span.End = processor.Inline.Span.Start + (slice.Start - start) - 1;
                return true;
            }

            if (slice.CurrentChar is '\n' or '[') break;
            content.Append(slice.CurrentChar);
            slice.NextChar();
        }

        slice.Start = start;
        return false;
    }
}

public sealed class WikiLinkExtension : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        // Перед LinkInlineParser, иначе [[ разберётся как обычная ссылка.
        pipeline.InlineParsers.Insert(0, new WikiLinkParser());
    }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer) { }
}

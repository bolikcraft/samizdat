using Markdig.Renderers;
using Markdig.Syntax;

namespace Samizdat.Core.Rendering;

/// Блок ```mermaid: со схемами — pre.mermaid для app/assets/diagrams.js, без них — обычный блок кода.
/// Прочие огороженные блоки уходят рендереру кода code.
public sealed class MermaidRenderer(bool diagrams, IMarkdownObjectRenderer code) : IMarkdownObjectRenderer
{
    // Только сам FencedCodeBlock: его наследника MathBlock ($$) пишет свой рендерер.
    public bool Accept(RendererBase renderer, Type objectType) => objectType == typeof(FencedCodeBlock);

    public void Write(RendererBase renderer, MarkdownObject objectToRender)
    {
        var block = (FencedCodeBlock)objectToRender;
        if (!string.Equals(block.Info, "mermaid", StringComparison.OrdinalIgnoreCase))
        {
            code.Write(renderer, block);
            return;
        }

        // Без схем блок тоже пишем сами: UseDiagrams из UseAdvancedExtensions вывел бы pre.mermaid.
        var html = (HtmlRenderer)renderer;
        html.Write(diagrams ? "<pre class=\"mermaid\">" : "<pre><code class=\"language-mermaid\">");
        html.WriteLeafRawLines(block, writeEndOfLines: true, escape: true, softEscape: true);
        html.WriteLine(diagrams ? "</pre>" : "</code></pre>");
    }
}

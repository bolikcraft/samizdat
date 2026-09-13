using Markdig.Renderers;
using Markdig.Renderers.Html;

namespace Samizdat.Core.Rendering;

public sealed class CalloutRenderer : HtmlObjectRenderer<CalloutBlock>
{
    protected override void Write(HtmlRenderer renderer, CalloutBlock callout)
    {
        renderer.Write("<div class=\"callout callout-").WriteEscape(callout.Kind).Write("\">");
        renderer.Write("<div class=\"callout-title\">").WriteEscape(callout.Title).Write("</div>");
        renderer.Write("<div class=\"callout-body\">");
        renderer.WriteChildren(callout);
        renderer.Write("</div></div>");
    }
}

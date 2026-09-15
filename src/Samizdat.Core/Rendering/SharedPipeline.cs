using Markdig;

namespace Samizdat.Core.Rendering;

/// Общая часть двух конвейеров Markdig — рендера статьи и разбора для индекса. Расширения отсюда
/// нельзя развести по забывчивости: обе стороны зовут один и тот же метод.
/// UseColorCode() сюда не входит — она нужна только рендеру страницы: в индекс едёт текст, а не
/// разметка, подсвечивать в нём нечего.
public static class SharedPipeline
{
    public static MarkdownPipelineBuilder Builder() => new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Use<WikiLinkExtension>();
}

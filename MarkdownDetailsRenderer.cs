using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdTable = Markdig.Extensions.Tables.Table;
using MdRow = Markdig.Extensions.Tables.TableRow;
using MdCell = Markdig.Extensions.Tables.TableCell;

namespace StickyTodo;

// Render parsed Markdown directly into WPF elements, without HTML or external resources.
public static class MarkdownDetailsRenderer
{
    static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables().UseTaskLists()
        .UseEmphasisExtras(Markdig.Extensions.EmphasisExtras.EmphasisExtraOptions.Strikethrough).Build();

    public static FlowDocument Render(string text, Brush foreground, Action<Uri>? openLink = null)
    {
        var document = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"), FontSize = 13,
            Foreground = foreground, PagePadding = new Thickness(7), ColumnWidth = double.PositiveInfinity
        };
        document.Resources["MarkdownForeground"] = foreground;
        AddBlocks(Markdown.Parse(text, Pipeline), document.Blocks, document, openLink ?? OpenLink);
        return document;
    }

    static void AddBlocks(ContainerBlock source, BlockCollection target, FlowDocument document, Action<Uri> openLink)
    {
        foreach (var block in source)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    var title = Paragraph(heading, openLink);
                    title.FontSize = new[] { 26d, 22, 19, 16, 14, 13 }[heading.Level - 1];
                    title.FontWeight = FontWeights.Bold;
                    target.Add(title);
                    break;
                case ParagraphBlock paragraph:
                    target.Add(Paragraph(paragraph, openLink));
                    break;
                case CodeBlock code:
                    var codeText = code.Lines.ToString().Replace("\t", "    ");
                    var codeParagraph = new Paragraph(new Run(codeText))
                    {
                        FontFamily = new FontFamily("Consolas"), Margin = new Thickness(0, 4, 0, 8),
                        Padding = new Thickness(8), BorderBrush = document.Foreground, BorderThickness = new Thickness(1)
                    };
                    // Give long code lines a horizontal scroll range instead of clipping them.
                    var lineWidth = codeText.Split('\n').Select(line => new FormattedText(line.TrimEnd('\r'),
                        System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                        new Typeface("Consolas"), 13, document.Foreground, 1).WidthIncludingTrailingWhitespace).DefaultIfEmpty().Max();
                    document.MinPageWidth = Math.Max(document.MinPageWidth, lineWidth + 64);
                    target.Add(codeParagraph);
                    break;
                case QuoteBlock quote:
                    var section = new Section
                    {
                        Margin = new Thickness(8, 4, 0, 8), Padding = new Thickness(10, 0, 0, 0),
                        BorderBrush = document.Foreground, BorderThickness = new Thickness(2, 0, 0, 0)
                    };
                    AddBlocks(quote, section.Blocks, document, openLink);
                    target.Add(section);
                    break;
                case ListBlock list:
                    var result = new System.Windows.Documents.List
                    {
                        MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                        Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(24, 0, 0, 0)
                    };
                    if (list.IsOrdered && int.TryParse(list.OrderedStart, out var start) && start > 0) result.StartIndex = start;
                    foreach (var item in list.OfType<ListItemBlock>())
                    {
                        var listItem = new ListItem();
                        AddBlocks(item, listItem.Blocks, document, openLink);
                        result.ListItems.Add(listItem);
                    }
                    target.Add(result);
                    break;
                case MdTable table:
                    var renderedTable = new System.Windows.Documents.Table { CellSpacing = 0, Margin = new Thickness(0, 4, 0, 8) };
                    var group = new TableRowGroup();
                    renderedTable.RowGroups.Add(group);
                    document.MinPageWidth = Math.Max(document.MinPageWidth, table.ColumnDefinitions.Count * 120 + 32);
                    foreach (var row in table.OfType<MdRow>())
                    {
                        var renderedRow = new System.Windows.Documents.TableRow();
                        if (row.IsHeader) renderedRow.FontWeight = FontWeights.Bold;
                        for (var column = 0; column < row.Count; column++)
                        {
                            var cell = (MdCell)row[column];
                            var renderedCell = new System.Windows.Documents.TableCell
                            {
                                Padding = new Thickness(6), BorderBrush = document.Foreground, BorderThickness = new Thickness(0.5)
                            };
                            if (column < table.ColumnDefinitions.Count)
                                renderedCell.TextAlignment = table.ColumnDefinitions[column].Alignment switch
                                {
                                    Markdig.Extensions.Tables.TableColumnAlign.Center => TextAlignment.Center,
                                    Markdig.Extensions.Tables.TableColumnAlign.Right => TextAlignment.Right,
                                    _ => TextAlignment.Left
                                };
                            AddBlocks(cell, renderedCell.Blocks, document, openLink);
                            renderedRow.Cells.Add(renderedCell);
                        }
                        group.Rows.Add(renderedRow);
                    }
                    target.Add(renderedTable);
                    break;
                case ThematicBreakBlock:
                    target.Add(new Paragraph { BorderBrush = document.Foreground, BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 8, 0, 8) });
                    break;
                case LeafBlock leaf:
                    target.Add(new Paragraph(new Run(leaf.Lines.ToString())));
                    break;
            }
        }
    }

    static Paragraph Paragraph(LeafBlock block, Action<Uri> openLink)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
        if (block.Inline != null) AddInlines(block.Inline, paragraph.Inlines, openLink);
        return paragraph;
    }

    static void AddInlines(ContainerInline source, InlineCollection target, Action<Uri> openLink)
    {
        foreach (var inline in source)
        {
            switch (inline)
            {
                case LiteralInline literal: target.Add(new Run(literal.Content.ToString())); break;
                case CodeInline code: target.Add(new Run(code.Content) { FontFamily = new FontFamily("Consolas") }); break;
                case LineBreakInline line:
                    if (line.IsHard) target.Add(new LineBreak()); else target.Add(new Run(" "));
                    break;
                case EmphasisInline emphasis:
                    var span = new Span();
                    if (emphasis.DelimiterChar == '~') span.TextDecorations = TextDecorations.Strikethrough;
                    else if (emphasis.DelimiterCount == 2) span.FontWeight = FontWeights.Bold;
                    else span.FontStyle = FontStyles.Italic;
                    AddInlines(emphasis, span.Inlines, openLink);
                    target.Add(span);
                    break;
                case LinkInline link:
                    var label = link.IsImage ? new Span() : Link(link.Url, openLink);
                    AddInlines(link, label.Inlines, openLink);
                    target.Add(label);
                    break;
                case AutolinkInline auto:
                    var autoLink = Link(auto.IsEmail ? "mailto:" + auto.Url : auto.Url, openLink);
                    autoLink.Inlines.Add(new Run(auto.Url));
                    target.Add(autoLink);
                    break;
                case Markdig.Extensions.TaskLists.TaskList task:
                    target.Add(new Run(task.Checked ? "☑ " : "☐ "));
                    break;
                case HtmlInline html: target.Add(new Run(html.Tag)); break;
                case HtmlEntityInline entity: target.Add(new Run(entity.Transcoded.ToString())); break;
                case ContainerInline container: AddInlines(container, target, openLink); break;
            }
        }
    }

    static Span Link(string? url, Action<Uri> openLink)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return new Span();
        var link = new Hyperlink { NavigateUri = uri, ToolTip = uri.AbsoluteUri };
        // Inherit the note's contrast color rather than WPF's default blue.
        link.SetResourceReference(TextElement.ForegroundProperty, "MarkdownForeground");
        link.RequestNavigate += (_, e) => { e.Handled = true; openLink(uri); };
        return link;
    }

    static void OpenLink(Uri uri)
    {
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            MessageBox.Show($"Could not open this link.\n\n{ex.Message}", "Open link failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}

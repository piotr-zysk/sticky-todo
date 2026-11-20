using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;
using StickyTodo;

static partial class Program
{
    static string MarkdownText(FlowDocument document) => new TextRange(document.ContentStart, document.ContentEnd).Text;

    static IEnumerable<TextElement> MarkdownElements(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is TextElement element) yield return element;
            foreach (var nested in MarkdownElements(child)) yield return nested;
        }
    }

    static void CheckMarkdownRendering()
    {
        var opened = new List<Uri>();
        var document = MarkdownDetailsRenderer.Render("""
            # Heading

            **bold** and *italic*, ~~removed~~, `inline code`, &amp; Unicode Żółć.

            > A quotation

            3. Third
            4. Fourth
               - Nested

            - [x] Done
            - [ ] Pending

            | Left | Right |
            | :--- | ---: |
            | Value | **Two** |

            ```csharp
            Console.WriteLine("hello");
            ```

            ---

            [Web](https://example.com/path) [File](file:///C:/test.txt) [Script](javascript:alert)
            ![Image description](https://example.com/image.png)

            <script>alert('literal')</script>

            Inline <b>HTML</b>.
            """, Brushes.White, opened.Add);
        var text = MarkdownText(document);
        var elements = MarkdownElements(document).ToList();
        Check(text.Contains("Heading") && !text.Contains("# Heading") &&
              elements.OfType<Paragraph>().Any(p => p.FontSize == 26), "Markdown headings are formatted");
        Check(elements.OfType<Span>().Any(s => s.FontWeight == FontWeights.Bold) &&
              elements.OfType<Span>().Any(s => s.FontStyle == FontStyles.Italic) &&
              elements.OfType<Span>().Any(s => s.TextDecorations.Contains(TextDecorations.Strikethrough[0])),
              "Markdown emphasis and strikethrough are formatted");
        Check(text.Contains("& Unicode Żółć") && elements.OfType<Run>().Any(r => r.Text == "inline code" && r.FontFamily.Source == "Consolas"),
              "Markdown preserves Unicode, decodes entities, and formats inline code");
        Check(elements.OfType<Section>().Any() && elements.OfType<System.Windows.Documents.List>().Any(l => l.StartIndex == 3) &&
              elements.OfType<System.Windows.Documents.List>().Count() == 3, "quotes and nested/ordered lists are rendered");
        Check(elements.OfType<Run>().Any(r => r.Text == "☑ ") && elements.OfType<Run>().Any(r => r.Text == "☐ ") &&
              text.Contains("Done") && text.Contains("Pending"), "task checkboxes are rendered as read-only text");
        Check(elements.OfType<Table>().Single().RowGroups[0].Rows.Count == 2 &&
              elements.OfType<TableCell>().Any(c => c.TextAlignment == TextAlignment.Right), "Markdown tables include rows and column alignment");
        Check(elements.OfType<Paragraph>().Any(p => p.FontFamily.Source == "Consolas") && text.Contains("Console.WriteLine"),
              "fenced code is rendered literally in a monospace font");
        Check(text.Contains("<script>alert('literal')</script>") && text.Contains("<b>HTML</b>"), "raw HTML is displayed literally");
        Check(text.Contains("Image description") && !elements.OfType<InlineUIContainer>().Any(), "images use descriptions without loading resources");
        var link = elements.OfType<Hyperlink>().Single();
        Check(opened.Count == 0 && link.NavigateUri.AbsoluteUri == "https://example.com/path" && link.Foreground == Brushes.White,
              "only web links are interactive, inherit foreground, and do not open during rendering");
        link.RaiseEvent(new RequestNavigateEventArgs(link.NavigateUri, "") { RoutedEvent = Hyperlink.RequestNavigateEvent });
        Check(opened.Single() == link.NavigateUri, "explicit link navigation uses the browser callback");
        Check(MarkdownText(MarkdownDetailsRenderer.Render("", Brushes.Black)).Trim() == "", "empty Markdown renders without placeholder content");
        Check(MarkdownText(MarkdownDetailsRenderer.Render("**unfinished [link](", Brushes.Black)).Contains("unfinished"),
              "unfinished Markdown remains readable");
        var longCode = MarkdownDetailsRenderer.Render("```\n" + new string('W', 250) + "\n```", Brushes.Black);
        Check(longCode.MinPageWidth > 1000, "long code requests enough document width to scroll horizontally");
    }

    static void CheckMarkdownWindow()
    {
        var task = new TaskItem { Title = "Markdown title", UpdatedAt = Baseline };
        var writes = new List<string>();
        var session = new DetailsSaveSession("Original", writes.Add) { AutoSaveIntervalSeconds = 0 };
        var note = new DetailsWindow(task, session);
        try
        {
            note.Show();
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            var editor = Find<TextBox>(note, "Editor");
            var toggle = Find<ToggleButton>(note, "MarkdownButton");
            var preview = Find<FlowDocumentScrollViewer>(note, "MarkdownPreview");
            Check(!note.IsMarkdownView && editor.Visibility == Visibility.Visible, "details opens in the standard editor");
            editor.SelectAll();
            editor.SelectedText = "# Unsaved heading\n\n**bold**";
            editor.Select(2, 7);
            var width = note.Width;
            var height = note.Height;
            toggle.IsChecked = true;
            toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(note.IsMarkdownView && preview.Visibility == Visibility.Visible && editor.Visibility == Visibility.Collapsed &&
                  MarkdownText(preview.Document).Contains("Unsaved heading"), "preview renders live unsaved source");
            Check(session.IsDirty && writes.Count == 0 && task.UpdatedAt == Baseline && note.Width == width && note.Height == height,
                  "preview does not save, timestamp, or resize the text window");
            Find<Button>(note, "CopyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Clipboard.GetText() == task.Title + "\n\n" + editor.Text, "preview Copy retains original Markdown source");
            note.ShowSubtasks();
            Check(toggle.Visibility == Visibility.Collapsed && preview.Visibility == Visibility.Collapsed, "subtasks hide Markdown controls");
            Find<Button>(note, "ViewButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(note.IsMarkdownView && preview.Visibility == Visibility.Visible, "subtask round trip restores Markdown mode");
            toggle.IsChecked = false;
            toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(editor.SelectedText == "Unsaved" && editor.CanUndo && editor.Text.Contains("**bold**"),
                  "returning to standard mode preserves source, selection, and undo history");
            editor.Undo();
            Check(editor.Text == "Original", "source edits can still be undone after viewing Markdown");
            editor.Redo();
            toggle.IsChecked = true;
            toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            note.Flush();
            var refreshed = new TaskItem { Id = task.Id, Title = "Remote title", UpdatedAt = Baseline.AddDays(1) };
            note.RefreshTask(refreshed, "## Remote heading");
            Check(note.IsMarkdownView && MarkdownText(preview.Document).Contains("Remote heading") &&
                  !session.IsDirty && writes.Count == 1 && refreshed.UpdatedAt == Baseline.AddDays(1),
                  "sync refresh updates the preview and preserves mode without a local edit");
            note.ApplyAppearance(Brushes.Black, Brushes.Gray, Brushes.White, 0.8, 1.5);
            Check(preview.Document.Foreground == Brushes.White && note.Opacity == 0.8 &&
                  Find<DockPanel>(note, "RootPanel").LayoutTransform is ScaleTransform { ScaleX: 1.5 },
                  "Markdown preview receives appearance and zoom");
            note.SetCompletionState(true, false);
            toggle.IsChecked = false;
            toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(editor.IsReadOnly, "switching from preview preserves completion locking");
        }
        finally { note.Close(); }
        var reopened = new DetailsWindow(task, session);
        try { Check(!reopened.IsMarkdownView, "reopening starts in standard mode even with a retained save session"); }
        finally { reopened.Close(); }
    }
}

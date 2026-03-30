using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Linq;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.DefinitionLists;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.CustomContainers;
using Markdig.Extensions.Figures;
using Markdig.Extensions.Footers;
using Markdig.Extensions.Abbreviations;

namespace ConsoleInk
{
    /// <summary>
    /// Specifies text alignment within a table column.
    /// </summary>
    internal enum ColumnAlignment
    {
        Left,
        Center,
        Right
    }

    /// <summary>
    /// A TextWriter implementation that buffers Markdown input and renders
    /// ANSI-formatted output using Markdig for parsing.
    /// </summary>
    public class MarkdownConsoleWriter : TextWriter
    {
        private TextWriter _outputWriter;
        private readonly MarkdownRenderOptions _options;
        private readonly StringBuilder _inputBuffer = new StringBuilder();
        private readonly int _maxWidth;
        private bool _isDisposed = false;
        private bool _isCompleted = false;
        private string _outputContent = "";
        private string _rawTail = "";

        private readonly Action<string>? _logger;

        private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .UseEmojiAndSmiley()
            .Build();

        /// <summary>
        /// Gets the encoding for this writer (defaults to the output writer's encoding).
        /// </summary>
        public override Encoding Encoding => _outputWriter.Encoding;

        /// <summary>
        /// Initializes a new instance of the <see cref="MarkdownConsoleWriter"/> class.
        /// </summary>
        /// <param name="outputWriter">The underlying TextWriter to write formatted output to.</param>
        /// <param name="options">Optional configuration for rendering.</param>
        /// <param name="logger">Optional action to receive detailed logging messages.</param>
        public MarkdownConsoleWriter(TextWriter outputWriter, MarkdownRenderOptions? options = null, Action<string>? logger = null)
        {
            _outputWriter = outputWriter ?? throw new ArgumentNullException(nameof(outputWriter));
            _options = options ?? new MarkdownRenderOptions();
            _maxWidth = _options.ConsoleWidth > 0 ? _options.ConsoleWidth : 80;
            _logger = logger;
            _log($"Initialized MarkdownConsoleWriter. MaxWidth={_maxWidth}, EnableColors={_options.EnableColors}, StripHtml={_options.StripHtml}");
        }

        private void _log(string message)
        {
            _logger?.Invoke($"[CI] {message}");
        }

        /// <summary>
        /// Writes a single character to the input buffer.
        /// </summary>
        public override void Write(char value)
        {
            CheckDisposed();
            CheckCompleted();
            _inputBuffer.Append(value);
        }

        /// <summary>
        /// Writes a string to the input buffer.
        /// </summary>
        public override void Write(string? value)
        {
            CheckDisposed();
            CheckCompleted();
            if (value != null)
                _inputBuffer.Append(value);
        }

        /// <summary>
        /// Writes a string followed by a line terminator to the input buffer.
        /// </summary>
        public override void WriteLine(string? value)
        {
            CheckDisposed();
            CheckCompleted();
            _inputBuffer.Append(value);
            _inputBuffer.Append('\n');
        }

        /// <summary>
        /// Writes a line terminator to the input buffer.
        /// </summary>
        public override void WriteLine()
        {
            CheckDisposed();
            CheckCompleted();
            _inputBuffer.Append('\n');
        }

        /// <summary>
        /// Signals that all input has been written and triggers Markdown parsing and rendering.
        /// </summary>
        public void Complete()
        {
            CheckDisposed();
            if (_isCompleted) return;
            _log("Complete: Starting Markdig parsing and rendering.");

            // Final render with full erase support. RenderIncremental handles:
            // - erasing raw tail (if any) via the existing raw tail erase logic
            // - correcting divergent content (e.g., soft line breaks, table reinterpretation)
            // Since Complete() parses everything (no tail), no new raw tail is created.
            RenderIncremental(skipFootnotes: false, allowErase: true);

            _outputWriter.Flush();
            _isCompleted = true;
            _log("Complete: Finalization finished.");
        }

        /// <summary>
        /// Resets the writer so it can be reused for a new streaming session.
        /// Call this after Complete() to start writing new content.
        /// The previous output remains on screen; new content is appended after it.
        /// </summary>
        public void Reset()
        {
            CheckDisposed();
            _inputBuffer.Clear();
            _outputContent = "";
            _rawTail = "";
            _isCompleted = false;
            _log("Reset: Writer state cleared for reuse.");
        }

        /// <summary>
        /// Releases all resources used by the <see cref="MarkdownConsoleWriter"/>.
        /// </summary>
        public new void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    if (!_isCompleted)
                    {
                        try { Complete(); }
                        catch (Exception ex) { _log($"Dispose: Exception during Complete(): {ex.Message}"); }
                    }
                }
                _isDisposed = true;
            }
            base.Dispose(disposing);
        }

        ~MarkdownConsoleWriter()
        {
            Dispose(disposing: false);
        }

        public override Task WriteAsync(char value) { Write(value); return Task.CompletedTask; }
        public override Task WriteAsync(string? value) { Write(value); return Task.CompletedTask; }
        public override Task WriteLineAsync(string? value) { WriteLine(value); return Task.CompletedTask; }
        public override Task WriteLineAsync() { WriteLine(); return Task.CompletedTask; }
        public override Task FlushAsync() { Flush(); return Task.CompletedTask; }

        /// <summary>
        /// Clears all buffers and causes any buffered data to be written to the underlying device.
        /// Supports incremental rendering: each Flush() parses the accumulated buffer and outputs
        /// only the new content since the last flush.
        /// </summary>
        public override void Flush()
        {
            CheckDisposed();
            if (!_isCompleted)
                RenderIncremental(skipFootnotes: true);
            _outputWriter.Flush();
        }

        /// <summary>
        /// Performs incremental rendering by parsing the accumulated buffer and outputting
        /// only new content since the last flush. Complete lines are parsed by Markdig for
        /// proper formatting. When ANSI is enabled, the trailing incomplete line is shown
        /// as raw text so tokens appear immediately (erased when the line completes).
        /// All output for a single flush is buffered and written atomically to avoid flicker.
        /// When Markdig reinterprets earlier content (e.g., paragraph becomes table), ANSI
        /// cursor erase is used to correct the affected lines in-place.
        /// </summary>
        private void RenderIncremental(bool skipFootnotes = true, bool allowErase = true)
        {
            string fullBuffer = _inputBuffer.ToString();
            if (string.IsNullOrEmpty(fullBuffer)) return;

            // Split buffer into complete lines (for Markdig) and trailing incomplete line
            string? markdownText;
            string tailText;

            if (skipFootnotes)
            {
                int lastNewline = fullBuffer.LastIndexOf('\n');
                if (lastNewline >= 0)
                {
                    markdownText = fullBuffer.Substring(0, lastNewline + 1);
                    tailText = fullBuffer.Substring(lastNewline + 1);
                }
                else
                {
                    markdownText = null;
                    tailText = fullBuffer;
                }
            }
            else
            {
                // Complete(): parse everything, no tail
                markdownText = fullBuffer;
                tailText = "";
            }

            // Buffer all output for this flush into a single atomic write
            var flushBuffer = new StringBuilder();

            // Erase previous raw tail (only during streaming flushes)
            if (_rawTail.Length > 0 && _options.EnableColors && allowErase)
            {
                int tailWidth = GetDisplayWidth(_rawTail);
                int tailLines = (tailWidth + _maxWidth - 1) / _maxWidth;
                if (tailLines > 1)
                    flushBuffer.Append($"\x1b[{tailLines - 1}F");
                flushBuffer.Append("\r\x1b[J");
            }
            _rawTail = "";

            // Render complete lines with Markdig
            if (!string.IsNullOrEmpty(markdownText))
            {
                var document = Markdown.Parse(markdownText, Pipeline);
                var renderBuffer = new StringBuilder();
                var realWriter = _outputWriter;
                try
                {
                    _outputWriter = new StringWriter(renderBuffer);
                    RenderDocument(document, skipFootnotes);
                }
                finally
                {
                    _outputWriter = realWriter;
                }

                string fullRendered = renderBuffer.ToString();

                if (fullRendered.StartsWith(_outputContent, StringComparison.Ordinal))
                {
                    // Happy path: previous output is a prefix → append delta
                    if (fullRendered.Length > _outputContent.Length)
                    {
                        flushBuffer.Append(fullRendered.Substring(_outputContent.Length));
                        _outputContent = fullRendered;
                    }
                }
                else if (allowErase && _options.EnableColors)
                {
                    // Content changed (e.g., paragraph → table). Erase divergent lines and rewrite.
                    int divergeAt = 0;
                    int minLen = Math.Min(_outputContent.Length, fullRendered.Length);
                    for (int i = 0; i < minLen; i++)
                    {
                        if (_outputContent[i] != fullRendered[i]) break;
                        divergeAt = i + 1;
                    }

                    // Back up to line boundary
                    int lineStart = divergeAt;
                    while (lineStart > 0 && _outputContent[lineStart - 1] != '\n')
                        lineStart--;

                    // Count terminal lines from divergence to end of old output
                    int linesToErase = 0;
                    for (int i = lineStart; i < _outputContent.Length; i++)
                    {
                        if (_outputContent[i] == '\n') linesToErase++;
                    }

                    if (linesToErase > 0)
                        flushBuffer.Append($"\x1b[{linesToErase}F\x1b[J");
                    else
                        flushBuffer.Append("\r\x1b[J");

                    flushBuffer.Append(fullRendered.Substring(lineStart));
                    _outputContent = fullRendered;
                }
                else
                {
                    // No erase allowed or non-ANSI: append-only beyond old length
                    if (fullRendered.Length > _outputContent.Length)
                        flushBuffer.Append(fullRendered.Substring(_outputContent.Length));
                    _outputContent = fullRendered;
                }
            }

            // Show raw tail immediately (ANSI only, streaming flush only)
            if (tailText.Length > 0 && _options.EnableColors && allowErase)
            {
                flushBuffer.Append(tailText);
                _rawTail = tailText;
            }

            // Single atomic write — no flicker
            if (flushBuffer.Length > 0)
                _outputWriter.Write(flushBuffer.ToString());
        }

        private void CheckDisposed()
        {
            if (_isDisposed)
                throw new ObjectDisposedException(GetType().Name);
        }

        private void CheckCompleted()
        {
            if (_isCompleted)
                throw new InvalidOperationException("Cannot write after Complete(). Call Reset() to reuse this writer.");
        }

        // =====================================================================
        // AST Rendering
        // =====================================================================

        private void RenderDocument(MarkdownDocument document, bool skipFootnotes = false)
        {
            bool needsSeparation = false;
            foreach (var block in document)
            {
                if (skipFootnotes && block is FootnoteGroup) continue;
                bool produced = RenderBlock(block, ref needsSeparation);
                if (produced)
                    needsSeparation = true;
            }
        }

        private bool RenderBlock(Block block, ref bool needsSeparation)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    WriteSeparation(ref needsSeparation);
                    RenderHeading(heading);
                    return true;

                case ParagraphBlock paragraph:
                    WriteSeparation(ref needsSeparation);
                    RenderParagraph(paragraph);
                    return true;

                case MathBlock mathBlock:
                    WriteSeparation(ref needsSeparation);
                    RenderMathBlock(mathBlock);
                    return true;

                case FencedCodeBlock fencedCode:
                    WriteSeparation(ref needsSeparation);
                    RenderFencedCodeBlock(fencedCode);
                    return true;

                case CodeBlock codeBlock:
                    WriteSeparation(ref needsSeparation);
                    RenderCodeBlock(codeBlock);
                    return true;

                case ListBlock list:
                    WriteSeparation(ref needsSeparation);
                    RenderList(list);
                    return true;

                case QuoteBlock quote:
                    WriteSeparation(ref needsSeparation);
                    RenderBlockquote(quote);
                    return true;

                case ThematicBreakBlock:
                    WriteSeparation(ref needsSeparation);
                    RenderThematicBreak();
                    return true;

                case Table table:
                    WriteSeparation(ref needsSeparation);
                    RenderTable(table);
                    return true;

                case HtmlBlock htmlBlock:
                    if (!_options.StripHtml)
                    {
                        WriteSeparation(ref needsSeparation);
                        RenderHtmlBlock(htmlBlock);
                        return true;
                    }
                    return false;

                case LinkReferenceDefinitionGroup:
                    // Reference link definitions produce no output
                    return false;

                case FootnoteGroup footnoteGroup:
                    WriteSeparation(ref needsSeparation);
                    RenderFootnoteGroup(footnoteGroup);
                    return true;

                case DefinitionList definitionList:
                    WriteSeparation(ref needsSeparation);
                    RenderDefinitionList(definitionList);
                    return true;

                case Footnote:
                    // Individual footnotes are rendered via FootnoteGroup
                    return false;

                case CustomContainer customContainer:
                    WriteSeparation(ref needsSeparation);
                    RenderCustomContainer(customContainer);
                    return true;

                case Figure figure:
                    WriteSeparation(ref needsSeparation);
                    RenderFigure(figure);
                    return true;

                case FooterBlock footerBlock:
                    WriteSeparation(ref needsSeparation);
                    RenderFooterBlock(footerBlock);
                    return true;

                default:
                    _log($"RenderBlock: Unhandled block type: {block.GetType().Name}");
                    return false;
            }
        }

        private void WriteSeparation(ref bool needsSeparation)
        {
            if (needsSeparation)
            {
                _outputWriter.Write(Environment.NewLine);
                needsSeparation = false;
            }
        }

        // =====================================================================
        // Block Renderers
        // =====================================================================

        private void RenderHeading(HeadingBlock heading)
        {
            int level = heading.Level;
            string style = level switch
            {
                1 => _options.Theme.Heading1Style,
                2 => _options.Theme.Heading2Style,
                3 => _options.Theme.Heading3Style,
                _ => string.Empty
            };

            ConsoleColor? color = level switch
            {
                1 => _options.Theme.Heading1Color,
                2 => _options.Theme.Heading2Color,
                3 => _options.Theme.Heading3Color,
                4 => _options.Theme.Heading4Color,
                5 => _options.Theme.Heading5Color,
                6 => _options.Theme.Heading6Color,
                _ => null
            };

            string colorCode = _options.EnableColors ? Ansi.GetColorCode(color, foreground: true) : string.Empty;
            bool hasFormatting = _options.EnableColors && (!string.IsNullOrEmpty(style) || !string.IsNullOrEmpty(colorCode));

            if (hasFormatting)
            {
                if (!string.IsNullOrEmpty(style))
                    _outputWriter.Write(style);
                if (!string.IsNullOrEmpty(colorCode))
                    _outputWriter.Write(colorCode);
            }

            string text = GetInlineText(heading.Inline);
            _outputWriter.Write(text);

            if (hasFormatting)
                _outputWriter.Write(Ansi.Reset);

            _outputWriter.Write(Environment.NewLine);
            _outputWriter.Write(Environment.NewLine);
        }

        private void RenderParagraph(ParagraphBlock paragraph)
        {
            var sb = new StringBuilder();
            RenderInlineToBuffer(paragraph.Inline, sb);
            string text = sb.ToString();
            WriteWrappedText(text);
        }

        private void RenderFencedCodeBlock(FencedCodeBlock fencedCode)
        {
            if (_options.EnableColors && !string.IsNullOrEmpty(_options.Theme.CodeBlockStyle))
                _outputWriter.Write(_options.Theme.CodeBlockStyle);

            var lines = fencedCode.Lines;
            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines.Lines[i];
                _outputWriter.Write(line.Slice.ToString());
                _outputWriter.Write(Environment.NewLine);
            }

            if (_options.EnableColors && !string.IsNullOrEmpty(_options.Theme.CodeBlockStyle))
                _outputWriter.Write(Ansi.Reset);
        }

        private void RenderCodeBlock(CodeBlock codeBlock)
        {
            if (_options.EnableColors && !string.IsNullOrEmpty(_options.Theme.CodeBlockStyle))
                _outputWriter.Write(_options.Theme.CodeBlockStyle);

            var lines = codeBlock.Lines;
            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines.Lines[i];
                _outputWriter.Write(line.Slice.ToString());
                _outputWriter.Write(Environment.NewLine);
            }

            if (_options.EnableColors && !string.IsNullOrEmpty(_options.Theme.CodeBlockStyle))
                _outputWriter.Write(Ansi.Reset);
        }

        private void RenderList(ListBlock list, int indentLevel = 0, string linePrefix = "")
        {
            string indent = indentLevel > 0 ? new string(' ', indentLevel * 4) : string.Empty;

            int orderedIndex = 0;
            if (list.IsOrdered)
            {
                if (list.OrderedStart != null && int.TryParse(list.OrderedStart, out int startIndex))
                    orderedIndex = startIndex - 1;
            }

            for (int itemIdx = 0; itemIdx < list.Count; itemIdx++)
            {
                var item = list[itemIdx] as ListItemBlock;
                if (item == null) continue;

                string bullet;
                bool isTask = false;
                bool isChecked = false;

                // Check for task list item
                if (item.Count > 0 && item[0] is ParagraphBlock taskPara && taskPara.Inline != null)
                {
                    var firstInline = taskPara.Inline.FirstChild;
                    if (firstInline is TaskList taskList)
                    {
                        isTask = true;
                        isChecked = taskList.Checked;
                    }
                }

                if (isTask)
                {
                    bullet = isChecked ? _options.Theme.TaskListCheckedMarker : _options.Theme.TaskListUncheckedMarker;
                }
                else if (list.IsOrdered)
                {
                    orderedIndex++;
                    bullet = string.Format(_options.Theme.OrderedListPrefixFormat, orderedIndex);
                }
                else
                {
                    bullet = _options.Theme.UnorderedListPrefix;
                }

                // Write indent
                if (!string.IsNullOrEmpty(linePrefix))
                    _outputWriter.Write(linePrefix);
                if (!string.IsNullOrEmpty(indent))
                    _outputWriter.Write(indent);

                // Write bullet with color
                if (_options.EnableColors)
                {
                    string bulletStyle = Ansi.GetColorCode(_options.Theme.ListBulletColor, foreground: true);
                    if (!string.IsNullOrEmpty(bulletStyle))
                        _outputWriter.Write(bulletStyle);
                    _outputWriter.Write(bullet);
                    _outputWriter.Write(Ansi.Reset);
                }
                else
                {
                    _outputWriter.Write(bullet);
                }

                // Write item content (inline text from first paragraph)
                if (item.Count > 0 && item[0] is ParagraphBlock para)
                {
                    var sb = new StringBuilder();
                    RenderInlineToBuffer(para.Inline, sb, isTask);
                    _outputWriter.Write(sb.ToString());
                    _outputWriter.Write(Environment.NewLine);
                }
                else
                {
                    _outputWriter.Write(Environment.NewLine);
                }

                // Render sub-blocks (nested lists, continuation paragraphs, code blocks)
                string continuationIndent = indent + new string(' ', bullet.Length);
                for (int childIdx = 1; childIdx < item.Count; childIdx++)
                {
                    var childBlock = item[childIdx];
                    if (childBlock is ListBlock subList)
                    {
                        RenderList(subList, indentLevel + 1, linePrefix);
                    }
                    else if (childBlock is ParagraphBlock subPara)
                    {
                        if (!string.IsNullOrEmpty(linePrefix))
                            _outputWriter.Write(linePrefix);
                        _outputWriter.Write(continuationIndent);
                        var sb = new StringBuilder();
                        RenderInlineToBuffer(subPara.Inline, sb);
                        _outputWriter.Write(sb.ToString());
                        _outputWriter.Write(Environment.NewLine);
                    }
                    else if (childBlock is FencedCodeBlock subFencedCode)
                    {
                        if (_options.EnableColors && !string.IsNullOrEmpty(_options.Theme.CodeBlockStyle))
                            _outputWriter.Write(_options.Theme.CodeBlockStyle);
                        var lines = subFencedCode.Lines;
                        for (int i = 0; i < lines.Count; i++)
                        {
                            if (!string.IsNullOrEmpty(linePrefix))
                                _outputWriter.Write(linePrefix);
                            _outputWriter.Write(continuationIndent);
                            _outputWriter.Write(lines.Lines[i].Slice.ToString());
                            _outputWriter.Write(Environment.NewLine);
                        }
                        if (_options.EnableColors && !string.IsNullOrEmpty(_options.Theme.CodeBlockStyle))
                            _outputWriter.Write(Ansi.Reset);
                    }
                    else if (childBlock is QuoteBlock subQuote)
                    {
                        RenderBlockquoteWithPrefix(subQuote, linePrefix + continuationIndent);
                    }
                }
            }
        }

        private void RenderBlockquote(QuoteBlock quote)
        {
            RenderBlockquoteWithPrefix(quote, string.Empty);
        }

        private void RenderBlockquoteWithPrefix(QuoteBlock quote, string outerPrefix)
        {
            string prefixStyle = Ansi.GetColorCode(_options.Theme.BlockquoteColor, foreground: true);
            string marker = _options.Theme.BlockquotePrefix;

            foreach (var child in quote)
            {
                if (child is ParagraphBlock para)
                {
                    string text = GetInlineText(para.Inline);
                    var lines = text.Split(new[] { '\n' }, StringSplitOptions.None);
                    foreach (var line in lines)
                    {
                        _outputWriter.Write(outerPrefix);
                        if (_options.EnableColors && !string.IsNullOrEmpty(prefixStyle))
                            _outputWriter.Write(prefixStyle);
                        _outputWriter.Write(marker);
                        if (_options.EnableColors && !string.IsNullOrEmpty(prefixStyle))
                            _outputWriter.Write(Ansi.Reset);
                        _outputWriter.Write(line);
                        _outputWriter.Write(Environment.NewLine);
                    }
                }
                else if (child is QuoteBlock nestedQuote)
                {
                    // Nested blockquote: prepend outer marker
                    string nestedPrefix = outerPrefix;
                    if (_options.EnableColors && !string.IsNullOrEmpty(prefixStyle))
                        nestedPrefix += prefixStyle + marker + Ansi.Reset;
                    else
                        nestedPrefix += marker;
                    RenderBlockquoteWithPrefix(nestedQuote, nestedPrefix);
                }
                else if (child is ListBlock list)
                {
                    // Render list with blockquote marker as prefix
                    string styledPrefix;
                    if (_options.EnableColors && !string.IsNullOrEmpty(prefixStyle))
                        styledPrefix = outerPrefix + prefixStyle + marker + Ansi.Reset;
                    else
                        styledPrefix = outerPrefix + marker;
                    RenderList(list, 0, styledPrefix);
                }
                else if (child is FencedCodeBlock codeBlock)
                {
                    if (_options.EnableColors && !string.IsNullOrEmpty(_options.Theme.CodeBlockStyle))
                        _outputWriter.Write(_options.Theme.CodeBlockStyle);
                    var lines = codeBlock.Lines;
                    for (int i = 0; i < lines.Count; i++)
                    {
                        _outputWriter.Write(outerPrefix);
                        if (_options.EnableColors && !string.IsNullOrEmpty(prefixStyle))
                            _outputWriter.Write(prefixStyle);
                        _outputWriter.Write(marker);
                        if (_options.EnableColors && !string.IsNullOrEmpty(prefixStyle))
                            _outputWriter.Write(Ansi.Reset);
                        _outputWriter.Write(lines.Lines[i].Slice.ToString());
                        _outputWriter.Write(Environment.NewLine);
                    }
                    if (_options.EnableColors && !string.IsNullOrEmpty(_options.Theme.CodeBlockStyle))
                        _outputWriter.Write(Ansi.Reset);
                }
                else if (child is ThematicBreakBlock)
                {
                    _outputWriter.Write(outerPrefix);
                    if (_options.EnableColors && !string.IsNullOrEmpty(prefixStyle))
                        _outputWriter.Write(prefixStyle);
                    _outputWriter.Write(marker);
                    if (_options.EnableColors && !string.IsNullOrEmpty(prefixStyle))
                        _outputWriter.Write(Ansi.Reset);
                    RenderThematicBreak();
                }
            }
        }

        private void RenderThematicBreak()
        {
            string hrStyle = Ansi.GetColorCode(_options.Theme.HorizontalRuleColor, foreground: true);
            int width = Math.Min(_maxWidth, 40);

            if (_options.EnableColors && !string.IsNullOrEmpty(hrStyle))
                _outputWriter.Write(hrStyle);

            _outputWriter.Write(new string(_options.Theme.HorizontalRuleChar, width));

            if (_options.EnableColors && !string.IsNullOrEmpty(hrStyle))
                _outputWriter.Write(Ansi.Reset);

            _outputWriter.Write(Environment.NewLine);
        }

        private void RenderHtmlBlock(HtmlBlock htmlBlock)
        {
            var lines = htmlBlock.Lines;
            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines.Lines[i];
                _outputWriter.Write(line.Slice.ToString());
                _outputWriter.Write(Environment.NewLine);
            }
        }

        // =====================================================================
        // Footnote Rendering
        // =====================================================================

        private void RenderFootnoteGroup(FootnoteGroup footnoteGroup)
        {
            // Render a separator before footnotes
            RenderThematicBreak();

            foreach (var block in footnoteGroup)
            {
                if (block is Footnote footnote)
                {
                    RenderFootnote(footnote);
                }
            }
        }

        private void RenderFootnote(Footnote footnote)
        {
            // Render footnote label
            string label = footnote.Order.ToString();
            if (_options.EnableColors && !string.IsNullOrEmpty(_options.Theme.FootnoteDefinitionLabelStyle))
                _outputWriter.Write(_options.Theme.FootnoteDefinitionLabelStyle);
            _outputWriter.Write($"[{label}]: ");
            if (_options.EnableColors && !string.IsNullOrEmpty(_options.Theme.FootnoteDefinitionLabelStyle))
                _outputWriter.Write(Ansi.Reset);

            // Render footnote content
            bool firstBlock = true;
            string continuationIndent = new string(' ', label.Length + 4); // "[N]: " width
            foreach (var child in footnote)
            {
                if (child is ParagraphBlock para)
                {
                    if (!firstBlock)
                        _outputWriter.Write(continuationIndent);
                    var sb = new StringBuilder();
                    RenderInlineToBuffer(para.Inline, sb);
                    _outputWriter.Write(sb.ToString());
                    _outputWriter.Write(Environment.NewLine);
                    firstBlock = false;
                }
            }
        }

        // =====================================================================
        // Definition List Rendering
        // =====================================================================

        private void RenderDefinitionList(DefinitionList definitionList)
        {
            foreach (var block in definitionList)
            {
                if (block is DefinitionItem item)
                {
                    RenderDefinitionItem(item);
                }
            }
        }

        private void RenderDefinitionItem(DefinitionItem item)
        {
            foreach (var child in item)
            {
                if (child is DefinitionTerm term)
                {
                    // Render the term with styling
                    if (_options.EnableColors && !string.IsNullOrEmpty(_options.Theme.DefinitionTermStyle))
                        _outputWriter.Write(_options.Theme.DefinitionTermStyle);
                    string text = GetInlineText(term.Inline);
                    _outputWriter.Write(text);
                    if (_options.EnableColors && !string.IsNullOrEmpty(_options.Theme.DefinitionTermStyle))
                        _outputWriter.Write(Ansi.Reset);
                    _outputWriter.Write(Environment.NewLine);
                }
                else if (child is ParagraphBlock para)
                {
                    // Render the definition with indent
                    _outputWriter.Write("    ");
                    var sb = new StringBuilder();
                    RenderInlineToBuffer(para.Inline, sb);
                    _outputWriter.Write(sb.ToString());
                    _outputWriter.Write(Environment.NewLine);
                }
            }
        }

        // =====================================================================
        // Math Block Rendering
        // =====================================================================

        private void RenderMathBlock(MathBlock mathBlock)
        {
            if (_options.EnableColors && !string.IsNullOrEmpty(_options.Theme.MathStyle))
                _outputWriter.Write(_options.Theme.MathStyle);

            var lines = mathBlock.Lines;
            for (int i = 0; i < lines.Count; i++)
            {
                _outputWriter.Write(lines.Lines[i].Slice.ToString());
                _outputWriter.Write(Environment.NewLine);
            }

            if (_options.EnableColors && !string.IsNullOrEmpty(_options.Theme.MathStyle))
                _outputWriter.Write(Ansi.Reset);
        }

        // =====================================================================
        // Custom Container Rendering
        // =====================================================================

        private void RenderCustomContainer(CustomContainer container)
        {
            string info = container.Info ?? "";
            string borderStyle = _options.EnableColors ? _options.Theme.CustomContainerBorderStyle : "";

            // Opening border
            if (!string.IsNullOrEmpty(borderStyle))
                _outputWriter.Write(borderStyle);
            _outputWriter.Write("┌");
            if (!string.IsNullOrEmpty(info))
                _outputWriter.Write($" {info} ");
            int borderWidth = Math.Min(Math.Max(0, _maxWidth - info.Length - 4), 40);
            _outputWriter.Write(new string('─', borderWidth));
            if (!string.IsNullOrEmpty(borderStyle))
                _outputWriter.Write(Ansi.Reset);
            _outputWriter.Write(Environment.NewLine);

            // Render child blocks
            bool needsSep = false;
            foreach (var child in container)
            {
                RenderBlock(child, ref needsSep);
            }

            // Closing border
            if (!string.IsNullOrEmpty(borderStyle))
                _outputWriter.Write(borderStyle);
            _outputWriter.Write("└" + new string('─', Math.Min(Math.Max(0, _maxWidth - 2), 40)));
            if (!string.IsNullOrEmpty(borderStyle))
                _outputWriter.Write(Ansi.Reset);
            _outputWriter.Write(Environment.NewLine);
        }

        // =====================================================================
        // Figure Rendering
        // =====================================================================

        private void RenderFigure(Figure figure)
        {
            foreach (var child in figure)
            {
                if (child is FigureCaption caption)
                {
                    if (_options.EnableColors)
                        _outputWriter.Write(Ansi.Italic);
                    string text = GetInlineText(caption.Inline);
                    _outputWriter.Write(text);
                    if (_options.EnableColors)
                        _outputWriter.Write(Ansi.ItalicOff);
                    _outputWriter.Write(Environment.NewLine);
                }
                else if (child is ParagraphBlock para)
                {
                    RenderParagraph(para);
                }
                else
                {
                    bool needsSep = false;
                    RenderBlock(child, ref needsSep);
                }
            }
        }

        // =====================================================================
        // Footer Rendering
        // =====================================================================

        private void RenderFooterBlock(FooterBlock footerBlock)
        {
            RenderThematicBreak();
            foreach (var child in footerBlock)
            {
                if (child is ParagraphBlock para)
                {
                    if (_options.EnableColors)
                        _outputWriter.Write(Ansi.Faint);
                    string text = GetInlineText(para.Inline);
                    _outputWriter.Write(text);
                    if (_options.EnableColors)
                        _outputWriter.Write(Ansi.Reset);
                    _outputWriter.Write(Environment.NewLine);
                }
            }
        }

        // =====================================================================
        // Table Rendering
        // =====================================================================

        /// <summary>
        /// Calculates the display width of a string, accounting for CJK (fullwidth) characters,
        /// emoji (including surrogate pairs), variation selectors, and ANSI escape sequences.
        /// </summary>
        private static int GetDisplayWidth(string text)
        {
            int width = 0;
            bool inEscape = false;
            int lastCpWidth = 0; // width contribution of the last visible character

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];

                // Skip ANSI escape sequences
                if (c == '\x1b')
                {
                    inEscape = true;
                    continue;
                }
                if (inEscape)
                {
                    if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
                        inEscape = false;
                    continue;
                }

                // Resolve full Unicode codepoint (handle surrogate pairs)
                int cp;
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    cp = char.ConvertToUtf32(c, text[i + 1]);
                    i++; // skip low surrogate
                }
                else
                {
                    cp = c;
                }

                // VS16 (FE0F): upgrades previous text-presentation emoji to wide (2 columns)
                if (cp == 0xFE0F)
                {
                    if (lastCpWidth == 1)
                    {
                        width += 1; // narrow → wide
                        lastCpWidth = 2;
                    }
                    continue;
                }

                // Other zero-width characters
                if (cp == 0xFE0E                            // VS15 (text presentation)
                    || cp == 0x200D                          // zero-width joiner
                    || (cp >= 0x0300 && cp <= 0x036F)        // combining diacriticals
                    || (cp >= 0x1F3FB && cp <= 0x1F3FF)      // skin tone modifiers
                    || (cp >= 0x20D0 && cp <= 0x20FF)        // combining marks for symbols
                    || (cp >= 0xE0020 && cp <= 0xE007F))     // tags
                    continue;

                int cpWidth = IsWideCodepoint(cp) ? 2 : 1;
                width += cpWidth;
                lastCpWidth = cpWidth;
            }
            return width;
        }

        /// <summary>
        /// Determines if a Unicode codepoint displays as 2 columns in a terminal.
        /// Covers CJK, fullwidth forms, and emoji.
        /// </summary>
        private static bool IsWideCodepoint(int cp)
        {
            // --- CJK & Fullwidth ---
            if (cp >= 0x1100 && cp <= 0x115F) return true;    // Hangul Jamo
            if (cp >= 0x2E80 && cp <= 0x303E) return true;    // CJK Radicals .. CJK Symbols
            if (cp >= 0x3041 && cp <= 0x33BF) return true;    // Hiragana .. CJK Compat
            if (cp >= 0x3400 && cp <= 0x4DBF) return true;    // CJK Ext A
            if (cp >= 0x4E00 && cp <= 0x9FFF) return true;    // CJK Unified
            if (cp >= 0xA000 && cp <= 0xA4CF) return true;    // Yi
            if (cp >= 0xAC00 && cp <= 0xD7AF) return true;    // Hangul Syllables
            if (cp >= 0xF900 && cp <= 0xFAFF) return true;    // CJK Compat Ideographs
            if (cp >= 0xFE30 && cp <= 0xFE6F) return true;    // CJK Compat Forms
            if (cp >= 0xFF01 && cp <= 0xFF60) return true;    // Fullwidth Forms
            if (cp >= 0xFFE0 && cp <= 0xFFE6) return true;    // Fullwidth Signs

            // --- BMP Emoji with Emoji_Presentation=Yes (default 2 columns) ---
            // U+2300 block
            if (cp == 0x231A || cp == 0x231B) return true;    // ⌚⌛
            if (cp >= 0x23E9 && cp <= 0x23F3) return true;    // ⏩..⏳
            if (cp >= 0x23F8 && cp <= 0x23FA) return true;    // ⏸..⏺
            // U+2500 block
            if (cp == 0x25AA || cp == 0x25AB) return true;    // ▪▫
            if (cp == 0x25B6 || cp == 0x25C0) return true;    // ▶◀
            if (cp >= 0x25FB && cp <= 0x25FE) return true;    // ◻..◾
            // U+2600 Misc Symbols (only Emoji_Presentation=Yes)
            if (cp == 0x2614 || cp == 0x2615) return true;    // ☔☕
            if (cp >= 0x2648 && cp <= 0x2653) return true;    // ♈..♓ zodiac
            if (cp == 0x267F) return true;                     // ♿
            if (cp == 0x2693) return true;                     // ⚓
            if (cp == 0x26A1) return true;                     // ⚡
            if (cp == 0x26AA || cp == 0x26AB) return true;     // ⚪⚫
            if (cp == 0x26BD || cp == 0x26BE) return true;     // ⚽⚾
            if (cp == 0x26C4 || cp == 0x26C5) return true;     // ⛄⛅
            if (cp == 0x26CE) return true;                     // ⛎
            if (cp == 0x26D4) return true;                     // ⛔
            if (cp == 0x26EA) return true;                     // ⛪
            if (cp == 0x26F2 || cp == 0x26F3) return true;     // ⛲⛳
            if (cp == 0x26F5) return true;                     // ⛵
            if (cp == 0x26FA) return true;                     // ⛺
            if (cp == 0x26FD) return true;                     // ⛽
            // U+2700 Dingbats (only Emoji_Presentation=Yes)
            if (cp == 0x2705) return true;                     // ✅
            if (cp == 0x270A || cp == 0x270B) return true;     // ✊✋
            if (cp == 0x2728) return true;                     // ✨
            if (cp == 0x274C) return true;                     // ❌
            if (cp == 0x274E) return true;                     // ❎
            if (cp >= 0x2753 && cp <= 0x2755) return true;     // ❓❔❕
            if (cp == 0x2757) return true;                     // ❗
            if (cp >= 0x2795 && cp <= 0x2797) return true;     // ➕➖➗
            if (cp == 0x27B0) return true;                     // ➰
            if (cp == 0x27BF) return true;                     // ➿
            // U+2900+ block
            if (cp == 0x2934 || cp == 0x2935) return true;     // ⤴⤵
            if (cp >= 0x2B05 && cp <= 0x2B07) return true;     // ⬅⬆⬇
            if (cp >= 0x2B1B && cp <= 0x2B1C) return true;     // ⬛⬜
            if (cp == 0x2B50 || cp == 0x2B55) return true;     // ⭐⭕
            if (cp == 0x3030 || cp == 0x303D) return true;     // 〰〽
            if (cp == 0x3297 || cp == 0x3299) return true;     // ㊗㊙

            // --- Supplementary Plane Emoji & CJK ---
            if (cp >= 0x1F000 && cp <= 0x1FAFF) return true;  // All emoji blocks
            if (cp >= 0x1FB00 && cp <= 0x1FBFF) return true;  // Legacy computing symbols
            if (cp >= 0x20000 && cp <= 0x2FA1F) return true;  // CJK Ext B-F

            return false;
        }

        /// <summary>
        /// Pads a string to fill a given display width, respecting fullwidth characters.
        /// </summary>
        private static string PadToDisplayWidth(string text, int targetWidth, ColumnAlignment alignment)
        {
            int displayWidth = GetDisplayWidth(text);
            int padding = targetWidth - displayWidth;
            if (padding <= 0) return text;

            switch (alignment)
            {
                case ColumnAlignment.Right:
                    return new string(' ', padding) + text;
                case ColumnAlignment.Center:
                    int left = padding / 2;
                    int right = padding - left;
                    return new string(' ', left) + text + new string(' ', right);
                default:
                    return text + new string(' ', padding);
            }
        }

        private void RenderTable(Table table)
        {
            if (table.Count == 0) return;

            var theme = _options.Theme ?? ConsoleTheme.Default;
            bool useAnsi = _options.EnableColors;
            string borderStyle = useAnsi ? theme.TableBorderStyle : "";
            string headerStyle = useAnsi ? theme.TableHeaderStyle : "";
            string reset = useAnsi ? Ansi.Reset : "";

            // Gather column definitions
            var columnDefs = table.ColumnDefinitions;
            int columnCount = columnDefs?.Count ?? 0;
            if (columnCount == 0 && table.Count > 0 && table[0] is TableRow firstRow)
                columnCount = firstRow.Count;

            var alignments = new ColumnAlignment[columnCount];
            if (columnDefs != null)
            {
                for (int i = 0; i < columnCount && i < columnDefs.Count; i++)
                {
                    alignments[i] = columnDefs[i].Alignment switch
                    {
                        TableColumnAlign.Center => ColumnAlignment.Center,
                        TableColumnAlign.Right => ColumnAlignment.Right,
                        _ => ColumnAlignment.Left
                    };
                }
            }

            // Parse all cells into plain text
            var headerCells = new List<string>();
            var dataRows = new List<List<string>>();
            bool isFirst = true;

            foreach (var block in table)
            {
                if (block is TableRow row)
                {
                    var cells = new List<string>();
                    foreach (var cell in row)
                    {
                        if (cell is TableCell tc)
                        {
                            string cellText = "";
                            if (tc.Count > 0 && tc[0] is ParagraphBlock para)
                                cellText = GetInlineText(para.Inline);
                            cells.Add(cellText);
                        }
                    }

                    if (isFirst && row.IsHeader)
                    {
                        headerCells = cells;
                        isFirst = false;
                    }
                    else
                    {
                        dataRows.Add(cells);
                        isFirst = false;
                    }
                }
            }

            // Calculate column widths based on display width (CJK-aware)
            var colWidths = new int[columnCount];
            for (int i = 0; i < columnCount; i++)
            {
                int hw = (i < headerCells.Count) ? GetDisplayWidth(headerCells[i]) : 0;
                int maxData = 0;
                foreach (var row in dataRows)
                {
                    int cw = (i < row.Count) ? GetDisplayWidth(row[i]) : 0;
                    if (cw > maxData) maxData = cw;
                }
                colWidths[i] = Math.Max(Math.Max(hw, maxData), 3);
            }

            // Box-drawing characters
            const string TL = "┌", TR = "┐", BL = "└", BR = "┘";
            const string HZ = "─", VT = "│";
            const string TT = "┬", TB = "┴", ML = "├", MR = "┤", CX = "┼";

            var sb = new System.Text.StringBuilder();

            // Helper to write a horizontal border line
            void AppendBorder(string left, string mid, string right)
            {
                sb.Append(borderStyle);
                sb.Append(left);
                for (int i = 0; i < columnCount; i++)
                {
                    sb.Append(new string('─', colWidths[i] + 2)); // +2 for cell padding
                    sb.Append(i < columnCount - 1 ? mid : right);
                }
                if (useAnsi) sb.Append(reset);
                sb.Append(Environment.NewLine);
            }

            // Helper to write a data row
            void AppendRow(List<string> cells, bool isHeader)
            {
                sb.Append(borderStyle);
                sb.Append(VT);
                if (useAnsi) sb.Append(reset);

                for (int i = 0; i < columnCount; i++)
                {
                    string text = (i < cells.Count) ? cells[i] : "";
                    string padded = PadToDisplayWidth(text, colWidths[i], alignments[i]);
                    sb.Append(' ');
                    if (isHeader && headerStyle.Length > 0)
                    {
                        sb.Append(headerStyle);
                        sb.Append(padded);
                        sb.Append(reset);
                    }
                    else
                    {
                        sb.Append(padded);
                    }
                    sb.Append(' ');
                    sb.Append(borderStyle);
                    sb.Append(VT);
                    if (useAnsi) sb.Append(reset);
                }
                sb.Append(Environment.NewLine);
            }

            // ┌───┬───┐
            AppendBorder(TL, TT, TR);

            // │ Header │
            if (headerCells.Count > 0)
            {
                AppendRow(headerCells, isHeader: true);
                // ├───┼───┤
                AppendBorder(ML, CX, MR);
            }

            // │ Data │
            for (int r = 0; r < dataRows.Count; r++)
            {
                AppendRow(dataRows[r], isHeader: false);
            }

            // └───┴───┘
            AppendBorder(BL, TB, BR);

            _outputWriter.Write(sb.ToString());
        }

        // =====================================================================
        // Inline Rendering
        // =====================================================================

        private string GetInlineText(ContainerInline? container)
        {
            if (container == null) return string.Empty;
            var sb = new StringBuilder();
            RenderInlineToBuffer(container, sb);
            return sb.ToString();
        }

        private void RenderInlineToBuffer(ContainerInline? container, StringBuilder sb, bool skipFirstTaskList = false)
        {
            if (container == null) return;

            bool skippedTask = false;
            foreach (var inline in container)
            {
                // Skip the TaskList inline if we're in a task list item (bullet handles it)
                if (skipFirstTaskList && !skippedTask && inline is TaskList)
                {
                    skippedTask = true;
                    continue;
                }

                RenderSingleInline(inline, sb);
            }
        }

        private void RenderSingleInline(Inline inline, StringBuilder sb)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    sb.Append(literal.Content.ToString());
                    break;

                case EmphasisInline emphasis:
                    RenderEmphasis(emphasis, sb);
                    break;

                case CodeInline code:
                    RenderInlineCode(code, sb);
                    break;

                case LinkInline link:
                    RenderLink(link, sb);
                    break;

                case AutolinkInline autolink:
                    if (_options.EnableColors)
                        sb.Append(_options.Theme.LinkTextStyle);
                    sb.Append(autolink.Url);
                    if (_options.EnableColors)
                        sb.Append(Ansi.Reset);
                    break;

                case FootnoteLink footnoteLink:
                    if (!footnoteLink.IsBackLink && footnoteLink.Footnote != null)
                    {
                        if (_options.EnableColors)
                            sb.Append(_options.Theme.FootnoteReferenceStyle);
                        sb.Append($"[{footnoteLink.Footnote.Order}]");
                        if (_options.EnableColors)
                            sb.Append(Ansi.Reset);
                    }
                    // Back-references are not rendered in console output
                    break;

                case LineBreakInline lineBreak:
                    if (lineBreak.IsHard)
                        sb.Append(Environment.NewLine);
                    else
                        sb.Append(' ');
                    break;

                case HtmlInline htmlInline:
                    if (!_options.StripHtml)
                        sb.Append(htmlInline.Tag);
                    break;

                case HtmlEntityInline htmlEntity:
                    sb.Append(htmlEntity.Transcoded.ToString());
                    break;

                case MathInline mathInline:
                    if (_options.EnableColors && !string.IsNullOrEmpty(_options.Theme.MathStyle))
                        sb.Append(_options.Theme.MathStyle);
                    sb.Append(mathInline.Content.ToString());
                    if (_options.EnableColors && !string.IsNullOrEmpty(_options.Theme.MathStyle))
                        sb.Append(Ansi.Reset);
                    break;

                case AbbreviationInline abbreviation:
                    if (_options.EnableColors && !string.IsNullOrEmpty(_options.Theme.AbbreviationStyle))
                        sb.Append(_options.Theme.AbbreviationStyle);
                    sb.Append(abbreviation.Abbreviation.Label);
                    if (_options.EnableColors && !string.IsNullOrEmpty(_options.Theme.AbbreviationStyle))
                        sb.Append(Ansi.Reset);
                    break;

                case ContainerInline container:
                    RenderInlineToBuffer(container, sb);
                    break;

                default:
                    _log($"RenderSingleInline: Unhandled inline type: {inline.GetType().Name}");
                    break;
            }
        }

        private void RenderEmphasis(EmphasisInline emphasis, StringBuilder sb)
        {
            string openStyle = string.Empty;
            string closeStyle = string.Empty;

            switch (emphasis.DelimiterChar)
            {
                case '~' when emphasis.DelimiterCount == 2:
                    openStyle = _options.Theme.StrikethroughStyle;
                    closeStyle = Ansi.StrikethroughOff;
                    break;
                case '~' when emphasis.DelimiterCount == 1:
                    openStyle = _options.Theme.SubscriptStyle;
                    closeStyle = Ansi.Reset;
                    break;
                case '^':
                    openStyle = _options.Theme.SuperscriptStyle;
                    closeStyle = Ansi.Reset;
                    break;
                case '+' when emphasis.DelimiterCount == 2:
                    openStyle = _options.Theme.InsertedStyle;
                    closeStyle = Ansi.UnderlineOff;
                    break;
                case '=' when emphasis.DelimiterCount == 2:
                    openStyle = _options.Theme.MarkedStyle;
                    closeStyle = Ansi.ReverseVideoOff;
                    break;
                case '"':
                    openStyle = _options.Theme.CitationStyle;
                    closeStyle = Ansi.ItalicOff;
                    break;
                default:
                    // Standard * or _ emphasis
                    if (emphasis.DelimiterCount >= 3)
                    {
                        openStyle = _options.Theme.BoldStyle + _options.Theme.ItalicStyle;
                        closeStyle = Ansi.ItalicOff + Ansi.BoldOff;
                    }
                    else if (emphasis.DelimiterCount == 2)
                    {
                        openStyle = _options.Theme.BoldStyle;
                        closeStyle = Ansi.BoldOff;
                    }
                    else
                    {
                        openStyle = _options.Theme.ItalicStyle;
                        closeStyle = Ansi.ItalicOff;
                    }
                    break;
            }

            if (_options.EnableColors && !string.IsNullOrEmpty(openStyle))
                sb.Append(openStyle);

            foreach (var child in emphasis)
                RenderSingleInline(child, sb);

            if (_options.EnableColors && !string.IsNullOrEmpty(closeStyle))
                sb.Append(closeStyle);
        }

        private void RenderInlineCode(CodeInline code, StringBuilder sb)
        {
            if (_options.EnableColors && !string.IsNullOrEmpty(_options.Theme.InlineCodeStyle))
                sb.Append(_options.Theme.InlineCodeStyle);

            sb.Append(code.Content);

            if (_options.EnableColors && !string.IsNullOrEmpty(_options.Theme.InlineCodeStyle))
                sb.Append(Ansi.Reset);
        }

        private void RenderLink(LinkInline link, StringBuilder sb)
        {
            if (link.IsImage)
            {
                RenderImage(link, sb);
                return;
            }

            string linkText = GetInlineText(link);
            string? url = link.Url;

            if (_options.UseHyperlinks && !string.IsNullOrEmpty(url))
            {
                sb.Append(Ansi.HyperlinkStart(url));
                sb.Append(linkText);
                sb.Append(Ansi.HyperlinkEnd());
            }
            else
            {
                if (_options.EnableColors)
                    sb.Append(_options.Theme.LinkTextStyle);
                sb.Append(linkText);
                if (_options.EnableColors)
                    sb.Append(Ansi.Reset);

                if (!string.IsNullOrEmpty(url))
                {
                    sb.Append(" (");
                    if (_options.EnableColors)
                        sb.Append(_options.Theme.LinkUrlStyle);
                    sb.Append(url);
                    if (_options.EnableColors)
                        sb.Append(Ansi.Reset);
                    sb.Append(")");
                }
            }
        }

        private void RenderImage(LinkInline image, StringBuilder sb)
        {
            // Get alt text from children
            string altText = GetInlineText(image);

            sb.Append(_options.Theme.ImagePrefix);

            if (_options.EnableColors)
                sb.Append(Ansi.Faint);
            sb.Append(altText);
            if (_options.EnableColors)
                sb.Append(Ansi.Reset);

            sb.Append(_options.Theme.ImageSuffix);
        }

        // =====================================================================
        // Text Wrapping (ANSI-aware)
        // =====================================================================

        private void WriteWrappedText(string text)
        {
            int consoleWidth = _maxWidth;
            int currentPos = 0;

            while (currentPos < text.Length)
            {
                // Walk through the string counting only visible characters
                int visibleCount = 0;
                int scanPos = currentPos;
                int lastSpacePos = -1;

                while (scanPos < text.Length && visibleCount < consoleWidth)
                {
                    if (text[scanPos] == '\x1B')
                    {
                        // Skip ANSI escape sequence
                        scanPos = SkipAnsiSequence(text, scanPos);
                        continue;
                    }

                    if (text[scanPos] == ' ')
                        lastSpacePos = scanPos;

                    visibleCount++;
                    scanPos++;
                }

                // If all remaining text fits, write it and done
                if (scanPos >= text.Length)
                {
                    _outputWriter.Write(text.Substring(currentPos));
                    _outputWriter.WriteLine();
                    break;
                }

                // Need to wrap
                if (lastSpacePos > currentPos)
                {
                    _outputWriter.Write(text.Substring(currentPos, lastSpacePos - currentPos));
                    _outputWriter.WriteLine();
                    currentPos = lastSpacePos + 1;
                }
                else
                {
                    // No space found, hard break
                    _outputWriter.Write(text.Substring(currentPos, scanPos - currentPos));
                    _outputWriter.WriteLine();
                    currentPos = scanPos;
                }
            }
        }

        /// <summary>
        /// Advances past an ANSI escape sequence starting at position i (which points to ESC).
        /// Returns the position after the sequence.
        /// </summary>
        private static int SkipAnsiSequence(string text, int i)
        {
            i++; // skip ESC
            if (i >= text.Length) return i;

            if (text[i] == '[')
            {
                // CSI sequence: ESC [ (params) final_byte
                i++;
                while (i < text.Length && text[i] < 0x40)
                    i++;
                if (i < text.Length) i++; // skip final byte
            }
            else if (text[i] == ']')
            {
                // OSC sequence: ESC ] ... BEL or ESC \
                i++;
                while (i < text.Length && text[i] != '\a' && text[i] != '\x1B')
                    i++;
                if (i < text.Length && text[i] == '\a')
                    i++;
                else if (i + 1 < text.Length && text[i] == '\x1B' && text[i + 1] == '\\')
                    i += 2;
            }
            else
            {
                i++; // Other 2-byte escape sequence
            }

            return i;
        }
    }
}
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Text;
using Robust.Client.Graphics;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.RichText;
using Robust.Shared.Collections;
using Robust.Shared.Maths;
using Robust.Shared.Utility;

namespace Robust.Client.UserInterface
{
    /// <summary>
    ///     Used by <see cref="OutputPanel"/> and <see cref="RichTextLabel"/> to handle rich text layout.
    /// </summary>
    internal struct RichTextEntry
    {
        private readonly Color _defaultColor;
        private readonly MarkupTagManager _tagManager;
        private readonly Type[]? _tagsAllowed;

        public readonly FormattedMessage Message;

        /// <summary>
        ///     The vertical size of this entry, in pixels.
        /// </summary>
        public int Height;

        /// <summary>
        ///     The horizontal size of this entry, in pixels.
        /// </summary>
        public int Width;

        /// <summary>
        ///     How many times was this message repeated in chat.
        /// </summary>
        public int ChatStacks;

        /// <summary>
        ///     The combined text indices in the message's text tags to put line breaks.
        /// </summary>
        public ValueList<int> LineBreaks;

        /// <summary>
        ///     All fonts used in this message.
        /// </summary>
        public List<Font> Fonts;

        private readonly Dictionary<int, Control> _tagControls = new();

        public RichTextEntry(FormattedMessage message, Control parent, MarkupTagManager tagManager, Type[]? tagsAllowed = null, Color? defaultColor = null)
        {
            Message = message;
            Height = 0;
            Width = 0;
            LineBreaks = default;
            Fonts = [];
            _defaultColor = defaultColor ?? new(200, 200, 200);
            _tagManager = tagManager;
            _tagsAllowed = tagsAllowed;

            var nodeIndex = -1;
            foreach (var node in Message.Nodes)
            {
                nodeIndex++;

                if (node.Name == null)
                    continue;

                if (!_tagManager.TryGetMarkupTag(node.Name, _tagsAllowed, out var tag) || !tag.TryGetControl(node, out var control))
                    continue;

                parent.Children.Add(control);
                _tagControls.Add(nodeIndex, control);
            }
        }

        /// <summary>
        ///     Recalculate line dimensions and where it has line breaks for word wrapping.
        /// </summary>
        /// <param name="defaultFont">The font being used for display.</param>
        /// <param name="maxSizeX">The maximum horizontal size of the container of this entry.</param>
        /// <param name="uiScale"></param>
        /// <param name="lineHeightScale"></param>
        public void Update(Font defaultFont, float maxSizeX, float uiScale, float lineHeightScale = 1)
        {
            // This method is gonna suck due to complexity.
            // Bear with me here.
            // I am so deeply sorry for the person adding stuff to this in the future.

            Height = 0;
            LineBreaks.Clear();
            Fonts = [];

            Fonts.Add(defaultFont);

            int? breakLine;
            var wordWrap = new WordWrap(maxSizeX);
            var context = new MarkupDrawingContext();
            context.Font.Push(defaultFont);
            context.Color.Push(_defaultColor);

            // Go over every node.
            // Nodes can change the markup drawing context and return additional text.
            // It's also possible for nodes to return inline controls. They get treated as one large rune.
            var nodeIndex = -1;
            foreach (var node in Message.Nodes)
            {
                nodeIndex++;
                var text = ProcessNode(node, context);

                if (text == "")
                    continue;

                if (!context.Font.TryPeek(out var font))
                    font = defaultFont;

                // And go over every character.
                foreach (var rune in text.EnumerateRunes())
                {
                    if (!Fonts.Contains(font))
                    {
                        Fonts.Add(font);
                    }

                    if (ProcessRune(ref this, rune, out breakLine))
                        continue;

                    // Uh just skip unknown characters I guess.
                    if (!font.TryGetCharMetrics(rune, uiScale, out var metrics))
                        continue;

                    if (ProcessMetric(ref this, metrics, out breakLine))
                        return;
                }

                if (!_tagControls.TryGetValue(nodeIndex, out var control))
                    continue;

                if (ProcessRune(ref this, new Rune(' '), out breakLine))
                    continue;

                control.Measure(new Vector2(Width, Height));

                var desiredSize = control.DesiredPixelSize;
                var controlMetrics = new CharMetrics(
                    0, 0,
                    desiredSize.X,
                    desiredSize.X,
                    desiredSize.Y);

                if (ProcessMetric(ref this, controlMetrics, out breakLine))
                    return;
            }

            Height += GetLineHeightScaled(Fonts, uiScale, lineHeightScale);

            Width = wordWrap.FinalizeText(out breakLine);
            CheckLineBreak(ref this, breakLine);

            bool ProcessRune(ref RichTextEntry src, Rune rune, out int? outBreakLine)
            {
                wordWrap.NextRune(rune, out breakLine, out var breakNewLine, out var skip);
                CheckLineBreak(ref src, breakLine);
                CheckLineBreak(ref src, breakNewLine);
                outBreakLine = breakLine;
                return skip;
            }

            bool ProcessMetric(ref RichTextEntry src, CharMetrics metrics, out int? outBreakLine)
            {
                wordWrap.NextMetrics(metrics, out breakLine, out var abort);
                CheckLineBreak(ref src, breakLine);
                outBreakLine = breakLine;
                return abort;
            }

            void CheckLineBreak(ref RichTextEntry src, int? line)
            {
                if (line is { } l)
                {
                    src.LineBreaks.Add(l);

                    src.Height += GetLineHeightScaled(src.Fonts, uiScale, lineHeightScale);
                }
            }
        }

        /* I MADE THIS SHIT SOMEWHAT WORK WITH LARGER FONTS
         IT DOES NOT SUPPORT VARYING FONTS THO
         OR SOMETIMES IT DOES, USE AT YOUR OWN RISK */

        public readonly void Draw(
            DrawingHandleScreen handle,
            Font defaultFont,
            UIBox2 drawBox,
            float verticalOffset,
            MarkupDrawingContext context,
            float uiScale,
            float lineHeightScale = 1)
        {
            context.Clear();
            context.Color.Push(_defaultColor);
            context.Font.Push(defaultFont);

            var globalBreakCounter = 0;
            var lineBreakIndex = 0;

            var baseLine = drawBox.TopLeft + new Vector2(0, GetAscentScaled(Fonts, uiScale, lineHeightScale) + verticalOffset);
            var controlYAdvance = 0f;

            var nodeIndex = -1;
            foreach (var node in Message.Nodes)
            {
                nodeIndex++;
                var text = ProcessNode(node, context);

                if (text == "")
                    continue;

                //System.Console.WriteLine($"{text} adding -> {GetAscentScaled(LineFonts[1], uiScale, lineHeightScale)}");

                if (!context.Color.TryPeek(out var color))
                {
                    color = _defaultColor;
                }

                if (!context.Font.TryPeek(out var font))
                {
                    font = defaultFont;
                }

                foreach (var rune in text.EnumerateRunes())
                {
                    if (lineBreakIndex < LineBreaks.Count &&
                        LineBreaks[lineBreakIndex] == globalBreakCounter)
                    {
                        baseLine = new Vector2(drawBox.Left, baseLine.Y + GetLineHeightScaled(Fonts, uiScale, lineHeightScale) + controlYAdvance);
                        controlYAdvance = 0;
                        lineBreakIndex += 1;
                    }

                    var advance = font.DrawChar(handle, rune, baseLine, uiScale, color);
                    baseLine += new Vector2(advance, 0);

                    globalBreakCounter += 1;
                }

                if (!_tagControls.TryGetValue(nodeIndex, out var control))
                    continue;

                var invertedScale = 1f / uiScale;

                control.Position = new Vector2(baseLine.X * invertedScale, (baseLine.Y - GetLargestFont(Fonts, uiScale).GetAscent(uiScale)) * invertedScale);
                control.Measure(new Vector2(Width, Height));
                var advanceX = control.DesiredPixelSize.X;
                controlYAdvance = Math.Max(0f, (control.DesiredPixelSize.Y - GetLineHeightScaled(Fonts, uiScale, lineHeightScale) * invertedScale));
                baseLine += new Vector2(advanceX, 0);
            }


            // Draw the chat stacks icon

            if (ChatStacks > 1)
            {
                // Draw the background circle
                baseLine += new Vector2(0, 7 * uiScale);
                defaultFont.DrawChar(handle, new Rune('\u25cf'), baseLine, uiScale * 3f, Color.Crimson);
                baseLine += new Vector2(7 * uiScale, -9 * uiScale);

                var chatStacksString = $"x{ChatStacks}";
                var scale = 0.8f;

                if (ChatStacks > 9)
                {
                    baseLine += new Vector2(-2 * uiScale, -1 * uiScale);
                    scale = 0.7f;
                }

                foreach (var rune in chatStacksString.EnumerateRunes())
                {
                    var advance = defaultFont.DrawChar(handle, rune, baseLine, uiScale * scale, Color.White);
                    baseLine += new Vector2(advance, 0);
                }
            }
        }

        private readonly string ProcessNode(MarkupNode node, MarkupDrawingContext context)
        {
            // If a nodes name is null it's a text node.
            if (node.Name == null)
                return node.Value.StringValue ?? "";

            //Skip the node if there is no markup tag for it.
            if (!_tagManager.TryGetMarkupTag(node.Name, _tagsAllowed, out var tag))
                return "";

            if (!node.Closing)
            {
                context.Tags.Add(tag);
                tag.PushDrawContext(node, context);
                return tag.TextBefore(node);
            }

            context.Tags.Remove(tag);
            tag.PopDrawContext(node, context);
            return tag.TextAfter(node);
        }

        private static int GetLineHeightScaled(List<Font> fontsInLine, float uiScale, float lineHeightScale)
        {
            var height = 0;

            foreach (var font in fontsInLine)
            {
                if (font.GetLineHeight(uiScale) > height)
                    height = font.GetLineHeight(uiScale);
            }

            return (int)(height * lineHeightScale);
        }

        private static int GetAscentScaled(List<Font> fontsInLine, float uiScale, float lineHeightScale)
        {
            var ascent = 0;

            foreach (var font in fontsInLine)
            {
                if (font.GetAscent(uiScale) > ascent)
                    ascent = font.GetAscent(uiScale);
            }

            return (int)(ascent * lineHeightScale);
        }

        private static Font GetLargestFont(List<Font> fontsInLine, float uiScale)
        {
            var largestfont = fontsInLine.Last();

            foreach (var font in fontsInLine)
            {
                if (font.GetLineHeight(uiScale) > largestfont.GetLineHeight(uiScale))
                    largestfont = font;
            }

            return largestfont;
        }
    }
}

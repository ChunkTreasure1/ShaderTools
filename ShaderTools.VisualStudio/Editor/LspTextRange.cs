using System;
using Microsoft.VisualStudio.Text;
using Newtonsoft.Json.Linq;

namespace ShaderTools.VisualStudio.Editor
{
    internal readonly struct LspTextPosition : IComparable<LspTextPosition>
    {
        internal LspTextPosition(int line, int character)
        {
            Line = line;
            Character = character;
        }

        internal int Line { get; }

        internal int Character { get; }

        public int CompareTo(LspTextPosition other)
        {
            var lineComparison = Line.CompareTo(other.Line);
            return lineComparison != 0 ? lineComparison : Character.CompareTo(other.Character);
        }
    }

    internal readonly struct LspTextRange
    {
        internal LspTextRange(LspTextPosition start, LspTextPosition end)
        {
            Start = start;
            End = end;
        }

        internal LspTextPosition Start { get; }

        internal LspTextPosition End { get; }

        internal static bool TryParse(JToken token, out LspTextRange range)
        {
            range = default;
            if (!(token is JObject rangeObject) ||
                !TryParsePosition(rangeObject["start"], out var start) ||
                !TryParsePosition(rangeObject["end"], out var end) ||
                start.CompareTo(end) >= 0)
            {
                return false;
            }

            range = new LspTextRange(start, end);
            return true;
        }

        internal bool TryGetSpan(ITextSnapshot snapshot, out Span span)
        {
            span = default;
            if (!TryGetOffset(snapshot, Start, out var start) ||
                !TryGetOffset(snapshot, End, out var end) ||
                end <= start)
            {
                return false;
            }

            span = Span.FromBounds(start, end);
            return true;
        }

        private static bool TryParsePosition(JToken token, out LspTextPosition position)
        {
            position = default;
            if (!(token is JObject positionObject) ||
                !TryReadNonNegativeInt(positionObject["line"], out var line) ||
                !TryReadNonNegativeInt(positionObject["character"], out var character))
            {
                return false;
            }

            position = new LspTextPosition(line, character);
            return true;
        }

        internal static bool TryReadNonNegativeInt(JToken token, out int value)
        {
            value = 0;
            if (token == null || token.Type != JTokenType.Integer)
            {
                return false;
            }

            var longValue = token.Value<long>();
            if (longValue < 0 || longValue > int.MaxValue)
            {
                return false;
            }

            value = (int)longValue;
            return true;
        }

        private static bool TryGetOffset(ITextSnapshot snapshot, LspTextPosition position, out int offset)
        {
            offset = 0;
            if (position.Line < 0 || position.Line >= snapshot.LineCount)
            {
                return false;
            }

            var line = snapshot.GetLineFromLineNumber(position.Line);
            if (position.Character < 0 || position.Character > line.Length)
            {
                return false;
            }

            offset = line.Start.Position + position.Character;
            return true;
        }
    }
}

internal static class JsonSyntax
{
    public static IReadOnlyList<Segment> Highlight(string line)
    {
        var segments = new List<Segment>();
        var i = 0;

        while (i < line.Length)
        {
            var c = line[i];

            if (c == '"')
            {
                var end = i + 1;
                while (end < line.Length)
                {
                    if (line[end] == '\\' && end + 1 < line.Length)
                    {
                        end += 2;
                        continue;
                    }

                    if (line[end] == '"')
                    {
                        end++;
                        break;
                    }

                    end++;
                }

                var peek = end;
                while (peek < line.Length && char.IsWhiteSpace(line[peek]))
                    peek++;

                var isKey = peek < line.Length && line[peek] == ':';
                segments.Add(new Segment(line[i..end], isKey ? Palette.Key : Palette.String));
                i = end;
                continue;
            }

            if (char.IsAsciiDigit(c) || (c == '-' && i + 1 < line.Length && char.IsAsciiDigit(line[i + 1])))
            {
                var end = i + 1;
                while (end < line.Length && (char.IsAsciiDigit(line[end]) || line[end] is '.' or 'e' or 'E' or '+' or '-'))
                    end++;

                segments.Add(new Segment(line[i..end], Palette.Number));
                i = end;
                continue;
            }

            if (char.IsAsciiLetter(c))
            {
                var end = i + 1;
                while (end < line.Length && char.IsAsciiLetter(line[end]))
                    end++;

                var word = line[i..end];
                segments.Add(new Segment(word, word is "true" or "false" or "null" ? Palette.Keyword : Palette.Normal));
                i = end;
                continue;
            }

            segments.Add(new Segment(c.ToString(), "{}[]:,".Contains(c) ? Palette.Punctuation : Palette.Normal));
            i++;
        }

        return segments;
    }
}

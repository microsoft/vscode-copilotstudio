namespace Microsoft.PowerPlatformLS.Contracts.Internal
{
    public class IndentationInfo(int Size = IndentationInfo.DefaultSize, char Character = IndentationInfo.DefaultCharacter)
    {
        public char Character { get; } = Character;
        public int Size { get; } = Size;

        public const char DefaultCharacter = ' ';
        public const int DefaultSize = 2;

        public string Indent()
        {
            return new string(Character, Size);
        }

        /// <summary>
        /// Read the first lines of <paramref name="text"/> until one line has a white space, check whether it's a tab or space and count how many. Assume this is the indentation style of the text.
        /// </summary>
        public static IndentationInfo FromText(string text)
        {
            using var reader = new StringReader(text);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                // find the first line with heading white space
                if (string.IsNullOrWhiteSpace(line) || !char.IsWhiteSpace(line[0]))
                {
                    continue;
                }

                var indentation = line.TakeWhile(char.IsWhiteSpace).ToArray();

                // indentation cannot be empty because otherwise, line would be skipped
                var type = indentation[0];
                var size = type == '\t' ? 1 : indentation.Length;

                return new(size, type);
            }

            return new();
        }
    }
}
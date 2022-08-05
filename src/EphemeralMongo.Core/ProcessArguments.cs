using System.Text;

namespace EphemeralMongo;

public static class ProcessArguments
{
    private const char DoubleQuote = '"';
    private const char Backslash = '\\';

    // Inspired from https://github.com/dotnet/runtime/blob/v6.0.0/src/libraries/System.Private.CoreLib/src/System/PasteArguments.cs
    public static string Escape(string path)
    {
        // Path does not need to be escaped
        if (path.Length != 0 && path.All(c => !char.IsWhiteSpace(c) && c != DoubleQuote))
        {
            return path;
        }

        var stringBuilder = new StringBuilder();

        stringBuilder.Append(DoubleQuote);

        for (var i = 0; i < path.Length;)
        {
            var c = path[i++];

            if (c == Backslash)
            {
                var backslashCount = 1;
                while (i < path.Length && path[i] == Backslash)
                {
                    backslashCount++;
                    i++;
                }

                if (i == path.Length)
                {
                    stringBuilder.Append(Backslash, backslashCount * 2);
                }
                else if (path[i] == DoubleQuote)
                {
                    stringBuilder.Append(Backslash, backslashCount * 2 + 1).Append(DoubleQuote);
                    i++;
                }
                else
                {
                    stringBuilder.Append(Backslash, backslashCount);
                }
            }
            else if (c == DoubleQuote)
            {
                stringBuilder.Append(Backslash).Append(DoubleQuote);
            }
            else
            {
                stringBuilder.Append(c);
            }
        }

        stringBuilder.Append(DoubleQuote);

        return stringBuilder.ToString();
    }
}
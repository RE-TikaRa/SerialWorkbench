using System.Globalization;

namespace SerialWorkbench.Protocols;

public static class HexCodec
{
    public static byte[] Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var digits = new List<char>(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (char.IsWhiteSpace(character) || character is '-' or ':' or ',')
            {
                continue;
            }

            if (character == '0' && index + 1 < text.Length && text[index + 1] is 'x' or 'X')
            {
                index++;
                continue;
            }

            if (!Uri.IsHexDigit(character))
            {
                throw new FormatException($"Invalid hexadecimal character '{character}' at index {index}.");
            }

            digits.Add(character);
        }

        if ((digits.Count & 1) != 0)
        {
            throw new FormatException("Hexadecimal input must contain an even number of digits.");
        }

        var result = new byte[digits.Count / 2];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = byte.Parse([digits[index * 2], digits[(index * 2) + 1]], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return result;
    }

    public static string Format(ReadOnlySpan<byte> data, string separator = " ") => string.Join(separator, data.ToArray().Select(static value => value.ToString("X2", CultureInfo.InvariantCulture)));
}

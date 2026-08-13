using System.Text;

namespace SerialWorkbench.Terminal;

public sealed class TerminalState
{
    private readonly TerminalCell[,] cells;
    private readonly StringBuilder escape = new();
    private ParserState parserState;
    private TerminalColor foreground = TerminalColor.Default;
    private TerminalColor background = TerminalColor.Default;

    public TerminalState(int columns = 80, int rows = 25)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rows);
        Columns = columns;
        Rows = rows;
        cells = new TerminalCell[rows, columns];
    }

    public int Columns { get; }

    public int Rows { get; }

    public int CursorColumn { get; private set; }

    public int CursorRow { get; private set; }

    public void Feed(ReadOnlySpan<char> text)
    {
        foreach (var character in text)
        {
            Feed(character);
        }
    }

    public TerminalCell GetCell(int row, int column) => cells[row, column];

    public string GetLine(int row)
    {
        var characters = new char[Columns];
        for (var column = 0; column < Columns; column++)
        {
            characters[column] = cells[row, column].Character is '\0' ? ' ' : cells[row, column].Character;
        }

        return new string(characters).TrimEnd();
    }

    public void Clear()
    {
        Array.Clear(cells);
        CursorColumn = 0;
        CursorRow = 0;
    }

    private void Feed(char character)
    {
        switch (parserState)
        {
            case ParserState.Text when character == '\x1B':
                parserState = ParserState.Escape;
                return;
            case ParserState.Escape when character == '[':
                parserState = ParserState.Csi;
                escape.Clear();
                return;
            case ParserState.Csi when character is >= '@' and <= '~':
                ExecuteCsi(character, escape.ToString());
                parserState = ParserState.Text;
                return;
            case ParserState.Csi:
                escape.Append(character);
                return;
            case ParserState.Escape:
                parserState = ParserState.Text;
                return;
        }

        switch (character)
        {
            case '\r':
                CursorColumn = 0;
                break;
            case '\n':
                NewLine();
                break;
            case '\b':
                CursorColumn = Math.Max(0, CursorColumn - 1);
                break;
            case '\t':
                CursorColumn = Math.Min(Columns - 1, ((CursorColumn / 8) + 1) * 8);
                break;
            case >= ' ':
                cells[CursorRow, CursorColumn] = new TerminalCell(character, foreground, background, false, false);
                CursorColumn++;
                if (CursorColumn >= Columns)
                {
                    CursorColumn = 0;
                    NewLine();
                }

                break;
        }
    }

    private void ExecuteCsi(char command, string parameters)
    {
        var values = parameters.Length == 0
            ? [0]
            : parameters.Split(';').Select(static item => int.TryParse(item, out var value) ? value : 0).ToArray();

        switch (command)
        {
            case 'A':
                CursorRow = Math.Max(0, CursorRow - Math.Max(1, values[0]));
                break;
            case 'B':
                CursorRow = Math.Min(Rows - 1, CursorRow + Math.Max(1, values[0]));
                break;
            case 'C':
                CursorColumn = Math.Min(Columns - 1, CursorColumn + Math.Max(1, values[0]));
                break;
            case 'D':
                CursorColumn = Math.Max(0, CursorColumn - Math.Max(1, values[0]));
                break;
            case 'H' or 'f':
                CursorRow = Math.Clamp((values.ElementAtOrDefault(0) is 0 ? 1 : values[0]) - 1, 0, Rows - 1);
                CursorColumn = Math.Clamp((values.ElementAtOrDefault(1) is 0 ? 1 : values.ElementAtOrDefault(1)) - 1, 0, Columns - 1);
                break;
            case 'J' when values[0] == 2:
                Clear();
                break;
            case 'm':
                ApplyGraphics(values);
                break;
        }
    }

    private void ApplyGraphics(int[] values)
    {
        foreach (var value in values)
        {
            if (value == 0)
            {
                foreground = TerminalColor.Default;
                background = TerminalColor.Default;
            }
            else if (value is >= 30 and <= 37)
            {
                foreground = (TerminalColor)(value - 29);
            }
            else if (value is >= 40 and <= 47)
            {
                background = (TerminalColor)(value - 39);
            }
            else if (value == 39)
            {
                foreground = TerminalColor.Default;
            }
            else if (value == 49)
            {
                background = TerminalColor.Default;
            }
        }
    }

    private void NewLine()
    {
        CursorRow++;
        if (CursorRow < Rows)
        {
            return;
        }

        for (var row = 1; row < Rows; row++)
        {
            for (var column = 0; column < Columns; column++)
            {
                cells[row - 1, column] = cells[row, column];
            }
        }

        for (var column = 0; column < Columns; column++)
        {
            cells[Rows - 1, column] = default;
        }

        CursorRow = Rows - 1;
    }

    private enum ParserState
    {
        Text,
        Escape,
        Csi,
    }
}

public readonly record struct TerminalCell(char Character, TerminalColor Foreground, TerminalColor Background, bool Bold, bool Underline);

public enum TerminalColor
{
    Default,
    Black,
    Red,
    Green,
    Yellow,
    Blue,
    Magenta,
    Cyan,
    White,
}

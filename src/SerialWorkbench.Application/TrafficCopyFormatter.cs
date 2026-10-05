using System.Runtime.InteropServices;
using System.Text;
using SerialWorkbench.Protocols;

namespace SerialWorkbench.Application;

public enum TrafficCopyFormat
{
    CurrentDisplay,
    Text,
    Hex,
    CompactHex,
    Log,
}

public static class TrafficCopyFormatter
{
    public static string Format(IReadOnlyList<TrafficRow> rows, TrafficCopyFormat format) => format switch
    {
        TrafficCopyFormat.CurrentDisplay => string.Join(Environment.NewLine, rows.Select(static row => row.Display)),
        TrafficCopyFormat.Text => FormatText(rows),
        TrafficCopyFormat.Hex => string.Join(Environment.NewLine, rows.Select(static row => HexCodec.Format(row.GetData()))),
        TrafficCopyFormat.CompactHex => string.Concat(rows.Select(static row => row.Hex)),
        TrafficCopyFormat.Log => string.Join(Environment.NewLine, rows.Select(static row => $"{row.Time} {(row.IsReceive ? "RX" : "TX")} [{row.Source}] {row.Display}")),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported traffic copy format."),
    };

    private static string FormatText(IReadOnlyList<TrafficRow> rows)
    {
        var output = new StringBuilder();
        var bytes = new List<byte>();
        TrafficRow? previous = null;
        foreach (var row in rows)
        {
            if (previous is not null && (previous.Identity.ConnectionId != row.Identity.ConnectionId
                || previous.IsReceive != row.IsReceive || previous.Source != row.Source || previous.Encoding.CodePage != row.Encoding.CodePage))
            {
                AppendText(output, bytes, previous.Encoding);
            }

            foreach (var value in row.GetData())
            {
                bytes.Add(value);
            }

            previous = row;
        }

        if (previous is not null)
        {
            AppendText(output, bytes, previous.Encoding);
        }

        return output.ToString();
    }

    private static void AppendText(StringBuilder output, List<byte> bytes, Encoding encoding)
    {
        var text = encoding.GetString(CollectionsMarshal.AsSpan(bytes));
        if (output.Length != 0 && text.Length != 0 && output[^1] is not ('\r' or '\n') && text[0] is not ('\r' or '\n'))
        {
            output.Append(Environment.NewLine);
        }

        output.Append(text);
        bytes.Clear();
    }
}

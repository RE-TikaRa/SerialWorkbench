using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using SerialWorkbench.Domain;
using SerialWorkbench.Protocols;

namespace SerialWorkbench.WinUI;

public sealed class TrafficBuffer(Encoding encoding, int capacity = 20_000)
{
    private readonly int capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    private readonly List<SerialTrafficEvent> events = [];
    private readonly Dictionary<(Guid ConnectionId, SerialDirection Direction, string Source), TextStream> streams = [];
    private Encoding encoding = encoding;
    private bool text;
    private bool showTime = true;
    private (Guid ConnectionId, SerialDirection Direction, string Source)? currentStream;
    private TrafficRow? currentRow;

    public ObservableCollection<TrafficRow> Rows { get; } = [];

    public void SetPresentation(Encoding encoding, bool text, bool showTime)
    {
        var rebuild = this.encoding.CodePage != encoding.CodePage || this.text != text;
        if (!rebuild && this.showTime == showTime)
        {
            return;
        }

        this.encoding = encoding;
        this.text = text;
        this.showTime = showTime;
        if (rebuild)
        {
            RebuildRows();
        }
        else
        {
            foreach (var row in Rows)
            {
                row.Refresh(text, showTime);
            }
        }
    }

    public void Append(IReadOnlyList<SerialTrafficEvent> items)
    {
        events.AddRange(items);
        if (events.Count > capacity)
        {
            events.RemoveRange(0, events.Count - capacity);
            RebuildRows();
            return;
        }

        var changed = new HashSet<TrafficRow>();
        foreach (var item in items)
        {
            AppendEvent(item, changed);
        }

        foreach (var row in changed)
        {
            row.Refresh(text, showTime);
        }
    }

    public void Clear()
    {
        events.Clear();
        streams.Clear();
        currentStream = null;
        currentRow = null;
        Rows.Clear();
    }

    private void RebuildRows()
    {
        streams.Clear();
        currentStream = null;
        currentRow = null;
        Rows.Clear();
        var changed = new HashSet<TrafficRow>();
        foreach (var item in events)
        {
            AppendEvent(item, changed);
        }

        foreach (var row in changed)
        {
            row.Refresh(text, showTime);
        }
    }

    private void AppendEvent(SerialTrafficEvent item, HashSet<TrafficRow> changed)
    {
        if (!text)
        {
            var row = new TrafficRow(item);
            row.AppendData(item.Data);
            Rows.Add(row);
            changed.Add(row);
            return;
        }

        if (item.Data.Length == 0)
        {
            return;
        }

        var key = (item.ConnectionId, item.Direction, item.Source);
        if (currentStream != key)
        {
            currentStream = key;
            currentRow = null;
        }

        if (!streams.TryGetValue(key, out var stream))
        {
            stream = new TextStream(encoding.GetDecoder());
            streams.Add(key, stream);
        }

        Span<char> characters = stackalloc char[encoding.GetMaxCharCount(1)];
        for (var index = 0; index < item.Data.Length; index++)
        {
            stream.PendingEvent ??= item;
            stream.PendingBytes.Add(item.Data[index]);
            var count = stream.Decoder.GetChars(item.Data.AsSpan(index, 1), characters, false);
            if (count == 0)
            {
                continue;
            }

            var decoded = characters[..count];
            if (stream.CarriageReturnRow is { } previous && decoded[0] == '\n')
            {
                previous.AppendData(stream.PendingBytes);
                changed.Add(previous);
                stream.PendingBytes.Clear();
                stream.PendingEvent = null;
                decoded = decoded[1..];
            }

            stream.CarriageReturnRow = null;
            foreach (var character in decoded)
            {
                if (currentRow is null)
                {
                    currentRow = new TrafficRow(stream.PendingEvent ?? item);
                    Rows.Add(currentRow);
                }

                currentRow.AppendData(stream.PendingBytes);
                stream.PendingBytes.Clear();
                stream.PendingEvent = null;
                changed.Add(currentRow);
                if (character is '\r' or '\n')
                {
                    stream.CarriageReturnRow = character == '\r' ? currentRow : null;
                    currentRow = null;
                }
                else
                {
                    currentRow.AppendText(character);
                }
            }
        }
    }

    private sealed class TextStream(Decoder decoder)
    {
        public Decoder Decoder { get; } = decoder;
        public List<byte> PendingBytes { get; } = [];
        public SerialTrafficEvent? PendingEvent { get; set; }
        public TrafficRow? CarriageReturnRow { get; set; }
    }
}

public sealed partial class TrafficRow : INotifyPropertyChanged
{
    private readonly List<byte> data = [];
    private readonly StringBuilder content = new();

    internal TrafficRow(SerialTrafficEvent item)
    {
        IsReceive = item.Direction == SerialDirection.Receive;
        Time = item.Utc.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        Source = item.Source;
    }

    private TrafficRow(TrafficRow row)
    {
        IsReceive = row.IsReceive;
        Time = row.Time;
        Source = row.Source;
        Display = row.Display;
        Hex = row.Hex;
        ShowTimestamp = row.ShowTimestamp;
        data.AddRange(row.data);
        content.Append(row.content);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Time { get; }
    public string Display { get; private set; } = "";
    public string Hex { get; private set; } = "";
    public string Source { get; }
    public bool IsReceive { get; }
    public bool IsTransmit => !IsReceive;
    public bool ShowTimestamp { get; private set; }

    public TrafficRow Snapshot() => new(this);

    internal void AppendData(IEnumerable<byte> bytes) => data.AddRange(bytes);

    internal void AppendText(char character) => content.Append(character);

    internal void Refresh(bool text, bool showTime)
    {
        Display = text ? content.ToString() : HexCodec.Format(CollectionsMarshal.AsSpan(data));
        Hex = Convert.ToHexString(CollectionsMarshal.AsSpan(data));
        ShowTimestamp = showTime;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Display)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Hex)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowTimestamp)));
        OnRefreshed();
    }

    public static TrafficRow From(SerialTrafficEvent item, bool text, Encoding encoding, bool showTime, Decoder? decoder = null)
    {
        var row = new TrafficRow(item);
        row.AppendData(item.Data);
        row.Render(text, encoding, showTime, decoder);
        return row;
    }

    public void Render(bool text, Encoding encoding, bool showTime, Decoder? decoder)
    {
        var bytes = CollectionsMarshal.AsSpan(data);
        Display = text
            ? IsReceive && decoder is not null ? DecodeText(bytes, decoder) : encoding.GetString(bytes)
            : HexCodec.Format(bytes);
        Hex = Convert.ToHexString(bytes);
        ShowTimestamp = showTime;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Display)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Hex)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowTimestamp)));
        OnRefreshed();
    }

    partial void OnRefreshed();

    private static string DecodeText(ReadOnlySpan<byte> data, Decoder decoder)
    {
        var chars = new char[decoder.GetCharCount(data, false)];
        decoder.GetChars(data, chars, false);
        return new string(chars);
    }
}

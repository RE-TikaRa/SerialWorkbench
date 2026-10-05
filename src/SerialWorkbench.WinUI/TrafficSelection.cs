using System.Collections.ObjectModel;
using SerialWorkbench.Application;

namespace SerialWorkbench.WinUI;

public static class TrafficSelection
{
    public static void UpdateRows(ObservableCollection<TrafficRow> rows, IReadOnlyList<TrafficRow> source)
    {
        var desired = source.ToHashSet();
        for (var index = rows.Count - 1; index >= 0; index--)
        {
            if (!desired.Contains(rows[index]))
            {
                rows.RemoveAt(index);
            }
        }

        for (var index = 0; index < source.Count; index++)
        {
            if (index == rows.Count)
            {
                rows.Add(source[index]);
            }
            else if (!ReferenceEquals(rows[index], source[index]))
            {
                var previous = rows.IndexOf(source[index]);
                if (previous >= 0)
                {
                    rows.Move(previous, index);
                }
                else
                {
                    rows.Insert(index, source[index]);
                }
            }
        }
    }

    public static IReadOnlyList<TrafficRow> GetSelectedRows(IEnumerable<TrafficRow> rows, IReadOnlySet<TrafficRowIdentity> selected) =>
        selected.Count == 0 ? [] : rows.Where(row => selected.Contains(row.Identity)).ToArray();
}

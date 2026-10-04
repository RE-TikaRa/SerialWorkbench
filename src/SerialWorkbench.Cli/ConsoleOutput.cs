using System.Text.Json;
using SerialWorkbench.Ipc;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace SerialWorkbench.Cli;

public static class ConsoleOutput
{
    public static void Write(string command, object value)
    {
        var data = JsonSerializer.SerializeToElement(value, OperationJson.Options);
        AnsiConsole.Write(new Panel(Render(data)).Header(new PanelHeader(command)).Border(BoxBorder.Rounded));
    }

    private static IRenderable Render(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Array)
        {
            var entries = data.EnumerateArray().ToArray();
            if (entries.Length == 0)
            {
                return new Text("无记录");
            }

            if (entries.All(static item => item.ValueKind == JsonValueKind.Object))
            {
                var names = entries[0].EnumerateObject().Select(static item => item.Name).ToArray();
                var table = new Table().Border(TableBorder.Simple);
                foreach (var name in names)
                {
                    table.AddColumn(new TableColumn(new Text(name)));
                }

                foreach (var entry in entries)
                {
                    table.AddRow(names.Select(name => (IRenderable)new Text(entry.TryGetProperty(name, out var field) ? Display(field) : "")).ToArray());
                }

                return table;
            }

            return new Text(string.Join(Environment.NewLine, entries.Select(Display)));
        }

        if (data.ValueKind == JsonValueKind.Object)
        {
            var table = new Table().Border(TableBorder.Simple).AddColumn("字段").AddColumn("值");
            foreach (var property in data.EnumerateObject())
            {
                table.AddRow(new Text(property.Name), property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                    ? Render(property.Value) : new Text(Display(property.Value)));
            }

            return table;
        }

        return new Text(Display(data));
    }

    private static string Display(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Null => "—",
        _ => value.GetRawText(),
    };
}

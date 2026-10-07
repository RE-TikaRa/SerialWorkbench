using System.CommandLine;
using System.CommandLine.Help;
using System.Globalization;
using System.Text;
using SerialWorkbench.Cli;
using SerialWorkbench.Cli.Tui;
using SerialWorkbench.Ipc;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
Console.OutputEncoding = new UTF8Encoding(false);

var applicationRoot = new Option<string>("--app-root")
{
    Description = "应用目录",
    DefaultValueFactory = _ => AppContext.BaseDirectory,
};
var culture = new Option<string>("--culture")
{
    Description = "界面语言",
    DefaultValueFactory = _ => CultureInfo.CurrentUICulture.Name,
};
var command = new RootCommand("SerialWorkbench 终端工作台");
foreach (var option in command.Options.OfType<HelpOption>())
{
    option.Action = new ExecutableHelpAction("SW_TUI");
}
command.Options.Add(applicationRoot);
command.Options.Add(culture);
command.SetAction((result, token) => TerminalWorkbench.RunAsync(
    result.GetValue(applicationRoot) ?? AppContext.BaseDirectory,
    result.GetValue(culture) ?? CultureInfo.CurrentUICulture.Name, token));

try
{
    return await command.Parse(args).InvokeAsync().ConfigureAwait(false);
}
catch (HostAccessException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 3;
}

using Microsoft.UI.Xaml.Controls;
using SerialWorkbench.Modbus;
using SerialWorkbench.Protocols;

namespace SerialWorkbench.WinUI.Pages;

public sealed partial class ModbusPage : Page
{
    public ModbusPage()
    {
        InitializeComponent();
        UpdatePreview();
    }

    public event EventHandler? SendRequested;

    public byte[]? RequestFrame { get; private set; }

    public void SetSending(bool sending) => SendButton.IsEnabled = !sending;

    public void ShowResult(string message, InfoBarSeverity severity)
    {
        Result.Title = severity == InfoBarSeverity.Success ? "已发送" : "Modbus RTU";
        Result.Message = message;
        Result.Severity = severity;
        Result.IsOpen = true;
    }

    private byte FunctionValue => FunctionCode.SelectedIndex switch
    {
        0 => 3,
        1 => 4,
        2 => 6,
        _ => 3,
    };

    private void FunctionCode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Quantity is not null)
        {
            Quantity.Header = FunctionValue == 6 ? "寄存器值" : "数量";
        }

        UpdatePreview();
    }

    private void SlaveAddress_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => UpdatePreview();

    private byte[] BuildFrame()
    {
        var slave = (byte)SlaveAddress.Value;
        var address = (ushort)StartAddress.Value;
        var value = (ushort)Quantity.Value;
        return FunctionValue == 6
            ? ModbusRtuCodec.BuildWriteSingleRegister(slave, address, value)
            : ModbusRtuCodec.BuildReadRequest(slave, FunctionValue, address, value);
    }

    private void UpdatePreview()
    {
        if (RequestPreview is null)
        {
            return;
        }

        try
        {
            RequestPreview.Text = HexCodec.Format(BuildFrame());
        }
        catch (Exception ex)
        {
            RequestPreview.Text = ex.Message;
        }
    }

    private void SendButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        try
        {
            RequestFrame = BuildFrame();
        }
        catch (Exception ex)
        {
            ShowResult(ex.Message, InfoBarSeverity.Error);
            return;
        }

        SendRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ParseButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        try
        {
            var frame = HexCodec.Parse(ResponseInput.Text);
            var registers = ModbusRtuCodec.ParseRegisterResponse(frame, (byte)SlaveAddress.Value, FunctionValue == 6 ? (byte)6 : FunctionValue);
            RegisterList.ItemsSource = registers
                .Select((value, index) => new RegisterRow($"[{index}]", $"0x{value:X4}", value.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                .ToArray();
            ShowResult($"解析出 {registers.Length} 个寄存器。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            RegisterList.ItemsSource = null;
            ShowResult(ex.Message, InfoBarSeverity.Error);
        }
    }
}

public sealed record RegisterRow(string Index, string Hex, string DecimalText);

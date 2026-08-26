using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SerialWorkbench.Domain;
using SerialWorkbench.Modbus;
using SerialWorkbench.Protocols;

namespace SerialWorkbench.WinUI.Pages;

public sealed partial class ModbusPage : Page
{
    private const double CompactLayoutWidth = 480;
    private const double SplitLayoutWidth = 960;
    private const double WideLayoutWidth = 1440;

    public ModbusPage()
    {
        InitializeComponent();
        UpdatePreview();
    }

    public event EventHandler? SendRequested;

    public byte[]? RequestFrame { get; private set; }

    public byte SlaveAddressValue => (byte)SlaveAddress.Value;

    public byte FunctionCodeValue => FunctionValue;

    public void SetSending(bool sending) => SendButton.IsEnabled = !sending;

    public void ShowResult(string message, InfoBarSeverity severity)
    {
        Result.Title = severity == InfoBarSeverity.Success ? "已发送" : "Modbus RTU";
        Result.Message = message;
        Result.Severity = severity;
        Result.IsOpen = true;
    }

    public void ShowResponse(ModbusTransactionResult response)
    {
        ResponseInput.Text = HexCodec.Format(response.ResponseFrame);
        if (response.Success)
        {
            var rows = response.FunctionCode == 6 && response.Address is { } address && response.Value is { } value
                ? [new RegisterRow($"0x{address:X4}", $"0x{value:X4}", value.ToString(System.Globalization.CultureInfo.InvariantCulture))]
                : response.Registers
                    .Select((value, index) => new RegisterRow($"[{index}]", $"0x{value:X4}", value.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                    .ToArray();
            RegisterList.ItemsSource = rows;
            ShowResult($"已收到响应 · {response.Duration.TotalMilliseconds:N0} ms", InfoBarSeverity.Success);
            return;
        }

        RegisterList.ItemsSource = null;
        if (response.Error?.StartsWith("Detected TX echo; waiting for slave response.", StringComparison.Ordinal) == true)
        {
            ShowResult("检测到 TX 回显，等待从站响应。", InfoBarSeverity.Warning);
            return;
        }

        ShowResult(response.Error ?? "Modbus 请求失败。", InfoBarSeverity.Error);
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

    private void ModbusPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var state = e.NewSize.Width switch
        {
            < CompactLayoutWidth => "Compact",
            < WideLayoutWidth => "Narrow",
            _ => "Wide",
        };
        VisualStateManager.GoToState(this, state, false);
        VisualStateManager.GoToState(this, e.NewSize.Width < SplitLayoutWidth ? "StackedCards" : "SplitCards", false);
        VisualStateManager.GoToState(this, e.NewSize.Width < 641 ? "CompactPageMargins" : "StandardPageMargins", false);
    }

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

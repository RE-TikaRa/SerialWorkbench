using System.Collections.ObjectModel;
using System.Globalization;
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

    public event EventHandler? ScanRequested;

    public event EventHandler? ScanCancelRequested;

    public byte[]? RequestFrame { get; private set; }

    public byte SlaveAddressValue => (byte)SlaveAddress.Value;

    public byte FunctionCodeValue => FunctionValue;

    public byte ScanFromValue => checked((byte)ScanFrom.Value);

    public byte ScanToValue => checked((byte)ScanTo.Value);

    public ushort ScanAddressValue => checked((ushort)ScanAddress.Value);

    public int ScanTimeoutMilliseconds => checked((int)ScanTimeout.Value);

    public int ScanIntervalMilliseconds => checked((int)ScanInterval.Value);

    public ObservableCollection<ModbusScanRow> ScanResults { get; } = [];

    public void SetSending(bool sending) => SendButton.IsEnabled = !sending;

    public void SetScanning(bool scanning)
    {
        ScanButton.IsEnabled = !scanning;
        CancelScanButton.IsEnabled = scanning;
        SendButton.IsEnabled = !scanning;
    }

    public void ClearScanResults()
    {
        ScanResults.Clear();
        ScanStatusText.Text = "";
    }

    public void AddScanResult(byte slave, ModbusTransactionResult result) => ScanResults.Add(ModbusScanRow.From(slave, result));

    public void SetScanStatus(string text) => ScanStatusText.Text = text;

    public void ShowResult(string message, InfoBarSeverity severity)
    {
        Result.Title = severity == InfoBarSeverity.Success ? "已发送" : "Modbus RTU";
        Result.Message = message;
        Result.Severity = severity;
        Result.IsOpen = true;
    }

    public void ShowScanResult(string message, InfoBarSeverity severity)
    {
        Result.Title = "从站扫描";
        Result.Message = message;
        Result.Severity = severity;
        Result.IsOpen = true;
    }

    public void ShowResponse(ModbusTransactionResult response)
    {
        ResponseInput.Text = HexCodec.Format(response.ResponseFrame);
        if (response.Success)
        {
            RegisterRow[] rows;
            if (response.FunctionCode is 5 or 6 or 15 or 16 && response.Address is { } address && response.Value is { } value)
            {
                rows = response.FunctionCode == 5
                    ? [new RegisterRow($"0x{address:X4}", value == 0 ? "0" : "1", value == 0 ? "False" : "True")]
                    : response.FunctionCode is 15 or 16
                        ? [new RegisterRow($"0x{address:X4}", value.ToString(CultureInfo.InvariantCulture), "写入数量")]
                        : [new RegisterRow($"0x{address:X4}", $"0x{value:X4}", value.ToString(CultureInfo.InvariantCulture))];
            }
            else if (response.Bits is { } bits)
            {
                rows = bits.Select((bit, index) => new RegisterRow($"[{index}]", bit ? "1" : "0", bit ? "True" : "False")).ToArray();
            }
            else
            {
                rows = response.Registers
                    .Select((register, index) => new RegisterRow($"[{index}]", $"0x{register:X4}", register.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                    .ToArray();
            }

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
        3 => 1,
        4 => 2,
        5 => 5,
        6 => 15,
        7 => 16,
        8 => 17,
        _ => 3,
    };

    private void FunctionCode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Quantity is not null)
        {
            var multiple = FunctionValue is 15 or 16;
            Quantity.Header = FunctionValue switch
            {
                6 => "寄存器值",
                5 => "线圈值(0/1)",
                17 => "无需填写",
                _ => "数量",
            };
            Quantity.Minimum = FunctionValue is 5 or 6 or 17 ? 0 : 1;
            Quantity.IsEnabled = !multiple;
            ValuesInput.Visibility = multiple ? Visibility.Visible : Visibility.Collapsed;
            Quantity.Maximum = FunctionValue switch
            {
                6 => ushort.MaxValue,
                5 => 1,
                17 => 0,
                1 or 2 => 2000,
                _ => 125,
            };
            if (FunctionValue == 6 && Quantity.Value is < 0)
            {
                Quantity.Value = 0;
            }
            else if (FunctionValue == 5 && Quantity.Value is > 1)
            {
                Quantity.Value = 1;
            }
            else if (FunctionValue is 3 or 4 && Quantity.Value is > 125)
            {
                Quantity.Value = 125;
            }
            else if (FunctionValue is 1 or 2 && Quantity.Value is > 2000)
            {
                Quantity.Value = 2000;
            }

            Quantity.IsEnabled = FunctionValue != 17;
            StartAddress.IsEnabled = FunctionValue != 17;
        }

        UpdatePreview();
    }

    private void SlaveAddress_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => UpdatePreview();

    private void ValuesInput_TextChanged(object sender, TextChangedEventArgs e) => UpdatePreview();

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
        return FunctionValue switch
        {
            5 => ModbusRtuCodec.BuildWriteSingleCoil(slave, address, value != 0),
            6 => ModbusRtuCodec.BuildWriteSingleRegister(slave, address, value),
            15 => ModbusRtuCodec.BuildWriteMultipleCoils(slave, address, ParseCoilValues(ValuesInput.Text)),
            16 => ModbusRtuCodec.BuildWriteMultipleRegisters(slave, address, ParseRegisterValues(ValuesInput.Text)),
            17 => ModbusRtuCodec.BuildReportServerIdRequest(slave),
            _ => ModbusRtuCodec.BuildReadRequest(slave, FunctionValue, address, value),
        };
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

    private void ScanButton_Click(object sender, RoutedEventArgs e) => ScanRequested?.Invoke(this, EventArgs.Empty);

    private void CancelScanButton_Click(object sender, RoutedEventArgs e) => ScanCancelRequested?.Invoke(this, EventArgs.Empty);

    private void ParseButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        try
        {
            var frame = HexCodec.Parse(ResponseInput.Text);
            if (FunctionValue == 6)
            {
                var result = ModbusRtuCodec.ParseWriteSingleRegisterResponse(frame, (byte)SlaveAddress.Value);
                RegisterList.ItemsSource = new[] { new RegisterRow($"0x{result.Address:X4}", $"0x{result.Value:X4}", result.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)) };
                ShowResult("已解析写单寄存器响应。", InfoBarSeverity.Success);
                return;
            }

            if (FunctionValue == 5)
            {
                var result = ModbusRtuCodec.ParseWriteSingleCoilResponse(frame, (byte)SlaveAddress.Value);
                RegisterList.ItemsSource = new[] { new RegisterRow($"0x{result.Address:X4}", result.Value ? "1" : "0", result.Value ? "True" : "False") };
                ShowResult("已解析写单个线圈响应。", InfoBarSeverity.Success);
                return;
            }

            if (FunctionValue is 15 or 16)
            {
                var result = ModbusRtuCodec.ParseWriteMultipleResponse(frame, (byte)SlaveAddress.Value, FunctionValue);
                RegisterList.ItemsSource = new[] { new RegisterRow($"0x{result.Address:X4}", result.Quantity.ToString(CultureInfo.InvariantCulture), "写入数量") };
                ShowResult("已解析批量写入响应。", InfoBarSeverity.Success);
                return;
            }

            if (FunctionValue == 17)
            {
                ShowResult("已解析设备标识响应。", InfoBarSeverity.Success);
                return;
            }

            if (FunctionValue is 1 or 2)
            {
                var bits = ModbusRtuCodec.ParseBitResponse(frame, (byte)SlaveAddress.Value, FunctionValue, (ushort)Quantity.Value);
                RegisterList.ItemsSource = bits.Select((bit, index) => new RegisterRow($"[{index}]", bit ? "1" : "0", bit ? "True" : "False")).ToArray();
                ShowResult($"解析出 {bits.Length} 位。", InfoBarSeverity.Success);
                return;
            }

            var registers = ModbusRtuCodec.ParseRegisterResponse(frame, (byte)SlaveAddress.Value, FunctionValue);
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

    private static bool[] ParseCoilValues(string text)
    {
        var values = SplitValues(text)
            .Select(static value => value switch
            {
                "0" => false,
                "1" => true,
                _ => throw new FormatException("线圈值必须为 0 或 1。"),
            })
            .ToArray();
        return values.Length is >= 1 and <= 1968
            ? values
            : throw new FormatException("线圈值数量必须在 1 到 1968 之间。");
    }

    private static ushort[] ParseRegisterValues(string text)
    {
        var values = SplitValues(text)
            .Select(static value => value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? ushort.Parse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                : ushort.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture))
            .ToArray();
        return values.Length is >= 1 and <= 123
            ? values
            : throw new FormatException("寄存器值数量必须在 1 到 123 之间。");
    }

    private static string[] SplitValues(string text) =>
        string.IsNullOrWhiteSpace(text)
            ? throw new FormatException("请输入值列表。")
            : text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public sealed record RegisterRow(string Index, string Hex, string DecimalText);

public sealed class ModbusScanRow
{
    public string Slave { get; private init; } = "";
    public string Status { get; private init; } = "";
    public string Duration { get; private init; } = "";
    public string Details { get; private init; } = "";

    public static ModbusScanRow From(byte slave, ModbusTransactionResult result)
    {
        var details = result.Success
            ? result.Registers.Length > 0
                ? string.Join(" ", result.Registers.Select(static value => $"0x{value:X4}"))
                : "响应"
            : result.Error ?? "Modbus 异常";
        return new ModbusScanRow
        {
            Slave = $"从站 {slave}",
            Status = result.Success ? "响应" : result.ExceptionCode is { } code ? $"异常 0x{code:X2}" : "失败",
            Duration = $"{result.Duration.TotalMilliseconds:N0} ms",
            Details = details,
        };
    }
}

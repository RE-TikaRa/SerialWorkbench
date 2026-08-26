using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SerialWorkbench.Modbus;
using SerialWorkbench.Protocols;

namespace SerialWorkbench.WinUI.Pages;

public sealed partial class ProtocolInspectorPage : Page
{
    public ProtocolInspectorPage() => InitializeComponent();

    private void InspectButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var frame = HexCodec.Parse(FrameInput.Text);
            var inspection = ModbusRtuCodec.Inspect(frame);
            KindText.Text = inspection.Kind;
            AddressText.Text = FormatByte(inspection.Address);
            FunctionText.Text = FormatByte(inspection.FunctionCode);
            LengthText.Text = inspection.ExpectedLength is { } expected
                ? $"{inspection.FrameLength} / {expected}"
                : inspection.FrameLength.ToString(System.Globalization.CultureInfo.InvariantCulture);
            CalculatedCrcText.Text = FormatUShort(inspection.CalculatedCrc);
            ActualCrcText.Text = FormatUShort(inspection.ActualCrc);
            ExceptionText.Text = FormatByte(inspection.ExceptionCode);
            ByteCountText.Text = inspection.ByteCount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "—";
            Result.Title = inspection.IsValid ? "解析成功" : "解析失败";
            Result.Message = inspection.Error ?? "Modbus RTU 帧字段有效。";
            Result.Severity = inspection.IsValid ? InfoBarSeverity.Success : InfoBarSeverity.Error;
            Result.IsOpen = true;
        }
        catch (Exception ex)
        {
            Result.Title = "解析失败";
            Result.Message = ex.Message;
            Result.Severity = InfoBarSeverity.Error;
            Result.IsOpen = true;
        }
    }

    private static string FormatByte(byte? value) => value is { } item ? $"0x{item:X2}" : "—";

    private static string FormatUShort(ushort? value) => value is { } item ? $"0x{item:X4}" : "—";
}

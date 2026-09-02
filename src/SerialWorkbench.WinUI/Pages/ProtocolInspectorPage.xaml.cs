using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SerialWorkbench.Modbus;
using SerialWorkbench.Protocols;

namespace SerialWorkbench.WinUI.Pages;

public sealed partial class ProtocolInspectorPage : Page
{
    public ProtocolInspectorPage()
    {
        InitializeComponent();
        TemplateSelector.SelectedIndex = 0;
        TemplateJson.Text = ProtocolTemplateCodec.Serialize(new ProtocolTemplateDefinition(
            "sensor",
            "AA",
            9,
            null,
            [
                new ProtocolFieldDefinition("address", 1, ProtocolFieldType.U8),
                new ProtocolFieldDefinition("value", 2, ProtocolFieldType.F32, ByteOrder: ProtocolByteOrder.LittleEndian),
            ],
            new ProtocolChecksumDefinition(ProtocolChecksumKind.Xor, 8, 0, 8)));
    }

    private void InspectButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var frame = HexCodec.Parse(FrameInput.Text);
            if (TemplateSelector.SelectedIndex == 1)
            {
                var templateInspection = ProtocolTemplateParser.Inspect(ProtocolTemplateCodec.Deserialize(TemplateJson.Text), frame);
                GenericFieldsText.Text = string.Join(Environment.NewLine, templateInspection.Fields.Select(static field => $"{field.Name} = {field.Value} ({field.Hex})"));
                GenericFieldsPanel.Visibility = Visibility.Visible;
                Result.Title = templateInspection.IsValid ? "解析成功" : "解析失败";
                Result.Message = templateInspection.Error ?? "通用协议帧字段有效。";
                Result.Severity = templateInspection.IsValid ? InfoBarSeverity.Success : InfoBarSeverity.Error;
                Result.IsOpen = true;
                return;
            }

            GenericFieldsPanel.Visibility = Visibility.Collapsed;
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

    private void TemplateSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var custom = TemplateSelector.SelectedIndex == 1;
        TemplateJson.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        GenericFieldsPanel.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string FormatByte(byte? value) => value is { } item ? $"0x{item:X2}" : "—";

    private static string FormatUShort(ushort? value) => value is { } item ? $"0x{item:X4}" : "—";
}

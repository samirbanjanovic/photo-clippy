using System.Collections.Frozen;
using System.Text.RegularExpressions;
using PhotoClippy.Contracts;

namespace PhotoClippy.Core.Normalization;

/// <summary>Heuristic device classification from EXIF strings and the crop factor (no camera database).</summary>
public static partial class DeviceClassifier
{
    private static readonly FrozenSet<string> PhoneMakes = new[]
    {
        "Apple", "Google", "Samsung", "Huawei", "Xiaomi", "OnePlus", "Motorola", "Nokia", "OPPO", "vivo", "Essential",
        "Nothing", "Fairphone", "Honor", "realme", "LG", "HTC", "ZTE", "ASUS",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> DroneMakes = new[] { "DJI", "Parrot", "Skydio" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static DeviceClass Classify(string? make, string? model, string? software, bool hasExposure, double? cropFactor, bool hasLensInfo)
    {
        if (make is null && model is null)
        {
            if (software is not null && ScannerSoftware().IsMatch(software))
            {
                return DeviceClass.Scanner;
            }

            return hasExposure ? DeviceClass.Unknown : DeviceClass.Software;
        }

        var text = $"{make} {model}";
        if (ScannerModel().IsMatch(text) || (software is not null && ScannerSoftware().IsMatch(software)))
        {
            return DeviceClass.Scanner;
        }

        if (make is not null && DroneMakes.Contains(make))
        {
            return model is not null && DjiHandheld().IsMatch(model) ? DeviceClass.FixedLens : DeviceClass.Drone;
        }

        if (make is not null && PhoneMakes.Contains(make) && !(make.Equals("Samsung", StringComparison.OrdinalIgnoreCase) && SamsungCamera().IsMatch(model ?? string.Empty)))
        {
            return DeviceClass.Phone;
        }

        if (make is "Sony" && model is not null && SonyPhone().IsMatch(model))
        {
            return DeviceClass.Phone;
        }

        if (model is not null && FixedLensModel().IsMatch(text))
        {
            return DeviceClass.FixedLens;
        }

        if (cropFactor is >= 3.5)
        {
            return DeviceClass.FixedLens;
        }

        return !hasExposure && !hasLensInfo && model is null ? DeviceClass.Unknown : DeviceClass.InterchangeableLens;
    }

    [GeneratedRegex(@"scan|coolscan|perfection|flextight|plustek|opticfilm|canoscan|imacon|pakon|noritsu|frontier", RegexOptions.IgnoreCase)]
    private static partial Regex ScannerModel();

    [GeneratedRegex(@"vuescan|silverfast|epson scan|nikon scan|canoscan|negativelabpro|imacon|flexcolor", RegexOptions.IgnoreCase)]
    private static partial Regex ScannerSoftware();

    [GeneratedRegex(@"osmo|pocket|action", RegexOptions.IgnoreCase)]
    private static partial Regex DjiHandheld();

    [GeneratedRegex(@"^(NX|EX|WB|ST|PL|ES|GX|NV)\d", RegexOptions.IgnoreCase)]
    private static partial Regex SamsungCamera();

    [GeneratedRegex(@"^(XQ-|SO-|SOG|SOV|Xperia|[CDEFGH]\d{4})", RegexOptions.IgnoreCase)]
    private static partial Regex SonyPhone();

    /// <summary>Common fixed-lens compact/bridge/action camera model families.</summary>
    [GeneratedRegex(
        @"\b(X100\w*|X70|XF10|XQ\d|X30|X20|X-S1|GR\s?(II|III|IIIx|IV|DIGITAL)?|GXR|DSC-|RX100|RX10|RX1R?|ZV-1\b|PowerShot|IXUS|IXY|ELPH|COOLPIX|DMC-(LX|TZ|ZS|FZ|LF|FX|TS|FT)|DC-(LX|TZ|ZS|FZ|LF|TS|FT)|Leica Q\d?|D-LUX|C-LUX|V-LUX|X \(Typ|X-U|X Vario|TG-\d|STYLUS|XZ-\d|SH-\d|Tough|HERO\d*|FinePix|Optio|WG-\d|CYBER-?SHOT|DP\d\s?(Merrill|Quattro)?|EX-[FZ]|Insta360|ONE X)",
        RegexOptions.IgnoreCase)]
    private static partial Regex FixedLensModel();
}

public static class SensorFormats
{
    public const string MediumFormat = "medium-format";
    public const string FullFrame = "full-frame";
    public const string ApsH = "aps-h";
    public const string ApsC = "aps-c";
    public const string MicroFourThirds = "mft";
    public const string OneInch = "1-inch";
    public const string SmallSensor = "small-sensor";

    public static string FromCrop(double crop) => crop switch
    {
        < 0.9 => MediumFormat,
        < 1.18 => FullFrame,
        < 1.4 => ApsH,
        < 1.75 => ApsC,
        < 2.3 => MicroFourThirds,
        < 3.5 => OneInch,
        _ => SmallSensor,
    };

    public static string Display(string format) => format switch
    {
        MediumFormat => "Medium format",
        FullFrame => "Full frame",
        ApsH => "APS-H",
        ApsC => "APS-C",
        MicroFourThirds => "Micro Four Thirds",
        OneInch => "1-inch",
        SmallSensor => "Small sensor",
        _ => format,
    };
}

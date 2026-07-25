namespace Vulcano.Core.Services;

/// <summary>
/// GATT service and characteristic UUIDs for the Storz &amp; Bickel Volcano,
/// reverse-engineered from https://github.com/firsttris/reactive-volcano-app
/// </summary>
public static class VolcanoUuids
{
    public static class Services
    {
        public static readonly Guid DeviceState = new("10100000-5354-4f52-5a26-4249434b454c");
        public static readonly Guid DeviceControl = new("10110000-5354-4f52-5a26-4249434b454c");
    }

    public static class Characteristics
    {
        // DeviceControl service
        public static readonly Guid CurrentTemperature = new("10110001-5354-4f52-5a26-4249434b454c");
        public static readonly Guid TargetTemperature = new("10110003-5354-4f52-5a26-4249434b454c");
        public static readonly Guid Brightness = new("10110005-5354-4f52-5a26-4249434b454c");
        public static readonly Guid CurrentAutoOffValue = new("1011000c-5354-4f52-5a26-4249434b454c");
        public static readonly Guid ShutoffTime = new("1011000d-5354-4f52-5a26-4249434b454c");
        public static readonly Guid HeaterOn = new("1011000f-5354-4f52-5a26-4249434b454c");
        public static readonly Guid HeaterOff = new("10110010-5354-4f52-5a26-4249434b454c");
        public static readonly Guid PumpOn = new("10110013-5354-4f52-5a26-4249434b454c");
        public static readonly Guid PumpOff = new("10110014-5354-4f52-5a26-4249434b454c");
        public static readonly Guid HoursOfHeating = new("10110015-5354-4f52-5a26-4249434b454c");
        public static readonly Guid MinutesOfHeating = new("10110016-5354-4f52-5a26-4249434b454c");

        // DeviceState service
        public static readonly Guid FirmwareVersion = new("10100003-5354-4f52-5a26-4249434b454c");
        public static readonly Guid FirmwareBleVersion = new("10100004-5354-4f52-5a26-4249434b454c");
        public static readonly Guid SerialNumber = new("10100008-5354-4f52-5a26-4249434b454c");
        public static readonly Guid Activity = new("1010000c-5354-4f52-5a26-4249434b454c");
        public static readonly Guid Display = new("1010000d-5354-4f52-5a26-4249434b454c");
        public static readonly Guid Vibration = new("1010000e-5354-4f52-5a26-4249434b454c");
    }

    /// <summary>Bit flags decoded from the <see cref="Characteristics.Activity"/> notification.</summary>
    public static class ActivityFlags
    {
        public const ushort HeatingEnabled = 0x0020;
        public const ushort AutoShutdownEnabled = 0x0200;
        public const ushort PumpEnabled = 0x2000;
    }

    /// <summary>Bit flags read from/written to the <see cref="Characteristics.Display"/> characteristic.</summary>
    public static class DisplayFlags
    {
        public const ushort FahrenheitEnabled = 0x0200;
        public const ushort DisplayOnCoolingEnabled = 0x1000;
    }

    /// <summary>Bit flag read from/written to the <see cref="Characteristics.Vibration"/> characteristic.</summary>
    public static class VibrationFlags
    {
        public const ushort VibrationEnabled = 0x0400;
    }

    /// <summary>BLE advertisement local-name prefixes used to identify a Volcano.</summary>
    public static readonly string[] NamePrefixes = ["STORZ&BICKEL", "S&B"];
}

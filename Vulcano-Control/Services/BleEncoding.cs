namespace Vulcano_Control.Services;

/// <summary>Little-endian byte encoding helpers matching the Volcano's BLE wire format.</summary>
public static class BleEncoding
{
    public static byte[] ToUInt16LEBytes(ushort value) => BitConverter.GetBytes(value);

    public static ushort FromUInt16LEBytes(byte[] bytes) => BitConverter.ToUInt16(bytes, 0);

    /// <summary>Encodes a Celsius value as the device's raw UInt16 (°C * 10).</summary>
    public static ushort EncodeTemperature(double celsius) => (ushort)Math.Round(celsius * 10.0);

    /// <summary>Decodes the device's raw UInt16 (°C * 10) back to Celsius.</summary>
    public static double DecodeTemperature(ushort raw) => raw / 10.0;
}

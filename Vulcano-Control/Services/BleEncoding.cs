namespace Vulcano_Control.Services;

/// <summary>Little-endian byte encoding helpers matching the Volcano's BLE wire format.</summary>
public static class BleEncoding
{
    public static byte[] ToUInt16LEBytes(ushort value) => BitConverter.GetBytes(value);

    public static ushort FromUInt16LEBytes(byte[] bytes) => BitConverter.ToUInt16(bytes, 0);

    public static byte[] ToUInt32LEBytes(uint value) => BitConverter.GetBytes(value);

    /// <summary>Decodes a UTF-8 device string, trimming null-byte padding.</summary>
    public static string DecodeUtf8(byte[] bytes) => System.Text.Encoding.UTF8.GetString(bytes).TrimEnd('\0').Trim();

    /// <summary>Encodes a Celsius value as the device's raw UInt16 (°C * 10).</summary>
    public static ushort EncodeTemperature(double celsius) => (ushort)Math.Round(celsius * 10.0);

    /// <summary>Decodes the device's raw UInt16 (°C * 10) back to Celsius.</summary>
    public static double DecodeTemperature(ushort raw) => raw / 10.0;
}

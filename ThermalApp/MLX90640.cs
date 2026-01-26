using System;
using System.Device.I2c;

namespace ThermalApp;
public class MLX90640 : IDisposable
{
    private readonly I2cDevice _device;

    // MLX90640 default I2C address is 0x33
    public MLX90640(int busId = 1, int deviceAddress = 0x33)
    {
        var settings = new I2cConnectionSettings(busId, deviceAddress);
        _device = I2cDevice.Create(settings);
    }

    /// <summary>
    /// Reads one frame of raw data from the MLX90640 sensor
    /// and returns it as a 24x32 double array.
    /// NOTE: This is simplified; real conversion requires calibration parameters.
    /// </summary>
    public double[,] GetFrameData()
    {
        // MLX90640 frame data size: 832 words (1664 bytes)
        byte[] frameData = new byte[1664];

        // Set start address (0x0400) — simplified example
        // In practice, you need to handle register addressing carefully
        _device.Write(new byte[] { 0x04, 0x00 });
        _device.Read(frameData);

        var data = new double[24, 32];
        int index = 0;

        for (int row = 0; row < 24; row++)
        {
            for (int col = 0; col < 32; col++)
            {
                ushort raw = (ushort)(frameData[index] | (frameData[index + 1] << 8));
                index += 2;

                // Simplified conversion: scale raw value
                data[row, col] = raw * 0.01;
            }
        }

        return data;
    }

    public void Dispose()
    {
        _device?.Dispose();
    }
}

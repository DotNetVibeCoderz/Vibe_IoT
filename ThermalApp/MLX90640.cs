using System;
using System.Buffers.Binary;
using System.Device.I2c;
using System.Threading;
namespace ThermalApp;
public class MLX90640 : IDisposable
{
    private readonly I2cDevice _i2cDevice;

    // MLX90640 Constants
    private const int RAM_Base_Address = 0x0400;
    private const int Status_Register = 0x8000;

    // 24 rows * 32 columns = 768 pixels
    private const int PixelCount = 768;

    // MLX90640 default I2C address is 0x33
    public MLX90640(int busId = 1, int deviceAddress = 0x33)
    {
        var settings = new I2cConnectionSettings(busId, deviceAddress);
        _i2cDevice = I2cDevice.Create(settings);
        // Optional: Perform initial reset or configuration here
        SetRefreshRate(RefreshRate.Hz_2); // Set default to 2Hz
    }
    public MLX90640(I2cDevice i2cDevice)
    {
        _i2cDevice = i2cDevice ?? throw new ArgumentNullException(nameof(i2cDevice));

        // Optional: Perform initial reset or configuration here
        SetRefreshRate(RefreshRate.Hz_2); // Set default to 2Hz
    }

    /// <summary>
    /// Reads the current frame from the sensor and returns it as a 24x32 matrix.
    /// </summary>
    public double[,] GetFrameData()
    {
        // 1. Wait for data to be ready
        WaitForDataReady();

        // 2. Read the RAM (Pixels are stored from 0x0400 to 0x07FF)
        // We need 768 words * 2 bytes = 1536 bytes
        Span<byte> rawBytes = stackalloc byte[PixelCount * 2];
        ReadBlock(RAM_Base_Address, rawBytes);

        // 3. Clear the status register to start the next measurement
        ClearStatusRegister();

        // 4. Convert Raw Bytes to Double Matrix
        var data = new double[24, 32];

        for (int i = 0; i < PixelCount; i++)
        {
            // Extract the 16-bit value (Big Endian)
            short rawValue = BinaryPrimitives.ReadInt16BigEndian(rawBytes.Slice(i * 2, 2));

            // Map 1D array index to 2D grid (Row, Column)
            int row = i / 32;
            int col = i % 32;

            // TODO: INSERT CALIBRATION ALGORITHM HERE
            // Without the 800-line math library, we return the raw sensor value.
            // Raw values usually range ~10000-20000 depending on gain.
            data[row, col] = (double)rawValue;
        }

        return data;
    }

    private void WaitForDataReady()
    {
        // Poll the status register (0x8000) until Bit 3 (New Data Available) is set
        while (true)
        {
            ushort status = Read16Bit(Status_Register);

            // Check bit 3
            if ((status & 0x0008) != 0)
            {
                return; // Data is ready
            }

            Thread.Sleep(5); // Wait briefly before checking again
        }
    }

    private void ClearStatusRegister()
    {
        // We need to clear bit 3 in register 0x8000
        // Usually we read the status, modify it, and write it back
        ushort status = Read16Bit(Status_Register);

        // Clear bit 3 (New Data Available) -> 0xFFF7 mask
        status = (ushort)(status & 0xFFF7);

        Write16Bit(Status_Register, status);
    }

    private void SetRefreshRate(RefreshRate rate)
    {
        // Control Register 1 is at 0x800D
        ushort controlReg = Read16Bit(0x800D);

        // Bits 7, 8, 9 control refresh rate. Clear them first (Mask 0xFC7F)
        controlReg &= 0xFC7F;
        // Set new rate
        controlReg |= (ushort)((int)rate << 7);

        Write16Bit(0x800D, controlReg);
    }

    // --- Low Level I2C Helpers ---

    private void ReadBlock(ushort address, Span<byte> buffer)
    {
        // Write address (2 bytes, Big Endian)
        Span<byte> addrCmd = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(addrCmd, address);

        _i2cDevice.Write(addrCmd);
        _i2cDevice.Read(buffer);
    }

    private ushort Read16Bit(ushort address)
    {
        Span<byte> addrCmd = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(addrCmd, address);

        Span<byte> data = stackalloc byte[2];

        _i2cDevice.WriteRead(addrCmd, data);

        return BinaryPrimitives.ReadUInt16BigEndian(data);
    }

    private void Write16Bit(ushort address, ushort value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(0, 2), address);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(2, 2), value);

        _i2cDevice.Write(buffer);
    }

    public void Dispose()
    {
        _i2cDevice?.Dispose();
    }

    // Enum matching MLX90640 Datasheet
    public enum RefreshRate
    {
        Hz_0_5 = 0,
        Hz_1 = 1,
        Hz_2 = 2,
        Hz_4 = 3,
        Hz_8 = 4,
        Hz_16 = 5,
        Hz_32 = 6,
        Hz_64 = 7
    }
}
/*
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
*/
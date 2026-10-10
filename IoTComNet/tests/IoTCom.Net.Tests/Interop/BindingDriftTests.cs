using System.Reflection;
using System.Runtime.InteropServices;

namespace IoTCom.Net.Tests.Interop;

/// <summary>
/// Compares the hand-written <c>LibraryImport</c> bindings of every native library with the declarations csbindgen
/// generates from the Rust sources (rust/tools/iotcom-bindgen). A changed Rust signature, a renamed export or a
/// different struct layout fails here; CI also regenerates the files and fails when they are not committed.
/// </summary>
public class BindingDriftTests
{
    public static TheoryData<string, string, string> Libraries => new()
    {
        { "IoTCom.Net.Native.Modbus", "IoTCom.Net.Native.Modbus.Interop.NativeMethods", "ModbusNative" },
        { "IoTCom.Net.Protocols.IsoTp", "IoTCom.Net.Protocols.IsoTp.Interop.NativeMethods", "IsoTpNative" },
        { "IoTCom.Net.Transport.Ble", "IoTCom.Net.Transport.Ble.BleNativeMethods", "BleNative" },
        { "IoTCom.Net.Transport.Usb", "IoTCom.Net.Transport.Usb.UsbNativeMethods", "UsbNative" },
        { "IoTCom.Net.Adapters.Zenoh", "IoTCom.Net.Adapters.Zenoh.ZenohNativeMethods", "ZenohNative" },
    };

    private const BindingFlags All = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>Size class of a parameter at the ABI: pointers, handles, refs and outs are all one machine word.</summary>
    private static string Abi(Type t)
    {
        if (t.IsByRef || t.IsPointer || t == typeof(nint) || t == typeof(nuint) || typeof(SafeHandle).IsAssignableFrom(t)) return "word";
        if (t == typeof(void)) return "void";
        if (t == typeof(bool)) return "1";
        return t.IsEnum ? Marshal.SizeOf(Enum.GetUnderlyingType(t)).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : Marshal.SizeOf(t).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static Dictionary<string, MethodInfo> Exports(Type type, bool generated) => type.GetMethods(All)
        .Select(m => (m, entry: generated ? m.GetCustomAttribute<DllImportAttribute>()?.EntryPoint : m.GetCustomAttribute<LibraryImportAttribute>()?.EntryPoint))
        .Where(x => x.entry is not null)
        .ToDictionary(x => x.entry!, x => x.m, StringComparer.Ordinal);

    [Theory]
    [MemberData(nameof(Libraries))]
    public void Hand_written_bindings_match_the_rust_exports(string assembly, string handType, string generatedClass)
    {
        var hand = Assembly.Load(assembly).GetType(handType, throwOnError: true)!;
        var generated = typeof(BindingDriftTests).Assembly.GetType($"IoTCom.Net.Tests.Interop.Generated.{generatedClass}", throwOnError: true)!;
        var handExports = Exports(hand, generated: false);
        var rustExports = Exports(generated, generated: true);
        Assert.NotEmpty(handExports);

        foreach (var (entry, method) in handExports)
        {
            if (entry is "iotcom_abi_version" or "iotcom_last_error") continue;   // from export_common!, checked by the ABI version
            Assert.True(rustExports.TryGetValue(entry, out var rust), $"{assembly}: '{entry}' is bound in C# but not exported by the Rust crate.");
            var hp = method.GetParameters();
            var rp = rust!.GetParameters();
            Assert.True(hp.Length == rp.Length, $"{entry}: C# has {hp.Length} parameters, Rust has {rp.Length}.");
            for (var i = 0; i < hp.Length; i++)
                Assert.True(Abi(hp[i].ParameterType) == Abi(rp[i].ParameterType),
                    $"{entry} parameter {i} ({rp[i].Name}): C# {hp[i].ParameterType.Name} vs Rust {rp[i].ParameterType.Name}.");
            Assert.True(Abi(method.ReturnType) == Abi(rust.ReturnType), $"{entry}: return C# {method.ReturnType.Name} vs Rust {rust.ReturnType.Name}.");
        }

        // Every Rust export is either bound or deliberately unused; list the unused ones so they stay visible.
        var unused = rustExports.Keys.Except(handExports.Keys).ToList();
        Assert.True(unused.Count <= 2, $"{assembly}: Rust exports not bound in C#: {string.Join(", ", unused)}");
    }

    [Theory]
    [InlineData("IoTCom.Net.Native.Modbus", "IoTCom.Net.Native.Modbus.Interop.ModbusMasterCfg", "ModbusMasterCfg")]
    [InlineData("IoTCom.Net.Native.Modbus", "IoTCom.Net.Native.Modbus.Interop.ModbusEventNative", "ModbusEvent")]
    [InlineData("IoTCom.Net.Protocols.IsoTp", "IoTCom.Net.Protocols.IsoTp.Interop.IsoTpCfgNative", "IsoTpCfg")]
    [InlineData("IoTCom.Net.Protocols.IsoTp", "IoTCom.Net.Protocols.IsoTp.Interop.IsoTpEventNative", "IsoTpEventC")]
    public void Struct_layouts_match(string assembly, string handType, string generatedStruct)
    {
        var hand = Assembly.Load(assembly).GetType(handType, throwOnError: true)!;
        var generated = typeof(BindingDriftTests).Assembly.GetType($"IoTCom.Net.Tests.Interop.Generated.{generatedStruct}", throwOnError: true)!;
        Assert.Equal(Marshal.SizeOf(generated), Marshal.SizeOf(hand));
        var hf = hand.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var gf = generated.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.Equal(gf.Length, hf.Length);
        for (var i = 0; i < gf.Length; i++)
            Assert.Equal(Marshal.OffsetOf(generated, gf[i].Name), Marshal.OffsetOf(hand, hf[i].Name));
    }
}

using System.Reflection;
using System.Runtime.InteropServices;

namespace IoTCom.Net.Native;

/// <summary>
/// Resolves IoTCom.Net Rust native libraries (<c>iotcom_*</c>) shipped under <c>runtimes/{rid}/native/</c>.
/// The default .NET probing already handles NuGet layouts; this resolver adds fallbacks for
/// self-contained, single-file and development (<c>rust/target</c>) layouts and an explicit override
/// via the <c>IOTCOM_NATIVE_PATH</c> environment variable.
/// </summary>
public static class NativeLibraryLoader
{
    private static readonly Lock Gate = new();
    private static readonly HashSet<Assembly> Registered = [];

    /// <summary>Environment variable that points to a directory containing the native libraries.</summary>
    public const string OverrideVariable = "IOTCOM_NATIVE_PATH";

    /// <summary>Registers the resolver for <paramref name="assembly"/> (idempotent).</summary>
    public static void Register(Assembly assembly)
    {
        lock (Gate)
        {
            if (!Registered.Add(assembly)) return;
            NativeLibrary.SetDllImportResolver(assembly, Resolve);
        }
    }

    /// <summary>Current runtime identifier such as <c>win-x64</c> or <c>linux-arm64</c>.</summary>
    public static string RuntimeIdentifier
    {
        get
        {
            var os = OperatingSystem.IsWindows() ? "win"
                : OperatingSystem.IsMacOS() ? "osx"
                : IsMusl() ? "linux-musl" : "linux";
            var arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.X86 => "x86",
                Architecture.Arm64 => "arm64",
                Architecture.Arm => "arm",
                var a => a.ToString().ToLowerInvariant(),
            };
            return $"{os}-{arch}";
        }
    }

    /// <summary>Platform specific file name for a library, e.g. <c>iotcom_modbus.dll</c> / <c>libiotcom_modbus.so</c>.</summary>
    public static string GetFileName(string libraryName) =>
        OperatingSystem.IsWindows() ? $"{libraryName}.dll"
        : OperatingSystem.IsMacOS() ? $"lib{libraryName}.dylib"
        : $"lib{libraryName}.so";

    /// <summary>Candidate paths probed for <paramref name="libraryName"/>, in order.</summary>
    public static IEnumerable<string> GetProbePaths(string libraryName, Assembly? assembly = null)
    {
        var file = GetFileName(libraryName);
        var env = Environment.GetEnvironmentVariable(OverrideVariable);
        if (!string.IsNullOrEmpty(env)) yield return Path.Combine(env, file);

        var baseDir = AppContext.BaseDirectory;
        var rid = RuntimeIdentifier;
        yield return Path.Combine(baseDir, file);
        yield return Path.Combine(baseDir, "runtimes", rid, "native", file);

        var asmDir = GetAssemblyDirectory(assembly);
        if (asmDir is not null && asmDir != baseDir)
        {
            yield return Path.Combine(asmDir, file);
            yield return Path.Combine(asmDir, "runtimes", rid, "native", file);
        }

        // Development: walk up to find rust/target/{release,debug}
        for (var dir = new DirectoryInfo(baseDir); dir is not null; dir = dir.Parent)
        {
            var target = Path.Combine(dir.FullName, "rust", "target");
            if (Directory.Exists(target))
            {
                yield return Path.Combine(target, "release", file);
                yield return Path.Combine(target, "debug", file);
                break;
            }
        }
    }

    /// <summary>Returns true when the native library can be loaded on this machine.</summary>
    public static bool IsAvailable(string libraryName, Assembly? assembly = null)
    {
        if (TryLoad(libraryName, assembly, out var handle))
        {
            NativeLibrary.Free(handle);
            return true;
        }
        return false;
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!libraryName.StartsWith("iotcom_", StringComparison.Ordinal)) return IntPtr.Zero;
        return TryLoad(libraryName, assembly, out var handle) ? handle : IntPtr.Zero;
    }

    private static bool TryLoad(string libraryName, Assembly? assembly, out IntPtr handle)
    {
        foreach (var path in GetProbePaths(libraryName, assembly))
        {
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out handle)) return true;
        }
        if (assembly is not null && NativeLibrary.TryLoad(libraryName, assembly, DllImportSearchPath.SafeDirectories | DllImportSearchPath.AssemblyDirectory, out handle))
            return true;
        handle = IntPtr.Zero;
        return false;
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("SingleFile", "IL3000",
        Justification = "Empty Location (single-file) is handled; AppContext.BaseDirectory is probed first.")]
    private static string? GetAssemblyDirectory(Assembly? assembly)
    {
        var location = assembly?.Location;
        return string.IsNullOrEmpty(location) ? null : Path.GetDirectoryName(location);
    }

    private static bool IsMusl()
    {
        if (!OperatingSystem.IsLinux()) return false;
        try
        {
            return File.Exists("/etc/alpine-release")
                || Directory.EnumerateFiles("/lib", "ld-musl-*").Any();
        }
        catch { return false; }
    }
}

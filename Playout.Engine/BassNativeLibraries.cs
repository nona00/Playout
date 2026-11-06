using System.Reflection;
using System.Runtime.InteropServices;

internal static class BassNativeLibraries
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, IntPtr> Handles = new(StringComparer.OrdinalIgnoreCase);

    public static void Register()
    {
        NativeLibrary.SetDllImportResolver(typeof(BassNativeLibraries).Assembly, Resolve);
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!OperatingSystem.IsWindows() ||
            !IsBassLibrary(libraryName))
        {
            return IntPtr.Zero;
        }

        string runtime = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => "win-x86",
            Architecture.X64 => "win-x64",
            _ => throw new DllNotFoundException(
                $"Für die Prozessarchitektur {RuntimeInformation.ProcessArchitecture} ist keine BASS-DLL enthalten.")
        };

        string directory = Path.Combine(AppContext.BaseDirectory, "runtimes", runtime, "native");

        lock (Sync)
        {
            if (string.Equals(libraryName, "basswasapi.dll", StringComparison.OrdinalIgnoreCase))
            {
                Load(directory, "bass.dll");
            }

            return Load(directory, libraryName);
        }
    }

    private static bool IsBassLibrary(string name)
    {
        return string.Equals(name, "bass.dll", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "basswasapi.dll", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "bassasio.dll", StringComparison.OrdinalIgnoreCase);
    }

    private static IntPtr Load(string directory, string name)
    {
        if (!Handles.TryGetValue(name, out IntPtr handle))
        {
            handle = NativeLibrary.Load(Path.Combine(directory, name));
            Handles.Add(name, handle);
        }

        return handle;
    }
}

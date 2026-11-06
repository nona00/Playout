using System.Runtime.InteropServices;

internal static class AudioNative
{
    internal const uint StreamDecode = 0x200000;
    internal const uint SampleFloat = 0x100;
    internal const uint Unicode = 0x80000000;
    internal const uint AsioFloat = 19;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate uint WasapiProcedure(IntPtr buffer, uint length, IntPtr user);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate uint AsioProcedure(int input, uint channel, IntPtr buffer, uint length, IntPtr user);

    [StructLayout(LayoutKind.Sequential)]
    internal struct ChannelInfo
    {
        internal uint Frequency;
        internal uint Channels;
        internal uint Flags;
        internal uint Type;
        internal uint OriginalResolution;
        internal uint Plugin;
        internal uint Sample;
        internal IntPtr Filename;
    }

    [DllImport("bass.dll", EntryPoint = "BASS_Init")]
    internal static extern int Init(int device, uint frequency, uint flags, IntPtr window, IntPtr clsid);
    [DllImport("bass.dll", EntryPoint = "BASS_SetDevice")]
    internal static extern int SetDevice(uint device);
    [DllImport("bass.dll", EntryPoint = "BASS_Free")]
    internal static extern int Free();
    [DllImport("bass.dll", EntryPoint = "BASS_ErrorGetCode")]
    internal static extern int Error();
    [DllImport("bass.dll", EntryPoint = "BASS_StreamCreateFile", CharSet = CharSet.Unicode)]
    internal static extern uint StreamCreateFile(int memory, string file, ulong offset, ulong length, uint flags);
    [DllImport("bass.dll", EntryPoint = "BASS_StreamFree")]
    internal static extern int StreamFree(uint handle);
    [DllImport("bass.dll", EntryPoint = "BASS_ChannelGetInfo")]
    internal static extern int ChannelGetInfo(uint handle, out ChannelInfo info);
    [DllImport("bass.dll", EntryPoint = "BASS_ChannelGetData")]
    internal static extern int ChannelGetData(uint handle, [Out] float[] buffer, uint length);
    [DllImport("bass.dll", EntryPoint = "BASS_ChannelSetPosition")]
    internal static extern int ChannelSetPosition(uint handle, ulong position, uint mode);

    [DllImport("basswasapi.dll", EntryPoint = "BASS_WASAPI_Init")]
    internal static extern int WasapiInit(int device, uint frequency, uint channels, uint flags,
        float buffer, float period, WasapiProcedure procedure, IntPtr user);
    [DllImport("basswasapi.dll", EntryPoint = "BASS_WASAPI_SetDevice")]
    internal static extern int WasapiSetDevice(uint device);
    [DllImport("basswasapi.dll", EntryPoint = "BASS_WASAPI_Start")]
    internal static extern int WasapiStart();
    [DllImport("basswasapi.dll", EntryPoint = "BASS_WASAPI_Stop")]
    internal static extern int WasapiStop(int reset);
    [DllImport("basswasapi.dll", EntryPoint = "BASS_WASAPI_Free")]
    internal static extern int WasapiFree();

    [DllImport("bassasio.dll", EntryPoint = "BASS_ASIO_Init")]
    internal static extern int AsioInit(int device, uint flags);
    [DllImport("bassasio.dll", EntryPoint = "BASS_ASIO_SetDevice")]
    internal static extern int AsioSetDevice(uint device);
    [DllImport("bassasio.dll", EntryPoint = "BASS_ASIO_Free")]
    internal static extern int AsioFree();
    [DllImport("bassasio.dll", EntryPoint = "BASS_ASIO_ErrorGetCode")]
    internal static extern int AsioError();
    [DllImport("bassasio.dll", EntryPoint = "BASS_ASIO_GetRate")]
    internal static extern double AsioGetRate();
    [DllImport("bassasio.dll", EntryPoint = "BASS_ASIO_ChannelEnable")]
    internal static extern int AsioChannelEnable(int input, uint channel, AsioProcedure procedure, IntPtr user);
    [DllImport("bassasio.dll", EntryPoint = "BASS_ASIO_ChannelJoin")]
    internal static extern int AsioChannelJoin(int input, uint channel, uint channel2);
    [DllImport("bassasio.dll", EntryPoint = "BASS_ASIO_ChannelSetFormat")]
    internal static extern int AsioChannelSetFormat(int input, uint channel, uint format);
    [DllImport("bassasio.dll", EntryPoint = "BASS_ASIO_Start")]
    internal static extern int AsioStart(uint bufferLength, uint threads);
    [DllImport("bassasio.dll", EntryPoint = "BASS_ASIO_Stop")]
    internal static extern int AsioStop();
}

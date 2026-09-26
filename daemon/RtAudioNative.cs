// RtAudioNative.cs
//
// Raw P/Invoke declarations against RtAudio's official C API (rtaudio_c.h)
//
// ONE KNOWN PLATFORM HAZARD, handled explicitly: rtaudio_format_t is
// `typedef unsigned long` in the C header. unsigned long is 4 bytes on
// Windows (LLP64) but 8 bytes on Linux/macOS (LP64) - same struct, two
// different native sizes depending on platform. Declaring one fixed C#
// layout would silently misalign every field after native_formats on
// whichever platform guessed wrong. Solved here with two struct layouts
// (DeviceInfoWin / DeviceInfoUnix) and runtime dispatch based on
// RuntimeInformation.IsOSPlatform - see Device.Get() below.

using System;
using System.Runtime.InteropServices;

namespace daemon
{
    internal static class RtAudioNative
    {
        // Resolves to rtaudio.dll / librtaudio.so / librtaudio.dylib per
        // platform, found via the runtimes/{rid}/native/ convention - see
        // the accompanying build-chain notes.
        private const string LibName = "rtaudio";

        public const int RTAUDIO_API_UNSPECIFIED = 0; // let RtAudio auto-select from whatever was compiled in
        public const int RTAUDIO_API_MACOSX_CORE = 1;
        public const int RTAUDIO_API_LINUX_ALSA = 2;
        public const int RTAUDIO_API_UNIX_JACK = 3;
        public const int RTAUDIO_API_LINUX_PULSE = 4;
        public const int RTAUDIO_API_WINDOWS_WASAPI = 7;

        public const uint RTAUDIO_FORMAT_SINT16 = 0x02;

        public const int NUM_SAMPLE_RATES = 16;
        public const int MAX_NAME_LENGTH = 512;

        [StructLayout(LayoutKind.Sequential)]
        public struct StreamParameters
        {
            public uint device_id;
            public uint num_channels;
            public uint first_channel;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct StreamOptions
        {
            public uint flags;
            public uint num_buffers;
            public int priority;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_NAME_LENGTH)]
            public string name;
        }

        // native_formats: uint (4 bytes) - matches Windows' 4-byte unsigned long
        [StructLayout(LayoutKind.Sequential)]
        public struct DeviceInfoWin
        {
            public uint id;
            public uint output_channels;
            public uint input_channels;
            public uint duplex_channels;
            public int is_default_output;  // C `int` bool convention (nonzero = true), not a marshaled bool
            public int is_default_input;
            public uint native_formats;
            public uint preferred_sample_rate;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = NUM_SAMPLE_RATES)]
            public uint[] sample_rates;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_NAME_LENGTH)]
            public string name;
        }

        // native_formats: ulong (8 bytes) - matches Linux/macOS's 8-byte unsigned long
        [StructLayout(LayoutKind.Sequential)]
        public struct DeviceInfoUnix
        {
            public uint id;
            public uint output_channels;
            public uint input_channels;
            public uint duplex_channels;
            public int is_default_output;
            public int is_default_input;
            public ulong native_formats;
            public uint preferred_sample_rate;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = NUM_SAMPLE_RATES)]
            public uint[] sample_rates;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_NAME_LENGTH)]
            public string name;
        }

        /// <summary>Platform-agnostic view of rtaudio_device_info_t - callers
        /// use this, never the two raw structs above directly (see Device.Get).</summary>
        public struct DeviceInfo
        {
            public uint Id;
            public uint OutputChannels;
            public uint InputChannels;
            public bool IsDefaultOutput;
            public bool IsDefaultInput;
            public string Name;
        }

        /// <summary>Resolves the platform-width hazard once, in one place.</summary>
        public static class Device
        {
            public static DeviceInfo Get(IntPtr audio, uint id)
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    var info = rtaudio_get_device_info_win(audio, id);
                    return new DeviceInfo
                    {
                        Id = info.id,
                        OutputChannels = info.output_channels,
                        InputChannels = info.input_channels,
                        IsDefaultOutput = info.is_default_output != 0,
                        IsDefaultInput = info.is_default_input != 0,
                        Name = info.name,
                    };
                }
                else
                {
                    var info = rtaudio_get_device_info_unix(audio, id);
                    return new DeviceInfo
                    {
                        Id = info.id,
                        OutputChannels = info.output_channels,
                        InputChannels = info.input_channels,
                        IsDefaultOutput = info.is_default_output != 0,
                        IsDefaultInput = info.is_default_input != 0,
                        Name = info.name,
                    };
                }
            }
        }

        // Both entry points below target the SAME native symbol
        // (rtaudio_get_device_info) with different C# return struct layouts -
        // never call these two directly, always go through Device.Get above.
        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rtaudio_get_device_info")]
        private static extern DeviceInfoWin rtaudio_get_device_info_win(IntPtr audio, uint id);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rtaudio_get_device_info")]
        private static extern DeviceInfoUnix rtaudio_get_device_info_unix(IntPtr audio, uint id);

        /// <summary>Matches rtaudio_cb_t: (out, in, nFrames, streamTime, status, userdata) -> int.
        /// Return 0 to continue the stream, per RtAudio's convention.</summary>
        public delegate int AudioCallback(IntPtr output, IntPtr input, uint nFrames, double streamTime, uint status, IntPtr userData);

        public delegate void ErrorCallback(int err, IntPtr msg);

        /// <summary>
        /// Creates an rtaudio_t instance, requesting a specific backend
        /// explicitly rather than RTAUDIO_API_UNSPECIFIED - see the comment
        /// on the API constants above for why auto-detection isn't safe to
        /// rely on here. On Linux, tries PulseAudio first (the whole reason
        /// for this library) and falls back to ALSA only if Pulse creation
        /// itself fails (e.g. a machine with no Pulse/PipeWire at all) -
        /// this fallback is deliberate and logged, unlike RtAudio's own
        /// silent internal one.
        /// </summary>
        public static IntPtr CreateAudioInstance()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return rtaudio_create(RTAUDIO_API_WINDOWS_WASAPI);
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return rtaudio_create(RTAUDIO_API_MACOSX_CORE);
            }
 
            IntPtr handle = rtaudio_create(RTAUDIO_API_LINUX_PULSE);
            if (handle != IntPtr.Zero)
            {
                return handle;
            }
 
            Serilog.Log.Logger.Warning(
                "Could not create a PulseAudio RtAudio instance (no PulseAudio/PipeWire running?) - " +
                "falling back to ALSA. Named/remapped devices (module-remap-source etc.) will not be visible.");
            return rtaudio_create(RTAUDIO_API_LINUX_ALSA);
        }

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr rtaudio_create(int api);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void rtaudio_destroy(IntPtr audio);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int rtaudio_device_count(IntPtr audio);

        /// <summary>Returns a stable device ID for the given 0-based index
        /// (0..rtaudio_device_count()-1). NOTE: this ID, not the index, is
        /// what every other function (including device info lookup) expects.</summary>
        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint rtaudio_get_device_id(IntPtr audio, int index);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint rtaudio_get_default_output_device(IntPtr audio);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint rtaudio_get_default_input_device(IntPtr audio);

        /// <summary>output_params/input_params/options may each be IntPtr.Zero
        /// (e.g. output_params for a capture-only, RX-only stream) - pass
        /// Marshal.AllocHGlobal + StructureToPtr for the ones you want, and
        /// free them once rtaudio_open_stream returns (RtAudio copies what
        /// it needs internally, per its docs).</summary>
        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int rtaudio_open_stream(
            IntPtr audio,
            IntPtr outputParams,
            IntPtr inputParams,
            uint format,
            uint sampleRate,
            ref uint bufferFrames,
            AudioCallback cb,
            IntPtr userData,
            IntPtr options,
            IntPtr errorCallback);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void rtaudio_close_stream(IntPtr audio);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int rtaudio_start_stream(IntPtr audio);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int rtaudio_stop_stream(IntPtr audio);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr rtaudio_error(IntPtr audio);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void rtaudio_show_warnings(IntPtr audio, int show);
    }
}
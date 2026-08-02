#pragma warning disable CS0649
#pragma warning disable IDE1006

using System;
using System.Runtime.InteropServices;

namespace CSCore.Ffmpeg.Interops
{
    public static unsafe partial class ffmpeg
    {
        // avio_alloc_context is not in the generated bindings with the delegate overload we need,
        // so keep a manual binding that accepts our custom delegates via FunctionPtr marshaling.
        [DllImport("avformat-62", EntryPoint = "avio_alloc_context", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        internal static extern AVIOContext* avio_alloc_context(byte* @buffer, int @buffer_size, int @write_flag, void* @opaque,
            [MarshalAs(UnmanagedType.FunctionPtr)] FfmpegCalls.AvioReadData @read_packet,
            [MarshalAs(UnmanagedType.FunctionPtr)] FfmpegCalls.AvioWriteData @write_packet,
            [MarshalAs(UnmanagedType.FunctionPtr)] FfmpegCalls.AvioSeek @seek);
    }
}

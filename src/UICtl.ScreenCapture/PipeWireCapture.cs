using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace UICtl.ScreenCapture;

/// <summary>
/// Hand-rolled P/Invoke against `libpipewire-0.3.so.0` (confirmed installed,
/// v1.6.2 via `dpkg -l`) - no .NET binding for PipeWire exists. Function
/// signatures and struct layouts below are transcribed from PipeWire
/// 1.6.2's real C headers (pipewire/thread-loop.h, context.h, core.h,
/// stream.h, properties.h; spa/buffer/buffer.h), fetched directly from the
/// PipeWire GitHub repo at the exact installed version rather than
/// guessed - a wrong signature here is a native ABI mismatch, not a
/// catchable .NET exception.
///
/// Runs entirely in this standalone process (`uictl-screencapture`),
/// deliberately isolated from the daemon (see Program.cs's doc comment
/// for why) - this is genuinely risky, unproven-in-this-codebase native
/// interop (function pointer callbacks, manual SPA POD encoding/decoding,
/// raw unmanaged memory reads for pixel data), and a crash here must not
/// be able to take the daemon down with it.
/// </summary>
internal static unsafe class PipeWireCapture
{
    private const string Lib = "libpipewire-0.3.so.0";
    private const int SpaDirectionInput = 0;
    private const int PwStreamFlagAutoconnect = 1 << 0;
    private const int PwStreamFlagMapBuffers = 1 << 2;
    private const int PwStreamStateError = -1;
    private const int PwStreamStateStreaming = 4;

    [DllImport(Lib)] private static extern void pw_init(IntPtr argc, IntPtr argv);
    [DllImport(Lib)] private static extern IntPtr pw_thread_loop_new(string? name, IntPtr props);
    [DllImport(Lib)] private static extern void pw_thread_loop_destroy(IntPtr loop);
    [DllImport(Lib)] private static extern int pw_thread_loop_start(IntPtr loop);
    [DllImport(Lib)] private static extern void pw_thread_loop_stop(IntPtr loop);
    [DllImport(Lib)] private static extern void pw_thread_loop_lock(IntPtr loop);
    [DllImport(Lib)] private static extern void pw_thread_loop_unlock(IntPtr loop);
    [DllImport(Lib)] private static extern IntPtr pw_thread_loop_get_loop(IntPtr loop);

    [DllImport(Lib)] private static extern IntPtr pw_context_new(IntPtr mainLoop, IntPtr props, nuint userDataSize);
    [DllImport(Lib)] private static extern void pw_context_destroy(IntPtr context);
    [DllImport(Lib)] private static extern IntPtr pw_context_connect_fd(IntPtr context, int fd, IntPtr props, nuint userDataSize);
    [DllImport(Lib)] private static extern int pw_core_disconnect(IntPtr core);

    [DllImport(Lib)] private static extern IntPtr pw_properties_new_string(string args);
    [DllImport(Lib)] private static extern int pw_properties_set(IntPtr properties, string key, string value);

    [DllImport(Lib)] private static extern IntPtr pw_stream_new(IntPtr core, string name, IntPtr props);
    [DllImport(Lib)] private static extern void pw_stream_add_listener(IntPtr stream, IntPtr listenerHook, IntPtr events, IntPtr data);
    [DllImport(Lib)] private static extern int pw_stream_connect(IntPtr stream, int direction, uint targetId, int flags, IntPtr* @params, uint nParams);
    [DllImport(Lib)] private static extern IntPtr pw_stream_dequeue_buffer(IntPtr stream);
    [DllImport(Lib)] private static extern int pw_stream_queue_buffer(IntPtr stream, IntPtr buffer);
    [DllImport(Lib)] private static extern int pw_stream_disconnect(IntPtr stream);
    [DllImport(Lib)] private static extern void pw_stream_destroy(IntPtr stream);

    [DllImport("libc", EntryPoint = "dup")] private static extern int Dup(int fd);

    /// <summary>Mirrors `struct pw_stream_events` (PW_VERSION_STREAM_EVENTS = 2) exactly - field order and count matter, this is a raw native vtable PipeWire walks by offset.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PwStreamEvents
    {
        public uint Version;
        public IntPtr Destroy;
        public IntPtr StateChanged;
        public IntPtr ControlInfo;
        public IntPtr IoChanged;
        public IntPtr ParamChanged;
        public IntPtr AddBuffer;
        public IntPtr RemoveBuffer;
        public IntPtr Process;
        public IntPtr Drained;
        public IntPtr Command;
        public IntPtr TriggerDone;
    }

    private static uint _capturedWidth;
    private static uint _capturedHeight;
    private static IntPtr _streamPtr;
    private static readonly ManualResetEventSlim FrameReady = new(false);
    private static byte[]? _capturedRgba;
    private static string? _errorMessage;

    /// <summary>
    /// Connects to the ScreenCast session's PipeWire node, negotiates a raw
    /// video format, waits for exactly one frame, and returns it as RGBA
    /// bytes (PngCodec.Encode's expected layout) plus its dimensions.
    /// Throws on timeout, stream error, or a format PipeWire can't/won't
    /// negotiate. Every native call here can, in principle, crash the
    /// process outright rather than throw - this is why it runs in its own
    /// executable (Program.cs), not in the daemon.
    /// </summary>
    public static (byte[] Rgba, int Width, int Height) CaptureFrame(SafeHandle pipeWireFd, uint nodeId, TimeSpan timeout)
    {
        _capturedWidth = _capturedHeight = 0;
        _capturedRgba = null;
        _errorMessage = null;
        FrameReady.Reset();

        pw_init(IntPtr.Zero, IntPtr.Zero);

        IntPtr loop = pw_thread_loop_new("uictl-screencapture", IntPtr.Zero);
        if (loop == IntPtr.Zero) throw new InvalidOperationException("pw_thread_loop_new failed");
        if (pw_thread_loop_start(loop) != 0) throw new InvalidOperationException("pw_thread_loop_start failed");

        IntPtr context = IntPtr.Zero, core = IntPtr.Zero, stream = IntPtr.Zero;
        IntPtr eventsPtr = IntPtr.Zero, hookPtr = IntPtr.Zero, formatParamPtr = IntPtr.Zero;
        try
        {
            pw_thread_loop_lock(loop);

            IntPtr mainLoop = pw_thread_loop_get_loop(loop);
            context = pw_context_new(mainLoop, IntPtr.Zero, 0);
            if (context == IntPtr.Zero) throw new InvalidOperationException("pw_context_new failed");

            // Dup the fd rather than hand PipeWire the same fd Tmds.DBus's
            // CloseSafeHandle already owns - pw_context_connect_fd takes
            // ownership and closes it itself on disconnect, which would
            // otherwise race/double-close against the SafeHandle's own
            // disposal.
            int dupFd = Dup((int)pipeWireFd.DangerousGetHandle());
            if (dupFd < 0) throw new InvalidOperationException("dup() of the PipeWire fd failed");

            core = pw_context_connect_fd(context, dupFd, IntPtr.Zero, 0);
            if (core == IntPtr.Zero) throw new InvalidOperationException("pw_context_connect_fd failed");

            IntPtr streamProps = pw_properties_new_string("");
            pw_properties_set(streamProps, "media.type", "Video");
            pw_properties_set(streamProps, "media.category", "Capture");
            pw_properties_set(streamProps, "media.role", "Screen");

            stream = pw_stream_new(core, "uictl-screencapture", streamProps);
            if (stream == IntPtr.Zero) throw new InvalidOperationException("pw_stream_new failed");
            _streamPtr = stream;

            var events = new PwStreamEvents
            {
                Version = 2,
                StateChanged = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int, int, IntPtr, void>)&OnStateChanged,
                ParamChanged = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, uint, IntPtr, void>)&OnParamChanged,
                Process = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, void>)&OnProcess,
            };
            eventsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PwStreamEvents>());
            Marshal.StructureToPtr(events, eventsPtr, false);

            // struct spa_hook is an opaque, PipeWire-managed linked-list
            // node - callers just need to own stable storage for it, never
            // read/write its fields directly. 128 bytes is comfortably
            // larger than its real size on any ABI this targets.
            hookPtr = Marshal.AllocHGlobal(128);
            pw_stream_add_listener(stream, hookPtr, eventsPtr, IntPtr.Zero);

            byte[] formatParam = BuildEnumFormatParam();
            formatParamPtr = Marshal.AllocHGlobal(formatParam.Length);
            Marshal.Copy(formatParam, 0, formatParamPtr, formatParam.Length);

            IntPtr* paramsArray = stackalloc IntPtr[1];
            paramsArray[0] = formatParamPtr;

            int rc = pw_stream_connect(stream, SpaDirectionInput, nodeId, PwStreamFlagAutoconnect | PwStreamFlagMapBuffers, paramsArray, 1);
            if (rc != 0) throw new InvalidOperationException($"pw_stream_connect failed (rc={rc})");
        }
        finally
        {
            pw_thread_loop_unlock(loop);
        }

        bool got = FrameReady.Wait(timeout);

        pw_thread_loop_lock(loop);
        try
        {
            if (stream != IntPtr.Zero) { pw_stream_disconnect(stream); pw_stream_destroy(stream); }
        }
        finally
        {
            pw_thread_loop_unlock(loop);
        }
        if (core != IntPtr.Zero) pw_core_disconnect(core);
        if (context != IntPtr.Zero) pw_context_destroy(context);
        pw_thread_loop_stop(loop);
        pw_thread_loop_destroy(loop);
        if (eventsPtr != IntPtr.Zero) Marshal.FreeHGlobal(eventsPtr);
        if (hookPtr != IntPtr.Zero) Marshal.FreeHGlobal(hookPtr);
        if (formatParamPtr != IntPtr.Zero) Marshal.FreeHGlobal(formatParamPtr);

        if (_errorMessage is not null)
            throw new InvalidOperationException($"PipeWire stream error: {_errorMessage}");
        if (!got || _capturedRgba is null)
            throw new TimeoutException($"no frame received from PipeWire within {timeout.TotalSeconds:F0}s");

        return (_capturedRgba, (int)_capturedWidth, (int)_capturedHeight);
    }

    /// <summary>
    /// Offers a single, fixed BGRx format - GNOME/Mutter's well-known
    /// default for screencast - rather than a Choice-Enum of alternatives.
    /// Deliberate simplification, not an oversight: `param_changed` never
    /// fired with id=SPA_PARAM_Format in live testing here (only
    /// SPA_PARAM_Props did, even though the stream reached PW_STREAM_STATE_PAUSED,
    /// meaning negotiation *did* succeed at the SPA level) - so there's no
    /// reliable way observed here to read back *which* format among
    /// several alternatives was actually chosen. Requesting exactly one
    /// format removes the ambiguity entirely: OnProcess doesn't need to
    /// learn what was negotiated, since this dictates it. If BGRx isn't
    /// actually available on some other compositor, negotiation fails
    /// outright (a clear, catchable error) rather than silently guessing
    /// wrong - revisit with multiple alternatives + real format read-back
    /// if that's ever observed. Size is a generous range (any real
    /// monitor); actual width/height are derived from the delivered
    /// buffer's own stride/chunk size in OnProcess, not from this
    /// negotiation - sidesteps the same read-back problem for size too.
    /// </summary>
    private static byte[] BuildEnumFormatParam()
    {
        var mediaTypeVal = new SpaPodWriter(); mediaTypeVal.WriteId(SpaMediaType.Video);
        var mediaSubtypeVal = new SpaPodWriter(); mediaSubtypeVal.WriteId(SpaMediaSubtype.Raw);
        var formatVal = new SpaPodWriter();
        formatVal.WriteId(SpaVideoFormat.BGRx);
        var sizeVal = new SpaPodWriter();
        sizeVal.WriteRectangleChoiceRange([(1920u, 1080u), (1u, 1u), (8192u, 8192u)]);
        var framerateVal = new SpaPodWriter();
        framerateVal.WriteFractionChoiceRange([(0u, 1u), (0u, 1u), (1000u, 1u)]);

        var props = new SpaPodWriter();
        props.WriteProp(SpaFormatKey.MediaType, mediaTypeVal.ToArray());
        props.WriteProp(SpaFormatKey.MediaSubtype, mediaSubtypeVal.ToArray());
        props.WriteProp(SpaFormatKey.VideoFormat, formatVal.ToArray());
        props.WriteProp(SpaFormatKey.VideoSize, sizeVal.ToArray());
        props.WriteProp(SpaFormatKey.VideoFramerate, framerateVal.ToArray());
        byte[] propsBytes = props.ToArray();

        var obj = new SpaPodWriter();
        obj.WriteObjectHeader(8 + (uint)propsBytes.Length, SpaType.ObjectFormat, SpaParamType.EnumFormat);
        obj.WriteRaw(propsBytes);
        return obj.ToArray();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnStateChanged(IntPtr data, int oldState, int state, IntPtr error)
    {
        string? msg = error == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(error);
        Console.Error.WriteLine($"uictl-screencapture: stream state {oldState} -> {state}{(msg is null ? "" : $" ({msg})")}");
        if (state == PwStreamStateError)
        {
            _errorMessage = msg ?? "unknown stream error";
            FrameReady.Set();
        }
    }

    /// <summary>
    /// Deliberately a no-op beyond logging: live testing showed
    /// param_changed never fires with id=SPA_PARAM_Format for this
    /// screencast node the way a "normal" PipeWire device node would
    /// (only SPA_PARAM_Props(2) reliably fires before the stream reaches
    /// PAUSED) - so this doesn't try to read back the negotiated
    /// format/size. BuildEnumFormatParam requests a single fixed format
    /// for exactly this reason (nothing to read back, since there was
    /// only ever one option), and OnProcess derives width/height from the
    /// delivered buffer's own stride/chunk size instead.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnParamChanged(IntPtr data, uint id, IntPtr param)
    {
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnProcess(IntPtr data)
    {
        if (_capturedRgba is not null) return; // only ever want the first frame
        IntPtr pwBuffer = pw_stream_dequeue_buffer(_streamPtr);
        if (pwBuffer == IntPtr.Zero) return;
        try
        {
            // struct pw_buffer { struct spa_buffer *buffer; void *user_data; uint64 size; uint64 requested; uint64 time; }
            IntPtr spaBuffer = Marshal.ReadIntPtr(pwBuffer, 0);
            if (spaBuffer == IntPtr.Zero) return;

            // struct spa_buffer { uint32 n_metas; uint32 n_datas; spa_meta* metas; spa_data* datas; }
            uint nDatas = (uint)Marshal.ReadInt32(spaBuffer, 4);
            IntPtr datasPtr = Marshal.ReadIntPtr(spaBuffer, 16);
            if (nDatas == 0 || datasPtr == IntPtr.Zero) return;

            // struct spa_data { uint32 type; uint32 flags; int64 fd; uint32 mapoffset; uint32 maxsize; void* data; spa_chunk* chunk; } - 8-byte pointer alignment padding after maxsize.
            IntPtr dataPtr = Marshal.ReadIntPtr(datasPtr, 24);
            IntPtr chunkPtr = Marshal.ReadIntPtr(datasPtr, 32);
            if (dataPtr == IntPtr.Zero || chunkPtr == IntPtr.Zero) return;

            // struct spa_chunk { uint32 offset; uint32 size; int32 stride; int32 flags; }
            uint chunkOffset = (uint)Marshal.ReadInt32(chunkPtr, 0);
            uint chunkSize = (uint)Marshal.ReadInt32(chunkPtr, 4);
            int stride = Marshal.ReadInt32(chunkPtr, 8);

            // Width/height are derived from the delivered buffer itself
            // (stride is bytes-per-row for our fixed 4-byte-per-pixel BGRx
            // request; height = valid bytes / stride) rather than from a
            // pre-negotiated size - see BuildEnumFormatParam's doc comment
            // for why param_changed's own read-back wasn't reliable here.
            if (stride <= 0 || chunkSize == 0) { Console.Error.WriteLine($"uictl-screencapture: buffer has no usable stride/size (stride={stride}, chunkSize={chunkSize})"); return; }
            int width = stride / 4;
            int height = (int)(chunkSize / stride);
            if (width <= 0 || height <= 0) { Console.Error.WriteLine($"uictl-screencapture: computed non-positive frame size ({width}x{height})"); return; }

            Console.Error.WriteLine($"uictl-screencapture: captured frame {width}x{height} (stride={stride})");
            _capturedRgba = ConvertBgrxToRgba(dataPtr + (int)chunkOffset, stride, width, height);
            _capturedWidth = (uint)width;
            _capturedHeight = (uint)height;
        }
        finally
        {
            pw_stream_queue_buffer(_streamPtr, pwBuffer);
            if (_capturedRgba is not null) FrameReady.Set();
        }
    }

    /// <summary>BGRx -> RGBA: swap R/B, force alpha opaque (the 4th byte in BGRx is padding, not real alpha).</summary>
    private static byte[] ConvertBgrxToRgba(IntPtr src, int stride, int width, int height)
    {
        byte[] rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            byte* row = (byte*)src + (long)y * stride;
            int rowOut = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                byte b = row[x * 4 + 0], g = row[x * 4 + 1], r = row[x * 4 + 2];
                int o = rowOut + x * 4;
                rgba[o] = r;
                rgba[o + 1] = g;
                rgba[o + 2] = b;
                rgba[o + 3] = 255;
            }
        }
        return rgba;
    }
}

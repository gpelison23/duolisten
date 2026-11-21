using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace DuoListen;

// ---------------------------------------------------------------------------
// Captura "todo o som do PC, menos o deste app" (Windows 11 / build 20348+).
//
// Usa a captura por processo do Windows (AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK)
// no modo EXCLUDE_TARGET_PROCESS_TREE apontando para o nosso próprio processo.
// O áudio é pego dos programas antes do volume/mudo do alto-falante, então dá
// para deixar os alto-falantes do PC no mudo e os fones continuam recebendo o som.
// ---------------------------------------------------------------------------
sealed class ProcessLoopbackCapture : IWaveIn
{
    const string VirtualDeviceProcessLoopback = "VAD\\Process_Loopback";
    static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");

    AudioClient _client;
    Thread _thread;
    volatile bool _stop;
    readonly SynchronizationContext _sync = SynchronizationContext.Current;

    public WaveFormat WaveFormat { get; set; } = new WaveFormat(48000, 16, 2);
    public event EventHandler<WaveInEventArgs> DataAvailable;
    public event EventHandler<StoppedEventArgs> RecordingStopped;

    public static bool IsSupported => Environment.OSVersion.Version.Build >= 20348;

    // Ativação, Initialize e captura acontecem todas na mesma thread (MTA) de áudio.
    // StartRecording espera a ativação terminar e repassa qualquer erro.
    public void StartRecording()
    {
        _stop = false;
        Exception startError = null;
        // Sem "using": se der tempo esgotado, a thread de captura ainda chama ready.Set() depois,
        // e um evento já descartado derrubaria o app.
        var ready = new ManualResetEventSlim(false);
        _thread = new Thread(() => CaptureLoop(ready, e => startError = e))
            { IsBackground = true, Name = "DuoListen capture", Priority = ThreadPriority.AboveNormal };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(8)))
        {
            _stop = true;
            throw new TimeoutException("Tempo esgotado ao iniciar a captura de áudio.");
        }
        if (startError != null)
        {
            _thread.Join(2000);
            _thread = null;
            throw startError;
        }
    }

    public void StopRecording() => _stop = true;

    void CaptureLoop(ManualResetEventSlim ready, Action<Exception> reportStartError)
    {
        try
        {
            _client = new AudioClient(Activate((uint)Environment.ProcessId));
            // Buffer de 100 ms; o Windows converte para o formato pedido.
            _client.Initialize(AudioClientShareMode.Shared,
                AudioClientStreamFlags.Loopback | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
                1_000_000, 0, WaveFormat, Guid.Empty);
            _client.Start();
        }
        catch (Exception ex)
        {
            reportStartError(ex);
            try { _client?.Dispose(); } catch { }
            _client = null;
            ready.Set();
            return;
        }
        ready.Set();

        Exception error = null;
        try
        {
            var capture = _client.AudioCaptureClient;
            int blockAlign = WaveFormat.BlockAlign;
            var buffer = new byte[WaveFormat.AverageBytesPerSecond]; // 1 s, sobra
            while (!_stop)
            {
                Thread.Sleep(10);
                int filled = 0;
                int packet = capture.GetNextPacketSize();
                while (packet > 0)
                {
                    IntPtr ptr = capture.GetBuffer(out int frames, out AudioClientBufferFlags flags);
                    int bytes = frames * blockAlign;
                    if (filled + bytes > buffer.Length) Array.Resize(ref buffer, filled + bytes);
                    if ((flags & AudioClientBufferFlags.Silent) != 0) Array.Clear(buffer, filled, bytes);
                    else Marshal.Copy(ptr, buffer, filled, bytes);
                    filled += bytes;
                    capture.ReleaseBuffer(frames);
                    packet = capture.GetNextPacketSize();
                }
                if (filled > 0) DataAvailable?.Invoke(this, new WaveInEventArgs(buffer, filled));
            }
        }
        catch (Exception ex) { error = ex; }
        finally
        {
            try { _client.Stop(); } catch { }
            try { _client.Dispose(); } catch { }
            _client = null;
            var args = new StoppedEventArgs(error);
            if (_sync != null) _sync.Post(_ => RecordingStopped?.Invoke(this, args), null);
            else RecordingStopped?.Invoke(this, args);
        }
    }

    public void Dispose()
    {
        _stop = true;
        if (_thread != null && _thread != Thread.CurrentThread) _thread.Join(2000);
        _thread = null;
    }

    // ----------------------------- interop -----------------------------

    static IAudioClient Activate(uint pid)
    {
        var p = new AUDIOCLIENT_ACTIVATION_PARAMS
        {
            ActivationType = 1,            // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
            TargetProcessId = pid,
            ProcessLoopbackMode = 1,       // PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE
        };
        int paramsSize = Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>();
        IntPtr paramsPtr = Marshal.AllocHGlobal(paramsSize);
        IntPtr propPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PROPVARIANT_BLOB>());
        try
        {
            Marshal.StructureToPtr(p, paramsPtr, false);
            var prop = new PROPVARIANT_BLOB { vt = 65 /* VT_BLOB */, cbSize = (uint)paramsSize, pBlobData = paramsPtr };
            Marshal.StructureToPtr(prop, propPtr, false);

            var handler = new ActivationHandler();
            ActivateAudioInterfaceAsync(VirtualDeviceProcessLoopback, IID_IAudioClient, propPtr, handler, out _);
            if (!handler.Done.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Tempo esgotado ao ativar a captura de áudio.");
            if (handler.Error != null) throw handler.Error;
            return handler.Client;
        }
        finally
        {
            Marshal.FreeHGlobal(propPtr);
            Marshal.FreeHGlobal(paramsPtr);
        }
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [StructLayout(LayoutKind.Sequential)]
    struct AUDIOCLIENT_ACTIVATION_PARAMS
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    // PROPVARIANT com VT_BLOB (layout x64: vt @0, cbSize @8, pBlobData @16)
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct PROPVARIANT_BLOB
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public uint cbSize;
        [FieldOffset(16)] public IntPtr pBlobData;
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    // O Windows exige que o callback seja "agile"
    [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAgileObject { }

    [ComVisible(true)]
    sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEventSlim Done = new(false);
        public IAudioClient Client;
        public Exception Error;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op)
        {
            try
            {
                op.GetActivateResult(out int hr, out object iface);
                if (hr < 0) Error = Marshal.GetExceptionForHR(hr) ?? new COMException("Falha ao ativar a captura", hr);
                else Client = (IAudioClient)iface;
            }
            catch (Exception ex) { Error = ex; }
            finally { Done.Set(); }
        }
    }
}

using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace DuoListen;

// ---------------------------------------------------------------------------
// Configuração salva em %AppData%\DuoListen\config.json
// ---------------------------------------------------------------------------
public class Config
{
    public string SourceId { get; set; }
    public string Out1Id { get; set; }
    public string Out2Id { get; set; }
    public int Vol1 { get; set; } = 100;
    public int Vol2 { get; set; } = 100;
    public int Delay1 { get; set; } = 0;
    public int Delay2 { get; set; } = 0;
    public string Lang { get; set; }
    public bool MuteSpeakers { get; set; } = true;
    // Saída que o app deixou no mudo; se o app fechar de forma inesperada, é desfeito na próxima abertura
    public string MutedDeviceId { get; set; }
    // Saída padrão original, quando o app trocou a padrão porque ela era um dos fones
    public string PrevDefaultId { get; set; }

    static string PathFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DuoListen", "config.json");

    public static Config Load()
    {
        try { return JsonSerializer.Deserialize<Config>(File.ReadAllText(PathFile)) ?? new Config(); }
        catch { return new Config(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathFile)!);
            // Grava num arquivo temporário e troca de uma vez: se o PC desligar no meio,
            // o config.json não fica corrompido (e o app não perde qual alto-falante ele deixou no mudo).
            string tmp = PathFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this));
            File.Move(tmp, PathFile, overwrite: true);
        }
        catch { /* não é crítico */ }
    }
}

// ---------------------------------------------------------------------------
// Uma saída (um fone): recebe áudio float, aplica volume/atraso e toca.
// ---------------------------------------------------------------------------
class OutputSink : IDisposable
{
    const int BaseLatencyMs = 100; // a captura loopback chega em blocos de ~50 ms

    readonly BufferedWaveProvider _buffer;
    readonly VolumeSampleProvider _volume;
    readonly WasapiOut _player;
    readonly WaveFormat _srcFormat;
    readonly byte[] _discard = new byte[64 * 1024];
    static readonly byte[] _silence = new byte[64 * 1024];
    volatile int _delayMs;

    public string Name { get; }
    public string Id { get; }
    public event Action<string> Error;

    public OutputSink(MMDevice device, WaveFormat srcFloatFormat, float volume, int delayMs)
    {
        Name = device.FriendlyName;
        Id = device.ID;
        _srcFormat = srcFloatFormat;
        _delayMs = delayMs;

        _buffer = new BufferedWaveProvider(srcFloatFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(3),
            DiscardOnBufferOverflow = true,
            ReadFully = true,
        };

        ISampleProvider chain = _buffer.ToSampleProvider();
        _volume = new VolumeSampleProvider(chain) { Volume = volume };
        chain = _volume;

        var mix = device.AudioClient.MixFormat;
        if (mix.Channels != srcFloatFormat.Channels)
            chain = new ChannelAdapter(chain, mix.Channels);
        if (mix.SampleRate != srcFloatFormat.SampleRate)
            chain = new WdlResamplingSampleProvider(chain, mix.SampleRate);

        _player = new WasapiOut(device, AudioClientShareMode.Shared, true, 40);
        try
        {
            _player.PlaybackStopped += (_, e) => { if (e.Exception != null) Error?.Invoke($"{Name}: {e.Exception.Message}"); };
            _player.Init(chain.ToWaveProvider());

            PadTo(TargetMs);
            _player.Play();
        }
        catch
        {
            // Fone que falhou ao abrir não pode ficar preso aberto
            try { _player.Dispose(); } catch { }
            throw;
        }
    }

    int TargetMs => BaseLatencyMs + _delayMs;

    public float Volume { set => _volume.Volume = value; }

    public int DelayMs
    {
        set => _delayMs = value; // aplicado no próximo bloco de áudio (thread de captura)
    }

    public void Push(byte[] data, int count)
    {
        _buffer.AddSamples(data, 0, count);
        Resync();
    }

    // Mantém o buffer perto do alvo: corrige deriva de relógio entre os
    // dispositivos e recupera o atraso depois de momentos de silêncio.
    void Resync()
    {
        double buffered = _buffer.BufferedDuration.TotalMilliseconds;
        int target = TargetMs;
        if (buffered > target + 100)
            Trim(buffered - target);
        else if (buffered < Math.Max(30, target - 70))
            PadTo(target);
    }

    void PadTo(int targetMs)
    {
        double missing = targetMs - _buffer.BufferedDuration.TotalMilliseconds;
        if (missing <= 0) return;
        int bytes = Align((int)(_srcFormat.AverageBytesPerSecond * missing / 1000.0));
        while (bytes > 0)
        {
            int n = Math.Min(bytes, _silence.Length);
            _buffer.AddSamples(_silence, 0, n);
            bytes -= n;
        }
    }

    void Trim(double ms)
    {
        int bytes = Align((int)(_srcFormat.AverageBytesPerSecond * ms / 1000.0));
        while (bytes > 0)
        {
            int n = _buffer.Read(_discard, 0, Math.Min(bytes, _discard.Length));
            if (n <= 0) break;
            bytes -= n;
        }
    }

    int Align(int bytes) => bytes - bytes % _srcFormat.BlockAlign;

    public void Dispose()
    {
        try { _player.Stop(); } catch { }
        try { _player.Dispose(); } catch { }
    }
}

// Converte número de canais (mono <-> estéreo <-> multicanal)
class ChannelAdapter : ISampleProvider
{
    readonly ISampleProvider _src;
    readonly int _inCh, _outCh;
    float[] _tmp = Array.Empty<float>();

    public WaveFormat WaveFormat { get; }

    public ChannelAdapter(ISampleProvider src, int outChannels)
    {
        _src = src;
        _inCh = src.WaveFormat.Channels;
        _outCh = outChannels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(src.WaveFormat.SampleRate, outChannels);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / _outCh;
        int need = frames * _inCh;
        if (_tmp.Length < need) _tmp = new float[need];
        int got = _src.Read(_tmp, 0, need) / _inCh;

        for (int f = 0; f < got; f++)
        {
            int i = f * _inCh, o = offset + f * _outCh;
            if (_outCh == 1)
            {
                float sum = 0;
                for (int c = 0; c < _inCh; c++) sum += _tmp[i + c];
                buffer[o] = sum / _inCh;
            }
            else
            {
                for (int c = 0; c < _outCh; c++)
                    buffer[o + c] = _inCh == 1 ? _tmp[i] : (c < _inCh ? _tmp[i + c] : 0f);
            }
        }
        return got * _outCh;
    }
}

// ---------------------------------------------------------------------------
// Motor: captura o que toca no dispositivo "fonte" e replica nos fones.
// ---------------------------------------------------------------------------
class Engine : IDisposable
{
    IWaveIn _capture;
    readonly List<OutputSink> _sinks = new();
    byte[] _floatBuf = Array.Empty<byte>();
    WaveFormat _capFormat, _floatFormat;
    bool _capIsFloat;
    readonly object _lock = new();
    volatile bool _failed;

    public event Action<string> Error;
    public IReadOnlyList<OutputSink> Sinks => _sinks;

    /// <param name="source">Dispositivo a capturar, ou null para "todo o som do PC" (captura por processo).</param>
    public void Start(MMDevice source, IEnumerable<(MMDevice dev, float vol, int delay)> outputs)
    {
        _capture = source == null ? new ProcessLoopbackCapture() : new WasapiLoopbackCapture(source);
        _capFormat = _capture.WaveFormat;
        _floatFormat = WaveFormat.CreateIeeeFloatWaveFormat(_capFormat.SampleRate, _capFormat.Channels);
        _capIsFloat = _capFormat.Encoding == WaveFormatEncoding.IeeeFloat ||
            (_capFormat is WaveFormatExtensible ext && ext.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);

        foreach (var (dev, vol, delay) in outputs)
        {
            var sink = new OutputSink(dev, _floatFormat, vol, delay);
            sink.Error += m => Error?.Invoke(m);
            _sinks.Add(sink);
        }

        _capture.DataAvailable += OnData;
        _capture.RecordingStopped += (_, e) => { if (e.Exception != null) Error?.Invoke("Captura: " + e.Exception.Message); };
        _capture.StartRecording();
    }

    void OnData(object sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0 || _failed) return;
        int bytes;
        try { bytes = ToFloat(e.Buffer, e.BytesRecorded); }
        catch (Exception ex) { Error?.Invoke(ex.Message); _failed = true; return; }
        lock (_lock)
            foreach (var s in _sinks) s.Push(_floatBuf, bytes);
    }

    // Converte o formato de captura (normalmente já float 32) para float 32.
    // A captura de "todo o som do PC" chega em 16 bits, então este é o caminho de todo bloco de áudio.
    int ToFloat(byte[] src, int count)
    {
        int bits = _capFormat.BitsPerSample;

        if (_capIsFloat && bits == 32)
        {
            EnsureBuf(count);
            Buffer.BlockCopy(src, 0, _floatBuf, 0, count);
            return count;
        }

        if (_capIsFloat || (bits != 16 && bits != 24 && bits != 32))
            throw new NotSupportedException($"Formato de captura não suportado ({_capFormat})");

        int bytesPerSample = bits / 8;
        int samples = count / bytesPerSample;
        EnsureBuf(samples * 4);
        var dst = MemoryMarshal.Cast<byte, float>(_floatBuf.AsSpan(0, samples * 4));
        switch (bits)
        {
            case 16:
            {
                var s = MemoryMarshal.Cast<byte, short>(src.AsSpan(0, samples * 2));
                for (int i = 0; i < s.Length; i++) dst[i] = s[i] * (1f / 32768f);
                break;
            }
            case 24:
                for (int i = 0, p = 0; i < samples; i++, p += 3)
                    dst[i] = ((src[p] << 8 | src[p + 1] << 16 | src[p + 2] << 24) >> 8) * (1f / 8388608f);
                break;
            case 32:
            {
                var s = MemoryMarshal.Cast<byte, int>(src.AsSpan(0, samples * 4));
                for (int i = 0; i < s.Length; i++) dst[i] = s[i] * (1f / 2147483648f);
                break;
            }
        }
        return samples * 4;
    }

    void EnsureBuf(int n) { if (_floatBuf.Length < n) _floatBuf = new byte[n]; }

    public void Dispose()
    {
        try { _capture?.StopRecording(); } catch { }
        try { _capture?.Dispose(); } catch { }
        _capture = null;
        lock (_lock)
        {
            foreach (var s in _sinks) s.Dispose();
            _sinks.Clear();
        }
    }
}


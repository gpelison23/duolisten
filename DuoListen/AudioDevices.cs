using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace DuoListen;

// ---------------------------------------------------------------------------
// Ajudantes de dispositivos: detectar fones Bluetooth e trocar a saída padrão.
// ---------------------------------------------------------------------------
static class AudioDevices
{
    const uint FormFactorSpeakers = 1, FormFactorHeadphones = 3, FormFactorHeadset = 5;

    /// <summary>Concatena os valores de texto do dispositivo (contêm o caminho PnP, ex.: BTHENUM\...).</summary>
    static string AllStrings(MMDevice d)
    {
        var parts = new List<string>();
        try
        {
            var store = d.Properties;
            for (int i = 0; i < store.Count; i++)
            {
                try { if (store.GetValue(i).Value is string s) parts.Add(s); } catch { }
            }
        }
        catch { }
        return string.Join("|", parts).ToUpperInvariant();
    }

    static uint FormFactor(MMDevice d)
    {
        try
        {
            if (d.Properties.Contains(PropertyKeys.PKEY_AudioEndpoint_FormFactor))
                return Convert.ToUInt32(d.Properties[PropertyKeys.PKEY_AudioEndpoint_FormFactor].Value);
        }
        catch { }
        return 0;
    }

    /// <summary>Fone Bluetooth estéreo (A2DP ou LE Audio). Exclui o perfil "Hands-Free" de chamadas.</summary>
    public static bool IsBluetoothStereo(MMDevice d)
    {
        string s = AllStrings(d);
        bool bt = s.Contains("BTHENUM") || s.Contains("BTHLE");
        bool handsFree = s.Contains("BTHHFENUM") || s.Contains("HANDS-FREE") || s.Contains("HANDSFREE");
        return bt && !handsFree;
    }

    static bool IsBluetooth(MMDevice d)
    {
        string s = AllStrings(d);
        return s.Contains("BTHENUM") || s.Contains("BTHLE") || s.Contains("BTHHFENUM");
    }

    /// <summary>Escolhe até dois fones para o primeiro uso: Bluetooth estéreo primeiro, depois fones com fio.</summary>
    public static List<MMDevice> PickHeadphones(IList<MMDevice> devices)
    {
        var picked = devices.Where(IsBluetoothStereo).Take(2).ToList();
        if (picked.Count < 2)
            picked.AddRange(devices.Where(d => !picked.Contains(d) && FormFactor(d) == FormFactorHeadphones && !IsBluetooth(d))
                                   .Take(2 - picked.Count));
        return picked;
    }

    /// <summary>Melhor saída para virar padrão enquanto o app toca (alto-falantes/monitor, nunca um fone).</summary>
    public static MMDevice PickSpeakerFallback(IList<MMDevice> devices, ICollection<string> excludeIds)
    {
        var candidates = devices.Where(d => !excludeIds.Contains(d.ID) && !IsBluetooth(d)).ToList();
        return candidates.FirstOrDefault(d => FormFactor(d) == FormFactorSpeakers)
            ?? candidates.FirstOrDefault(d => FormFactor(d) != FormFactorHeadphones && FormFactor(d) != FormFactorHeadset)
            ?? candidates.FirstOrDefault();
    }

    /// <summary>Desfaz o que o app mudou no Windows: tira do mudo e devolve a saída padrão original.</summary>
    public static void RestoreWindowsAudio(Config cfg)
    {
        UnmuteSaved(cfg);
        if (cfg.PrevDefaultId != null)
        {
            try
            {
                using var en = new MMDeviceEnumerator();
                if (en.GetDevice(cfg.PrevDefaultId).State == DeviceState.Active) SetDefaultOutput(cfg.PrevDefaultId);
            }
            catch { }
            cfg.PrevDefaultId = null;
        }
        cfg.Save();
    }

    public static void UnmuteSaved(Config cfg)
    {
        if (cfg.MutedDeviceId == null) return;
        try
        {
            using var en = new MMDeviceEnumerator();
            var dev = en.GetDevice(cfg.MutedDeviceId);
            if (dev.State == DeviceState.Active) dev.AudioEndpointVolume.Mute = false;
        }
        catch { }
        cfg.MutedDeviceId = null;
        cfg.Save();
    }

    public static string DefaultOutputId()
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            return en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
        }
        catch { return null; }
    }

    /// <summary>Define a saída padrão do Windows (sons do sistema e multimídia). Chamadas não mudam.</summary>
    public static bool SetDefaultOutput(string deviceId)
    {
        try
        {
            var policy = (IPolicyConfig)new CPolicyConfigClient();
            policy.SetDefaultEndpoint(deviceId, Role.Console);
            policy.SetDefaultEndpoint(deviceId, Role.Multimedia);
            Marshal.ReleaseComObject(policy);
            return true;
        }
        catch { return false; }
    }

    // Interface do Windows usada pelo painel de Som para trocar o dispositivo padrão
    // (não documentada, mas estável desde o Windows 7; usada por EarTrumpet, SoundSwitch etc.).
    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    class CPolicyConfigClient { }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(string id, IntPtr format);
        [PreserveSig] int GetDeviceFormat(string id, bool def, IntPtr format);
        [PreserveSig] int ResetDeviceFormat(string id);
        [PreserveSig] int SetDeviceFormat(string id, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod(string id, bool def, IntPtr defaultPeriod, IntPtr minPeriod);
        [PreserveSig] int SetProcessingPeriod(string id, IntPtr period);
        [PreserveSig] int GetShareMode(string id, IntPtr mode);
        [PreserveSig] int SetShareMode(string id, IntPtr mode);
        [PreserveSig] int GetPropertyValue(string id, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue(string id, IntPtr key, IntPtr value);
        void SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, Role role);
        [PreserveSig] int SetEndpointVisibility(string id, bool visible);
    }
}

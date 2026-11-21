using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace DuoListen;

// ---------------------------------------------------------------------------
// Textos em português e inglês
// ---------------------------------------------------------------------------
static class L
{
    public static bool Pt = true;

    static readonly Dictionary<string, (string pt, string en)> T = new()
    {
        ["mainTitle"]  = ("Tocar nos dois fones", "Play on both headphones"),
        ["descOff"]    = ("Escolha os dispositivos abaixo e ligue.", "Pick the devices below and turn it on."),
        ["descOn"]     = ("Tocando em: ", "Playing on: "),
        ["on"]         = ("Ligado", "On"),
        ["off"]        = ("Desligado", "Off"),
        ["hdrSource"]  = ("Origem do som", "Sound source"),
        ["refresh"]    = ("Atualizar", "Refresh"),
        ["srcTitle"]   = ("Capturar o som de", "Capture sound from"),
        ["srcDesc"]    = ("Recomendado: todo o som do PC.", "Recommended: all PC sound."),
        ["allPc"]      = ("Todo o som do PC", "All PC sound"),
        ["muteTitle"]  = ("Silenciar alto-falantes do PC", "Mute PC speakers"),
        ["muteDesc"]   = ("Enquanto toca, a saída padrão do Windows fica no mudo. Ao parar, o som volta.",
                          "While playing, Windows' default output is muted. It comes back when you stop."),
        ["muteDescNA"] = ("Disponível só com “Todo o som do PC”.", "Only available with “All PC sound”."),
        ["errDefPhone"]= ("A saída padrão do Windows é um dos fones e não há outra saída (alto-falantes ou monitor) para usar no lugar. Em “Capturar o som de”, escolha um dispositivo.",
                          "Windows' default output is one of the headphones and there's no other output (speakers or monitor) to use instead. In “Capture sound from”, pick a device."),
        ["errAllPc"]   = ("Não foi possível capturar todo o som do PC ({0}). Em “Capturar o som de”, escolha um dispositivo.",
                          "Couldn't capture all PC sound ({0}). In “Capture sound from”, pick a device."),
        ["hdrPhones"]  = ("Fones de ouvido", "Headphones"),
        ["phone"]      = ("Fone", "Headphone"),
        ["volume"]     = ("Volume", "Volume"),
        ["syncTitle"]  = ("Sincronia", "Sync"),
        ["syncDesc"]   = ("Atrase um fone se ele tocar antes do outro", "Delay a headphone if it plays ahead of the other"),
        ["delay"]      = ("Atraso do fone {0} (ms)", "Headphone {0} delay (ms)"),
        ["hdrGeneral"] = ("Geral", "General"),
        ["lang"]       = ("Idioma", "Language"),
        ["unTitle"]    = ("Desinstalar DuoListen", "Uninstall DuoListen"),
        ["unDesc"]     = ("Apaga o app, as configurações e os arquivos temporários.", "Deletes the app, its settings and temporary files."),
        ["unButton"]   = ("Desinstalar", "Uninstall"),
        ["unAskTitle"] = ("Desinstalar o DuoListen?", "Uninstall DuoListen?"),
        ["unAsk"]      = ("Isso apaga o DuoListen.exe, os arquivos que vieram no zip, suas configurações e os arquivos temporários do app. Os alto-falantes saem do mudo. Não dá para desfazer.",
                          "This deletes DuoListen.exe, the files that came in the zip, your settings and the app's temporary files. Speakers are unmuted. This can't be undone."),
        ["cancel"]     = ("Cancelar", "Cancel"),
        ["none"]       = ("Nenhum", "None"),
        ["errTitle"]   = ("Não foi possível tocar", "Couldn't play"),
        ["errSrc"]     = ("Escolha de onde vem o som.", "Choose where the sound comes from."),
        ["errOut"]     = ("Escolha pelo menos um fone.", "Choose at least one headphone."),
        ["errLoop"]    = ("A origem do som não pode ser um dos fones.", "The sound source can't be one of the headphones."),
        ["trayStart"]  = ("Tocar nos fones", "Play on headphones"),
        ["trayStop"]   = ("Parar", "Stop"),
        ["trayOpen"]   = ("Abrir DuoListen", "Open DuoListen"),
        ["trayExit"]   = ("Sair", "Exit"),
        ["tipOn"]      = ("DuoListen – tocando", "DuoListen – playing"),
        ["tipOff"]     = ("DuoListen – desligado", "DuoListen – off"),
    };

    public static string S(string key) => Pt ? T[key].pt : T[key].en;
}

public partial class MainWindow : FluentWindow
{
    readonly Config _cfg = Config.Load();
    List<MMDevice> _devices = new();
    Engine _engine;
    bool _loading = true;

    sealed record DeviceItem(MMDevice Dev)
    {
        public override string ToString() => Dev.FriendlyName;
    }

    // Item "Todo o som do PC" na lista de origem
    sealed class AllPcItem
    {
        public const string Id = "*all*";
        public override string ToString() => L.S("allPc");
    }

    bool AllPcSelected => SourceCombo.SelectedItem is AllPcItem;

    public MainWindow()
    {
        L.Pt = _cfg.Lang != null ? _cfg.Lang == "pt" : CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "pt";

        InitializeComponent();

        // Nunca maior que a tela (notebooks com escala 125%/150%): o conteúdo rola se faltar espaço
        MaxHeight = SystemParameters.WorkArea.Height - 24;

        // Mica + troca automática de claro/escuro quando o Windows mudar
        SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, true);
        ApplicationThemeManager.Changed += (_, _) => UpdateTray();

        AppTitleBar.MinimizeActionOverride = (_, _) => HideToTray();
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) HideToTray(); };
        Tray.LeftClick += (_, _) => ShowFromTray();
        Tray.LeftDoubleClick += (_, _) => ShowFromTray();

        Vol1.Value = Math.Clamp(_cfg.Vol1, 0, 100);
        Vol2.Value = Math.Clamp(_cfg.Vol2, 0, 100);
        Delay1.Value = Math.Clamp(_cfg.Delay1, 0, 1000);
        Delay2.Value = Math.Clamp(_cfg.Delay2, 0, 1000);
        LangCombo.SelectedIndex = L.Pt ? 0 : 1;

        Vol1.ValueChanged += (_, _) => ApplyLive();
        Vol2.ValueChanged += (_, _) => ApplyLive();
        Delay1.ValueChanged += (_, _) => ApplyLive();
        Delay2.ValueChanged += (_, _) => ApplyLive();
        LangCombo.SelectionChanged += (_, _) => { if (!_loading) SetLang(LangCombo.SelectedIndex == 0); };

        MuteSwitch.IsChecked = _cfg.MuteSpeakers;
        SourceCombo.SelectionChanged += (_, _) => UpdateMuteCard();
        Application.Current.SessionEnding += (_, _) => StopEngine();

        RestoreSpeakers(); // caso o app tenha fechado de forma inesperada com o áudio do Windows alterado
        LoadDevices();
        ApplyTexts();
        _loading = false;
    }

    // ---------------- textos ----------------
    void SetLang(bool pt)
    {
        L.Pt = pt;
        _cfg.Lang = pt ? "pt" : "en";
        _cfg.Save();
        LoadDevices(); // atualiza o item "Nenhum" mantendo as escolhas
        ApplyTexts();
    }

    void ApplyTexts()
    {
        bool on = _engine != null;
        MainTitle.Text = L.S("mainTitle");
        MainDesc.Text = on ? L.S("descOn") + string.Join(" + ", _engine.Sinks.Select(s => s.Name)) : L.S("descOff");
        PlaySwitch.OnContent = L.S("on");
        PlaySwitch.OffContent = L.S("off");
        HdrSource.Text = L.S("hdrSource");
        RefreshButton.Content = L.S("refresh");
        SourceTitle.Text = L.S("srcTitle");
        SourceDesc.Text = L.S("srcDesc");
        MuteTitle.Text = L.S("muteTitle");
        MuteSwitch.OnContent = L.S("on");
        MuteSwitch.OffContent = L.S("off");
        UpdateMuteCard();
        HdrPhones.Text = L.S("hdrPhones");
        Phone1Title.Text = L.S("phone") + " 1";
        Phone2Title.Text = L.S("phone") + " 2";
        Vol1Title.Text = Vol2Title.Text = L.S("volume");
        Vol1Pct.Text = $"{(int)Vol1.Value}%";
        Vol2Pct.Text = $"{(int)Vol2.Value}%";
        SyncTitle.Text = L.S("syncTitle");
        SyncDesc.Text = L.S("syncDesc");
        Delay1Label.Text = string.Format(L.S("delay"), 1);
        Delay2Label.Text = string.Format(L.S("delay"), 2);
        HdrGeneral.Text = L.S("hdrGeneral");
        LangTitle.Text = L.S("lang");
        UninstallTitle.Text = L.S("unTitle");
        UninstallDesc.Text = L.S("unDesc");
        UninstallButton.Content = L.S("unButton");
        ErrorBar.Title = L.S("errTitle");
        MiToggle.Header = L.S(on ? "trayStop" : "trayStart");
        MiToggleIcon.Symbol = on ? SymbolRegular.Stop24 : SymbolRegular.Play24;
        MiOpen.Header = L.S("trayOpen");
        MiExit.Header = L.S("trayExit");
        UpdateTray();
    }

    // ---------------- dispositivos ----------------
    void LoadDevices()
    {
        string src = AllPcSelected ? AllPcItem.Id : SelectedDevice(SourceCombo)?.ID ?? _cfg.SourceId;
        string o1 = Out1Combo.Items.Count > 0 ? SelectedDevice(Out1Combo)?.ID : _cfg.Out1Id;
        string o2 = Out2Combo.Items.Count > 0 ? SelectedDevice(Out2Combo)?.ID : _cfg.Out2Id;

        using var en = new MMDeviceEnumerator();
        _devices = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
        string defId = null;
        try { defId = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID; } catch { }
        bool allPcOk = ProcessLoopbackCapture.IsSupported;
        if (src == null || src == AllPcItem.Id) src = allPcOk ? AllPcItem.Id : defId;
        else if (!_devices.Any(d => d.ID == src)) src = allPcOk ? AllPcItem.Id : defId;

        Fill(SourceCombo, src == AllPcItem.Id ? null : src, false);
        if (allPcOk)
        {
            SourceCombo.Items.Insert(0, new AllPcItem());
            if (src == AllPcItem.Id) SourceCombo.SelectedIndex = 0;
        }
        UpdateMuteCard();
        Fill(Out1Combo, o1, true);
        Fill(Out2Combo, o2, true);

        // Primeiro uso (ou nenhum fone escolhido): seleciona sozinho os fones Bluetooth conectados
        if (Out1Combo.SelectedIndex <= 0 && Out2Combo.SelectedIndex <= 0)
        {
            var picks = AudioDevices.PickHeadphones(_devices);
            if (picks.Count > 0) Out1Combo.SelectedIndex = _devices.IndexOf(picks[0]) + 1;
            if (picks.Count > 1) Out2Combo.SelectedIndex = _devices.IndexOf(picks[1]) + 1;
        }
    }

    void Fill(ComboBox cb, string selectId, bool allowNone)
    {
        cb.Items.Clear();
        if (allowNone) cb.Items.Add(L.S("none"));
        foreach (var d in _devices) cb.Items.Add(new DeviceItem(d));
        int idx = _devices.FindIndex(d => d.ID == selectId);
        cb.SelectedIndex = idx >= 0 ? idx + (allowNone ? 1 : 0) : (allowNone ? 0 : -1);
    }

    static MMDevice SelectedDevice(ComboBox cb) => (cb.SelectedItem as DeviceItem)?.Dev;
    void Refresh_Click(object sender, RoutedEventArgs e) => LoadDevices();

    // ---------------- tocar / parar ----------------
    void PlaySwitch_Click(object sender, RoutedEventArgs e)
    {
        if (PlaySwitch.IsChecked == true) StartEngine();
        else StopEngine();
    }

    void Toggle()
    {
        if (_engine == null) StartEngine();
        else StopEngine();
    }

    void StartEngine()
    {
        ErrorBar.IsOpen = false;
        var src = SelectedDevice(SourceCombo);
        var o1 = SelectedDevice(Out1Combo);
        var o2 = SelectedDevice(Out2Combo);

        bool allPc = AllPcSelected;
        if (src == null && !allPc) { ShowError(L.S("errSrc")); return; }
        var outs = new List<(MMDevice, float, int)>();
        if (o1 != null) outs.Add((o1, (float)(Vol1.Value / 100), DelayOf(Delay1)));
        if (o2 != null && o2.ID != o1?.ID) outs.Add((o2, (float)(Vol2.Value / 100), DelayOf(Delay2)));
        if (outs.Count == 0) { ShowError(L.S("errOut")); return; }
        if (!allPc && outs.Any(o => o.Item1.ID == src.ID)) { ShowError(L.S("errLoop")); return; }
        if (allPc)
        {
            // Com "todo o som", os programas tocam na saída padrão; se ela for um dos fones, ele ouviria em dobro
            // Se a saída padrão for um dos fones (o Windows costuma fazer isso ao conectar um fone Bluetooth),
            // trocamos para os alto-falantes/monitor enquanto toca e devolvemos depois.
            string defId = AudioDevices.DefaultOutputId();
            if (defId != null && outs.Any(o => o.Item1.ID == defId))
            {
                var outIds = outs.Select(o => o.Item1.ID).ToList();
                var fallback = AudioDevices.PickSpeakerFallback(_devices, outIds);
                if (fallback == null) { ShowError(L.S("errDefPhone")); return; }
                if (_cfg.PrevDefaultId == null) { _cfg.PrevDefaultId = defId; _cfg.Save(); }
                if (!AudioDevices.SetDefaultOutput(fallback.ID))
                {
                    _cfg.PrevDefaultId = null; _cfg.Save();
                    ShowError(L.S("errDefPhone"));
                    return;
                }
            }
        }

        SaveCfg();
        var engine = new Engine();
        _engine = engine;
        engine.Error += m => Dispatcher.BeginInvoke(() =>
        {
            if (_engine != engine) return; // erro de uma sessão antiga
            StopEngine();
            ShowError(m);
        });
        try
        {
            engine.Start(allPc ? null : src, outs);
        }
        catch (Exception ex)
        {
            StopEngine();
            ShowError(allPc ? string.Format(L.S("errAllPc"), ex.Message) : ex.Message);
            return;
        }
        if (allPc && MuteSwitch.IsChecked == true) MuteSpeakers();
        SetRunningUi(true);
    }

    void StopEngine()
    {
        _engine?.Dispose();
        _engine = null;
        RestoreSpeakers();
        SetRunningUi(false);
    }

    void SetRunningUi(bool on)
    {
        PlaySwitch.IsChecked = on;
        SourceCombo.IsEnabled = Out1Combo.IsEnabled = Out2Combo.IsEnabled = RefreshButton.IsEnabled = !on;
        ApplyTexts();
    }

    void ShowError(string msg)
    {
        PlaySwitch.IsChecked = _engine != null;
        ErrorBar.Message = msg;
        ErrorBar.IsOpen = true;
        ShowFromTray();
    }

    static int DelayOf(NumberBox nb) => (int)Math.Clamp(nb.Value ?? 0, 0, 1000);

    void ApplyLive()
    {
        if (_loading) return;
        Vol1Pct.Text = $"{(int)Vol1.Value}%";
        Vol2Pct.Text = $"{(int)Vol2.Value}%";
        if (_engine == null) return;
        string id1 = SelectedDevice(Out1Combo)?.ID;
        foreach (var s in _engine.Sinks)
        {
            bool first = s.Id == id1;
            s.Volume = (float)((first ? Vol1.Value : Vol2.Value) / 100);
            s.DelayMs = DelayOf(first ? Delay1 : Delay2);
        }
    }

    void SaveCfg()
    {
        _cfg.SourceId = AllPcSelected ? AllPcItem.Id : SelectedDevice(SourceCombo)?.ID;
        _cfg.MuteSpeakers = MuteSwitch.IsChecked == true;
        _cfg.Out1Id = SelectedDevice(Out1Combo)?.ID;
        _cfg.Out2Id = SelectedDevice(Out2Combo)?.ID;
        _cfg.Vol1 = (int)Vol1.Value; _cfg.Vol2 = (int)Vol2.Value;
        _cfg.Delay1 = DelayOf(Delay1); _cfg.Delay2 = DelayOf(Delay2);
        _cfg.Save();
    }

    // ---------------- silenciar alto-falantes ----------------
    void UpdateMuteCard()
    {
        if (MuteTitle == null) return;
        bool ok = AllPcSelected;
        MuteSwitch.IsEnabled = ok;
        MuteDesc.Text = L.S(ok ? "muteDesc" : "muteDescNA");
    }

    void MuteSwitch_Click(object sender, RoutedEventArgs e)
    {
        _cfg.MuteSpeakers = MuteSwitch.IsChecked == true;
        _cfg.Save();
        if (_engine == null || !AllPcSelected) return;
        if (_cfg.MuteSpeakers) MuteSpeakers();
        else AudioDevices.UnmuteSaved(_cfg);
    }

    // Deixa a saída padrão do Windows no mudo. Só desfazemos o mudo que nós mesmos colocamos.
    void MuteSpeakers()
    {
        if (_cfg.MutedDeviceId != null) return;
        try
        {
            using var en = new MMDeviceEnumerator();
            var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (dev.AudioEndpointVolume.Mute) return; // já estava no mudo pelo usuário
            _cfg.MutedDeviceId = dev.ID;
            _cfg.Save(); // grava antes, para conseguir desfazer mesmo se o app travar
            dev.AudioEndpointVolume.Mute = true;
        }
        catch { /* não é crítico */ }
    }

    // Tira do mudo e devolve a saída padrão original (se o app mudou algo)
    void RestoreSpeakers() => AudioDevices.RestoreWindowsAudio(_cfg);

    // ---------------- desinstalar ----------------
    bool _uninstalling;

    async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = L.S("unAskTitle"),
            Content = new System.Windows.Controls.TextBlock { Text = L.S("unAsk"), TextWrapping = TextWrapping.Wrap, MaxWidth = 380 },
            PrimaryButtonText = L.S("unButton"),
            PrimaryButtonAppearance = ControlAppearance.Danger,
            CloseButtonText = L.S("cancel"),
            Owner = this,
        };
        if (await box.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary) return;

        _uninstalling = true;
        StopEngine();               // também tira os alto-falantes do mudo
        try { Tray.Unregister(); } catch { }
        Uninstaller.Run();          // limpa registro e agenda a remoção dos arquivos
        Application.Current.Shutdown();
    }

    // ---------------- bandeja ----------------
    void HideToTray()
    {
        Hide();
    }

    internal void ShowFromTray()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    void MiToggle_Click(object sender, RoutedEventArgs e) => Toggle();
    void MiOpen_Click(object sender, RoutedEventArgs e) => ShowFromTray();
    void MiExit_Click(object sender, RoutedEventArgs e) => Close();

    void UpdateTray()
    {
        bool on = _engine != null;
        Tray.TooltipText = L.S(on ? "tipOn" : "tipOff");
        Tray.Icon = MakeTrayIcon(on);
    }

    // Ícone de fone desenhado com a fonte de ícones do próprio Windows,
    // branco na barra de tarefas escura e preto na clara; ponto de destaque quando está tocando.
    static BitmapSource MakeTrayIcon(bool playing)
    {
        bool lightTaskbar = false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            lightTaskbar = key?.GetValue("SystemUsesLightTheme") is int v && v == 1;
        }
        catch { }

        const int size = 32;
        var fg = lightTaskbar ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Colors.White;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var glyph = new FormattedText("", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                26, new SolidColorBrush(fg), 1.0);
            dc.DrawText(glyph, new Point((size - glyph.Width) / 2, (size - glyph.Height) / 2));
            if (playing)
            {
                var accent = ApplicationAccentColorManager.SystemAccent;
                dc.DrawEllipse(new SolidColorBrush(accent), new Pen(new SolidColorBrush(lightTaskbar ? Colors.White : Color.FromRgb(0x20, 0x20, 0x20)), 2),
                    new Point(size - 7, size - 7), 6, 6);
            }
        }
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        return bmp;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_uninstalling) SaveCfg();
        try { Tray.Unregister(); } catch { }
        _engine?.Dispose();
        _engine = null;
        RestoreSpeakers();
        base.OnClosing(e);
    }
}

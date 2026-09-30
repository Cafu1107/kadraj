using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Kadraj.Camera;
using Kadraj.Effects;
using Kadraj.Server;
using Kadraj.Video;
using QRCoder;

namespace Kadraj;

public partial class MainWindow : Window
{
    readonly PhoneServer _server = new();
    readonly FramePump _pump;
    readonly Compositor _compositor;
    readonly Segmenter _segmenter = new();
    readonly VirtualCamera _camera = new();
    readonly SharedFrameWriter _cameraOutput = new();
    readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _cameraTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    WriteableBitmap? _bitmap;
    PhoneInfo? _phone;
    string _pairingKey = "";
    bool _cameraRunning;
    EffectSettings _effects;
    bool _loadingEffects;
    LanAddress? _shownAddress;
    readonly DispatcherTimer _confirmReset = new() { Interval = TimeSpan.FromSeconds(4) };

    public MainWindow()
    {
        InitializeComponent();
        ApplyText();

        _effects = EffectSettings.Load();
        _compositor = new Compositor(_effects, _segmenter);
        _segmenter.StatusChanged += () => Dispatcher.BeginInvoke(UpdateBackgroundStatus);
        BuildEffectsPanel();
        if (_effects.Background != "none") _ = _segmenter.EnsureStartedAsync();

        _pump = new FramePump(Dispatcher, _compositor, _cameraOutput, ShowFrame);
        _server.FrameReceived += _pump.Submit;
        _server.PhoneChanged += phone => Dispatcher.BeginInvoke(() => ShowPhone(phone));
        _statsTimer.Tick += (_, _) => UpdateStats();
        _confirmReset.Tick += (_, _) =>
        {
            _confirmReset.Stop();
            ResetPairingButton.Content = L.T("Reset pairing", "Eşleşmeyi sıfırla");
        };
        _cameraTimer.Tick += (_, _) => UpdateCameraUsage();
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            _effects.Save();
        };

        Loaded += async (_, _) =>
        {
            SetUpCamera();
            await StartServerAsync();
        };
        Closed += (_, _) =>
        {
            _effects.Save();
            _pump.Dispose();
            _segmenter.Dispose();
            _camera.Dispose();
            _cameraOutput.Dispose();
            _ = _server.StopAsync();
        };
    }

    // Effects ---------------------------------------------------------------

    void BuildEffectsPanel()
    {
        _loadingEffects = true;

        foreach (var (id, en, tr) in BackgroundReplacer.Modes)
            BackgroundChips.Children.Add(Chip("background", id, L.T(en, tr), _effects.Background == id,
                () => SetBackground(_effects with { Background = id })));

        var picture = Chip("background", "image", L.T("Load picture…", "Resim yükle…"), _effects.Background == "image", null);
        picture.Click += (_, _) => PickBackgroundImage();
        BackgroundChips.Children.Add(picture);
        BlurSlider.Value = _effects.BlurStrength;
        BlurRow.Visibility = _effects.Background == "blur" ? Visibility.Visible : Visibility.Collapsed;

        foreach (var (id, en, tr) in ColorFilter.Presets)
            FilterChips.Children.Add(Chip("filter", id, L.T(en, tr), _effects.Filter == id,
                () => ApplyEffects(_effects with { Filter = id })));

        foreach (var (id, en, tr) in OverlayRenderer.Frames)
            FrameChips.Children.Add(Chip("frame", id, L.T(en, tr), _effects.Frame == id,
                () => ApplyEffects(_effects with { Frame = id })));

        var custom = Chip("frame", "custom", L.T("Load PNG…", "PNG yükle…"), _effects.Frame == "custom", null);
        custom.ToolTip = L.T("A 16:9 PNG with a transparent middle, e.g. 1920 × 1080",
                             "Ortası saydam, 16:9 bir PNG, ör. 1920 × 1080");
        custom.Click += (_, _) => PickCustomFrame();
        FrameChips.Children.Add(custom);

        foreach (var hex in OverlayRenderer.Accents)
        {
            var swatch = new RadioButton
            {
                GroupName = "accent",
                Style = (Style)FindResource("Swatch"),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                IsChecked = string.Equals(_effects.Accent, hex, StringComparison.OrdinalIgnoreCase),
                ToolTip = L.T("Frame and name tag colour", "Çerçeve ve etiket rengi"),
            };
            swatch.Checked += (_, _) =>
            {
                if (!_loadingEffects) ApplyEffects(_effects with { Accent = hex });
            };
            AccentSwatches.Children.Add(swatch);
        }

        BrightnessSlider.Value = _effects.Brightness;
        ContrastSlider.Value = _effects.Contrast;
        SaturationSlider.Value = _effects.Saturation;
        NameTagSwitch.IsChecked = _effects.ShowNameTag;
        NameBox.Text = _effects.Name;
        TitleBox.Text = _effects.Title;
        MirrorSwitch.IsChecked = _effects.Mirror;
        FillSwitch.IsChecked = _effects.Fill;

        _loadingEffects = false;
    }

    RadioButton Chip(string group, string id, string text, bool isChecked, Action? onChecked)
    {
        var chip = new RadioButton
        {
            GroupName = group,
            Content = text,
            Tag = id,
            IsChecked = isChecked,
            Style = (Style)FindResource("Chip"),
        };
        if (onChecked is not null)
            chip.Checked += (_, _) =>
            {
                if (!_loadingEffects) onChecked();
            };
        return chip;
    }

    /// <summary>Hands new settings to the pipeline (next frame uses them) and saves shortly after.</summary>
    void ApplyEffects(EffectSettings next)
    {
        _effects = next;
        _compositor.Settings = next;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    void PickCustomFrame()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = L.T("Choose a frame", "Çerçeve seç"),
            Filter = L.T("PNG images", "PNG görselleri") + " (*.png)|*.png",
        };
        if (dialog.ShowDialog(this) == true)
        {
            try
            {
                // Keep our own copy so moving or deleting the original doesn't break the frame.
                File.Copy(dialog.FileName, EffectSettings.CustomFrameCopy, overwrite: true);
                ApplyEffects(_effects with { Frame = "custom", CustomFramePath = EffectSettings.CustomFrameCopy });
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        // Cancelled or failed: put the selection back on the frame actually in use.
        _loadingEffects = true;
        foreach (var chip in FrameChips.Children.OfType<RadioButton>())
            chip.IsChecked = (string)chip.Tag == _effects.Frame;
        _loadingEffects = false;
    }

    void SetBackground(EffectSettings next)
    {
        ApplyEffects(next);
        BlurRow.Visibility = next.Background == "blur" ? Visibility.Visible : Visibility.Collapsed;
        if (next.Background != "none") _ = _segmenter.EnsureStartedAsync();
        UpdateBackgroundStatus();
    }

    void PickBackgroundImage()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = L.T("Choose a background", "Arka plan seç"),
            Filter = L.T("Pictures", "Resimler") + " (*.jpg;*.jpeg;*.png)|*.jpg;*.jpeg;*.png",
        };
        if (dialog.ShowDialog(this) == true)
        {
            try
            {
                var copy = EffectSettings.BackgroundCopy(Path.GetExtension(dialog.FileName));
                File.Copy(dialog.FileName, copy, overwrite: true);
                SetBackground(_effects with { Background = "image", BackgroundImagePath = copy });
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        _loadingEffects = true;
        foreach (var chip in BackgroundChips.Children.OfType<RadioButton>())
            chip.IsChecked = (string)chip.Tag == _effects.Background;
        _loadingEffects = false;
    }

    void Blur_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingEffects || _compositor is null) return;
        ApplyEffects(_effects with { BlurStrength = BlurSlider.Value });
    }

    void UpdateBackgroundStatus()
    {
        BackgroundStatus.ToolTip = null;
        if (_effects.Background == "none")
        {
            BackgroundStatus.Text = "";
            return;
        }

        switch (_segmenter.State)
        {
            case SegmenterState.Downloading:
                BackgroundStatus.Text = L.T($"Downloading model… {_segmenter.DownloadPercent}%", $"Model indiriliyor… %{_segmenter.DownloadPercent}");
                BackgroundStatus.ToolTip = L.T("One-time download, about 25 MB", "Bir kerelik indirme, yaklaşık 25 MB");
                break;
            case SegmenterState.Loading:
                BackgroundStatus.Text = L.T("Preparing…", "Hazırlanıyor…");
                break;
            case SegmenterState.Ready:
                BackgroundStatus.Text = $"{(_segmenter.OnGpu ? L.T("GPU", "Ekran kartı") : L.T("CPU", "İşlemci"))} · {_segmenter.InferenceMs:0} ms";
                break;
            case SegmenterState.Failed:
                BackgroundStatus.Text = L.T("Couldn't start", "Başlatılamadı");
                BackgroundStatus.ToolTip = _segmenter.Error;
                break;
            default:
                BackgroundStatus.Text = "";
                break;
        }
    }

    void Adjustment_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingEffects || _compositor is null) return;
        ApplyEffects(_effects with
        {
            Brightness = BrightnessSlider.Value,
            Contrast = ContrastSlider.Value,
            Saturation = SaturationSlider.Value,
        });
    }

    void ResetAdjustments_Click(object sender, RoutedEventArgs e)
    {
        _loadingEffects = true;
        BrightnessSlider.Value = ContrastSlider.Value = SaturationSlider.Value = 0;
        _loadingEffects = false;
        ApplyEffects(_effects with { Brightness = 0, Contrast = 0, Saturation = 0 });
    }

    void NameTag_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingEffects || _compositor is null) return;
        ApplyEffects(_effects with
        {
            ShowNameTag = NameTagSwitch.IsChecked == true,
            Name = NameBox.Text,
            Title = TitleBox.Text,
        });
    }

    void Image_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingEffects || _compositor is null) return;
        ApplyEffects(_effects with { Mirror = MirrorSwitch.IsChecked == true, Fill = FillSwitch.IsChecked == true });
    }

    void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (ConnectPanel is null || EffectsPanel is null) return; // fires during InitializeComponent
        bool connect = ConnectTab.IsChecked == true;
        ConnectPanel.Visibility = connect ? Visibility.Visible : Visibility.Collapsed;
        EffectsPanel.Visibility = connect ? Visibility.Collapsed : Visibility.Visible;
    }

    void ApplyText()
    {
        Tagline.Text = L.T("Your phone as a webcam", "Telefonun, bilgisayarının kamerası");
        WaitingTitle.Text = L.T("Waiting for a phone", "Telefon bekleniyor");
        WaitingHint.Text = L.T(
            "Put the phone on the same Wi-Fi as this PC, scan the QR code on the right and tap Start.\n" +
            "Security warning in the browser? Advanced → Continue. The address is this PC; it asks only once.",
            "Telefonu bu bilgisayarla aynı Wi-Fi'a bağla, sağdaki QR kodu okut ve Başlat'a dokun.\n" +
            "Tarayıcı güvenlik uyarısı verirse Gelişmiş → Devam et. Adres bu bilgisayar; bir kez sorar.");
        LiveText.Text = L.T("LIVE", "CANLI");
        ConnectTitle.Text = L.T("Connect a phone", "Telefon bağla");
        OtherAddressesLabel.Text = L.T("Not connecting? Try another address:", "Bağlanmıyor mu? Başka adres dene:");
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version is null ? "" : $"v{version.Major}.{version.Minor}.{version.Build}";
        ResetPairingButton.Content = L.T("Reset pairing", "Eşleşmeyi sıfırla");
        ResetPairingButton.ToolTip = L.T("Makes a new QR code. Phones paired before must scan it again.",
                                         "Yeni QR kod oluşturur. Daha önce bağlanan telefonlar yeniden okutmalı.");
        DeviceLabel.Text = L.T("Device", "Cihaz");
        ResolutionLabel.Text = L.T("Resolution", "Çözünürlük");
        FpsLabel.Text = "FPS";
        CameraTitle.Text = L.T("Virtual camera", "Sanal kamera");

        BackgroundTitle.Text = L.T("Background", "Arka plan");
        BlurLabel.Text = L.T("Strength", "Şiddet");
        ConnectTab.Content = L.T("Connect", "Bağlan");
        EffectsTab.Content = L.T("Effects", "Efektler");
        FilterTitle.Text = L.T("Filter", "Filtre");
        AdjustTitle.Text = L.T("Adjust", "Ayarlar");
        ResetAdjustments.Content = L.T("Reset", "Sıfırla");
        BrightnessLabel.Text = L.T("Brightness", "Parlaklık");
        ContrastLabel.Text = L.T("Contrast", "Kontrast");
        SaturationLabel.Text = L.T("Saturation", "Doygunluk");
        FrameTitle.Text = L.T("Frame", "Çerçeve");
        NameTagTitle.Text = L.T("Name tag", "İsim etiketi");
        NameTagSwitch.Content = L.T("Show", "Göster");
        NameBox.Tag = L.T("Your name", "Ad Soyad");
        TitleBox.Tag = L.T("Title (optional)", "Unvan (isteğe bağlı)");
        ImageTitle.Text = L.T("Image", "Görüntü");
        MirrorSwitch.Content = L.T("Mirror", "Ayna görüntü");
        FillSwitch.Content = L.T("Fill the frame (crops edges)", "Kadrajı doldur (kenarları kırpar)");
    }

    void SetUpCamera()
    {
        CameraButton.Visibility = Visibility.Collapsed;
        CameraDot.Fill = (Brush)FindResource("Muted");

        switch (VirtualCamera.CheckSetup())
        {
            case CameraSetup.Installed:
                int hr = _camera.Start();
                if (hr < 0)
                {
                    CameraStatus.Text = L.T($"Could not start the camera (0x{hr:X8}).", $"Kamera başlatılamadı (0x{hr:X8}).");
                    return;
                }
                _cameraRunning = true;
                _cameraTimer.Start();
                UpdateCameraUsage();
                break;

            case CameraSetup.NotInstalled:
                CameraStatus.Text = L.T("One-time setup so Zoom, Teams and Discord can see Kadraj. Windows will ask for permission.",
                                        "Zoom, Teams ve Discord'un Kadraj'ı görmesi için bir kerelik kurulum. Windows izin isteyecek.");
                CameraButton.Content = L.T("Install camera", "Kamerayı kur");
                CameraButton.Visibility = Visibility.Visible;
                break;

            case CameraSetup.NeedsUpdate:
                CameraStatus.Text = L.T("A newer camera component is available.", "Kamera bileşeninin yeni sürümü var.");
                CameraButton.Content = L.T("Update camera", "Kamerayı güncelle");
                CameraButton.Visibility = Visibility.Visible;
                break;

            case CameraSetup.Unsupported:
                CameraStatus.Text = L.T("Needs Windows 11. The preview still works.", "Windows 11 gerekiyor. Önizleme yine çalışır.");
                break;

            case CameraSetup.Missing:
                CameraStatus.Text = L.T("Camera component not found. Build it with build.ps1.",
                                        "Kamera bileşeni bulunamadı. build.ps1 ile derle.");
                break;
        }
    }

    async void CameraButton_Click(object sender, RoutedEventArgs e)
    {
        CameraButton.IsEnabled = false;
        CameraStatus.Text = L.T("Installing…", "Kuruluyor…");

        _cameraTimer.Stop();
        _camera.Dispose();
        _cameraRunning = false;

        bool ok = await VirtualCamera.InstallElevatedAsync();
        CameraButton.IsEnabled = true;
        SetUpCamera();
        if (!ok && !_cameraRunning)
            CameraStatus.Text = L.T("Setup was cancelled or failed. Try again?", "Kurulum iptal edildi ya da başarısız oldu. Tekrar dene?");
    }

    void UpdateCameraUsage()
    {
        if (!_cameraRunning) return;
        if (_cameraOutput.RequestedSize is var (width, height))
        {
            CameraDot.Fill = (Brush)FindResource("Live");
            CameraStatus.Text = L.T($"In use · {width} × {height}", $"Kullanımda · {width} × {height}");
        }
        else
        {
            CameraDot.Fill = (Brush)FindResource("Muted");
            CameraStatus.Text = L.T("Ready. Pick “Kadraj” as the camera in Zoom, Teams or Discord.",
                                    "Hazır. Zoom, Teams veya Discord'da kamera olarak “Kadraj”ı seç.");
        }
    }

    async Task StartServerAsync()
    {
        var addresses = NetworkInfo.GetLanAddresses();
        if (addresses.Count == 0)
        {
            ShowProblem(L.T("No network connection", "Ağ bağlantısı yok"),
                        L.T("Connect this PC to Wi-Fi or Ethernet, then restart Kadraj.",
                            "Bilgisayarı Wi-Fi'a veya kabloya bağla, sonra Kadraj'ı yeniden aç."));
            return;
        }

        try
        {
            var (certificate, key) = await Task.Run(() => (
                Credentials.LoadOrCreateCertificate(addresses.Select(a => a.Address)),
                Credentials.LoadOrCreatePairingKey()));
            _pairingKey = key;
            await _server.StartAsync(certificate, key);
        }
        catch (IOException)
        {
            ShowProblem(L.T("Port already in use", "Port kullanımda"),
                        L.T($"Port {PhoneServer.HttpsPort} is taken. Is Kadraj already open?",
                            $"{PhoneServer.HttpsPort} portu dolu. Kadraj zaten açık olabilir mi?"));
            return;
        }

        ShowAddress(addresses[0]);

        if (addresses.Count > 1)
        {
            foreach (var address in addresses)
            {
                var button = new Button
                {
                    Content = address.Address.ToString(),
                    ToolTip = address.InterfaceName,
                    Style = (Style)FindResource("ChipButton"),
                };
                button.Click += (_, _) => ShowAddress(address);
                OtherAddressesList.Children.Add(button);
            }
            OtherAddressesPanel.Visibility = Visibility.Visible;
        }
    }

    void ShowAddress(LanAddress address)
    {
        _shownAddress = address;
        var baseUrl = $"https://{address.Address}:{PhoneServer.HttpsPort}";
        QrImage.Source = MakeQrCode($"{baseUrl}/?k={_pairingKey}");
        AddressText.Text = baseUrl;
    }

    /// <summary>
    /// New pairing key: phones that scanned the old QR code (or kept it in their browser history)
    /// can no longer connect. Asks for a second click first, since it drops a connected phone.
    /// </summary>
    void ResetPairing_Click(object sender, RoutedEventArgs e)
    {
        if (!_confirmReset.IsEnabled)
        {
            ResetPairingButton.Content = L.T("Sure? Click again to reset", "Emin misin? Sıfırlamak için tekrar tıkla");
            _confirmReset.Start();
            return;
        }

        _confirmReset.Stop();
        _pairingKey = Credentials.NewPairingKey();
        _server.SetPairingKey(_pairingKey);
        if (_shownAddress is not null) ShowAddress(_shownAddress);
        ResetPairingButton.Content = L.T("New QR code ready", "Yeni QR kod hazır");
    }

    // Dark title bar to match the window (Windows 10 20H1+ / 11)
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        int on = 1;
        DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int));
        int caption = 0x00121110; // COLORREF (BGR) of #101112, close to the window background
        DwmSetWindowAttribute(hwnd, 35 /* DWMWA_CAPTION_COLOR, Windows 11 */, ref caption, sizeof(int));
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    void ShowProblem(string title, string hint)
    {
        WaitingTitle.Text = title;
        WaitingHint.Text = hint;
        WaitingPanel.Visibility = Visibility.Visible;
    }

    void ShowPhone(PhoneInfo? phone)
    {
        _phone = phone;
        _segmenter.ResetMask();
        if (phone is null)
        {
            _statsTimer.Stop();
            _bitmap = null;
            PreviewImage.Source = null;
            LiveBadge.Visibility = Visibility.Collapsed;
            DeviceValue.Text = ResolutionValue.Text = FpsValue.Text = "—";
            ShowProblem(L.T("Waiting for a phone", "Telefon bekleniyor"),
                        L.T("The phone disconnected. Tap Start on it again, or scan the QR code.",
                            "Telefonun bağlantısı koptu. Telefonda tekrar Başlat'a dokun ya da QR kodu okut."));
            return;
        }

        DeviceValue.Text = $"{phone.Name} · {phone.Address}";
        ShowProblem(L.T($"{phone.Name} connected", $"{phone.Name} bağlandı"),
                    L.T("Waiting for video…", "Görüntü bekleniyor…"));
        _statsTimer.Start();
    }

    void ShowFrame(VideoFrame frame)
    {
        if (_phone is null) return; // late frame after a disconnect

        if (_bitmap is null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
        {
            _bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);
            PreviewImage.Source = _bitmap;
            ResolutionValue.Text = $"{frame.Width} × {frame.Height}";
        }

        _bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Pixels, frame.Stride, 0);
        WaitingPanel.Visibility = Visibility.Collapsed;
        LiveBadge.Visibility = Visibility.Visible;
    }

    void UpdateStats()
    {
        var (_, shown) = _pump.TakeCounts();
        if (_segmenter.State == SegmenterState.Ready) UpdateBackgroundStatus(); // refresh the ms reading
        FpsValue.Text = shown.ToString();
    }

    static BitmapImage MakeQrCode(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(8);

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(png);
        image.EndInit();
        image.Freeze();
        return image;
    }
}

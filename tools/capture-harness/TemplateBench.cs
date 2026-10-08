// Benchmarks and compares the real notification card: the user's own settings (config.json, with its
// background, glow, sizes and shadows), the bundled template or a template file, built and animated by
// the plugin's own code driven through reflection, in a DWM-composited window as the plugin shows it.
//
//   TemplateBench.exe [--template <AchievementToast.xaml>] [--cycles 3] [--hold 4] [--load N]
//                     [--compare <other AchievementToast.xaml>] [--kind rare] [--config <config.json>]
//
// Bench mode runs complete notifications - slide in, hold with the countdown, glow and background all
// running, slide out - and reads every frame DWM presents for the card's screen rectangle through
// desktop duplication. Per phase it reports how many presented frames changed the card and the longest
// gap between changes: frames that reached the screen, not frames WPF prepared.
//
// Compare mode shows the template (A) and the --compare template (B) at once, so both cards share one
// background player and show the same frame, pauses every animation, and compares the two on screen.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using D3D11 = SharpDX.Direct3D11;
using DXGI = SharpDX.DXGI;

public static class TemplateBench
{
    private const BindingFlags Flags =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static string _repo;
    private static string _pluginDir;
    private static string _templatePath;
    private static string _comparePath;
    private static string _configPath;
    private static string _kind = "rare";
    private static int _cycles = 3;
    private static double _holdSeconds = 4;
    private static int _load;
    private static bool _freezeMotion;
    private static string _snapshot;
    private static Assembly _plugin;
    private static object _logger;

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect32 rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left, Top, Right, Bottom;
    }

    [STAThread]
    public static int Main(string[] args)
    {
        if (Array.IndexOf(args, "--loadwindow") >= 0)
        {
            return 0;
        }

        var here = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        _repo = Path.GetFullPath(Path.Combine(here, "..", "..", ".."));
        _pluginDir = Path.Combine(_repo, "source", "bin", "Debug");
        _configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Playnite", "ExtensionsData", "e6aad2c9-6e06-4d8d-ac55-ac3b252b5f7b", "config.json");
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--template": _templatePath = args[++i]; break;
                case "--compare": _comparePath = args[++i]; break;
                case "--config": _configPath = args[++i]; break;
                case "--kind": _kind = args[++i]; break;
                case "--cycles": _cycles = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--hold": _holdSeconds = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--load": _load = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--plugin": _pluginDir = args[++i]; break;
                case "--snapshot": _snapshot = args[++i]; break;
                // Leave the quiet scope's stand-downs out, so the background and glow keep animating
                // through the slides.
                case "--animate-motion": _freezeMotion = false; break;
            }
        }

        _freezeMotion = Array.IndexOf(args, "--animate-motion") < 0;
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        var code = 0;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var loads = new List<Process>();
        app.Startup += async (s, e) =>
        {
            try
            {
                if (_load > 0)
                {
                    var exe = Path.Combine(here, "SlideCadenceProbe.exe");
                    for (var i = 0; i < _load; i++)
                    {
                        loads.Add(Process.Start(new ProcessStartInfo(exe, "--loadwindow") { UseShellExecute = false }));
                    }

                    await Task.Delay(2000);
                }

                await RunAsync(app);
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAILED: " + (ex.InnerException ?? ex));
                code = 1;
            }
            finally
            {
                foreach (var p in loads)
                {
                    try { p.Kill(); } catch { }
                }

                app.Shutdown();
            }
        };
        app.Run();
        return code;
    }

    private static Assembly Resolve(object sender, ResolveEventArgs e)
    {
        var name = new AssemblyName(e.Name).Name + ".dll";
        var direct = Path.Combine(_pluginDir, name);
        if (File.Exists(direct))
        {
            return Assembly.LoadFrom(direct);
        }

        var packages = Path.Combine(_repo, "source", "packages");
        var hit = Directory.GetFiles(packages, name, SearchOption.AllDirectories)
            .FirstOrDefault(p => p.IndexOf("\\net4", StringComparison.OrdinalIgnoreCase) >= 0);
        return hit != null ? Assembly.LoadFrom(hit) : null;
    }

    private static async Task RunAsync(Application app)
    {
        _plugin = Assembly.LoadFrom(Path.Combine(_pluginDir, "PlayniteAchievements.dll"));
        var sdk = _plugin.GetReferencedAssemblies().Select(Assembly.Load).First(a => a.GetName().Name == "Playnite.SDK");
        _logger = ConsoleLogger.Create(sdk.GetType("Playnite.SDK.ILogger"));
        InstallLocalization(sdk, app);

        var persisted = LoadPersisted();
        InstallPluginInstance(persisted);
        if (_comparePath != null)
        {
            // Animations off, so both cards hold one state; the background is paused below.
            persisted.GetType().GetProperty("AnimateRarityGlows").SetValue(persisted, false);
            await CompareAsync(persisted);
            return;
        }

        var card = await BuildCardAsync(persisted, _templatePath, left: true);
        Console.WriteLine("card window {0}x{1} px, background animating, glow pulse {2}",
            card.Rect.Right - card.Rect.Left, card.Rect.Bottom - card.Rect.Top,
            persisted.GetType().GetProperty("AnimateRarityGlows").GetValue(persisted));
        if (_snapshot != null)
        {
            SaveCapture(card.Rect, _snapshot);
            Console.WriteLine("snapshot saved to " + _snapshot);
        }

        await BenchAsync(card);
        card.Window.Close();
    }

    private sealed class Card
    {
        public Window Window;
        public Grid Host;
        public ItemsControl Surface;
        public TranslateTransform Slide;
        public object Service;
        public Rect32 Rect;
        public double TravelDip;
    }

    private static object _settings;

    private static object LoadPersisted()
    {
        var settingsType = _plugin.GetType("PlayniteAchievements.Models.PlayniteAchievementsSettings");
        var json = Assembly.Load("Newtonsoft.Json").GetType("Newtonsoft.Json.JsonConvert");
        var deserialize = json.GetMethods().First(m => m.Name == "DeserializeObject" && m.GetParameters().Length == 2
            && m.GetParameters()[1].ParameterType == typeof(Type));
        _settings = deserialize.Invoke(null, new object[] { File.ReadAllText(_configPath), settingsType });
        return settingsType.GetProperty("Persisted").GetValue(_settings);
    }

    /// <summary>
    /// A plugin instance carrying only what the card's image loading reaches for: the disk and
    /// memory image services over the user's plugin data folder, the ray track service, and the
    /// settings. Constructed as the plugin's own startup does, without Playnite.
    /// </summary>
    private static void InstallPluginInstance(object persisted)
    {
        var pluginType = _plugin.GetType("PlayniteAchievements.PlayniteAchievementsPlugin");
        var instance = FormatterServices.GetUninitializedObject(pluginType);
        var dataDir = Path.GetDirectoryName(_configPath);
        var disk = Activator.CreateInstance(_plugin.GetType("PlayniteAchievements.Services.Images.DiskImageService"), _logger, dataDir, 8);
        var memory = Activator.CreateInstance(_plugin.GetType("PlayniteAchievements.Services.Images.MemoryImageService"), _logger, disk, 512);
        var rays = Activator.CreateInstance(_plugin.GetType("PlayniteAchievements.Services.Images.RayTrackService"), _logger, memory, 1024);
        Set(instance, "_diskImageService", disk);
        Set(instance, "_imageService", memory);
        Set(instance, "_rayTrackService", rays);

        var settingsVmType = pluginType.GetField("_settingsViewModel", Flags).FieldType;
        var settingsVm = FormatterServices.GetUninitializedObject(settingsVmType);
        var settingsField = settingsVmType.GetFields(Flags).First(f => f.FieldType == _settings.GetType());
        settingsField.SetValue(settingsVm, _settings);
        Set(instance, "_settingsViewModel", settingsVm);
        pluginType.GetField("<Instance>k__BackingField", Flags).SetValue(null, instance);
    }

    private static DataTemplate LoadTemplate(string path)
    {
        var resolverType = _plugin.GetType("PlayniteAchievements.Services.UI.AchievementToastTemplateResolver");
        var resolver = Activator.CreateInstance(resolverType, new object[] { null, _logger, null, null, null });
        if (path == null)
        {
            return (DataTemplate)resolverType.GetMethod("ResolveBundledDefaultTemplate").Invoke(resolver, new object[] { false });
        }

        var call = new object[] { File.ReadAllText(path), false, null, null };
        var ok = (bool)resolverType.GetMethod("TryLoadTemplateFromXaml").Invoke(resolver, call);
        if (!ok || call[2] == null)
        {
            throw new InvalidOperationException("template did not load: " + call[3]);
        }

        return (DataTemplate)call[2];
    }

    private static async Task<Card> BuildCardAsync(object persisted, string templatePath, bool left)
    {
        var template = LoadTemplate(templatePath);
        var argsFactory = _plugin.GetType("PlayniteAchievements.Services.UI.ToastPreviewFactory");
        var unlock = argsFactory.GetMethod("BuildPreviewArgs", Flags).Invoke(null, new object[] { _kind, null, null });
        var vmType = _plugin.GetType("PlayniteAchievements.ViewModels.AchievementToastViewModel");
        var ctor = vmType.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        var values = ctor.GetParameters().Select(p =>
            p.ParameterType == unlock.GetType() ? unlock
            : p.ParameterType == persisted.GetType() ? persisted
            : p.ParameterType == typeof(bool) ? (object)false
            : null).ToArray();
        var vm = ctor.Invoke(values);
        await (Task)vmType.GetMethod("PrepareImagesAsync").Invoke(vm, null);

        var items = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(vmType));
        items.Add(vm);
        var factory = _plugin.GetType("PlayniteAchievements.Services.UI.ToastSurfaceFactory");
        var surface = (ItemsControl)factory.GetMethod("BuildToastSurface", Flags).Invoke(null, new object[] { items, template });
        var hostArgs = new object[] { surface, null };
        var host = (Grid)factory.GetMethod("BuildSlideHost", Flags).Invoke(null, hostArgs);
        var slide = (TranslateTransform)hostArgs[1];

        var provider = _plugin.GetType("PlayniteAchievements.Views.Helpers.PlayniteUiProvider");
        var window = (Window)provider.GetMethod("CreateBorderlessTopmostWindow").Invoke(null, new object[] { null, "TemplateBench" });
        var surfaceType = _plugin.GetType("PlayniteAchievements.Services.UI.ToastWindowSurface");
        surfaceType.GetMethod("UseDwmComposition").Invoke(null, new object[] { window });
        window.Content = host;
        window.Left = left ? 60 : 60;
        window.Top = left ? 60 : 760;

        var svcType = _plugin.GetType("PlayniteAchievements.Services.UI.ToastNotificationService");
        var svc = FormatterServices.GetUninitializedObject(svcType);
        Set(svc, "_logger", _logger);
        Set(svc, "_activeWindow", window);
        Set(svc, "_activeSlideHost", host);
        Set(svc, "_activeSlideTransform", slide);
        Set(svc, "_activeCardSurface", surface);
        Set(svc, "_activeDwmComposed", true);
        Set(svc, "_activeMonitorScale", 1.0);
        Set(svc, "_activeSlideInTravels", true);
        Set(svc, "_activeSlideOutTravels", true);
        var corner = _plugin.GetType("PlayniteAchievements.Models.Settings.ToastScreenCorner");
        Set(svc, "_activePosition", Enum.Parse(corner, "BottomRight"));

        // A still, solid backdrop under the card's transparent margins: whatever else is on screen
        // there (a blinking caret) would otherwise count as the card changing.
        var backdrop = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            Background = new SolidColorBrush(Color.FromRgb(0x14, 0x18, 0x20)),
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = window.Left - 20,
            Top = window.Top - 20,
            Width = 1600,
            Height = 700,
        };
        backdrop.Show();
        window.Closed += (s, e) => backdrop.Close();

        window.Show();
        factory.GetMethod("ApplyMeasuredCardGaps", Flags)?.Invoke(null, new object[] { surface });
        svcType.GetMethod("ReserveSlideTravel", Flags).Invoke(svc, new object[] { window, surface });
        window.UpdateLayout();

        // Let the background player load and start.
        await Task.Delay(2500);
        GetWindowRect(new WindowInteropHelper(window).Handle, out var rect);
        var travel = (double)svcType.GetMethod("SlideDistanceDip", Flags).Invoke(svc, new object[] { window });
        return new Card { Window = window, Host = host, Surface = surface, Slide = slide, Service = svc, Rect = rect, TravelDip = travel };
    }

    private static async Task BenchAsync(Card card)
    {
        var svcType = card.Service.GetType();
        var easeIn = svcType.GetField("DefaultSlideInEase", Flags).GetValue(null);
        var easeOut = svcType.GetField("DefaultSlideOutEase", Flags).GetValue(null);
        var run = svcType.GetMethod("RunSlideStoryboard", Flags);
        var stop = svcType.GetMethod("StopActiveSlide", Flags);
        var engage = svcType.GetMethod("EngageMotionCache", Flags);
        var countdown = svcType.GetMethod("AnimateCountdownBars", Flags);
        var stopCountdown = svcType.GetMethod("StopCountdownBars", Flags);
        const double inMs = 300, outMs = 250;

        var phases = new Dictionary<string, List<PhaseStats>>
        {
            ["entrance"] = new List<PhaseStats>(),
            ["hold"] = new List<PhaseStats>(),
            ["exit"] = new List<PhaseStats>(),
        };

        using (var recorder = new RegionRecorder(card.Rect))
        {
            recorder.Start();
            for (var c = 0; c < _cycles; c++)
            {
                // Entrance, as the wave runs it: cache realized a frame ahead, then the slide.
                card.Slide.Y = card.TravelDip;
                engage.Invoke(null, new object[] { card.Host, card.Window });
                await Frames(3);
                var t0 = recorder.Now;
                run.Invoke(card.Service, new object[] { null, card.TravelDip, 0d, easeIn, inMs, true, "in", false });
                if (!_freezeMotion)
                {
                    ResumeAnimations(card.Host);
                }

                await Task.Delay((int)inMs + 20);
                var t1 = recorder.Now;
                stop.Invoke(card.Service, null);

                countdown.Invoke(card.Service, new object[] { card.Window, (int)(_holdSeconds * 1000) });
                await Task.Delay(TimeSpan.FromSeconds(_holdSeconds));
                var t2 = recorder.Now;
                stopCountdown.Invoke(card.Service, new object[] { card.Window });

                engage.Invoke(null, new object[] { card.Host, card.Window });
                await Frames(1);
                var t3 = recorder.Now;
                run.Invoke(card.Service, new object[] { null, 0d, card.TravelDip, easeOut, outMs, true, "out", false });
                if (!_freezeMotion)
                {
                    ResumeAnimations(card.Host);
                }

                await Task.Delay((int)outMs + 20);
                var t4 = recorder.Now;
                stop.Invoke(card.Service, null);
                await Task.Delay(300);

                phases["entrance"].Add(recorder.Stats(t0, t1));
                phases["hold"].Add(recorder.Stats(t1, t2));
                phases["exit"].Add(recorder.Stats(t3, t4));
            }
        }

        var hz = 1000.0 / DisplayPeriodMs();
        Console.WriteLine("display {0:0.0} Hz; per phase, medians of {1} cycles:", hz, _cycles);
        Console.WriteLine("{0,-9} {1,10} {2,11} {3,12} {4,10}", "phase", "changed/s", "% of display", "longest gap", "presents/s");
        foreach (var phase in phases)
        {
            var mid = phase.Value.OrderBy(p => p.ChangedPerSecond).ElementAt(phase.Value.Count / 2);
            Console.WriteLine("{0,-9} {1,10:0.0} {2,10:0}% {3,10:0.0}ms {4,10:0.0}",
                phase.Key, mid.ChangedPerSecond, 100 * mid.ChangedPerSecond / hz, mid.LongestGapMs, mid.PresentsPerSecond);
        }

        Console.WriteLine("  changed/s: presented frames in which the card's pixels changed. The hold's ceiling is");
        Console.WriteLine("  set by what animates in it, not the display: a still card changes nothing.");
    }

    /// <summary>
    /// Undoes the quiet scope's stand-downs for --animate-motion: the background and glow keep
    /// animating while the card slides.
    /// </summary>
    private static void ResumeAnimations(Grid host)
    {
        _plugin.GetType("PlayniteAchievements.Views.Helpers.AsyncImage").GetMethod("ResumeGifAnimationsUnder", Flags)?.Invoke(null, new object[] { host });
        _plugin.GetType("PlayniteAchievements.Views.Helpers.RarityGlowPulse").GetMethod("ResumeUnder", Flags)?.Invoke(null, new object[] { host });
    }

    private static async Task CompareAsync(object persisted)
    {
        var a = await BuildCardAsync(persisted, _templatePath, left: true);
        var b = await BuildCardAsync(persisted, _comparePath, left: false);
        var asyncImage = _plugin.GetType("PlayniteAchievements.Views.Helpers.AsyncImage");
        var pause = asyncImage.GetMethod("PauseGifAnimationsUnder", Flags);
        pause.Invoke(null, new object[] { a.Host });
        pause.Invoke(null, new object[] { b.Host });
        await Frames(10);
        await Task.Delay(500);

        var pa = Capture(a.Rect);
        var pb = Capture(b.Rect);
        if (pa.Length != pb.Length)
        {
            Console.WriteLine("window sizes differ: A {0}x{1}, B {2}x{3}",
                a.Rect.Right - a.Rect.Left, a.Rect.Bottom - a.Rect.Top, b.Rect.Right - b.Rect.Left, b.Rect.Bottom - b.Rect.Top);
        }
        else
        {
            var differing = 0;
            var over2 = 0;
            var max = 0;
            for (var i = 0; i < pa.Length; i++)
            {
                if (pa[i] == pb[i])
                {
                    continue;
                }

                differing++;
                var d = 0;
                for (var s = 0; s < 24; s += 8)
                {
                    d = Math.Max(d, Math.Abs(((pa[i] >> s) & 255) - ((pb[i] >> s) & 255)));
                }

                max = Math.Max(max, d);
                if (d > 2)
                {
                    over2++;
                }
            }

            Console.WriteLine("B vs A on screen: {0} of {1} pixels differ, {2} by more than 2 levels, max {3} levels",
                differing, pa.Length, over2, max);
        }

        a.Window.Close();
        b.Window.Close();
    }

    private static void SaveCapture(Rect32 r, string path)
    {
        using (var bitmap = new System.Drawing.Bitmap(r.Right - r.Left, r.Bottom - r.Top))
        {
            using (var g = System.Drawing.Graphics.FromImage(bitmap))
            {
                g.CopyFromScreen(r.Left, r.Top, 0, 0, bitmap.Size);
            }

            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
    }

    private static int[] Capture(Rect32 r)
    {
        var w = r.Right - r.Left;
        var h = r.Bottom - r.Top;
        using (var bitmap = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
        {
            using (var g = System.Drawing.Graphics.FromImage(bitmap))
            {
                g.CopyFromScreen(r.Left, r.Top, 0, 0, new System.Drawing.Size(w, h));
            }

            var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, w, h),
                System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var px = new int[w * h];
            Marshal.Copy(data.Scan0, px, 0, px.Length);
            bitmap.UnlockBits(data);
            return px;
        }
    }

    private static void InstallLocalization(Assembly sdk, Application app)
    {
        var enUs = Path.Combine(_repo, "source", "Localization", "en_US.xaml");
        var dictionary = (ResourceDictionary)XamlReader.Load(File.OpenRead(enUs));
        app.Resources.MergedDictionaries.Add(dictionary);
        var strings = new Dictionary<string, string>();
        foreach (var key in dictionary.Keys)
        {
            if (key is string k && dictionary[k] is string v)
            {
                strings[k] = v;
            }
        }

        var providerType = sdk.GetType("Playnite.SDK.ResourceProvider");
        var set = providerType.GetMethod("SetGlobalProvider", BindingFlags.Static | BindingFlags.NonPublic);
        set?.Invoke(null, new[] { StringProvider.Create(sdk.GetType("Playnite.SDK.IResourceProvider"), strings) });
    }

    private static double DisplayPeriodMs()
    {
        var info = new DwmTimingInfo { cbSize = (uint)Marshal.SizeOf(typeof(DwmTimingInfo)) };
        return DwmGetCompositionTimingInfo(IntPtr.Zero, ref info) == 0 && info.rateRefreshNum > 0
            ? 1000.0 * info.rateRefreshDen / info.rateRefreshNum
            : 1000.0 / 60;
    }

    private static Task Frames(int n)
    {
        var tcs = new TaskCompletionSource<bool>();
        var count = 0;
        EventHandler h = null;
        h = (s, e) =>
        {
            if (++count >= n)
            {
                CompositionTarget.Rendering -= h;
                tcs.TrySetResult(true);
            }
        };
        CompositionTarget.Rendering += h;
        return tcs.Task;
    }

    private static void Set(object target, string name, object value)
    {
        var field = target.GetType().GetField(name, Flags) ?? throw new MissingFieldException(target.GetType().Name, name);
        field.SetValue(target, value);
    }

    private struct PhaseStats
    {
        public double ChangedPerSecond;
        public double PresentsPerSecond;
        public double LongestGapMs;
    }

    /// <summary>
    /// Desktop duplication of the card's screen rectangle. For every presented frame it records the
    /// present time and whether the rectangle's pixels changed (sampled on a grid).
    /// </summary>
    private sealed class RegionRecorder : IDisposable
    {
        private readonly Rect32 _rect;
        private readonly List<KeyValuePair<long, bool>> _frames = new List<KeyValuePair<long, bool>>();
        private DXGI.Factory1 _factory;
        private DXGI.Adapter1 _adapter;
        private D3D11.Device _device;
        private DXGI.OutputDuplication _duplication;
        private D3D11.Texture2D _staging;
        private SharpDX.Mathematics.Interop.RawRectangle _bounds;
        private Thread _thread;
        private volatile bool _stop;

        public RegionRecorder(Rect32 rect)
        {
            _rect = rect;
            _factory = new DXGI.Factory1();
            _adapter = _factory.GetAdapter1(0);
            for (var o = 0; o < _adapter.GetOutputCount(); o++)
            {
                using (var output = _adapter.GetOutput(o))
                {
                    var b = output.Description.DesktopBounds;
                    if (rect.Left < b.Left || rect.Left >= b.Right || rect.Top < b.Top || rect.Top >= b.Bottom)
                    {
                        continue;
                    }

                    _bounds = b;
                    _device = new D3D11.Device(_adapter);
                    using (var output1 = output.QueryInterface<DXGI.Output1>())
                    {
                        _duplication = output1.DuplicateOutput(_device);
                    }

                    var w = Math.Min(rect.Right, b.Right) - rect.Left;
                    var h = Math.Min(rect.Bottom, b.Bottom) - rect.Top;
                    _staging = new D3D11.Texture2D(_device, new D3D11.Texture2DDescription
                    {
                        Width = w,
                        Height = h,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = DXGI.Format.B8G8R8A8_UNorm,
                        SampleDescription = new DXGI.SampleDescription(1, 0),
                        Usage = D3D11.ResourceUsage.Staging,
                        CpuAccessFlags = D3D11.CpuAccessFlags.Read,
                    });
                    return;
                }
            }

            throw new InvalidOperationException("no output contains the card");
        }

        public long Now => Stopwatch.GetTimestamp();

        public void Start()
        {
            _thread = new Thread(Pump) { IsBackground = true, Priority = ThreadPriority.Highest };
            _thread.Start();
        }

        private void Pump()
        {
            var context = _device.ImmediateContext;
            var w = _staging.Description.Width;
            var h = _staging.Description.Height;
            var region = new D3D11.ResourceRegion(_rect.Left - _bounds.Left, _rect.Top - _bounds.Top, 0,
                _rect.Left - _bounds.Left + w, _rect.Top - _bounds.Top + h, 1);
            long previous = 0;
            while (!_stop)
            {
                var hr = _duplication.TryAcquireNextFrame(50, out var info, out var resource);
                if (hr.Failure)
                {
                    continue;
                }

                try
                {
                    if (info.LastPresentTime == 0)
                    {
                        continue;
                    }

                    using (var texture = resource.QueryInterface<D3D11.Texture2D>())
                    {
                        context.CopySubresourceRegion(texture, 0, region, _staging, 0);
                    }

                    var box = context.MapSubresource(_staging, 0, D3D11.MapMode.Read, D3D11.MapFlags.None);
                    long hash = 17;
                    try
                    {
                        for (var y = 0; y < h; y += 3)
                        {
                            var row = box.DataPointer + (y * box.RowPitch);
                            for (var x = 0; x < w; x += 3)
                            {
                                hash = (hash * 31) ^ SharpDX.Utilities.Read<int>(row + (x * 4));
                            }
                        }
                    }
                    finally
                    {
                        context.UnmapSubresource(_staging, 0);
                    }

                    lock (_frames)
                    {
                        _frames.Add(new KeyValuePair<long, bool>(info.LastPresentTime, hash != previous));
                    }

                    previous = hash;
                }
                finally
                {
                    resource?.Dispose();
                    _duplication.ReleaseFrame();
                }
            }
        }

        public PhaseStats Stats(long from, long to)
        {
            List<KeyValuePair<long, bool>> frames;
            lock (_frames)
            {
                frames = _frames.Where(f => f.Key >= from && f.Key <= to).ToList();
            }

            var seconds = (to - from) / (double)Stopwatch.Frequency;
            var changes = frames.Where(f => f.Value).Select(f => f.Key).ToList();
            var gap = 0d;
            var last = from;
            foreach (var t in changes)
            {
                gap = Math.Max(gap, (t - last) * 1000.0 / Stopwatch.Frequency);
                last = t;
            }

            return new PhaseStats
            {
                ChangedPerSecond = changes.Count / seconds,
                PresentsPerSecond = frames.Count / seconds,
                LongestGapMs = gap,
            };
        }

        public void Dispose()
        {
            _stop = true;
            _thread?.Join(2000);
            _staging?.Dispose();
            _duplication?.Dispose();
            _device?.Dispose();
            _adapter?.Dispose();
            _factory?.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct DwmTimingInfo
    {
        public uint cbSize;
        public uint rateRefreshNum, rateRefreshDen;
        public ulong qpcRefreshPeriod;
        public uint rateComposeNum, rateComposeDen;
        public ulong qpcVBlank, cRefresh;
        public uint cDXRefresh;
        public ulong qpcCompose, cFrame;
        public uint cDXPresent;
        public ulong cRefreshFrame, cFrameSubmitted;
        public uint cDXPresentSubmitted;
        public ulong cFrameConfirmed;
        public uint cDXPresentConfirmed;
        public ulong cRefreshConfirmed;
        public uint cDXRefreshConfirmed;
        public ulong cFramesLate;
        public uint cFramesOutstanding;
        public ulong cFrameDisplayed, qpcFrameDisplayed, cRefreshFrameDisplayed, cFrameComplete, qpcFrameComplete;
        public ulong cFramePending, qpcFramePending, cFramesDisplayed, cFramesComplete, cFramesPending;
        public ulong cFramesAvailable, cFramesDropped, cFramesMissed, cRefreshNextDisplayed, cRefreshNextPresented;
        public ulong cRefreshesDisplayed, cRefreshesPresented, cRefreshStarted, cPixelsReceived, cPixelsDrawn;
        public ulong cBuffersEmpty;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetCompositionTimingInfo(IntPtr hwnd, ref DwmTimingInfo info);

    /// <summary>An IResourceProvider over the English strings, built at runtime against the SDK.</summary>
    public static class StringProvider
    {
        private static Dictionary<string, string> _strings;

        public static object Create(Type providerInterface, Dictionary<string, string> strings)
        {
            _strings = strings;
            var assembly = AppDomain.CurrentDomain.DefineDynamicAssembly(
                new AssemblyName("BenchStrings"), System.Reflection.Emit.AssemblyBuilderAccess.Run);
            var type = assembly.DefineDynamicModule("main").DefineType(
                "BenchStringsImpl", TypeAttributes.Public | TypeAttributes.Class, typeof(object), new[] { providerInterface });
            foreach (var method in providerInterface.GetMethods())
            {
                var impl = type.DefineMethod(method.Name,
                    MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
                    method.ReturnType, method.GetParameters().Select(p => p.ParameterType).ToArray());
                var il = impl.GetILGenerator();
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
                il.Emit(System.Reflection.Emit.OpCodes.Call, typeof(StringProvider).GetMethod(
                    method.ReturnType == typeof(string) ? "GetString" : "GetResource"));
                il.Emit(System.Reflection.Emit.OpCodes.Ret);
                type.DefineMethodOverride(impl, method);
            }

            return Activator.CreateInstance(type.CreateType());
        }

        public static string GetString(string key) => _strings.TryGetValue(key, out var v) ? v : "<!" + key + "!>";

        public static object GetResource(string key) => _strings.TryGetValue(key, out var v) ? v : null;
    }

    public static class ConsoleLogger
    {
        public static object Create(Type loggerInterface)
        {
            var assembly = AppDomain.CurrentDomain.DefineDynamicAssembly(
                new AssemblyName("BenchLogger"), System.Reflection.Emit.AssemblyBuilderAccess.Run);
            var type = assembly.DefineDynamicModule("main").DefineType(
                "BenchLoggerImpl", TypeAttributes.Public | TypeAttributes.Class, typeof(object), new[] { loggerInterface });
            var write = typeof(ConsoleLogger).GetMethod("Write");
            foreach (var method in loggerInterface.GetMethods())
            {
                var parameters = method.GetParameters();
                var impl = type.DefineMethod(method.Name,
                    MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
                    method.ReturnType, parameters.Select(p => p.ParameterType).ToArray());
                var il = impl.GetILGenerator();
                il.Emit(System.Reflection.Emit.OpCodes.Ldstr, method.Name);
                il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4, parameters.Length);
                il.Emit(System.Reflection.Emit.OpCodes.Newarr, typeof(object));
                for (var i = 0; i < parameters.Length; i++)
                {
                    il.Emit(System.Reflection.Emit.OpCodes.Dup);
                    il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4, i);
                    il.Emit(System.Reflection.Emit.OpCodes.Ldarg, i + 1);
                    if (parameters[i].ParameterType.IsValueType)
                    {
                        il.Emit(System.Reflection.Emit.OpCodes.Box, parameters[i].ParameterType);
                    }

                    il.Emit(System.Reflection.Emit.OpCodes.Stelem_Ref);
                }

                il.Emit(System.Reflection.Emit.OpCodes.Call, write);
                il.Emit(System.Reflection.Emit.OpCodes.Ret);
                type.DefineMethodOverride(impl, method);
            }

            return Activator.CreateInstance(type.CreateType());
        }

        public static void Write(string level, object[] args)
        {
            if (level == "Trace" || level == "Debug")
            {
                return;
            }

            Console.WriteLine("[plugin " + level + "] " + string.Join(" ", args.Where(a => a != null && !(a is Exception))));
        }
    }
}

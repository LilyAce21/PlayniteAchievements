// Stands in for a game under the notification: a borderless window covering a monitor that renders
// with Direct3D 11 through a flip-model swap chain and presents as fast as the GPU allows (tearing
// allowed, as VRR games present), or at a fixed cap. Each frame runs a pixel shader whose cost is set
// by --work, so the GPU can be held at full use the way a GPU-bound game holds it. CPU threads can
// spin alongside it.
//
//   GameLoad.exe [--work 400] [--fps-cap 0] [--cpu 0] [--screen 0] [--seconds 0]
//
// The picture depends only on position, not time, so it never changes under the card and a frame
// counter over the card counts only the card's own changes. It prints the achieved frame rate every
// two seconds. --seconds 0 runs until killed.
using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;
using SharpDX;
using SharpDX.D3DCompiler;
using D3D = SharpDX.Direct3D;
using D3D11 = SharpDX.Direct3D11;
using DXGI = SharpDX.DXGI;

public static class GameLoad
{
    private const string Shader = @"
cbuffer Work : register(b0) { int iterations; int3 pad; };
float4 vs(uint id : SV_VertexID) : SV_Position
{
    float2 uv = float2((id << 1) & 2, id & 2);
    return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}
float4 ps(float4 pos : SV_Position) : SV_Target
{
    float2 p = pos.xy / 512.0;
    float a = 0;
    [loop] for (int i = 0; i < iterations; i++)
    {
        p = float2(sin(p.y * 1.3 + a), cos(p.x * 0.7 - a));
        a += 0.01 * dot(p, p);
    }
    return float4(0.15 + 0.1 * p.x, 0.12 + 0.1 * p.y, 0.2 + 0.05 * frac(a), 1);
}";

    [STAThread]
    public static int Main(string[] args)
    {
        var work = 400;
        var cap = 0;
        var cpu = 0;
        var screen = 0;
        var seconds = 0d;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--work": work = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--fps-cap": cap = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--cpu": cpu = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--screen": screen = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--seconds": seconds = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
            }
        }

        var running = true;
        for (var i = 0; i < cpu; i++)
        {
            new Thread(() =>
            {
                var x = 1.0;
                while (Volatile.Read(ref running))
                {
                    x = Math.Sqrt(x + 1.0);
                }
            }) { IsBackground = true, Priority = ThreadPriority.Normal }.Start();
        }

        var bounds = Screen.AllScreens[Math.Min(screen, Screen.AllScreens.Length - 1)].Bounds;
        var form = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Bounds = bounds,
            Text = "GameLoad",
            ShowInTaskbar = false,
        };
        form.Show();
        Application.DoEvents();

        D3D11.Device device;
        DXGI.SwapChain1 chain;
        using (var factory = new DXGI.Factory2())
        {
            device = new D3D11.Device(D3D.DriverType.Hardware, D3D11.DeviceCreationFlags.BgraSupport);
            var description = new DXGI.SwapChainDescription1
            {
                Width = bounds.Width,
                Height = bounds.Height,
                Format = DXGI.Format.B8G8R8A8_UNorm,
                SampleDescription = new DXGI.SampleDescription(1, 0),
                Usage = DXGI.Usage.RenderTargetOutput,
                BufferCount = 3,
                SwapEffect = DXGI.SwapEffect.FlipDiscard,
                Flags = DXGI.SwapChainFlags.AllowTearing,
            };
            chain = new DXGI.SwapChain1(factory, device, form.Handle, ref description);
            factory.MakeWindowAssociation(form.Handle, DXGI.WindowAssociationFlags.IgnoreAll);
        }

        var context = device.ImmediateContext;
        D3D11.RenderTargetView target;
        using (var back = chain.GetBackBuffer<D3D11.Texture2D>(0))
        {
            target = new D3D11.RenderTargetView(device, back);
        }

        using (var vsCode = ShaderBytecode.Compile(Shader, "vs", "vs_5_0"))
        using (var psCode = ShaderBytecode.Compile(Shader, "ps", "ps_5_0"))
        {
            context.VertexShader.Set(new D3D11.VertexShader(device, vsCode));
            context.PixelShader.Set(new D3D11.PixelShader(device, psCode));
        }

        var constants = D3D11.Buffer.Create(device, D3D11.BindFlags.ConstantBuffer, new[] { work, 0, 0, 0 });
        context.PixelShader.SetConstantBuffer(0, constants);
        context.InputAssembler.PrimitiveTopology = D3D.PrimitiveTopology.TriangleList;
        context.Rasterizer.SetViewport(0, 0, bounds.Width, bounds.Height);
        context.OutputMerger.SetRenderTargets(target);

        form.FormClosed += (s, e) => Volatile.Write(ref running, false);
        var total = Stopwatch.StartNew();
        var window = Stopwatch.StartNew();
        var frames = 0;
        var period = cap > 0 ? Stopwatch.Frequency / cap : 0;
        var next = Stopwatch.GetTimestamp();
        while (Volatile.Read(ref running) && (seconds <= 0 || total.Elapsed.TotalSeconds < seconds))
        {
            Application.DoEvents();
            context.OutputMerger.SetRenderTargets(target);
            context.Draw(3, 0);
            chain.Present(0, cap > 0 ? DXGI.PresentFlags.None : DXGI.PresentFlags.AllowTearing);
            frames++;
            if (period > 0)
            {
                next += period;
                while (Stopwatch.GetTimestamp() < next)
                {
                    Thread.Sleep(0);
                }
            }

            if (window.ElapsedMilliseconds >= 2000)
            {
                Console.WriteLine("game fps {0:0.0}", frames * 1000.0 / window.ElapsedMilliseconds);
                frames = 0;
                window.Restart();
            }
        }

        Volatile.Write(ref running, false);
        return 0;
    }
}

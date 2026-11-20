using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using WinRT;

namespace PyWinUI.Host.Rendering;

/// <summary>
/// D3D11 三维视口，宿主在 <see cref="SwapChainPanel"/> 上直接呈现。
///
/// 与 Canvas2D 的根本区别：这里是真的 GPU 管线，
/// 有深度缓冲（正确遮挡）、顶点/像素着色器（真实光照），
/// 面数上限从几百提到几十万，且渲染循环完全在 C# 里跑，不经过 Python。
///
/// Python 只负责描述场景（网格 / 相机），改场景才发一条 patch；
/// 自转这类每帧变化的东西由宿主自己算，不产生跨进程流量。
/// </summary>
internal static class Viewport3DSupport
{
    private const string ShaderSource = """
        cbuffer Constants : register(b0)
        {
            float4x4 WorldViewProj;
            float4x4 World;
            float4   LightDirection;
            float4   BaseColor;
        };

        struct VSInput
        {
            float3 Position : POSITION;
            float3 Normal   : NORMAL;
        };

        struct PSInput
        {
            float4 Position : SV_POSITION;
            float3 Normal   : NORMAL;
        };

        PSInput VSMain(VSInput input)
        {
            PSInput o;
            o.Position = mul(float4(input.Position, 1.0), WorldViewProj);
            o.Normal   = mul(input.Normal, (float3x3)World);
            return o;
        }

        float4 PSMain(PSInput input) : SV_TARGET
        {
            float3 n = normalize(input.Normal);
            float ndl = saturate(dot(n, -LightDirection.xyz));
            float lighting = 0.32 + 0.68 * ndl;
            return float4(BaseColor.rgb * lighting, 1.0);
        }
        """;

    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public Matrix4x4 WorldViewProj;
        public Matrix4x4 World;
        public Vector4 LightDirection;
        public Vector4 BaseColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Vertex
    {
        public float X, Y, Z;
        public float NX, NY, NZ;

        public Vertex(float x, float y, float z, float nx = 0, float ny = 0, float nz = 0)
        {
            X = x;
            Y = y;
            Z = z;
            NX = nx;
            NY = ny;
            NZ = nz;
        }
    }

    [ComImport]
    [Guid("63aad0b8-7c24-40ff-85a8-640d944cc325")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISwapChainPanelNative
    {
        [PreserveSig]
        int SetSwapChain(IntPtr swapChain);
    }

    private sealed class Device
    {
        public ID3D11Device D3D = null!;
        public ID3D11DeviceContext Context = null!;
        public IDXGISwapChain1 SwapChain = null!;
        public ID3D11RenderTargetView? RenderTarget;
        public ID3D11Texture2D? DepthTexture;
        public ID3D11DepthStencilView? DepthView;

        public ID3D11VertexShader? VertexShader;
        public ID3D11PixelShader? PixelShader;
        public ID3D11InputLayout? InputLayout;
        public ID3D11Buffer? ConstantBuffer;

        public ID3D11Buffer? VertexBuffer;
        public ID3D11Buffer? IndexBuffer;
        public int IndexCount;
        public Vector4 Color = new(0.31f, 0.76f, 0.97f, 1f);

        public int Width;
        public int Height;

        /// <summary>自转累计角度（度），由宿主按时间推进。</summary>
        public double Spin;
        public long LastTick;
    }

    private static readonly Dictionary<SwapChainPanel, Device> Devices = new();
    private static readonly Dictionary<SwapChainPanel, JsonObject> PendingMeshes = new();
    private static readonly Dictionary<SwapChainPanel, JsonObject> Cameras = new();
    private static bool _renderingHooked;

    public static SwapChainPanel Create()
    {
        var panel = new SwapChainPanel();
        panel.Loaded += (_, _) => Attach(panel);
        panel.Unloaded += (_, _) => Detach(panel);
        panel.SizeChanged += (_, _) => Resize(panel);
        return panel;
    }

    public static void SetMesh(SwapChainPanel panel, JsonObject mesh)
    {
        // 设备可能还没建好（panel 未 Loaded），先缓存，渲染线程里再上传
        PendingMeshes[panel] = mesh;
    }

    public static void SetCamera(SwapChainPanel panel, JsonObject camera)
    {
        Cameras[panel] = camera;
    }

    private static void Attach(SwapChainPanel panel)
    {
        if (Devices.ContainsKey(panel))
        {
            return;
        }

        try
        {
            Devices[panel] = CreateDevice(panel);

            if (!_renderingHooked)
            {
                CompositionTarget.Rendering += OnRendering;
                _renderingHooked = true;
            }
        }
        catch (Exception ex)
        {
            HostLog.Write(ex, "Viewport3D.Attach");
        }
    }

    private static void Detach(SwapChainPanel panel)
    {
        if (Devices.Remove(panel, out var device))
        {
            try
            {
                device.VertexBuffer?.Dispose();
                device.IndexBuffer?.Dispose();
                device.ConstantBuffer?.Dispose();
                device.InputLayout?.Dispose();
                device.VertexShader?.Dispose();
                device.PixelShader?.Dispose();
                device.DepthView?.Dispose();
                device.DepthTexture?.Dispose();
                device.RenderTarget?.Dispose();
                device.SwapChain.Dispose();
                device.Context.Dispose();
                device.D3D.Dispose();
            }
            catch
            {
                // 释放失败不影响后续，进程退出时统一回收
            }
        }

        PendingMeshes.Remove(panel);
        Cameras.Remove(panel);
    }

    private static Device CreateDevice(SwapChainPanel panel)
    {
        var width = Math.Max(1, (int)panel.ActualWidth);
        var height = Math.Max(1, (int)panel.ActualHeight);

        // 用「只取设备」的重载：另一个重载的 out 参数类型与它撞签名，
        // 直接写会报二义性；上下文从 ImmediateContext 拿即可
        FeatureLevel[] levels =
        {
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0,
            FeatureLevel.Level_10_1,
        };

        D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            levels,
            out ID3D11Device d3d,
            out FeatureLevel _).CheckError();

        var context = d3d.ImmediateContext;

        using var dxgiDevice = d3d.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();

        var desc = new SwapChainDescription1
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = Format.B8G8R8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = AlphaMode.Premultiplied,
            Flags = SwapChainFlags.None,
        };

        var swapChain = factory.CreateSwapChainForComposition(d3d, desc);

        var native = panel.As<ISwapChainPanelNative>();
        native.SetSwapChain(swapChain.NativePointer);

        var device = new Device
        {
            D3D = d3d,
            Context = context,
            SwapChain = swapChain,
            Width = width,
            Height = height,
        };

        CreateDeviceResources(device);
        CreateSizeDependentResources(device);
        return device;
    }

    private static void CreateDeviceResources(Device device)
    {
        var vsCode = CompileShader(ShaderSource, "VSMain", "vs_5_0");
        var psCode = CompileShader(ShaderSource, "PSMain", "ps_5_0");

        device.VertexShader = device.D3D.CreateVertexShader(vsCode);
        device.PixelShader = device.D3D.CreatePixelShader(psCode);

        var elements = new[]
        {
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
        };

        device.InputLayout = device.D3D.CreateInputLayout(elements, vsCode);

        device.ConstantBuffer = device.D3D.CreateBuffer(new BufferDescription(
            (uint)Marshal.SizeOf<Constants>(),
            BindFlags.ConstantBuffer,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            ResourceOptionFlags.None,
            0));
    }

    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
    private static extern int D3DCompile(
        byte[] pSrcData,
        UIntPtr srcDataSize,
        string? pSourceName,
        IntPtr pDefines,
        IntPtr pInclude,
        string pEntrypoint,
        string pTarget,
        uint flags1,
        uint flags2,
        out IntPtr ppCode,
        out IntPtr ppErrorMsgs);

    [ComImport]
    [Guid("8BA5FB08-5195-40e2-AC58-0D989C3A0102")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ID3DBlob
    {
        // 必须 PreserveSig：否则 CLR 会把返回值当 HRESULT 检查，
        // 而这里返回的是裸指针，检查必然失败
        [PreserveSig]
        IntPtr GetBufferPointer();

        [PreserveSig]
        UIntPtr GetBufferSize();
    }

    /// <summary>
    /// 直接调 D3DCompile，不用 Vortice 的 Compile 包装。
    ///
    /// 包装层的重载在 3.8 里改过签名，而且不需要 .cso 预编译——
    /// 运行时编译在这台机器上可用（d3dcompiler_47.dll 是系统组件）。
    /// 真遇到精简系统缺这个 DLL，把编译结果存成 .cso 打包即可。
    /// </summary>
    private static byte[] CompileShader(string source, string entryPoint, string profile)
    {
        var bytes = Encoding.ASCII.GetBytes(source);

        var hr = D3DCompile(
            bytes,
            (UIntPtr)(uint)bytes.Length,
            "pywinui3-viewport",
            IntPtr.Zero,
            IntPtr.Zero,
            entryPoint,
            profile,
            0,
            0,
            out var codePtr,
            out var errorPtr);

        if (hr != 0)
        {
            var message = errorPtr != IntPtr.Zero
                ? Marshal.PtrToStringAnsi(errorPtr) ?? $"HRESULT 0x{hr:X8}"
                : $"HRESULT 0x{hr:X8}";

            throw new InvalidOperationException($"着色器 {entryPoint} 编译失败: {message}");
        }

        try
        {
            var blob = (ID3DBlob)Marshal.GetObjectForIUnknown(codePtr);
            var size = (int)blob.GetBufferSize();
            var data = new byte[size];
            Marshal.Copy(blob.GetBufferPointer(), data, 0, size);
            return data;
        }
        finally
        {
            if (codePtr != IntPtr.Zero)
            {
                Marshal.Release(codePtr);
            }

            if (errorPtr != IntPtr.Zero)
            {
                Marshal.Release(errorPtr);
            }
        }
    }

    private static void CreateSizeDependentResources(Device device)
    {
        device.RenderTarget?.Dispose();
        device.DepthView?.Dispose();
        device.DepthTexture?.Dispose();

        using var backBuffer = device.SwapChain.GetBuffer<ID3D11Texture2D>(0);
        device.RenderTarget = device.D3D.CreateRenderTargetView(backBuffer);

        device.DepthTexture = device.D3D.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)device.Width,
            Height = (uint)device.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.D24_UNorm_S8_UInt,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.DepthStencil,
        });

        device.DepthView = device.D3D.CreateDepthStencilView(device.DepthTexture);
    }

    private static void Resize(SwapChainPanel panel)
    {
        if (!Devices.TryGetValue(panel, out var device))
        {
            return;
        }

        var width = Math.Max(1, (int)panel.ActualWidth);
        var height = Math.Max(1, (int)panel.ActualHeight);

        if (width == device.Width && height == device.Height)
        {
            return;
        }

        try
        {
            device.Context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
            device.RenderTarget?.Dispose();
            device.RenderTarget = null;

            device.SwapChain.ResizeBuffers(2, (uint)width, (uint)height, Format.B8G8R8A8_UNorm, SwapChainFlags.None);

            device.Width = width;
            device.Height = height;
            CreateSizeDependentResources(device);
        }
        catch (Exception ex)
        {
            HostLog.Write(ex, "Viewport3D.Resize");
        }
    }

    private static void OnRendering(object? sender, object args)
    {
        foreach (var (panel, device) in Devices)
        {
            if (device.RenderTarget is null || device.DepthView is null)
            {
                continue;
            }

            try
            {
                UploadPendingMesh(panel, device);
                Render(panel, device);
            }
            catch (Exception ex)
            {
                HostLog.Write(ex, "Viewport3D.Render");
            }
        }
    }

    /// <summary>把 Python 下发的顶点/索引上传成 GPU buffer，只在场景变化时发生。</summary>
    private static void UploadPendingMesh(SwapChainPanel panel, Device device)
    {
        if (!PendingMeshes.Remove(panel, out var mesh))
        {
            return;
        }

        if (mesh["vertices"] is not JsonArray vertexArray || vertexArray.Count < 3)
        {
            return;
        }

        var vertices = new List<Vertex>(vertexArray.Count);
        foreach (var item in vertexArray)
        {
            if (item is JsonArray point && point.Count >= 3)
            {
                vertices.Add(new Vertex(
                    (float)(ValueParsers.AsDouble(point[0]) ?? 0),
                    (float)(ValueParsers.AsDouble(point[1]) ?? 0),
                    (float)(ValueParsers.AsDouble(point[2]) ?? 0)));
            }
        }

        var indices = new List<uint>();
        if (mesh["indices"] is JsonArray indexArray)
        {
            foreach (var item in indexArray)
            {
                if (ValueParsers.AsInt(item) is { } index && index >= 0)
                {
                    indices.Add((uint)index);
                }
            }
        }

        if (vertices.Count < 3 || indices.Count < 3)
        {
            return;
        }

        // 没给法线就按面法线累加，否则光照会是一片死黑
        ComputeNormals(vertices, indices);

        if (ValueParsers.AsColor(mesh["color"]) is { } color)
        {
            device.Color = new Vector4(color.R / 255f, color.G / 255f, color.B / 255f, 1f);
        }

        device.VertexBuffer?.Dispose();
        device.IndexBuffer?.Dispose();

        device.VertexBuffer = device.D3D.CreateBuffer(vertices.ToArray(), BindFlags.VertexBuffer);
        device.IndexBuffer = device.D3D.CreateBuffer(indices.ToArray(), BindFlags.IndexBuffer);
        device.IndexCount = indices.Count;
    }

    private static void ComputeNormals(List<Vertex> vertices, List<uint> indices)
    {
        var normals = new Vector3[vertices.Count];

        for (var i = 0; i + 2 < indices.Count; i += 3)
        {
            var i0 = (int)indices[i];
            var i1 = (int)indices[i + 1];
            var i2 = (int)indices[i + 2];

            if (i0 >= vertices.Count || i1 >= vertices.Count || i2 >= vertices.Count)
            {
                continue;
            }

            var p0 = new Vector3(vertices[i0].X, vertices[i0].Y, vertices[i0].Z);
            var p1 = new Vector3(vertices[i1].X, vertices[i1].Y, vertices[i1].Z);
            var p2 = new Vector3(vertices[i2].X, vertices[i2].Y, vertices[i2].Z);

            var normal = Vector3.Cross(p1 - p0, p2 - p0);
            normals[i0] += normal;
            normals[i1] += normal;
            normals[i2] += normal;
        }

        for (var i = 0; i < vertices.Count; i++)
        {
            var n = normals[i].LengthSquared() > 1e-8f ? Vector3.Normalize(normals[i]) : Vector3.UnitY;
            vertices[i] = new Vertex(vertices[i].X, vertices[i].Y, vertices[i].Z, n.X, n.Y, n.Z);
        }
    }

    private static void Render(SwapChainPanel panel, Device device)
    {
        var context = device.Context;

        context.OMSetRenderTargets(device.RenderTarget!, device.DepthView!);
        context.RSSetViewport(new Viewport(0, 0, device.Width, device.Height));

        context.ClearRenderTargetView(device.RenderTarget!, new Color4(0.09f, 0.10f, 0.13f, 1f));
        context.ClearDepthStencilView(device.DepthView!, DepthStencilClearFlags.Depth, 1f, 0);

        if (device.VertexBuffer is null || device.IndexBuffer is null || device.IndexCount == 0)
        {
            device.SwapChain.Present(1, PresentFlags.None);
            return;
        }

        // 相机与自转：都在宿主里按帧时间推进，不经过 Python
        var now = Environment.TickCount64;
        var dt = device.LastTick == 0 ? 0 : (now - device.LastTick) / 1000.0;
        device.LastTick = now;

        var camera = Cameras.TryGetValue(panel, out var cam) ? cam : null;
        var yaw = ValueParsers.AsDouble(camera?["yaw"]) ?? 0;
        var pitch = ValueParsers.AsDouble(camera?["pitch"]) ?? 20;
        var distance = ValueParsers.AsDouble(camera?["distance"]) ?? 3.2;
        var autoRotate = ValueParsers.AsDouble(camera?["autoRotate"]) ?? 0;

        device.Spin = (device.Spin + autoRotate * dt) % 360;

        var totalYaw = (yaw + device.Spin) * Math.PI / 180.0;
        var pitchRad = Math.Max(-89.0, Math.Min(89.0, pitch)) * Math.PI / 180.0;

        var cy = MathF.Cos((float)pitchRad);
        var eye = new Vector3(
            (float)(distance * cy * Math.Sin(totalYaw)),
            (float)(distance * Math.Sin(pitchRad)),
            (float)(distance * cy * Math.Cos(totalYaw)));

        var aspect = device.Width / (float)device.Height;
        var view = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4f, aspect, 0.1f, 100f);

        var world = Matrix4x4.Identity;
        var wvp = world * view * projection;

        var constants = new Constants
        {
            // HLSL 用行向量 × 矩阵，System.Numerics 是行主序，传转置即可对齐
            WorldViewProj = Matrix4x4.Transpose(wvp),
            World = Matrix4x4.Transpose(world),
            LightDirection = new Vector4(0.4f, -0.7f, -0.6f, 0f),
            BaseColor = device.Color,
        };

        context.UpdateSubresource(constants, device.ConstantBuffer!);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.IASetInputLayout(device.InputLayout);
        context.IASetVertexBuffer(0, device.VertexBuffer, (uint)Unsafe.SizeOf<Vertex>());
        context.IASetIndexBuffer(device.IndexBuffer, Format.R32_UInt, 0);
        context.VSSetShader(device.VertexShader);
        context.VSSetConstantBuffer(0, device.ConstantBuffer);
        context.PSSetShader(device.PixelShader);
        context.PSSetConstantBuffer(0, device.ConstantBuffer);
        context.DrawIndexed((uint)device.IndexCount, 0, 0);

        device.SwapChain.Present(1, PresentFlags.None);
    }
}

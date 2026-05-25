using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WiredMonitorClient.Diagnostics;
using WiredMonitorClient.Video;

namespace WiredMonitorClient.Rendering;

internal sealed class D3D11VideoRenderer : IDisposable
{
    private static readonly Guid IidD3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    private const int DxgiFormatR8G8Unorm = 49;
    private const int DxgiFormatR8Unorm = 61;
    private const int D3D11SrvDimensionTexture2DArray = 5;
    private const uint D3D11PrimitiveTopologyTriangleList = 4;
    private const int D3D11FilterMinMagMipLinear = 0x15;
    private const int D3D11TextureAddressClamp = 3;
    private const int D3D11ComparisonAlways = 8;

    private IntPtr _device;
    private IntPtr _deviceContext;
    private IntPtr _sharedHandle;
    private IntPtr _sharedTexture;
    private IntPtr _renderTargetView;
    private IntPtr _vertexShader;
    private IntPtr _pixelShader;
    private IntPtr _samplerState;
    private int _width;
    private int _height;

    public bool Initialize(IntPtr device, IntPtr deviceContext, IntPtr sharedHandle, int width, int height)
    {
        if (device == IntPtr.Zero || deviceContext == IntPtr.Zero || sharedHandle == IntPtr.Zero)
            return false;

        if (_device == device
            && _deviceContext == deviceContext
            && _sharedHandle == sharedHandle
            && _width == width
            && _height == height
            && _renderTargetView != IntPtr.Zero)
        {
            return true;
        }

        DisposeResources();

        _device = device;
        _deviceContext = deviceContext;
        _sharedHandle = sharedHandle;
        _width = width;
        _height = height;

        var textureIid = IidD3D11Texture2D;
        var hr = OpenSharedResource(_device, sharedHandle, ref textureIid, out _sharedTexture);
        if (Failed(hr) || _sharedTexture == IntPtr.Zero)
        {
            DiagLog.Write($"D3D11直通初始化失败: OpenSharedResource hr=0x{hr:x8}");
            DisposeResources();
            return false;
        }

        hr = CreateRenderTargetView(_device, _sharedTexture, IntPtr.Zero, out _renderTargetView);
        if (Failed(hr) || _renderTargetView == IntPtr.Zero)
        {
            DiagLog.Write($"D3D11直通初始化失败: CreateRenderTargetView hr=0x{hr:x8}");
            DisposeResources();
            return false;
        }

        if (!CreateShaders() || !CreateSampler())
        {
            DisposeResources();
            return false;
        }

        DiagLog.Write($"D3D11直通渲染器已初始化: {width}x{height}, shared=0x{sharedHandle:x}");
        return true;
    }

    public bool Present(D3D11DecodedFrame frame, out D3D11DirectPresentMetrics metrics)
    {
        metrics = default;

        if (_device == IntPtr.Zero
            || _deviceContext == IntPtr.Zero
            || _renderTargetView == IntPtr.Zero
            || _vertexShader == IntPtr.Zero
            || _pixelShader == IntPtr.Zero
            || _samplerState == IntPtr.Zero
            || frame.Texture == IntPtr.Zero)
        {
            return false;
        }

        var srvStart = Stopwatch.GetTimestamp();
        if (!CreatePlaneShaderResourceViews(frame, out var yView, out var uvView))
            return false;

        metrics = metrics with { SrvTicks = Stopwatch.GetTimestamp() - srvStart };

        try
        {
            var drawStart = Stopwatch.GetTimestamp();
            DrawFrame(yView, uvView);
            metrics = metrics with { DrawTicks = Stopwatch.GetTimestamp() - drawStart };
            return true;
        }
        finally
        {
            UnbindShaderResourceViews();
            ReleaseCom(ref uvView);
            ReleaseCom(ref yView);
        }
    }

    private bool CreatePlaneShaderResourceViews(D3D11DecodedFrame frame, out IntPtr yView, out IntPtr uvView)
    {
        yView = IntPtr.Zero;
        uvView = IntPtr.Zero;

        var yDesc = ShaderResourceViewDesc(DxgiFormatR8Unorm, frame.ArraySlice);
        var hr = CreateShaderResourceView(_device, frame.Texture, ref yDesc, out yView);
        if (Failed(hr) || yView == IntPtr.Zero)
        {
            DiagLog.Write($"D3D11直通失败: 创建 Y 平面 SRV 失败 hr=0x{hr:x8}, slice={frame.ArraySlice}");
            return false;
        }

        var uvDesc = ShaderResourceViewDesc(DxgiFormatR8G8Unorm, frame.ArraySlice);
        hr = CreateShaderResourceView(_device, frame.Texture, ref uvDesc, out uvView);
        if (Failed(hr) || uvView == IntPtr.Zero)
        {
            DiagLog.Write($"D3D11直通失败: 创建 UV 平面 SRV 失败 hr=0x{hr:x8}, slice={frame.ArraySlice}");
            ReleaseCom(ref yView);
            return false;
        }

        return true;
    }

    private static D3D11_SHADER_RESOURCE_VIEW_DESC ShaderResourceViewDesc(int format, int arraySlice) => new()
    {
        Format = format,
        ViewDimension = D3D11SrvDimensionTexture2DArray,
        MostDetailedMip = 0,
        MipLevels = 1,
        FirstArraySlice = unchecked((uint)arraySlice),
        ArraySize = 1,
    };

    private unsafe void DrawFrame(IntPtr yView, IntPtr uvView)
    {
        var viewport = new D3D11_VIEWPORT
        {
            Width = _width,
            Height = _height,
            MinDepth = 0,
            MaxDepth = 1,
        };

        var renderTargets = stackalloc IntPtr[1];
        renderTargets[0] = _renderTargetView;
        var shaderResources = stackalloc IntPtr[2];
        shaderResources[0] = yView;
        shaderResources[1] = uvView;
        var samplers = stackalloc IntPtr[1];
        samplers[0] = _samplerState;

        RSSetViewports(_deviceContext, 1, ref viewport);
        OMSetRenderTargets(_deviceContext, 1, (IntPtr)renderTargets, IntPtr.Zero);
        IASetPrimitiveTopology(_deviceContext, D3D11PrimitiveTopologyTriangleList);
        VSSetShader(_deviceContext, _vertexShader, IntPtr.Zero, 0);
        PSSetShader(_deviceContext, _pixelShader, IntPtr.Zero, 0);
        PSSetShaderResources(_deviceContext, 0, 2, (IntPtr)shaderResources);
        PSSetSamplers(_deviceContext, 0, 1, (IntPtr)samplers);
        Draw(_deviceContext, 3, 0);
        Flush(_deviceContext);
    }

    private unsafe void UnbindShaderResourceViews()
    {
        var empty = stackalloc IntPtr[2];
        PSSetShaderResources(_deviceContext, 0, 2, (IntPtr)empty);
    }

    private bool CreateShaders()
    {
        var vertexShaderSource = """
            struct VSOut
            {
                float4 position : SV_POSITION;
                float2 texCoord : TEXCOORD0;
            };

            VSOut main(uint vertexId : SV_VertexID)
            {
                VSOut output;
                float2 positions[3] = {
                    float2(-1.0, -1.0),
                    float2(-1.0,  3.0),
                    float2( 3.0, -1.0)
                };
                float2 texCoords[3] = {
                    float2(0.0, 1.0),
                    float2(0.0, -1.0),
                    float2(2.0, 1.0)
                };
                output.position = float4(positions[vertexId], 0.0, 1.0);
                output.texCoord = texCoords[vertexId];
                return output;
            }
            """;

        var useFullRange = ShouldUseFullColorRange();
        var pixelShaderSource = useFullRange ? FullRangePixelShaderSource : LimitedRangePixelShaderSource;
        DiagLog.Write($"D3D11直通颜色转换: BT.709 {(useFullRange ? "full" : "limited")} range");

        return CompileShader(vertexShaderSource, "vs_4_0", out _vertexShader, CreateVertexShader)
            && CompileShader(pixelShaderSource, "ps_4_0", out _pixelShader, CreatePixelShader);
    }

    private const string LimitedRangePixelShaderSource = """
        Texture2DArray<float> yPlane : register(t0);
        Texture2DArray<float2> uvPlane : register(t1);
        SamplerState linearSampler : register(s0);

        float4 main(float4 position : SV_POSITION, float2 texCoord : TEXCOORD0) : SV_TARGET
        {
            float ySample = yPlane.Sample(linearSampler, float3(texCoord, 0.0)).r;
            float2 uvSample = uvPlane.Sample(linearSampler, float3(texCoord, 0.0)).rg;
            float y = saturate((ySample - 16.0 / 255.0) * (255.0 / 219.0));
            float2 uv = (uvSample - float2(128.0 / 255.0, 128.0 / 255.0)) * (255.0 / 224.0);
            float r = y + 1.5748 * uv.y;
            float g = y - 0.1873 * uv.x - 0.4681 * uv.y;
            float b = y + 1.8556 * uv.x;
            return float4(saturate(r), saturate(g), saturate(b), 1.0);
        }
        """;

    private const string FullRangePixelShaderSource = """
        Texture2DArray<float> yPlane : register(t0);
        Texture2DArray<float2> uvPlane : register(t1);
        SamplerState linearSampler : register(s0);

        float4 main(float4 position : SV_POSITION, float2 texCoord : TEXCOORD0) : SV_TARGET
        {
            float y = yPlane.Sample(linearSampler, float3(texCoord, 0.0)).r;
            float2 uv = uvPlane.Sample(linearSampler, float3(texCoord, 0.0)).rg - float2(0.5, 0.5);
            float r = y + 1.5748 * uv.y;
            float g = y - 0.1873 * uv.x - 0.4681 * uv.y;
            float b = y + 1.8556 * uv.x;
            return float4(saturate(r), saturate(g), saturate(b), 1.0);
        }
        """;

    private static bool ShouldUseFullColorRange()
    {
        var range = Environment.GetEnvironmentVariable("WIRED_MONITOR_D3D11_COLOR_RANGE");
        return string.Equals(range, "full", StringComparison.OrdinalIgnoreCase);
    }

    private delegate int CreateShaderFromBlob(IntPtr device, IntPtr bytecode, nuint bytecodeLength, IntPtr classLinkage, out IntPtr shader);

    private bool CompileShader(string source, string target, out IntPtr shader, CreateShaderFromBlob createShader)
    {
        shader = IntPtr.Zero;
        var hr = D3DCompile(
            source,
            (nuint)Encoding.ASCII.GetByteCount(source),
            null,
            IntPtr.Zero,
            IntPtr.Zero,
            "main",
            target,
            0,
            0,
            out var blob,
            out var errorBlob);

        try
        {
            if (Failed(hr) || blob == IntPtr.Zero)
            {
                var error = errorBlob == IntPtr.Zero ? "<none>" : Marshal.PtrToStringAnsi(GetBlobPointer(errorBlob));
                DiagLog.Write($"D3D11直通初始化失败: D3DCompile {target} hr=0x{hr:x8}, error={error}");
                return false;
            }

            hr = createShader(_device, GetBlobPointer(blob), GetBlobSize(blob), IntPtr.Zero, out shader);
            if (Failed(hr) || shader == IntPtr.Zero)
            {
                DiagLog.Write($"D3D11直通初始化失败: CreateShader {target} hr=0x{hr:x8}");
                return false;
            }

            return true;
        }
        finally
        {
            ReleaseCom(ref errorBlob);
            ReleaseCom(ref blob);
        }
    }

    private bool CreateSampler()
    {
        var samplerDesc = new D3D11_SAMPLER_DESC
        {
            Filter = D3D11FilterMinMagMipLinear,
            AddressU = D3D11TextureAddressClamp,
            AddressV = D3D11TextureAddressClamp,
            AddressW = D3D11TextureAddressClamp,
            ComparisonFunc = D3D11ComparisonAlways,
            MaxLOD = float.MaxValue,
        };

        var hr = CreateSamplerState(_device, ref samplerDesc, out _samplerState);
        if (Failed(hr) || _samplerState == IntPtr.Zero)
        {
            DiagLog.Write($"D3D11直通初始化失败: CreateSamplerState hr=0x{hr:x8}");
            return false;
        }

        return true;
    }

    public void Dispose()
    {
        DisposeResources();
        GC.SuppressFinalize(this);
    }

    private void DisposeResources()
    {
        ReleaseCom(ref _samplerState);
        ReleaseCom(ref _pixelShader);
        ReleaseCom(ref _vertexShader);
        ReleaseCom(ref _renderTargetView);
        ReleaseCom(ref _sharedTexture);
        _device = IntPtr.Zero;
        _deviceContext = IntPtr.Zero;
        _sharedHandle = IntPtr.Zero;
        _width = 0;
        _height = 0;
    }

    private static bool Failed(int hr) => hr < 0;

    private static void ReleaseCom(ref IntPtr unknown)
    {
        if (unknown == IntPtr.Zero)
            return;

        _ = Marshal.Release(unknown);
        unknown = IntPtr.Zero;
    }

    private static T GetMethod<T>(IntPtr comObject, int slot) where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(comObject);
        var method = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(method);
    }

    private static int OpenSharedResource(IntPtr device, IntPtr sharedHandle, ref Guid iid, out IntPtr resource)
    {
        var openSharedResource = GetMethod<OpenSharedResourceDelegate>(device, 28);
        return openSharedResource(device, sharedHandle, ref iid, out resource);
    }

    private static int CreateRenderTargetView(IntPtr device, IntPtr resource, IntPtr desc, out IntPtr renderTargetView)
    {
        var createRenderTargetView = GetMethod<CreateRenderTargetViewDelegate>(device, 9);
        return createRenderTargetView(device, resource, desc, out renderTargetView);
    }

    private static int CreateShaderResourceView(
        IntPtr device,
        IntPtr resource,
        ref D3D11_SHADER_RESOURCE_VIEW_DESC desc,
        out IntPtr shaderResourceView)
    {
        var createShaderResourceView = GetMethod<CreateShaderResourceViewDelegate>(device, 7);
        return createShaderResourceView(device, resource, ref desc, out shaderResourceView);
    }

    private static int CreateVertexShader(IntPtr device, IntPtr bytecode, nuint bytecodeLength, IntPtr classLinkage, out IntPtr shader)
    {
        var createVertexShader = GetMethod<CreateShaderDelegate>(device, 12);
        return createVertexShader(device, bytecode, bytecodeLength, classLinkage, out shader);
    }

    private static int CreatePixelShader(IntPtr device, IntPtr bytecode, nuint bytecodeLength, IntPtr classLinkage, out IntPtr shader)
    {
        var createPixelShader = GetMethod<CreateShaderDelegate>(device, 15);
        return createPixelShader(device, bytecode, bytecodeLength, classLinkage, out shader);
    }

    private static int CreateSamplerState(IntPtr device, ref D3D11_SAMPLER_DESC desc, out IntPtr samplerState)
    {
        var createSamplerState = GetMethod<CreateSamplerStateDelegate>(device, 23);
        return createSamplerState(device, ref desc, out samplerState);
    }

    private static void RSSetViewports(IntPtr context, uint viewportCount, ref D3D11_VIEWPORT viewport)
    {
        var rsSetViewports = GetMethod<RSSetViewportsDelegate>(context, 44);
        rsSetViewports(context, viewportCount, ref viewport);
    }

    private static void OMSetRenderTargets(IntPtr context, uint renderTargetCount, IntPtr renderTargetViews, IntPtr depthStencilView)
    {
        var omSetRenderTargets = GetMethod<OMSetRenderTargetsDelegate>(context, 33);
        omSetRenderTargets(context, renderTargetCount, renderTargetViews, depthStencilView);
    }

    private static void IASetPrimitiveTopology(IntPtr context, uint primitiveTopology)
    {
        var iaSetPrimitiveTopology = GetMethod<IASetPrimitiveTopologyDelegate>(context, 24);
        iaSetPrimitiveTopology(context, primitiveTopology);
    }

    private static void VSSetShader(IntPtr context, IntPtr shader, IntPtr classInstances, uint classInstanceCount)
    {
        var vsSetShader = GetMethod<SetShaderDelegate>(context, 11);
        vsSetShader(context, shader, classInstances, classInstanceCount);
    }

    private static void PSSetShader(IntPtr context, IntPtr shader, IntPtr classInstances, uint classInstanceCount)
    {
        var psSetShader = GetMethod<SetShaderDelegate>(context, 9);
        psSetShader(context, shader, classInstances, classInstanceCount);
    }

    private static void PSSetShaderResources(IntPtr context, uint startSlot, uint viewCount, IntPtr shaderResourceViews)
    {
        var psSetShaderResources = GetMethod<SetResourcesDelegate>(context, 8);
        psSetShaderResources(context, startSlot, viewCount, shaderResourceViews);
    }

    private static void PSSetSamplers(IntPtr context, uint startSlot, uint samplerCount, IntPtr samplers)
    {
        var psSetSamplers = GetMethod<SetResourcesDelegate>(context, 10);
        psSetSamplers(context, startSlot, samplerCount, samplers);
    }

    private static void Draw(IntPtr context, uint vertexCount, uint startVertexLocation)
    {
        var draw = GetMethod<DrawDelegate>(context, 13);
        draw(context, vertexCount, startVertexLocation);
    }

    private static void Flush(IntPtr context)
    {
        var flush = GetMethod<FlushDelegate>(context, 111);
        flush(context);
    }

    private static IntPtr GetBlobPointer(IntPtr blob)
    {
        var getBufferPointer = GetMethod<GetBufferPointerDelegate>(blob, 3);
        return getBufferPointer(blob);
    }

    private static nuint GetBlobSize(IntPtr blob)
    {
        var getBufferSize = GetMethod<GetBufferSizeDelegate>(blob, 4);
        return getBufferSize(blob);
    }

    [DllImport("d3dcompiler_47.dll", ExactSpelling = true, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private static extern int D3DCompile(
        string sourceData,
        nuint sourceDataSize,
        string? sourceName,
        IntPtr defines,
        IntPtr include,
        string entryPoint,
        string target,
        uint flags1,
        uint flags2,
        out IntPtr code,
        out IntPtr errorMessages);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int OpenSharedResourceDelegate(IntPtr self, IntPtr resourceHandle, ref Guid returnedInterface, out IntPtr resource);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateRenderTargetViewDelegate(IntPtr self, IntPtr resource, IntPtr desc, out IntPtr renderTargetView);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateShaderResourceViewDelegate(IntPtr self, IntPtr resource, ref D3D11_SHADER_RESOURCE_VIEW_DESC desc, out IntPtr shaderResourceView);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateShaderDelegate(IntPtr self, IntPtr bytecode, nuint bytecodeLength, IntPtr classLinkage, out IntPtr shader);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateSamplerStateDelegate(IntPtr self, ref D3D11_SAMPLER_DESC desc, out IntPtr samplerState);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void RSSetViewportsDelegate(IntPtr self, uint viewportCount, ref D3D11_VIEWPORT viewports);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void OMSetRenderTargetsDelegate(IntPtr self, uint renderTargetCount, IntPtr renderTargetViews, IntPtr depthStencilView);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void IASetPrimitiveTopologyDelegate(IntPtr self, uint topology);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void SetShaderDelegate(IntPtr self, IntPtr shader, IntPtr classInstances, uint classInstanceCount);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void SetResourcesDelegate(IntPtr self, uint startSlot, uint resourceCount, IntPtr resources);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void DrawDelegate(IntPtr self, uint vertexCount, uint startVertexLocation);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void FlushDelegate(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr GetBufferPointerDelegate(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate nuint GetBufferSizeDelegate(IntPtr self);

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_SHADER_RESOURCE_VIEW_DESC
    {
        public int Format;
        public int ViewDimension;
        public uint MostDetailedMip;
        public uint MipLevels;
        public uint FirstArraySlice;
        public uint ArraySize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_SAMPLER_DESC
    {
        public int Filter;
        public int AddressU;
        public int AddressV;
        public int AddressW;
        public float MipLODBias;
        public uint MaxAnisotropy;
        public int ComparisonFunc;
        public float BorderColor0;
        public float BorderColor1;
        public float BorderColor2;
        public float BorderColor3;
        public float MinLOD;
        public float MaxLOD;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_VIEWPORT
    {
        public float TopLeftX;
        public float TopLeftY;
        public float Width;
        public float Height;
        public float MinDepth;
        public float MaxDepth;
    }
}

internal readonly record struct D3D11DirectPresentMetrics(long SrvTicks, long DrawTicks, long DirtyTicks);

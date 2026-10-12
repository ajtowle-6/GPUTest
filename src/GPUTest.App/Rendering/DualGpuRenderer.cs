using System.Numerics;
using System.Runtime.InteropServices;
using GPUTest.App.Simulation;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace GPUTest.App.Rendering;

internal sealed unsafe partial class DualGpuRenderer : IDisposable
{
    private const Format ColorFormat = Format.R8G8B8A8_UNorm;
    private readonly IDXGIFactory4 factory;
    private readonly AdapterContext primary;
    private readonly AdapterContext secondary;
    private readonly IDXGISwapChain3 swapChain;
    private readonly ID3D12Resource[] backBuffers = new ID3D12Resource[2];
    private readonly ID3D12Heap sharedHeap;
    private readonly ID3D12Heap importedHeap;
    private readonly ID3D12Resource exportBuffer;
    private readonly ID3D12Resource importBuffer;
    private readonly ID3D12Fence exportedFence;
    private readonly ID3D12Fence importedFence;
    private readonly PlacedSubresourceFootPrint footprint;
    private readonly int width;
    private readonly int height;
    private readonly int leftWidth;
    private ulong frameId;

    public string Devices => $"{primary.Name} [{primary.Luid}] + {secondary.Name} [{secondary.Luid}]";
    public ulong FrameId => frameId;

    public DualGpuRenderer(nint window, int width, int height)
    {
        if (width < 2 || height < 1) throw new ArgumentOutOfRangeException(nameof(width));
        this.width = width;
        this.height = height;
        leftWidth = width / 2;
        factory = DXGI.CreateDXGIFactory1<IDXGIFactory4>();
        var adapters = new List<IDXGIAdapter1>();
        for (uint index = 0; factory.EnumAdapters1(index, out IDXGIAdapter1 adapter).Success; index++)
        {
            if (adapter.Description1.VendorId == 0x10DE && (adapter.Description1.Flags & AdapterFlags.Software) == 0)
                adapters.Add(adapter);
            else adapter.Dispose();
        }
        try
        {
            if (adapters.Count < 2) throw new InvalidOperationException("Two NVIDIA D3D12 adapters are required.");
            primary = new AdapterContext(adapters[0], leftWidth, height);
            secondary = new AdapterContext(adapters[1], width - leftWidth, height);
        }
        finally { foreach (var adapter in adapters) adapter.Dispose(); }

        using IDXGISwapChain1 initial = factory.CreateSwapChainForHwnd(primary.Queue, window, new SwapChainDescription1
        {
            Width = (uint)width, Height = (uint)height, Format = ColorFormat,
            BufferCount = 2, BufferUsage = Usage.RenderTargetOutput,
            SampleDescription = SampleDescription.Default, SwapEffect = SwapEffect.FlipDiscard
        });
        swapChain = initial.QueryInterface<IDXGISwapChain3>();
        factory.MakeWindowAssociation(window, WindowAssociationFlags.IgnoreAltEnter);
        for (uint index = 0; index < 2; index++) backBuffers[index] = swapChain.GetBuffer<ID3D12Resource>(index);

        uint rowPitch = ((uint)(width - leftWidth) * 4 + 255) & ~255u;
        ulong bufferSize = (ulong)rowPitch * (uint)height;
        ulong heapSize = (bufferSize + 65535) & ~65535UL;
        sharedHeap = secondary.Device.CreateHeap<ID3D12Heap>(new HeapDescription
        {
            SizeInBytes = heapSize,
            Properties = new HeapProperties(HeapType.Default),
            Flags = HeapFlags.Shared | HeapFlags.SharedCrossAdapter
        });
        nint heapHandle = secondary.Device.CreateSharedHandle(sharedHeap, null, null!);
        try { importedHeap = primary.Device.OpenSharedHandle<ID3D12Heap>(heapHandle); }
        finally { CloseHandle(heapHandle); }
        ResourceDescription transferDescription = ResourceDescription.Buffer(bufferSize, ResourceFlags.AllowCrossAdapter);
        exportBuffer = secondary.Device.CreatePlacedResource<ID3D12Resource>(sharedHeap, 0, transferDescription, ResourceStates.Common);
        importBuffer = primary.Device.CreatePlacedResource<ID3D12Resource>(importedHeap, 0, transferDescription, ResourceStates.Common);
        exportedFence = secondary.Device.CreateFence(0, FenceFlags.Shared | FenceFlags.SharedCrossAdapter);
        nint fenceHandle = secondary.Device.CreateSharedHandle(exportedFence, null, null!);
        try { importedFence = primary.Device.OpenSharedHandle<ID3D12Fence>(fenceHandle); }
        finally { CloseHandle(fenceHandle); }
        footprint = new PlacedSubresourceFootPrint
        {
            Offset = 0,
            Footprint = new SubresourceFootPrint
            {
                Format = ColorFormat, Width = (uint)(width - leftWidth), Height = (uint)height, Depth = 1, RowPitch = rowPitch
            }
        };
        Console.WriteLine($"Dual-GPU renderer: {Devices}; {width}x{height}; cross-adapter buffer {bufferSize:N0} bytes");
    }

    public void Render(ReadOnlySpan<BallPose> poses, Vector3 cubePosition, Quaternion cubeOrientation)
    {
        Vector3 eye = new(13, 10, 19);
        Matrix4x4 view = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitY);
        var constants = new CameraConstants
        {
            ViewProjection = view * Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4, (float)width / height, 0.1f, 100),
            CubeWorld = Matrix4x4.CreateFromQuaternion(cubeOrientation) * Matrix4x4.CreateTranslation(cubePosition),
            Eye = new Vector4(eye, 1),
            Right = new Vector4(view.M11, view.M21, view.M31, 0),
            Up = new Vector4(view.M12, view.M22, view.M32, 0)
        };
        primary.Record(poses, constants, width, 0);
        secondary.Record(poses, constants, width, leftWidth);

        var secondaryCommands = secondary.Commands;
        secondaryCommands.ResourceBarrierTransition(exportBuffer, ResourceStates.Common, ResourceStates.CopyDest);
        secondaryCommands.CopyTextureRegion(new TextureCopyLocation(exportBuffer, footprint), 0, 0, 0, new TextureCopyLocation(secondary.Color, 0));
        secondaryCommands.ResourceBarrierTransition(exportBuffer, ResourceStates.CopyDest, ResourceStates.Common);
        secondaryCommands.Close();
        secondary.Queue.ExecuteCommandList(secondaryCommands);
        secondary.Queue.Signal(exportedFence, ++frameId);

        var primaryCommands = primary.Commands;
        ID3D12Resource backBuffer = backBuffers[swapChain.CurrentBackBufferIndex];
        primaryCommands.ResourceBarrierTransition(backBuffer, ResourceStates.Present, ResourceStates.CopyDest);
        primaryCommands.CopyTextureRegion(new TextureCopyLocation(backBuffer, 0), 0, 0, 0, new TextureCopyLocation(primary.Color, 0));
        primaryCommands.ResourceBarrierTransition(importBuffer, ResourceStates.Common, ResourceStates.CopySource);
        primaryCommands.CopyTextureRegion(new TextureCopyLocation(backBuffer, 0), (uint)leftWidth, 0, 0, new TextureCopyLocation(importBuffer, footprint));
        primaryCommands.ResourceBarrierTransition(importBuffer, ResourceStates.CopySource, ResourceStates.Common);
        primaryCommands.ResourceBarrierTransition(backBuffer, ResourceStates.CopyDest, ResourceStates.Present);
        primaryCommands.Close();
        primary.Queue.Wait(importedFence, frameId);
        primary.Queue.ExecuteCommandList(primaryCommands);
        swapChain.Present(1, PresentFlags.None).CheckError();
        primary.WaitIdle();
    }

    public void Dispose()
    {
        primary.WaitIdle();
        secondary.WaitIdle();
        foreach (var resource in backBuffers) resource.Dispose();
        swapChain.Dispose();
        importBuffer.Dispose();
        exportBuffer.Dispose();
        importedFence.Dispose();
        exportedFence.Dispose();
        importedHeap.Dispose();
        sharedHeap.Dispose();
        primary.Dispose();
        secondary.Dispose();
        factory.Dispose();
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct CameraConstants
    {
        public Matrix4x4 ViewProjection;
        public Matrix4x4 CubeWorld;
        public Vector4 Eye;
        public Vector4 Right;
        public Vector4 Up;
    }

    private sealed class AdapterContext : IDisposable
    {
        public ID3D12Device Device { get; }
        public ID3D12CommandQueue Queue { get; }
        public ID3D12GraphicsCommandList Commands { get; }
        public ID3D12Resource Color { get; }
        public string Name { get; }
        public long Luid { get; }
        private readonly ID3D12CommandAllocator allocator;
        private readonly ID3D12DescriptorHeap renderTargetHeap;
        private readonly ID3D12DescriptorHeap depthHeap;
        private readonly ID3D12Resource depth;
        private readonly ID3D12Resource instances;
        private readonly ID3D12Resource camera;
        private readonly ID3D12RootSignature rootSignature;
        private readonly ID3D12PipelineState spheres;
        private readonly ID3D12PipelineState lines;
        private readonly ID3D12Fence fence;
        private readonly AutoResetEvent completion = new(false);
        private ulong fenceValue;
        private readonly int regionWidth;
        private readonly int height;
        private readonly void* instancePointer;
        private readonly void* cameraPointer;

        public AdapterContext(IDXGIAdapter1 adapter, int regionWidth, int height)
        {
            this.regionWidth = regionWidth;
            this.height = height;
            Name = adapter.Description1.Description.Trim();
            Luid = adapter.Description1.Luid;
            Device = D3D12.D3D12CreateDevice<ID3D12Device>(adapter, FeatureLevel.Level_12_0);
            Queue = Device.CreateCommandQueue(CommandListType.Direct);
            Queue.Name = $"Scene rendering on {Luid}";
            allocator = Device.CreateCommandAllocator(CommandListType.Direct);
            Commands = Device.CreateCommandList<ID3D12GraphicsCommandList>(CommandListType.Direct, allocator);
            Commands.Close();
            fence = Device.CreateFence(0);
            renderTargetHeap = Device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, 1));
            depthHeap = Device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.DepthStencilView, 1));
            Color = Device.CreateCommittedResource(HeapType.Default,
                ResourceDescription.Texture2D(ColorFormat, (uint)regionWidth, (uint)height, flags: ResourceFlags.AllowRenderTarget), ResourceStates.CopySource);
            depth = Device.CreateCommittedResource(HeapType.Default,
                ResourceDescription.Texture2D(Format.D32_Float, (uint)regionWidth, (uint)height, flags: ResourceFlags.AllowDepthStencil), ResourceStates.DepthWrite,
                new ClearValue(Format.D32_Float, 1, 0));
            Device.CreateRenderTargetView(Color, null, renderTargetHeap.GetCPUDescriptorHandleForHeapStart());
            Device.CreateDepthStencilView(depth, null, depthHeap.GetCPUDescriptorHandleForHeapStart());
            instances = Device.CreateCommittedResource(HeapType.Upload, ResourceDescription.Buffer(100_000 * 32), ResourceStates.GenericRead);
            camera = Device.CreateCommittedResource(HeapType.Upload, ResourceDescription.Buffer(256), ResourceStates.GenericRead);
            instancePointer = instances.Map<byte>(0);
            cameraPointer = camera.Map<byte>(0);
            rootSignature = Device.CreateRootSignature(new RootSignatureDescription1(RootSignatureFlags.None,
            [
                new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All),
                new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(0, 0), ShaderVisibility.Vertex)
            ]));
            string source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "Scene.hlsl"));
            GraphicsPipelineStateDescription description = new()
            {
                RootSignature = rootSignature,
                VertexShader = Compile(source, "SphereVS", DxcShaderStage.Vertex),
                PixelShader = Compile(source, "SpherePS", DxcShaderStage.Pixel),
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                RasterizerState = RasterizerDescription.CullNone,
                BlendState = BlendDescription.Opaque,
                DepthStencilState = DepthStencilDescription.Default,
                RenderTargetFormats = [ColorFormat],
                DepthStencilFormat = Format.D32_Float,
                SampleDescription = SampleDescription.Default
            };
            spheres = Device.CreateGraphicsPipelineState<ID3D12PipelineState>(description);
            description.VertexShader = Compile(source, "CubeVS", DxcShaderStage.Vertex);
            description.PixelShader = Compile(source, "CubePS", DxcShaderStage.Pixel);
            description.PrimitiveTopologyType = PrimitiveTopologyType.Line;
            lines = Device.CreateGraphicsPipelineState<ID3D12PipelineState>(description);
        }

        private static byte[] Compile(string source, string entryPoint, DxcShaderStage stage)
        {
            using IDxcResult result = DxcCompiler.Compile(stage, source, entryPoint, new DxcCompilerOptions { ShaderModel = DxcShaderModel.Model6_0 });
            if (result.GetStatus().Failure) throw new InvalidOperationException(result.GetErrors());
            return result.GetObjectBytecodeMemory().ToArray();
        }

        public void Record(ReadOnlySpan<BallPose> poses, CameraConstants constants, int fullWidth, int offset)
        {
            if (poses.Length > 100_000) throw new ArgumentOutOfRangeException(nameof(poses));
            poses.CopyTo(new Span<BallPose>(instancePointer, poses.Length));
            *(CameraConstants*)cameraPointer = constants;
            allocator.Reset();
            Commands.Reset(allocator);
            Commands.ResourceBarrierTransition(Color, ResourceStates.CopySource, ResourceStates.RenderTarget);
            CpuDescriptorHandle target = renderTargetHeap.GetCPUDescriptorHandleForHeapStart();
            CpuDescriptorHandle depthView = depthHeap.GetCPUDescriptorHandleForHeapStart();
            Commands.ClearRenderTargetView(target, new Color4(0.055f, 0.065f, 0.07f, 1));
            Commands.ClearDepthStencilView(depthView, ClearFlags.Depth, 1, 0);
            Commands.OMSetRenderTargets(target, depthView);
            Commands.RSSetViewport(new Viewport(-offset, 0, fullWidth, height));
            Commands.RSSetScissorRect(new RawRect(0, 0, regionWidth, height));
            Commands.SetGraphicsRootSignature(rootSignature);
            Commands.SetGraphicsRootConstantBufferView(0, camera.GPUVirtualAddress);
            Commands.SetGraphicsRootShaderResourceView(1, instances.GPUVirtualAddress);
            Commands.SetPipelineState(spheres);
            Commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            Commands.DrawInstanced(6, (uint)poses.Length, 0, 0);
            Commands.SetPipelineState(lines);
            Commands.IASetPrimitiveTopology(PrimitiveTopology.LineList);
            Commands.DrawInstanced(24, 1, 0, 0);
            Commands.ResourceBarrierTransition(Color, ResourceStates.RenderTarget, ResourceStates.CopySource);
        }

        public void WaitIdle()
        {
            Queue.Signal(fence, ++fenceValue);
            if (fence.CompletedValue < fenceValue)
            {
                fence.SetEventOnCompletion(fenceValue, completion.SafeWaitHandle.DangerousGetHandle());
                if (!completion.WaitOne(TimeSpan.FromSeconds(15))) throw new TimeoutException($"GPU {Luid} did not complete its work.");
            }
        }

        public void Dispose()
        {
            WaitIdle();
            instances.Unmap(0);
            camera.Unmap(0);
            lines.Dispose(); spheres.Dispose(); rootSignature.Dispose();
            instances.Dispose(); camera.Dispose(); depth.Dispose(); Color.Dispose();
            renderTargetHeap.Dispose(); depthHeap.Dispose(); Commands.Dispose(); allocator.Dispose();
            fence.Dispose(); completion.Dispose(); Queue.Dispose(); Device.Dispose();
        }
    }
}
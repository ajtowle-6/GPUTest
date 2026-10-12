using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using GPUTest.App.Simulation;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Debug;
using Vortice.Dxc;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace GPUTest.App.Rendering;

internal sealed unsafe partial class DualGpuRenderer : IDisposable
{
    private const Format ColorFormat = Format.R8G8B8A8_UNorm;
    private const int FrameCount = 3;
    private readonly IDXGIFactory4 factory;
    private readonly AdapterContext primary;
    private readonly AdapterContext secondary;
    private readonly IDXGISwapChain3 swapChain;
    private readonly ID3D12Resource[] backBuffers = new ID3D12Resource[FrameCount];
    private readonly ID3D12Heap sharedHeap;
    private readonly ID3D12Heap importedHeap;
    private readonly ID3D12Resource[] exportBuffers = new ID3D12Resource[FrameCount];
    private readonly ID3D12Resource[] importBuffers = new ID3D12Resource[FrameCount];
    private readonly ulong[] completionValues = new ulong[FrameCount];
    private readonly EventWaitHandle frameReady;
    private readonly ID3D12Fence exportedFence;
    private readonly ID3D12Fence importedFence;
    private readonly PlacedSubresourceFootPrint footprint;
    private readonly int width;
    private readonly int height;
    private readonly int leftWidth;
    private ulong frameId;
    private uint lastBackBuffer;

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
            BufferCount = FrameCount, BufferUsage = Usage.RenderTargetOutput,
            Flags = SwapChainFlags.FrameLatencyWaitableObject,
            SampleDescription = SampleDescription.Default, SwapEffect = SwapEffect.FlipDiscard
        });
        swapChain = initial.QueryInterface<IDXGISwapChain3>();
        swapChain.MaximumFrameLatency = FrameCount - 1;
        frameReady = new EventWaitHandle(false, EventResetMode.AutoReset)
        {
            SafeWaitHandle = new SafeWaitHandle(swapChain.FrameLatencyWaitableObject, true)
        };
        factory.MakeWindowAssociation(window, WindowAssociationFlags.IgnoreAltEnter);
        for (uint index = 0; index < FrameCount; index++) backBuffers[index] = swapChain.GetBuffer<ID3D12Resource>(index);

        uint rowPitch = ((uint)(width - leftWidth) * 4 + 255) & ~255u;
        ulong bufferSize = (ulong)rowPitch * (uint)height;
        ulong heapSize = (bufferSize + 65535) & ~65535UL;
        sharedHeap = secondary.Device.CreateHeap<ID3D12Heap>(new HeapDescription
        {
            SizeInBytes = heapSize * FrameCount,
            Properties = new HeapProperties(HeapType.Default),
            Flags = HeapFlags.Shared | HeapFlags.SharedCrossAdapter
        });
        nint heapHandle = secondary.Device.CreateSharedHandle(sharedHeap, null, null!);
        try { importedHeap = primary.Device.OpenSharedHandle<ID3D12Heap>(heapHandle); }
        finally { CloseHandle(heapHandle); }
        ResourceDescription transferDescription = ResourceDescription.Buffer(bufferSize, ResourceFlags.AllowCrossAdapter);
        for (int slot = 0; slot < FrameCount; slot++)
        {
            exportBuffers[slot] = secondary.Device.CreatePlacedResource<ID3D12Resource>(sharedHeap, heapSize * (ulong)slot, transferDescription, ResourceStates.Common);
            importBuffers[slot] = primary.Device.CreatePlacedResource<ID3D12Resource>(importedHeap, heapSize * (ulong)slot, transferDescription, ResourceStates.Common);
        }
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
        int slot = (int)(frameId % FrameCount);
        primary.WaitFor(completionValues[slot]);
        if (!frameReady.WaitOne(TimeSpan.FromSeconds(15))) throw new TimeoutException("Presentation did not become ready.");
        Submit(poses, cubePosition, cubeOrientation, slot);
    }

    public bool TryRender(ReadOnlySpan<BallPose> poses, Vector3 cubePosition, Quaternion cubeOrientation)
    {
        int slot = (int)(frameId % FrameCount);
        if (!primary.IsComplete(completionValues[slot]) || !frameReady.WaitOne(0)) return false;
        Submit(poses, cubePosition, cubeOrientation, slot);
        return true;
    }

    private void Submit(ReadOnlySpan<BallPose> poses, Vector3 cubePosition, Quaternion cubeOrientation, int slot)
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
        AdapterFrame primaryFrame = primary.Record(slot, poses, constants, width, 0);
        primaryFrame.Commands.Close();
        primary.Queue.ExecuteCommandList(primaryFrame.Commands);
        AdapterFrame secondaryFrame = secondary.Record(slot, poses, constants, width, leftWidth);

        ID3D12Resource exportBuffer = exportBuffers[slot];
        ID3D12Resource importBuffer = importBuffers[slot];
        var secondaryCommands = secondaryFrame.Commands;
        secondaryCommands.ResourceBarrierTransition(exportBuffer, ResourceStates.Common, ResourceStates.CopyDest);
        secondaryCommands.CopyTextureRegion(new TextureCopyLocation(exportBuffer, footprint), 0, 0, 0, new TextureCopyLocation(secondaryFrame.Color, 0));
        secondaryCommands.ResourceBarrierTransition(exportBuffer, ResourceStates.CopyDest, ResourceStates.Common);
        secondaryCommands.Close();
        secondary.Queue.ExecuteCommandList(secondaryCommands);
        secondary.Queue.Signal(exportedFence, ++frameId).CheckError();

        var primaryCommands = primaryFrame.Composition;
        primaryCommands.Reset(primaryFrame.Allocator);
        lastBackBuffer = swapChain.CurrentBackBufferIndex;
        ID3D12Resource backBuffer = backBuffers[lastBackBuffer];
        primaryCommands.ResourceBarrierTransition(backBuffer, ResourceStates.Present, ResourceStates.CopyDest);
        primaryCommands.CopyTextureRegion(new TextureCopyLocation(backBuffer, 0), 0, 0, 0, new TextureCopyLocation(primaryFrame.Color, 0));
        primaryCommands.ResourceBarrierTransition(importBuffer, ResourceStates.Common, ResourceStates.CopySource);
        primaryCommands.CopyTextureRegion(new TextureCopyLocation(backBuffer, 0), (uint)leftWidth, 0, 0, new TextureCopyLocation(importBuffer, footprint));
        primaryCommands.ResourceBarrierTransition(importBuffer, ResourceStates.CopySource, ResourceStates.Common);
        primaryCommands.ResourceBarrierTransition(backBuffer, ResourceStates.CopyDest, ResourceStates.Present);
        primaryCommands.Close();
        primary.Queue.Wait(importedFence, frameId).CheckError();
        primary.Queue.ExecuteCommandList(primaryCommands);
        swapChain.Present(1, PresentFlags.None).CheckError();
        completionValues[slot] = primary.Signal();
    }

    public void WaitIdle()
    {
        primary.WaitIdle();
        secondary.WaitIdle();
    }

    public Bitmap Capture()
    {
        if (frameId == 0) throw new InvalidOperationException("No rendered frame to capture.");
        WaitIdle();
        uint rowPitch = ((uint)width * 4 + 255) & ~255u;
        using var readback = primary.Device.CreateCommittedResource(HeapType.Readback,
            ResourceDescription.Buffer((ulong)rowPitch * (uint)height), ResourceStates.CopyDest);
        using var allocator = primary.Device.CreateCommandAllocator(CommandListType.Direct);
        using var commands = primary.Device.CreateCommandList<ID3D12GraphicsCommandList>(CommandListType.Direct, allocator);
        var captureFootprint = new PlacedSubresourceFootPrint
        {
            Footprint = new SubresourceFootPrint { Format = ColorFormat, Width = (uint)width, Height = (uint)height, Depth = 1, RowPitch = rowPitch }
        };
        ID3D12Resource backBuffer = backBuffers[lastBackBuffer];
        commands.ResourceBarrierTransition(backBuffer, ResourceStates.Present, ResourceStates.CopySource);
        commands.CopyTextureRegion(new TextureCopyLocation(readback, captureFootprint), 0, 0, 0, new TextureCopyLocation(backBuffer, 0));
        commands.ResourceBarrierTransition(backBuffer, ResourceStates.CopySource, ResourceStates.Present);
        commands.Close();
        primary.Queue.ExecuteCommandList(commands);
        primary.WaitFor(primary.Signal());
        var image = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var pixels = image.LockBits(new Rectangle(0, 0, width, height), System.Drawing.Imaging.ImageLockMode.WriteOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        byte* source = readback.Map<byte>(0);
        for (int row = 0; row < height; row++)
        {
            byte* target = (byte*)pixels.Scan0 + row * pixels.Stride;
            byte* input = source + row * rowPitch;
            for (int column = 0; column < width; column++)
            {
                target[column * 4] = input[column * 4 + 2];
                target[column * 4 + 1] = input[column * 4 + 1];
                target[column * 4 + 2] = input[column * 4];
                target[column * 4 + 3] = 255;
            }
        }
        readback.Unmap(0);
        image.UnlockBits(pixels);
        return image;
    }

    public void Dispose()
    {
        WaitIdle();
        foreach (var resource in backBuffers) resource.Dispose();
        swapChain.Dispose();
        frameReady.Dispose();
        foreach (var resource in importBuffers) resource.Dispose();
        foreach (var resource in exportBuffers) resource.Dispose();
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
        public string Name { get; }
        public long Luid { get; }
        private readonly AdapterFrame[] frames = new AdapterFrame[FrameCount];
        private readonly ID3D12RootSignature rootSignature;
        private readonly ID3D12PipelineState spheres;
        private readonly ID3D12PipelineState lines;
        private readonly ID3D12Fence fence;
        private readonly ID3D12InfoQueue? validation;
        private readonly AutoResetEvent completion = new(false);
        private ulong fenceValue;
        private readonly int regionWidth;
        private readonly int height;

        public AdapterContext(IDXGIAdapter1 adapter, int regionWidth, int height)
        {
            this.regionWidth = regionWidth;
            this.height = height;
            Name = adapter.Description1.Description.Trim();
            Luid = adapter.Description1.Luid;
            Device = D3D12.D3D12CreateDevice<ID3D12Device>(adapter, FeatureLevel.Level_12_0);
            validation = Device.QueryInterfaceOrNull<ID3D12InfoQueue>();
            Queue = Device.CreateCommandQueue(CommandListType.Direct);
            Queue.Name = $"Scene rendering on {Luid}";
            fence = Device.CreateFence(0);
            for (int slot = 0; slot < FrameCount; slot++) frames[slot] = new AdapterFrame(Device, regionWidth, height);
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

        public AdapterFrame Record(int slot, ReadOnlySpan<BallPose> poses, CameraConstants constants, int fullWidth, int offset)
        {
            if (poses.Length > 100_000) throw new ArgumentOutOfRangeException(nameof(poses));
            AdapterFrame frame = frames[slot];
            poses.CopyTo(new Span<BallPose>(frame.InstancePointer, poses.Length));
            *(CameraConstants*)frame.CameraPointer = constants;
            frame.Allocator.Reset();
            var Commands = frame.Commands;
            Commands.Reset(frame.Allocator);
            Commands.ResourceBarrierTransition(frame.Color, ResourceStates.CopySource, ResourceStates.RenderTarget);
            CpuDescriptorHandle target = frame.RenderTargetHeap.GetCPUDescriptorHandleForHeapStart();
            CpuDescriptorHandle depthView = frame.DepthHeap.GetCPUDescriptorHandleForHeapStart();
            Commands.ClearRenderTargetView(target, new Color4(0.055f, 0.065f, 0.07f, 1));
            Commands.ClearDepthStencilView(depthView, ClearFlags.Depth, 1, 0);
            Commands.OMSetRenderTargets(target, depthView);
            Commands.RSSetViewport(new Viewport(-offset, 0, fullWidth, height));
            Commands.RSSetScissorRect(new RawRect(0, 0, regionWidth, height));
            Commands.SetGraphicsRootSignature(rootSignature);
            Commands.SetGraphicsRootConstantBufferView(0, frame.Camera.GPUVirtualAddress);
            Commands.SetGraphicsRootShaderResourceView(1, frame.Instances.GPUVirtualAddress);
            Commands.SetPipelineState(spheres);
            Commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            Commands.DrawInstanced(6, (uint)poses.Length, 0, 0);
            Commands.SetPipelineState(lines);
            Commands.IASetPrimitiveTopology(PrimitiveTopology.LineList);
            Commands.DrawInstanced(24, 1, 0, 0);
            Commands.ResourceBarrierTransition(frame.Color, ResourceStates.RenderTarget, ResourceStates.CopySource);
            return frame;
        }

        public ulong Signal()
        {
            Queue.Signal(fence, ++fenceValue).CheckError();
            return fenceValue;
        }

        public bool IsComplete(ulong value)
        {
            ulong completed = fence.CompletedValue;
            if (completed == ulong.MaxValue) throw new InvalidOperationException($"GPU {Luid} was removed.");
            return completed >= value;
        }

        public void WaitFor(ulong value)
        {
            if (!IsComplete(value))
            {
                fence.SetEventOnCompletion(value, completion.SafeWaitHandle.DangerousGetHandle()).CheckError();
                if (!completion.WaitOne(TimeSpan.FromSeconds(15))) throw new TimeoutException($"GPU {Luid} did not complete its work.");
            }
        }

        public void WaitIdle()
        {
            WaitFor(Signal());
            if (validation is null) return;
            for (ulong index = 0; index < validation.NumStoredMessagesAllowedByRetrievalFilter; index++)
            {
                var message = validation.GetMessage(index);
                if (message.Severity is MessageSeverity.Error or MessageSeverity.Corruption)
                    throw new InvalidOperationException($"GPU {Luid}: {message.Description}");
            }
            validation.ClearStoredMessages();
        }

        public void Dispose()
        {
            WaitIdle();
            lines.Dispose(); spheres.Dispose(); rootSignature.Dispose();
            foreach (var frame in frames) frame.Dispose();
            fence.Dispose(); completion.Dispose(); validation?.Dispose(); Queue.Dispose(); Device.Dispose();
        }
    }

    private sealed class AdapterFrame : IDisposable
    {
        public readonly ID3D12CommandAllocator Allocator;
        public readonly ID3D12GraphicsCommandList Commands;
        public readonly ID3D12GraphicsCommandList Composition;
        public readonly ID3D12DescriptorHeap RenderTargetHeap;
        public readonly ID3D12DescriptorHeap DepthHeap;
        public readonly ID3D12Resource Color;
        public readonly ID3D12Resource Depth;
        public readonly ID3D12Resource Instances;
        public readonly ID3D12Resource Camera;
        public readonly void* InstancePointer;
        public readonly void* CameraPointer;

        public AdapterFrame(ID3D12Device device, int width, int height)
        {
            Allocator = device.CreateCommandAllocator(CommandListType.Direct);
            Commands = device.CreateCommandList<ID3D12GraphicsCommandList>(CommandListType.Direct, Allocator);
            Commands.Close();
            Composition = device.CreateCommandList<ID3D12GraphicsCommandList>(CommandListType.Direct, Allocator);
            Composition.Close();
            RenderTargetHeap = device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, 1));
            DepthHeap = device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.DepthStencilView, 1));
            Color = device.CreateCommittedResource(HeapType.Default,
                ResourceDescription.Texture2D(ColorFormat, (uint)width, (uint)height, flags: ResourceFlags.AllowRenderTarget), ResourceStates.CopySource);
            Depth = device.CreateCommittedResource(HeapType.Default,
                ResourceDescription.Texture2D(Format.D32_Float, (uint)width, (uint)height, flags: ResourceFlags.AllowDepthStencil), ResourceStates.DepthWrite,
                new ClearValue(Format.D32_Float, 1, 0));
            device.CreateRenderTargetView(Color, null, RenderTargetHeap.GetCPUDescriptorHandleForHeapStart());
            device.CreateDepthStencilView(Depth, null, DepthHeap.GetCPUDescriptorHandleForHeapStart());
            Instances = device.CreateCommittedResource(HeapType.Upload, ResourceDescription.Buffer(100_000 * 32), ResourceStates.GenericRead);
            Camera = device.CreateCommittedResource(HeapType.Upload, ResourceDescription.Buffer(256), ResourceStates.GenericRead);
            InstancePointer = Instances.Map<byte>(0);
            CameraPointer = Camera.Map<byte>(0);
        }

        public void Dispose()
        {
            Instances.Unmap(0); Camera.Unmap(0);
            Commands.Dispose(); Composition.Dispose(); Allocator.Dispose();
            Instances.Dispose(); Camera.Dispose(); Color.Dispose(); Depth.Dispose();
            RenderTargetHeap.Dispose(); DepthHeap.Dispose();
        }
    }
}
using System;
using System.Diagnostics;
using Silk.NET.Core.Native;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.WebGPU;
using Silk.NET.Windowing;
using Silk.NET.Windowing.Glfw;
using Silk.NET.Input.Glfw;
using AtomEngineV2.ImGuiBackend;
using ImGuiNET;

namespace AtomEngineV2.DesktopClient
{
    internal unsafe static class Program
    {
        private static IWindow _window;
        private static WebGPU _webGpu;
        private static Instance* _instance;
        private static Surface* _surface;
        private static Adapter* _adapter;
        private static Device* _device;
        private static Queue* _queue;
        private static TextureFormat _swapChainFormat;
        private static ImGuiController _imGuiController;

        private static unsafe void Main()
        {
            // Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", "");
            Environment.SetEnvironmentVariable("GLFW_PLATFORM", "x11");

            GlfwWindowing.RegisterPlatform();
            GlfwInput.RegisterPlatform();

            var options = WindowOptions.Default;
            options.API = GraphicsAPI.None;
            options.ShouldSwapAutomatically = false;
            options.IsContextControlDisabled = true;
            options.Position = new Vector2D<int>(100, 100);
            options.Size = new Vector2D<int>(1280, 720);
            options.Title = "AtomEngine Editor";

            _window = Window.Create(options);

            _window.Load += OnLoad;
            _window.Render += OnRender;
            _window.FramebufferResize += OnFramebufferResize;
            _window.Closing += OnClosing;

            _window.Run();
        }

        private static unsafe void OnLoad()
        {
            InitializeWebGPU();
            InitializeImGui();
        }

        private static unsafe void InitializeWebGPU()
        {
            _webGpu = WebGPU.GetApi();

            var instanceDescriptor = new InstanceDescriptor();
            _instance = _webGpu.CreateInstance(ref instanceDescriptor);

            _surface = _window.CreateWebGPUSurface(_webGpu, _instance);

            var requestAdapterOptions = new RequestAdapterOptions
            {
                CompatibleSurface = _surface,
                PowerPreference = PowerPreference.HighPerformance,
                ForceFallbackAdapter = false
            };

            Adapter* adapterResult = null;
            _webGpu.InstanceRequestAdapter(
                _instance,
                ref requestAdapterOptions,
                new PfnRequestAdapterCallback((status, adapter, message, userData) =>
                {
                    if (status != RequestAdapterStatus.Success)
                    {
                        var msg = SilkMarshal.PtrToString((IntPtr)message);
                        throw new Exception($"Failed to request adapter: {msg}");
                    }
                    adapterResult = adapter;
                }),
                null);

            _adapter = adapterResult;

            var deviceDescriptor = new DeviceDescriptor();
            Device* deviceResult = null;
            _webGpu.AdapterRequestDevice(
                _adapter,
                ref deviceDescriptor,
                new PfnRequestDeviceCallback((status, device, message, userData) =>
                {
                    if (status != RequestDeviceStatus.Success)
                    {
                        var msg = SilkMarshal.PtrToString((IntPtr)message);
                        throw new Exception($"Failed to request device: {msg}");
                    }
                    deviceResult = device;
                }),
                null);

            _device = deviceResult;

            _webGpu.DeviceSetUncapturedErrorCallback(
                _device,
                new PfnErrorCallback(OnDeviceError),
                null);

            _queue = _webGpu.DeviceGetQueue(_device);

            ConfigureSwapChain();
        }

        private static unsafe void ConfigureSwapChain()
        {
            if (_window.Size.X <= 0 || _window.Size.Y <= 0)
            {
                return;
            }

            _swapChainFormat = _webGpu.SurfaceGetPreferredFormat(_surface, _adapter);

            var config = new SurfaceConfiguration
            {
                Device = _device,
                Format = _swapChainFormat,
                Usage = TextureUsage.RenderAttachment,
                PresentMode = PresentMode.Fifo,
                Width = (uint)_window.FramebufferSize.X,
                Height = (uint)_window.FramebufferSize.Y
            };

            _webGpu.SurfaceConfigure(_surface, ref config);
        }

        private static unsafe void InitializeImGui()
        {
            var inputContext = _window.CreateInput();

            _imGuiController = new ImGuiController(
                _webGpu,
                _device,
                _window,
                inputContext,
                2,
                _swapChainFormat,
                null);
        }

        private static unsafe void OnRender(double deltaTime)
        {
            SurfaceTexture surfaceTexture;
            _webGpu.SurfaceGetCurrentTexture(_surface, &surfaceTexture);

            switch (surfaceTexture.Status)
            {
                case SurfaceGetCurrentTextureStatus.Success:
                    break;

                case SurfaceGetCurrentTextureStatus.Timeout:
                case SurfaceGetCurrentTextureStatus.Outdated:
                case SurfaceGetCurrentTextureStatus.Lost:
                    if (surfaceTexture.Texture != null)
                    {
                        _webGpu.TextureRelease(surfaceTexture.Texture);
                    }
                    ConfigureSwapChain();
                    return;

                default:
                    throw new Exception($"Failed to get surface texture: {surfaceTexture.Status}");
            }

            var textureView = _webGpu.TextureCreateView(surfaceTexture.Texture, null);

            var encoderDescriptor = new CommandEncoderDescriptor();
            var encoder = _webGpu.DeviceCreateCommandEncoder(_device, ref encoderDescriptor);

            var colorAttachment = new RenderPassColorAttachment
            {
                View = textureView,
                ResolveTarget = null,
                LoadOp = LoadOp.Clear,
                StoreOp = StoreOp.Store,
                ClearValue = new Color(0.1, 0.1, 0.1, 1.0)
            };

            var renderPassDescriptor = new RenderPassDescriptor
            {
                ColorAttachmentCount = 1,
                ColorAttachments = &colorAttachment
            };

            var renderPass = _webGpu.CommandEncoderBeginRenderPass(encoder, ref renderPassDescriptor);

            _imGuiController.Update((float)deltaTime);

            DrawUI();

            _imGuiController.Render(renderPass);

            _webGpu.RenderPassEncoderEnd(renderPass);
            _webGpu.RenderPassEncoderRelease(renderPass);

            _webGpu.TextureViewRelease(textureView);

            var commandBufferDescriptor = new CommandBufferDescriptor();
            var commandBuffer = _webGpu.CommandEncoderFinish(encoder, ref commandBufferDescriptor);

            _webGpu.QueueSubmit(_queue, 1, &commandBuffer);

            _webGpu.CommandBufferRelease(commandBuffer);
            _webGpu.CommandEncoderRelease(encoder);

            _webGpu.SurfacePresent(_surface);
        }

        private static void DrawUI()
        {
            ImGui.ShowDemoWindow();

            ImGui.Begin("AtomEngine Editor");
            ImGui.Text("Welcome to AtomEngine!");
            ImGui.Text($"FPS: {1.0 / ImGui.GetIO().DeltaTime:F1}");
            ImGui.End();
        }

        private static unsafe void OnFramebufferResize(Vector2D<int> size)
        {
            if (size.X > 0 && size.Y > 0)
            {
                ConfigureSwapChain();
            }
        }

        private static unsafe void OnClosing()
        {
            _imGuiController?.Dispose();

            _webGpu.QueueRelease(_queue);
            _webGpu.DeviceRelease(_device);
            _webGpu.AdapterRelease(_adapter);
            _webGpu.SurfaceRelease(_surface);
            _webGpu.InstanceRelease(_instance);
            _webGpu.Dispose();
        }

        private static unsafe void OnDeviceError(ErrorType type, byte* message, void* userData)
        {
            var msg = SilkMarshal.PtrToString((IntPtr)message);
            Debug.WriteLine($"WebGPU Error ({type}): {msg}");
            Console.WriteLine($"WebGPU Error ({type}): {msg}");
        }
    }
}
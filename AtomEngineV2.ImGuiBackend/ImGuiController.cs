using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Windowing;
using System.Numerics;
using Silk.NET.WebGPU;
using Silk.NET.Input;
using ImGuiNET;
using System;

using Buffer = Silk.NET.WebGPU.Buffer;

namespace AtomEngineV2.ImGuiBackend
{
    public unsafe class ImGuiController : IDisposable
    {
        private readonly WebGPU _webGpu;
        private readonly Device* _device;
        private readonly Queue* _queue;
        private readonly IView _view;
        private readonly IInputContext _inputContext;
        private readonly TextureFormat _swapChainFormat;
        private readonly TextureFormat? _depthFormat;
        private readonly uint _framesInFlight;

        private ShaderModule* _shaderModule;

        private Texture* _fontTexture;
        private Sampler* _fontSampler;
        private TextureView* _fontView;

        private BindGroupLayout* _commonBindGroupLayout;
        private BindGroupLayout* _imageBindGroupLayout;
        private BindGroup* _commonBindGroup;
        private RenderPipeline* _renderPipeline;

        private Buffer* _uniformsBuffer;

        private WindowRenderBuffers _windowRenderBuffers;

        private readonly Dictionary<IntPtr, IntPtr> _viewsById = new Dictionary<IntPtr, IntPtr>();
        private readonly List<char> _pressedChars = new List<char>();
        private readonly Dictionary<Key, bool> _keyEvents = new Dictionary<Key, bool>();

        public ImGuiController(
            WebGPU webGpu,
            Device* device,
            IView view,
            IInputContext inputContext,
            uint framesInFlight,
            TextureFormat swapChainFormat,
            TextureFormat? depthFormat)
        {
            _webGpu = webGpu;
            _device = device;
            _view = view;
            _inputContext = inputContext;
            _framesInFlight = framesInFlight;
            _swapChainFormat = swapChainFormat;
            _depthFormat = depthFormat;
            _queue = _webGpu.DeviceGetQueue(_device);

            Initialize();
        }


        private void Initialize()
        {
            var context = ImGui.CreateContext();
            ImGui.SetCurrentContext(context);

            _inputContext.Keyboards[0].KeyUp += OnKeyUp;
            _inputContext.Keyboards[0].KeyDown += OnKeyDown;
            _inputContext.Keyboards[0].KeyChar += OnKeyChar;

            InitializeShaders();
            InitializeFonts();
            InitializeBindGroupLayouts();
            InitializePipeline();
            InitializeUniformBuffer();
            InitializeBindGroups();

            SetPerFrameData(1f / 60f);
        }

        private void InitializeShaders()
        {
            var sourcePtr = SilkMarshal.StringToPtr(ImGuiShader.Code);
            var labelPtr = SilkMarshal.StringToPtr("ImGui Shader");

            ShaderModuleWGSLDescriptor wgslDescriptor = new ShaderModuleWGSLDescriptor()
            {
                Code = (byte*)sourcePtr,
                Chain = new ChainedStruct(sType: SType.ShaderModuleWgslDescriptor)
            };

            ShaderModuleDescriptor descriptor = new ShaderModuleDescriptor()
            {
                Label = (byte*)labelPtr,
                NextInChain = (ChainedStruct*)(&wgslDescriptor)
            };

            _shaderModule = _webGpu.DeviceCreateShaderModule(_device, ref descriptor);

            SilkMarshal.Free(sourcePtr);
            SilkMarshal.Free(labelPtr);
        }

        private void InitializeFonts()
        {
            var io = ImGui.GetIO();
            io.Fonts.GetTexDataAsRGBA32(out byte* pixels, out int width, out int height, out int bytesPerPixel);

            var textureDescriptor = new TextureDescriptor
            {
                Dimension = TextureDimension.Dimension2D,
                Size = new Extent3D
                {
                    Width = (uint)width,
                    Height = (uint)height,
                    DepthOrArrayLayers = 1
                },
                SampleCount = 1,
                Format = TextureFormat.Rgba8Unorm,
                MipLevelCount = 1,
                Usage = TextureUsage.CopyDst | TextureUsage.TextureBinding
            };

            _fontTexture = _webGpu.DeviceCreateTexture(_device, ref textureDescriptor);

            var viewDescriptor = new TextureViewDescriptor
            {
                Dimension = TextureViewDimension.Dimension2D,
                Format = TextureFormat.Rgba8Unorm,
                BaseMipLevel = 0,
                MipLevelCount = 1,
                BaseArrayLayer = 0,
                ArrayLayerCount = 1,
                Aspect = TextureAspect.All
            };

            _fontView = _webGpu.TextureCreateView(_fontTexture, ref viewDescriptor);

            var imageCopyTexture = new ImageCopyTexture
            {
                Texture = _fontTexture,
                MipLevel = 0,
                Aspect = TextureAspect.All
            };

            var dataLayout = new TextureDataLayout
            {
                Offset = 0,
                BytesPerRow = (uint)(width * bytesPerPixel),
                RowsPerImage = (uint)height
            };

            var extent = new Extent3D
            {
                Width = (uint)width,
                Height = (uint)height,
                DepthOrArrayLayers = 1
            };

            _webGpu.QueueWriteTexture(
                _queue,
                &imageCopyTexture,
                pixels,
                (UIntPtr)(width * height * bytesPerPixel),
                ref dataLayout,
                ref extent);

            var samplerDescriptor = new SamplerDescriptor
            {
                MinFilter = FilterMode.Linear,
                MagFilter = FilterMode.Linear,
                MipmapFilter = MipmapFilterMode.Linear,
                AddressModeU = AddressMode.Repeat,
                AddressModeV = AddressMode.Repeat,
                AddressModeW = AddressMode.Repeat,
                MaxAnisotropy = 1
            };

            _fontSampler = _webGpu.DeviceCreateSampler(_device, ref samplerDescriptor);

            io.Fonts.SetTexID((IntPtr)_fontView);
        }

        private void InitializeBindGroupLayouts()
        {
            var commonEntries = stackalloc BindGroupLayoutEntry[2];

            commonEntries[0].Binding = 0;
            commonEntries[0].Visibility = ShaderStage.Vertex | ShaderStage.Fragment;
            commonEntries[0].Buffer.Type = BufferBindingType.Uniform;

            commonEntries[1].Binding = 1;
            commonEntries[1].Visibility = ShaderStage.Fragment;
            commonEntries[1].Sampler.Type = SamplerBindingType.Filtering;

            var commonLayoutDescriptor = new BindGroupLayoutDescriptor
            {
                EntryCount = 2,
                Entries = commonEntries
            };

            _commonBindGroupLayout = _webGpu.DeviceCreateBindGroupLayout(_device, ref commonLayoutDescriptor);

            var imageEntry = stackalloc BindGroupLayoutEntry[1];

            imageEntry[0].Binding = 0;
            imageEntry[0].Visibility = ShaderStage.Fragment;
            imageEntry[0].Texture.SampleType = TextureSampleType.Float;
            imageEntry[0].Texture.ViewDimension = TextureViewDimension.Dimension2D;

            var imageLayoutDescriptor = new BindGroupLayoutDescriptor
            {
                EntryCount = 1,
                Entries = imageEntry
            };

            _imageBindGroupLayout = _webGpu.DeviceCreateBindGroupLayout(_device, ref imageLayoutDescriptor);
        }

        private void InitializePipeline()
        {
            var layouts = stackalloc BindGroupLayout*[2];
            layouts[0] = _commonBindGroupLayout;
            layouts[1] = _imageBindGroupLayout;

            var pipelineLayoutDescriptor = new PipelineLayoutDescriptor
            {
                BindGroupLayoutCount = 2,
                BindGroupLayouts = layouts
            };

            var pipelineLayout = _webGpu.DeviceCreatePipelineLayout(_device, ref pipelineLayoutDescriptor);

            var vertexEntryPtr = SilkMarshal.StringToPtr("vs_main");
            var fragmentEntryPtr = SilkMarshal.StringToPtr("fs_main");

            var vertexAttributes = stackalloc VertexAttribute[3];

            vertexAttributes[0].Format = VertexFormat.Float32x2;
            vertexAttributes[0].Offset = (ulong)Marshal.OffsetOf<ImDrawVert>(nameof(ImDrawVert.pos));
            vertexAttributes[0].ShaderLocation = 0;

            vertexAttributes[1].Format = VertexFormat.Float32x2;
            vertexAttributes[1].Offset = (ulong)Marshal.OffsetOf<ImDrawVert>(nameof(ImDrawVert.uv));
            vertexAttributes[1].ShaderLocation = 1;

            vertexAttributes[2].Format = VertexFormat.Unorm8x4;
            vertexAttributes[2].Offset = (ulong)Marshal.OffsetOf<ImDrawVert>(nameof(ImDrawVert.col));
            vertexAttributes[2].ShaderLocation = 2;

            var vertexBufferLayout = new VertexBufferLayout
            {
                ArrayStride = (ulong)sizeof(ImDrawVert),
                StepMode = VertexStepMode.Vertex,
                AttributeCount = 3,
                Attributes = vertexAttributes
            };

            var blendState = new BlendState();
            blendState.Alpha.Operation = BlendOperation.Add;
            blendState.Alpha.SrcFactor = BlendFactor.One;
            blendState.Alpha.DstFactor = BlendFactor.OneMinusSrcAlpha;
            blendState.Color.Operation = BlendOperation.Add;
            blendState.Color.SrcFactor = BlendFactor.SrcAlpha;
            blendState.Color.DstFactor = BlendFactor.OneMinusSrcAlpha;

            var colorTargetState = new ColorTargetState
            {
                Blend = &blendState,
                Format = _swapChainFormat,
                WriteMask = ColorWriteMask.All
            };

            var fragmentState = new FragmentState
            {
                Module = _shaderModule,
                EntryPoint = (byte*)fragmentEntryPtr,
                TargetCount = 1,
                Targets = &colorTargetState
            };

            var renderPipelineDescriptor = new RenderPipelineDescriptor
            {
                Vertex = new VertexState
                {
                    Module = _shaderModule,
                    EntryPoint = (byte*)vertexEntryPtr,
                    BufferCount = 1,
                    Buffers = &vertexBufferLayout
                },
                Primitive = new PrimitiveState
                {
                    Topology = PrimitiveTopology.TriangleList,
                    StripIndexFormat = IndexFormat.Undefined,
                    FrontFace = FrontFace.Ccw,
                    CullMode = CullMode.None
                },
                Multisample = new MultisampleState
                {
                    Count = 1,
                    Mask = ~0u,
                    AlphaToCoverageEnabled = false
                },
                Fragment = &fragmentState,
                Layout = pipelineLayout
            };

            if (_depthFormat.HasValue)
            {
                var depthStencilState = new DepthStencilState
                {
                    Format = _depthFormat.Value,
                    DepthWriteEnabled = false,
                    DepthCompare = CompareFunction.Always,
                    StencilFront = new StencilFaceState
                    {
                        Compare = CompareFunction.Always
                    },
                    StencilBack = new StencilFaceState
                    {
                        Compare = CompareFunction.Always
                    }
                };

                renderPipelineDescriptor.DepthStencil = &depthStencilState;
            }

            _renderPipeline = _webGpu.DeviceCreateRenderPipeline(_device, ref renderPipelineDescriptor);

            SilkMarshal.Free(vertexEntryPtr);
            SilkMarshal.Free(fragmentEntryPtr);
            _webGpu.PipelineLayoutRelease(pipelineLayout);
        }

        private void InitializeUniformBuffer()
        {
            var size = (ulong)Align(sizeof(ImGuiUniforms), 16);

            var descriptor = new BufferDescriptor
            {
                Usage = BufferUsage.CopyDst | BufferUsage.Uniform,
                Size = size
            };

            _uniformsBuffer = _webGpu.DeviceCreateBuffer(_device, ref descriptor);
        }

        private void InitializeBindGroups()
        {
            var entries = stackalloc BindGroupEntry[2];

            entries[0].Binding = 0;
            entries[0].Buffer = _uniformsBuffer;
            entries[0].Offset = 0;
            entries[0].Size = (ulong)Align(sizeof(ImGuiUniforms), 16);
            entries[0].Sampler = null;

            entries[1].Binding = 1;
            entries[1].Buffer = null;
            entries[1].Offset = 0;
            entries[1].Size = 0;
            entries[1].Sampler = _fontSampler;

            var descriptor = new BindGroupDescriptor
            {
                Layout = _commonBindGroupLayout,
                EntryCount = 2,
                Entries = entries
            };

            _commonBindGroup = _webGpu.DeviceCreateBindGroup(_device, ref descriptor);

            BindTextureView(_fontView);
        }

        private void OnKeyUp(IKeyboard keyboard, Key key, int scancode)
        {
            _keyEvents[key] = false;
        }

        private void OnKeyDown(IKeyboard keyboard, Key key, int scancode)
        {
            _keyEvents[key] = true;
        }

        private void OnKeyChar(IKeyboard keyboard, char character)
        {
            _pressedChars.Add(character);
        }

        private void SetPerFrameData(float deltaSeconds)
        {
            var io = ImGui.GetIO();
            var windowSize = _view.Size;

            io.DisplaySize = new Vector2(windowSize.X, windowSize.Y);

            if (windowSize.X > 0 && windowSize.Y > 0)
            {
                io.DisplayFramebufferScale = new Vector2(
                    _view.FramebufferSize.X / (float)windowSize.X,
                    _view.FramebufferSize.Y / (float)windowSize.Y);
            }

            io.DeltaTime = deltaSeconds;
        }

        private static int Align(int value, int alignment)
        {
            return (value + alignment - 1) / alignment * alignment;
        }

        public BindGroup* BindTextureView(TextureView* view)
        {
            var id = (IntPtr)view;

            if (_viewsById.TryGetValue(id, out IntPtr existingBindGroup))
            {
                return (BindGroup*)existingBindGroup;
            }

            var entry = new BindGroupEntry
            {
                Binding = 0,
                Buffer = null,
                Offset = 0,
                Size = 0,
                Sampler = null,
                TextureView = view
            };

            var descriptor = new BindGroupDescriptor
            {
                Layout = _imageBindGroupLayout,
                EntryCount = 1,
                Entries = &entry
            };

            var bindGroup = _webGpu.DeviceCreateBindGroup(_device, ref descriptor);
            _viewsById[id] = (IntPtr)bindGroup;

            return bindGroup;
        }


        public void Update(float deltaSeconds)
        {
            SetPerFrameData(deltaSeconds);
            UpdateInput();
            ImGui.NewFrame();
        }

        public void Render(RenderPassEncoder* encoder)
        {
            ImGui.Render();
            RenderDrawData(encoder);
        }

        private void UpdateInput()
        {
            var io = ImGui.GetIO();

            var mouse = _inputContext.Mice[0];

            io.MouseDown[0] = mouse.IsButtonPressed(MouseButton.Left);
            io.MouseDown[1] = mouse.IsButtonPressed(MouseButton.Right);
            io.MouseDown[2] = mouse.IsButtonPressed(MouseButton.Middle);

            io.MousePos = new Vector2(mouse.Position.X, mouse.Position.Y);

            var wheel = mouse.ScrollWheels[0];
            io.MouseWheel = wheel.Y;
            io.MouseWheelH = wheel.X;

            foreach (var character in _pressedChars)
            {
                io.AddInputCharacter(character);
            }
            _pressedChars.Clear();

            foreach (var keyEvent in _keyEvents)
            {
                if (TryMapKey(keyEvent.Key, out ImGuiKey imguiKey))
                {
                    io.AddKeyEvent(imguiKey, keyEvent.Value);
                }
            }
            _keyEvents.Clear();
        }

        private static bool TryMapKey(Key key, out ImGuiKey imguiKey)
        {
            imguiKey = key switch
            {
                Key.Backspace => ImGuiKey.Backspace,
                Key.Tab => ImGuiKey.Tab,
                Key.Enter => ImGuiKey.Enter,
                Key.CapsLock => ImGuiKey.CapsLock,
                Key.Escape => ImGuiKey.Escape,
                Key.Space => ImGuiKey.Space,
                Key.PageUp => ImGuiKey.PageUp,
                Key.PageDown => ImGuiKey.PageDown,
                Key.End => ImGuiKey.End,
                Key.Home => ImGuiKey.Home,
                Key.Left => ImGuiKey.LeftArrow,
                Key.Right => ImGuiKey.RightArrow,
                Key.Up => ImGuiKey.UpArrow,
                Key.Down => ImGuiKey.DownArrow,
                Key.PrintScreen => ImGuiKey.PrintScreen,
                Key.Insert => ImGuiKey.Insert,
                Key.Delete => ImGuiKey.Delete,
                Key.Number0 => ImGuiKey._0,
                Key.Number1 => ImGuiKey._1,
                Key.Number2 => ImGuiKey._2,
                Key.Number3 => ImGuiKey._3,
                Key.Number4 => ImGuiKey._4,
                Key.Number5 => ImGuiKey._5,
                Key.Number6 => ImGuiKey._6,
                Key.Number7 => ImGuiKey._7,
                Key.Number8 => ImGuiKey._8,
                Key.Number9 => ImGuiKey._9,
                Key.A => ImGuiKey.A,
                Key.B => ImGuiKey.B,
                Key.C => ImGuiKey.C,
                Key.D => ImGuiKey.D,
                Key.E => ImGuiKey.E,
                Key.F => ImGuiKey.F,
                Key.G => ImGuiKey.G,
                Key.H => ImGuiKey.H,
                Key.I => ImGuiKey.I,
                Key.J => ImGuiKey.J,
                Key.K => ImGuiKey.K,
                Key.L => ImGuiKey.L,
                Key.M => ImGuiKey.M,
                Key.N => ImGuiKey.N,
                Key.O => ImGuiKey.O,
                Key.P => ImGuiKey.P,
                Key.Q => ImGuiKey.Q,
                Key.R => ImGuiKey.R,
                Key.S => ImGuiKey.S,
                Key.T => ImGuiKey.T,
                Key.U => ImGuiKey.U,
                Key.V => ImGuiKey.V,
                Key.W => ImGuiKey.W,
                Key.X => ImGuiKey.X,
                Key.Y => ImGuiKey.Y,
                Key.Z => ImGuiKey.Z,
                Key.Keypad0 => ImGuiKey.Keypad0,
                Key.Keypad1 => ImGuiKey.Keypad1,
                Key.Keypad2 => ImGuiKey.Keypad2,
                Key.Keypad3 => ImGuiKey.Keypad3,
                Key.Keypad4 => ImGuiKey.Keypad4,
                Key.Keypad5 => ImGuiKey.Keypad5,
                Key.Keypad6 => ImGuiKey.Keypad6,
                Key.Keypad7 => ImGuiKey.Keypad7,
                Key.Keypad8 => ImGuiKey.Keypad8,
                Key.Keypad9 => ImGuiKey.Keypad9,
                Key.KeypadMultiply => ImGuiKey.KeypadMultiply,
                Key.KeypadAdd => ImGuiKey.KeypadAdd,
                Key.KeypadSubtract => ImGuiKey.KeypadSubtract,
                Key.KeypadDecimal => ImGuiKey.KeypadDecimal,
                Key.KeypadDivide => ImGuiKey.KeypadDivide,
                Key.KeypadEqual => ImGuiKey.KeypadEqual,
                Key.F1 => ImGuiKey.F1,
                Key.F2 => ImGuiKey.F2,
                Key.F3 => ImGuiKey.F3,
                Key.F4 => ImGuiKey.F4,
                Key.F5 => ImGuiKey.F5,
                Key.F6 => ImGuiKey.F6,
                Key.F7 => ImGuiKey.F7,
                Key.F8 => ImGuiKey.F8,
                Key.F9 => ImGuiKey.F9,
                Key.F10 => ImGuiKey.F10,
                Key.F11 => ImGuiKey.F11,
                Key.F12 => ImGuiKey.F12,
                Key.NumLock => ImGuiKey.NumLock,
                Key.ScrollLock => ImGuiKey.ScrollLock,
                Key.ShiftLeft => ImGuiKey.ModShift,
                Key.ShiftRight => ImGuiKey.ModShift,
                Key.ControlLeft => ImGuiKey.ModCtrl,
                Key.ControlRight => ImGuiKey.ModCtrl,
                Key.AltLeft => ImGuiKey.ModAlt,
                Key.AltRight => ImGuiKey.ModAlt,
                Key.SuperLeft => ImGuiKey.ModSuper,
                Key.SuperRight => ImGuiKey.ModSuper,
                Key.Semicolon => ImGuiKey.Semicolon,
                Key.Equal => ImGuiKey.Equal,
                Key.Comma => ImGuiKey.Comma,
                Key.Minus => ImGuiKey.Minus,
                Key.Period => ImGuiKey.Period,
                Key.GraveAccent => ImGuiKey.GraveAccent,
                Key.LeftBracket => ImGuiKey.LeftBracket,
                Key.RightBracket => ImGuiKey.RightBracket,
                Key.Apostrophe => ImGuiKey.Apostrophe,
                Key.Slash => ImGuiKey.Slash,
                Key.BackSlash => ImGuiKey.Backslash,
                Key.Pause => ImGuiKey.Pause,
                _ => ImGuiKey.None
            };

            return imguiKey != ImGuiKey.None;
        }

        private void RenderDrawData(RenderPassEncoder* encoder)
        {
            var drawData = ImGui.GetDrawData();
            drawData.ScaleClipRects(ImGui.GetIO().DisplayFramebufferScale);

            int framebufferWidth = (int)(drawData.DisplaySize.X * drawData.FramebufferScale.X);
            int framebufferHeight = (int)(drawData.DisplaySize.Y * drawData.FramebufferScale.Y);

            if (framebufferWidth <= 0 || framebufferHeight <= 0)
            {
                return;
            }

            if (_windowRenderBuffers.FrameRenderBuffers == null || _windowRenderBuffers.FrameRenderBuffers.Length == 0)
            {
                _windowRenderBuffers.Index = 0;
                _windowRenderBuffers.Count = _framesInFlight;
                _windowRenderBuffers.FrameRenderBuffers = new FrameRenderBuffer[_framesInFlight];
            }

            _windowRenderBuffers.Index = (_windowRenderBuffers.Index + 1) % _windowRenderBuffers.Count;
            ref FrameRenderBuffer frameBuffer = ref _windowRenderBuffers.FrameRenderBuffers[_windowRenderBuffers.Index];

            if (drawData.TotalVtxCount > 0)
            {
                ulong vertexSize = (ulong)Align(drawData.TotalVtxCount * sizeof(ImDrawVert), 4);
                ulong indexSize = (ulong)Align(drawData.TotalIdxCount * sizeof(ushort), 4);

                EnsureBuffersCapacity(ref frameBuffer, vertexSize, indexSize);

                ImDrawVert* vertexDst = frameBuffer.VertexBufferMemory.AsPtr<ImDrawVert>();
                ushort* indexDst = frameBuffer.IndexBufferMemory.AsPtr<ushort>();

                for (int i = 0; i < drawData.CmdListsCount; i++)
                {
                    ImDrawListPtr cmdList = drawData.CmdLists[i];

                    Unsafe.CopyBlock(
                        vertexDst,
                        cmdList.VtxBuffer.Data.ToPointer(),
                        (uint)(cmdList.VtxBuffer.Size * sizeof(ImDrawVert)));

                    Unsafe.CopyBlock(
                        indexDst,
                        cmdList.IdxBuffer.Data.ToPointer(),
                        (uint)(cmdList.IdxBuffer.Size * sizeof(ushort)));

                    vertexDst += cmdList.VtxBuffer.Size;
                    indexDst += cmdList.IdxBuffer.Size;
                }

                _webGpu.QueueWriteBuffer(
                    _queue,
                    frameBuffer.VertexBufferGpu,
                    0,
                    frameBuffer.VertexBufferMemory,
                    (UIntPtr)vertexSize);

                _webGpu.QueueWriteBuffer(
                    _queue,
                    frameBuffer.IndexBufferGpu,
                    0,
                    frameBuffer.IndexBufferMemory,
                    (UIntPtr)indexSize);
            }

            var uniforms = new ImGuiUniforms
            {
                Mvp = Matrix4x4.CreateOrthographicOffCenter(
                    0f,
                    drawData.DisplaySize.X,
                    drawData.DisplaySize.Y,
                    0f,
                    -1f,
                    1f),
                Gamma = 2.0f
            };

            _webGpu.QueueWriteBuffer(
                _queue,
                _uniformsBuffer,
                0,
                &uniforms,
                (UIntPtr)sizeof(ImGuiUniforms));

            _webGpu.RenderPassEncoderSetPipeline(encoder, _renderPipeline);

            if (drawData.TotalVtxCount > 0)
            {
                _webGpu.RenderPassEncoderSetVertexBuffer(
                    encoder,
                    0,
                    frameBuffer.VertexBufferGpu,
                    0,
                    frameBuffer.VertexBufferSize);

                _webGpu.RenderPassEncoderSetIndexBuffer(
                    encoder,
                    frameBuffer.IndexBufferGpu,
                    IndexFormat.Uint16,
                    0,
                    frameBuffer.IndexBufferSize);

                uint dynamicOffset = 0;
                _webGpu.RenderPassEncoderSetBindGroup(encoder, 0, _commonBindGroup, 0, ref dynamicOffset);
            }

            _webGpu.RenderPassEncoderSetViewport(
                encoder,
                0,
                0,
                drawData.FramebufferScale.X * drawData.DisplaySize.X,
                drawData.FramebufferScale.Y * drawData.DisplaySize.Y,
                0,
                1);

            int vertexOffset = 0;
            int indexOffset = 0;

            for (int n = 0; n < drawData.CmdListsCount; n++)
            {
                ImDrawListPtr cmdList = drawData.CmdLists[n];

                for (int cmd = 0; cmd < cmdList.CmdBuffer.Size; cmd++)
                {
                    ImDrawCmdPtr drawCmd = cmdList.CmdBuffer[cmd];

                    if (drawCmd.UserCallback != IntPtr.Zero)
                    {
                        continue;
                    }

                    var textureId = drawCmd.TextureId;
                    if (textureId != IntPtr.Zero)
                    {
                        if (_viewsById.TryGetValue(textureId, out IntPtr bindGroupPtr))
                        {
                            uint offset = 0;
                            _webGpu.RenderPassEncoderSetBindGroup(
                                encoder,
                                1,
                                (BindGroup*)bindGroupPtr,
                                0,
                                ref offset);
                        }
                    }

                    Vector2 clipMin = new Vector2(drawCmd.ClipRect.X, drawCmd.ClipRect.Y);
                    Vector2 clipMax = new Vector2(drawCmd.ClipRect.Z, drawCmd.ClipRect.W);

                    if (clipMax.X <= clipMin.X || clipMax.Y <= clipMin.Y)
                    {
                        continue;
                    }

                    _webGpu.RenderPassEncoderSetScissorRect(
                        encoder,
                        (uint)clipMin.X,
                        (uint)clipMin.Y,
                        (uint)(clipMax.X - clipMin.X),
                        (uint)(clipMax.Y - clipMin.Y));

                    _webGpu.RenderPassEncoderDrawIndexed(
                        encoder,
                        drawCmd.ElemCount,
                        1,
                        (uint)(indexOffset + (int)drawCmd.IdxOffset),
                        (int)(vertexOffset + (int)drawCmd.VtxOffset),
                        0);
                }

                vertexOffset += cmdList.VtxBuffer.Size;
                indexOffset += cmdList.IdxBuffer.Size;
            }
        }

        private void EnsureBuffersCapacity(ref FrameRenderBuffer frameBuffer, ulong vertexSize, ulong indexSize)
        {
            if (frameBuffer.VertexBufferGpu == null || frameBuffer.VertexBufferSize < vertexSize)
            {
                if (frameBuffer.VertexBufferMemory != null)
                {
                    frameBuffer.VertexBufferMemory.Dispose();
                }

                if (frameBuffer.VertexBufferGpu != null)
                {
                    _webGpu.BufferDestroy(frameBuffer.VertexBufferGpu);
                    _webGpu.BufferRelease(frameBuffer.VertexBufferGpu);
                }

                var descriptor = new BufferDescriptor
                {
                    Size = vertexSize,
                    Usage = BufferUsage.Vertex | BufferUsage.CopyDst
                };

                frameBuffer.VertexBufferGpu = _webGpu.DeviceCreateBuffer(_device, ref descriptor);
                frameBuffer.VertexBufferSize = vertexSize;
                frameBuffer.VertexBufferMemory = GlobalMemory.Allocate((int)vertexSize);
            }

            if (frameBuffer.IndexBufferGpu == null || frameBuffer.IndexBufferSize < indexSize)
            {
                if (frameBuffer.IndexBufferMemory != null)
                {
                    frameBuffer.IndexBufferMemory.Dispose();
                }

                if (frameBuffer.IndexBufferGpu != null)
                {
                    _webGpu.BufferDestroy(frameBuffer.IndexBufferGpu);
                    _webGpu.BufferRelease(frameBuffer.IndexBufferGpu);
                }

                var descriptor = new BufferDescriptor
                {
                    Size = indexSize,
                    Usage = BufferUsage.Index | BufferUsage.CopyDst
                };

                frameBuffer.IndexBufferGpu = _webGpu.DeviceCreateBuffer(_device, ref descriptor);
                frameBuffer.IndexBufferSize = indexSize;
                frameBuffer.IndexBufferMemory = GlobalMemory.Allocate((int)indexSize);
            }
        }

        public void Dispose()
        {
            _inputContext.Keyboards[0].KeyUp -= OnKeyUp;
            _inputContext.Keyboards[0].KeyDown -= OnKeyDown;
            _inputContext.Keyboards[0].KeyChar -= OnKeyChar;

            if (_windowRenderBuffers.FrameRenderBuffers != null)
            {
                foreach (var buffer in _windowRenderBuffers.FrameRenderBuffers)
                {
                    if (buffer.VertexBufferGpu != null)
                    {
                        _webGpu.BufferDestroy(buffer.VertexBufferGpu);
                        _webGpu.BufferRelease(buffer.VertexBufferGpu);
                    }

                    if (buffer.IndexBufferGpu != null)
                    {
                        _webGpu.BufferDestroy(buffer.IndexBufferGpu);
                        _webGpu.BufferRelease(buffer.IndexBufferGpu);
                    }

                    buffer.VertexBufferMemory?.Dispose();
                    buffer.IndexBufferMemory?.Dispose();
                }
            }

            foreach (var bindGroup in _viewsById)
            {
                _webGpu.BindGroupRelease((BindGroup*)bindGroup.Value);
            }
            _viewsById.Clear();

            if (_commonBindGroup != null)
            {
                _webGpu.BindGroupRelease(_commonBindGroup);
            }

            if (_uniformsBuffer != null)
            {
                _webGpu.BufferDestroy(_uniformsBuffer);
                _webGpu.BufferRelease(_uniformsBuffer);
            }

            if (_renderPipeline != null)
            {
                _webGpu.RenderPipelineRelease(_renderPipeline);
            }

            if (_commonBindGroupLayout != null)
            {
                _webGpu.BindGroupLayoutRelease(_commonBindGroupLayout);
            }

            if (_imageBindGroupLayout != null)
            {
                _webGpu.BindGroupLayoutRelease(_imageBindGroupLayout);
            }

            if (_fontSampler != null)
            {
                _webGpu.SamplerRelease(_fontSampler);
            }

            if (_fontView != null)
            {
                _webGpu.TextureViewRelease(_fontView);
            }

            if (_fontTexture != null)
            {
                _webGpu.TextureDestroy(_fontTexture);
                _webGpu.TextureRelease(_fontTexture);
            }

            if (_shaderModule != null)
            {
                _webGpu.ShaderModuleRelease(_shaderModule);
            }

            ImGui.DestroyContext();
        }

    }
}
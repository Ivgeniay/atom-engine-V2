using Silk.NET.Core.Native;
using Silk.NET.WebGPU;

namespace AtomEngineV2.ImGuiBackend
{
    internal unsafe struct FrameRenderBuffer
    {
        public ulong VertexBufferSize;
        public ulong IndexBufferSize;
        public Buffer* VertexBufferGpu;
        public Buffer* IndexBufferGpu;
        public GlobalMemory VertexBufferMemory;
        public GlobalMemory IndexBufferMemory;
    };
}
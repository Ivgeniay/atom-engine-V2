using System.Runtime.InteropServices;
using System.Numerics;

namespace AtomEngineV2.ImGuiBackend
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct ImGuiUniforms
    {
        public Matrix4x4 Mvp;
        public float Gamma;
    }
}
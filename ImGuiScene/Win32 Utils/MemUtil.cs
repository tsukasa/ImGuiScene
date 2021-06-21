using System;
using System.Runtime.InteropServices;

namespace ImGuiScene
{
    public static class MemUtil
    {
        public static IntPtr ToPointer<T>(T data)
        {
            IntPtr result = IntPtr.Zero;
            result = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
            Marshal.StructureToPtr(data, result, false);
            return result;
        }
    }
}

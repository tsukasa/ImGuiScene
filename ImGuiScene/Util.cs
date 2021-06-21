using Silk.NET.OpenGL;

namespace ImGuiScene
{
    internal class Util
    {
        private static GL gl;

        public static GL Gl
        {
            get
            {
                if (gl == default)
                {
                    var window = Silk.NET.Windowing.Window.GetView();
                    return gl = new GL(window.GLContext);

                }
                return gl;
            }
        }
    }
}

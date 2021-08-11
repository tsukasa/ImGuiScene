using ImGuiNET;
using System;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using PInvoke;

namespace ImGuiScene
{
    // largely a port of https://github.com/ocornut/imgui/blob/master/examples/imgui_impl_win32.cpp, though some changes
    // and wndproc hooking
    public unsafe class ImGui_Input_Impl_Direct : IImGuiInputHandler
    {
        private long _lastTime;

        private IntPtr _platformNamePtr;
        private IntPtr _iniPathPtr;
        private IntPtr _classNamePtr;
        private IntPtr _hWnd;

        private User32.WndProc _wndProcDelegate;
        private IntPtr _wndProcPtr;
        private IntPtr _oldWndProcPtr;
        private bool _imguiMouseIsDown;

        // private ImGuiMouseCursor _oldCursor = ImGuiMouseCursor.None;
        private IntPtr[] _cursors;

        public bool UpdateCursor { get; set; } = true;

        public unsafe ImGui_Input_Impl_Direct(IntPtr hWnd)
        {
            _hWnd = hWnd;

            // hook wndproc
            // have to hold onto the delegate to keep it in memory for unmanaged code
            _wndProcDelegate = WndProcDetour;
            _wndProcPtr = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
            _oldWndProcPtr = Win32.SetWindowLongPtr(_hWnd, WindowLongType.GWL_WNDPROC, _wndProcPtr);

            var io = ImGui.GetIO();

            io.BackendFlags |= ImGuiBackendFlags.HasMouseCursors |
                               ImGuiBackendFlags.HasSetMousePos |
                               ImGuiBackendFlags.RendererHasViewports |
                               ImGuiBackendFlags.PlatformHasViewports;

            _platformNamePtr = Marshal.StringToHGlobalAnsi("imgui_impl_win32_c#");
            io.NativePtr->BackendPlatformName = (byte*)_platformNamePtr.ToPointer();

            ImGuiViewportPtr mainViewport = ImGui.GetMainViewport();
            mainViewport.PlatformHandle = mainViewport.PlatformHandleRaw = hWnd;
            if (io.ConfigFlags.HasFlag(ImGuiConfigFlags.ViewportsEnable))
                ImGui_ImplWin32_InitPlatformInterface();

            io.KeyMap[(int)ImGuiKey.Tab] = (int)VirtualKey.Tab;
            io.KeyMap[(int)ImGuiKey.LeftArrow] = (int)VirtualKey.Left;
            io.KeyMap[(int)ImGuiKey.RightArrow] = (int)VirtualKey.Right;
            io.KeyMap[(int)ImGuiKey.UpArrow] = (int)VirtualKey.Up;
            io.KeyMap[(int)ImGuiKey.DownArrow] = (int)VirtualKey.Down;
            io.KeyMap[(int)ImGuiKey.PageUp] = (int)VirtualKey.Prior;
            io.KeyMap[(int)ImGuiKey.PageDown] = (int)VirtualKey.Next;
            io.KeyMap[(int)ImGuiKey.Home] = (int)VirtualKey.Home;
            io.KeyMap[(int)ImGuiKey.End] = (int)VirtualKey.End;
            io.KeyMap[(int)ImGuiKey.Insert] = (int)VirtualKey.Insert;
            io.KeyMap[(int)ImGuiKey.Delete] = (int)VirtualKey.Delete;
            io.KeyMap[(int)ImGuiKey.Backspace] = (int)VirtualKey.Back;
            io.KeyMap[(int)ImGuiKey.Space] = (int)VirtualKey.Space;
            io.KeyMap[(int)ImGuiKey.Enter] = (int)VirtualKey.Return;
            io.KeyMap[(int)ImGuiKey.Escape] = (int)VirtualKey.Escape;
            // same keycode, lparam is different.  Not sure if this will cause dupe events or not
            io.KeyMap[(int)ImGuiKey.KeyPadEnter] = (int)VirtualKey.Return;
            io.KeyMap[(int)ImGuiKey.A] = (int)VirtualKey.A;
            io.KeyMap[(int)ImGuiKey.C] = (int)VirtualKey.C;
            io.KeyMap[(int)ImGuiKey.V] = (int)VirtualKey.V;
            io.KeyMap[(int)ImGuiKey.X] = (int)VirtualKey.X;
            io.KeyMap[(int)ImGuiKey.Y] = (int)VirtualKey.Y;
            io.KeyMap[(int)ImGuiKey.Z] = (int)VirtualKey.Z;

            _cursors = new IntPtr[8];
            _cursors[(int)ImGuiMouseCursor.Arrow] = Win32.LoadCursor(IntPtr.Zero, Cursor.IDC_ARROW);
            _cursors[(int)ImGuiMouseCursor.TextInput] = Win32.LoadCursor(IntPtr.Zero, Cursor.IDC_IBEAM);
            _cursors[(int)ImGuiMouseCursor.ResizeAll] = Win32.LoadCursor(IntPtr.Zero, Cursor.IDC_SIZEALL);
            _cursors[(int)ImGuiMouseCursor.ResizeEW] = Win32.LoadCursor(IntPtr.Zero, Cursor.IDC_SIZEWE);
            _cursors[(int)ImGuiMouseCursor.ResizeNS] = Win32.LoadCursor(IntPtr.Zero, Cursor.IDC_SIZENS);
            _cursors[(int)ImGuiMouseCursor.ResizeNESW] = Win32.LoadCursor(IntPtr.Zero, Cursor.IDC_SIZENESW);
            _cursors[(int)ImGuiMouseCursor.ResizeNWSE] = Win32.LoadCursor(IntPtr.Zero, Cursor.IDC_SIZENWSE);
            _cursors[(int)ImGuiMouseCursor.Hand] = Win32.LoadCursor(IntPtr.Zero, Cursor.IDC_HAND);
        }

        public bool IsImGuiCursor(IntPtr hCursor)
        {
            return _cursors?.Contains(hCursor) ?? false;
        }

        public void NewFrame(int targetWidth, int targetHeight)
        {
            var io = ImGui.GetIO();

            io.DisplaySize.X = targetWidth;
            io.DisplaySize.Y = targetHeight;
            io.DisplayFramebufferScale.X = 1f;
            io.DisplayFramebufferScale.Y = 1f;

            var frequency = Stopwatch.Frequency;
            var currentTime = Stopwatch.GetTimestamp();
            io.DeltaTime = _lastTime > 0 ? (float)((double)(currentTime - _lastTime) / frequency) : 1f / 60;
            _lastTime = currentTime;

            io.KeyCtrl = (Win32.GetKeyState(VirtualKey.Control) & 0x8000) != 0;
            io.KeyShift = (Win32.GetKeyState(VirtualKey.Shift) & 0x8000) != 0;
            io.KeyAlt = (Win32.GetKeyState(VirtualKey.Menu) & 0x8000) != 0;
            io.KeySuper = false;

            UpdateMousePos();

            // TODO: need to figure out some way to unify all this
            // The bottom case works(?) if the caller hooks SetCursor, but otherwise causes fps issues
            // The top case more or less works if we use ImGui's software cursor (and ideally hide the
            // game's hardware cursor)
            // It would be nice if hooking WM_SETCURSOR worked as it 'should' so that external hooking
            // wasn't necessary

            // this is what imgui's example does, but it doesn't seem to work for us
            // this could be a timing issue.. or their logic could just be wrong for many applications
            //var cursor = io.MouseDrawCursor ? ImGuiMouseCursor.None : ImGui.GetMouseCursor();
            //if (_oldCursor != cursor)
            //{
            //    _oldCursor = cursor;
            //    UpdateMouseCursor();
            //}

            // hacky attempt to make cursors work how I think they 'should'
            if ((io.WantCaptureMouse || io.MouseDrawCursor) && UpdateCursor)
            {
                UpdateMouseCursor();
            }

            // From ImGui's FAQ:
            // Note: Text input widget releases focus on "Return KeyDown", so the subsequent "Return KeyUp" event
            // that your application receive will typically have io.WantCaptureKeyboard == false. Depending on your
            // application logic it may or not be inconvenient.
            //
            // With how the local wndproc works, this causes the key up event to be missed when exiting ImGui text entry
            // (eg, from hitting enter or escape.  There may be other ways as well)
            // This then causes the key to appear 'stuck' down, which breaks subsequent attempts to use the input field.
            // This is something of a brute force fix that basically makes key up events irrelevant
            // Holding a key will send repeated key down events and (re)set these where appropriate, so this should be ok.
            if (!io.WantTextInput)
            {
                for (int i = 0; i < io.KeysDown.Count; i++)
                {
                    io.KeysDown[i] = false;
                }
            }

            // Similar issue seen with overlapping mouse clicks
            // eg, right click and hold on imgui window, drag off, left click and hold
            //   release right click, release left click -> right click was 'stuck' and imgui
            //   would become unresponsive
            if (!io.WantCaptureMouse)
            {
                for (int i = 0; i < io.MouseDown.Count; i++)
                {
                    io.MouseDown[i] = false;
                }
            }
        }

        public void SetIniPath(string iniPath)
        {
            // TODO: error/messaging when trying to set after first render?
            if (iniPath != null)
            {
                if (_iniPathPtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_iniPathPtr);
                }

                _iniPathPtr = Marshal.StringToHGlobalAnsi(iniPath);
                unsafe
                {
                    ImGui.GetIO().NativePtr->IniFilename = (byte*)_iniPathPtr.ToPointer();
                }
            }
        }

        private void UpdateMousePos()
        {
            var io = ImGui.GetIO();

            // Depending on if Viewports are enabled, we have to change how we process
            // the cursor position. If viewports are enabled, we pass the absolute cursor
            // position to ImGui. Otherwise, we use the old method of passing client-local
            // mouse position to ImGui.
            if (io.ConfigFlags.HasFlag(ImGuiConfigFlags.ViewportsEnable))
            {
                if (io.WantSetMousePos)
                {
                    Win32.SetCursorPos((int)io.MousePos.X, (int)io.MousePos.Y);
                }

                if (Win32.GetCursorPos(out Win32.POINT pt))
                {
                    io.MousePos.X = pt.X;
                    io.MousePos.Y = pt.Y;
                }
                else
                {
                    io.MousePos.X = float.MinValue;
                    io.MousePos.Y = float.MinValue;
                }
            }
            else
            {
                if (io.WantSetMousePos)
                {
                    var pos = new Win32.POINT { X = (int)io.MousePos.X, Y = (int)io.MousePos.Y };
                    Win32.ClientToScreen(_hWnd, ref pos);
                    Win32.SetCursorPos(pos.X, pos.Y);
                }

                if (Win32.GetCursorPos(out Win32.POINT pt) && Win32.ScreenToClient(_hWnd, ref pt))
                {
                    io.MousePos.X = pt.X;
                    io.MousePos.Y = pt.Y;
                }
                else
                {
                    io.MousePos.X = float.MinValue;
                    io.MousePos.Y = float.MinValue;
                }
            }
        }

        // TODO This is kind of unnecessary unless we REALLY want viewport hovered support
        // It seems to mess with the mouse and get it stuck a lot. Do not know why
        // private void UpdateMousePos() {
        //     ImGuiIOPtr io = ImGui.GetIO();
        //
        //     // Set OS mouse position if requested (rarely used, only when ImGuiConfigFlags_NavEnableSetMousePos is enabled by user)
        //     // (When multi-viewports are enabled, all imgui positions are same as OS positions)
        //     if (io.WantSetMousePos) {
        //         POINT pos = new POINT() {x = (int) io.MousePos.X, y = (int) io.MousePos.Y};
        //         if ((io.ConfigFlags & ImGuiConfigFlags.ViewportsEnable) == 0)
        //             User32.ClientToScreen(_hWnd, ref pos);
        //         User32.SetCursorPos(pos.x, pos.y);
        //     }
        //
        //     io.MousePos = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
        //     io.MouseHoveredViewport = 0;
        //
        //     // Set imgui mouse position
        //     if (!User32.GetCursorPos(out POINT mouseScreenPos))
        //         return;
        //     IntPtr focusedHwnd = User32.GetForegroundWindow();
        //     if (focusedHwnd != IntPtr.Zero) {
        //         if (User32.IsChild(focusedHwnd, _hWnd))
        //             focusedHwnd = _hWnd;
        //         if ((io.ConfigFlags & ImGuiConfigFlags.ViewportsEnable) == ImGuiConfigFlags.ViewportsEnable) {
        //             // Multi-viewport mode: mouse position in OS absolute coordinates (io.MousePos is (0,0) when the mouse is on the upper-left of the primary monitor)
        //             // This is the position you can get with GetCursorPos(). In theory adding viewport->Pos is also the reverse operation of doing ScreenToClient().
        //             ImGuiViewportPtr viewport = ImGui.FindViewportByPlatformHandle(focusedHwnd);
        //             unsafe {
        //                 if (viewport.NativePtr != null)
        //                     io.MousePos = new Vector2(mouseScreenPos.x, mouseScreenPos.y);
        //             }
        //         } else {
        //             // Single viewport mode: mouse position in client window coordinates (io.MousePos is (0,0) when the mouse is on the upper-left corner of the app window.)
        //             // This is the position you can get with GetCursorPos() + ScreenToClient() or from WM_MOUSEMOVE.
        //             if (focusedHwnd == _hWnd) {
        //                 POINT mouseClientPos = mouseScreenPos;
        //                 User32.ScreenToClient(focusedHwnd, ref mouseClientPos);
        //                 io.MousePos = new Vector2(mouseClientPos.x, mouseClientPos.y);
        //             }
        //         }
        //     }
        //
        //     // (Optional) When using multiple viewports: set io.MouseHoveredViewport to the viewport the OS mouse cursor is hovering.
        //     // Important: this information is not easy to provide and many high-level windowing library won't be able to provide it correctly, because
        //     // - This is _ignoring_ viewports with the ImGuiViewportFlags_NoInputs flag (pass-through windows).
        //     // - This is _regardless_ of whether another viewport is focused or being dragged from.
        //     // If ImGuiBackendFlags_HasMouseHoveredViewport is not set by the backend, imgui will ignore this field and infer the information by relying on the
        //     // rectangles and last focused time of every viewports it knows about. It will be unaware of foreign windows that may be sitting between or over your windows.
        //     IntPtr hovered_hwnd = User32.WindowFromPoint(mouseScreenPos);
        //     if (hovered_hwnd != IntPtr.Zero) {
        //         ImGuiViewportPtr viewport = ImGui.FindViewportByPlatformHandle(focusedHwnd);
        //         unsafe {
        //             if (viewport.NativePtr != null)
        //                 if ((viewport.Flags & ImGuiViewportFlags.NoInputs) == 0
        //                 ) // FIXME: We still get our NoInputs window with WM_NCHITTEST/HTTRANSPARENT code when decorated?
        //                     io.MouseHoveredViewport = viewport.ID;
        //         }
        //     }
        // }

        private bool UpdateMouseCursor()
        {
            var io = ImGui.GetIO();
            if (io.ConfigFlags.HasFlag(ImGuiConfigFlags.NoMouseCursorChange))
                return false;

            var cur = ImGui.GetMouseCursor();
            if (cur == ImGuiMouseCursor.None || io.MouseDrawCursor)
                Win32.SetCursor(IntPtr.Zero);
            else
                Win32.SetCursor(_cursors[(int)cur]);

            return true;
        }

        private unsafe int ProcessWndProc(IntPtr hWnd, User32.WindowMessage msg, void* wParam, void* lParam)
        {
            if (ImGui.GetCurrentContext() != IntPtr.Zero &&
                (ImGui.GetIO().WantCaptureMouse || ImGui.GetIO().WantTextInput))
            {
                var io = ImGui.GetIO();

                switch (msg)
                {
                    case User32.WindowMessage.WM_LBUTTONDOWN:
                    case User32.WindowMessage.WM_LBUTTONDBLCLK:
                    case User32.WindowMessage.WM_RBUTTONDOWN:
                    case User32.WindowMessage.WM_RBUTTONDBLCLK:
                    case User32.WindowMessage.WM_MBUTTONDOWN:
                    case User32.WindowMessage.WM_MBUTTONDBLCLK:
                    case User32.WindowMessage.WM_XBUTTONDOWN:
                    case User32.WindowMessage.WM_XBUTTONDBLCLK:
                        if (io.WantCaptureMouse)
                        {
                            var button = 0;
                            if (msg == User32.WindowMessage.WM_LBUTTONDOWN ||
                                msg == User32.WindowMessage.WM_LBUTTONDBLCLK)
                            {
                                button = 0;
                            }
                            else if (msg == User32.WindowMessage.WM_RBUTTONDOWN ||
                                     msg == User32.WindowMessage.WM_RBUTTONDBLCLK)
                            {
                                button = 1;
                            }
                            else if (msg == User32.WindowMessage.WM_MBUTTONDOWN ||
                                     msg == User32.WindowMessage.WM_MBUTTONDBLCLK)
                            {
                                button = 2;
                            }
                            else if (msg == User32.WindowMessage.WM_XBUTTONDOWN ||
                                     msg == User32.WindowMessage.WM_XBUTTONDBLCLK)
                            {
                                button = Win32.GET_XBUTTON_WPARAM((ulong)wParam) == Win32Constants.XBUTTON1
                                             ? 3
                                             : 4;
                            }

                            if (!ImGui.IsAnyMouseDown() && Win32.GetCapture() == IntPtr.Zero)
                            {
                                Win32.SetCapture(hWnd);
                            }

                            io.MouseDown[button] = true;
                            this._imguiMouseIsDown = true;
                            return 0;
                        }

                        break;
                    case User32.WindowMessage.WM_LBUTTONUP:
                    case User32.WindowMessage.WM_RBUTTONUP:
                    case User32.WindowMessage.WM_MBUTTONUP:
                    case User32.WindowMessage.WM_XBUTTONUP:
                        if (io.WantCaptureMouse && this._imguiMouseIsDown)
                        {
                            var button = 0;
                            if (msg == User32.WindowMessage.WM_LBUTTONUP)
                            {
                                button = 0;
                            }
                            else if (msg == User32.WindowMessage.WM_RBUTTONUP)
                            {
                                button = 1;
                            }
                            else if (msg == User32.WindowMessage.WM_MBUTTONUP)
                            {
                                button = 2;
                            }
                            else if (msg == User32.WindowMessage.WM_XBUTTONUP)
                            {
                                button = Win32.GET_XBUTTON_WPARAM((ulong)wParam) == Win32Constants.XBUTTON1
                                             ? 3
                                             : 4;
                            }

                            if (!ImGui.IsAnyMouseDown() && Win32.GetCapture() == hWnd)
                            {
                                Win32.ReleaseCapture();
                            }

                            io.MouseDown[button] = false;
                            this._imguiMouseIsDown = false;
                            return 0;
                        }

                        break;
                    case User32.WindowMessage.WM_MOUSEWHEEL:
                        if (io.WantCaptureMouse)
                        {
                            io.MouseWheel += (float)Win32.GET_WHEEL_DELTA_WPARAM((ulong)wParam) /
                                             (float)Win32Constants.WHEEL_DELTA;
                            return 0;
                        }

                        break;
                    case User32.WindowMessage.WM_MOUSEHWHEEL:
                        if (io.WantCaptureMouse)
                        {
                            io.MouseWheelH += (float)Win32.GET_WHEEL_DELTA_WPARAM((ulong)wParam) /
                                              (float)Win32Constants.WHEEL_DELTA;
                            return 0;
                        }

                        break;
                    case User32.WindowMessage.WM_KEYDOWN:
                    case User32.WindowMessage.WM_SYSKEYDOWN:
                        if (io.WantTextInput)
                        {
                            if ((ulong)wParam < 256)
                            {
                                io.KeysDown[(int)wParam] = true;
                            }

                            return 0;
                        }

                        break;
                    case User32.WindowMessage.WM_KEYUP:
                    case User32.WindowMessage.WM_SYSKEYUP:
                        if (io.WantTextInput)
                        {
                            if ((ulong)wParam < 256)
                            {
                                io.KeysDown[(int)wParam] = false;
                            }

                            return 0;
                        }

                        break;
                    case User32.WindowMessage.WM_CHAR:
                        if (io.WantTextInput)
                        {
                            io.AddInputCharacter((uint)wParam);
                            return 0;
                        }

                        break;
                    // this never seemed to work reasonably, but I'll leave it for now
                    case User32.WindowMessage.WM_SETCURSOR:
                        if (io.WantCaptureMouse)
                        {
                            if (Win32.LOWORD((ulong)lParam) == Win32Constants.HTCLIENT && UpdateMouseCursor())
                            {
                                // this message returns 1 to block further processing
                                // because consistency is no fun
                                return 1;
                            }
                        }

                        break;
                    // TODO: Decode why IME is miserable
                    // case User32.WindowMessage.WM_IME_NOTIFY:
                    // return HandleImeMessage(hWnd, (long) wParam, (long) lParam);
                    default:
                        break;
                }
            }

            // We did not produce a result - return -1
            return -1;
        }

        // TODO: Decode why IME is miserable
        // private int HandleImeMessage(IntPtr hWnd, long wParam, long lParam) {
        //
        //     int result = -1;
        //     // if (io.WantCaptureKeyboard)
        //     result = (int) User32.DefWindowProc(hWnd, User32.WindowMessage.WM_IME_NOTIFY, (IntPtr) wParam, (IntPtr) lParam);
        //     System.Diagnostics.Debug.WriteLine($"ime command {(Win32.ImeCommand) wParam} result {result}");
        //
        //     return result;
        // }

        private unsafe IntPtr WndProcDetour(IntPtr hWnd, User32.WindowMessage msg, void* wParam, void* lParam)
        {
            // Attempt to process the result of this window message
            // We will return the result here if we consider the message handled
            var processResult = ProcessWndProc(hWnd, msg, wParam, lParam);

            if (processResult != -1) return (IntPtr)processResult;

            // This message was intended for the parent/main window
            if (hWnd == _hWnd)
                return (IntPtr)Win32.CallWindowProc(_oldWndProcPtr, hWnd, (uint)msg, (ulong)wParam, (long)lParam);

            // The message wasn't handled, but it's a platform window
            // So we have to handle some messages ourselves
            // BUT we might have disposed the context, so check that
            if (ImGui.GetCurrentContext() == IntPtr.Zero)
                return User32.DefWindowProc(hWnd, msg, (IntPtr)wParam, (IntPtr)lParam);
            ImGuiViewportPtr viewport = ImGui.FindViewportByPlatformHandle(hWnd);

            if (viewport.NativePtr != null)
            {
                switch (msg)
                {
                    case User32.WindowMessage.WM_CLOSE:
                        viewport.PlatformRequestClose = true;
                        return IntPtr.Zero;
                    case User32.WindowMessage.WM_MOVE:
                        viewport.PlatformRequestMove = true;
                        break;
                    case User32.WindowMessage.WM_SIZE:
                        viewport.PlatformRequestResize = true;
                        break;
                    case User32.WindowMessage.WM_MOUSEACTIVATE:
                        // We never want our platform windows to be active, or else Windows will think we
                        // want messages dispatched with its hWnd. We don't. The only way to activate a platform
                        // window is via clicking, it does not appear on the taskbar or alt-tab, so we just
                        // brute force behavior here.

                        // Make the game the foreground window. This prevents ImGui windows from becoming
                        // choppy when users have the "limit FPS" option enabled in-game
                        User32.SetForegroundWindow(_hWnd);

                        // Also set the window capture to the main window, as focus will not cause
                        // future messages to be dispatched to the main window unless it is receiving capture
                        User32.SetCapture(_hWnd);

                        // We still want to return MA_NOACTIVATE
                        // https://docs.microsoft.com/en-us/windows/win32/inputdev/wm-mouseactivate
                        return (IntPtr)0x3;
                    // break;
                    case User32.WindowMessage.WM_NCHITTEST:
                        // Let mouse pass-through the window. This will allow the backend to set io.MouseHoveredViewport properly (which is OPTIONAL).
                        // The ImGuiViewportFlags_NoInputs flag is set while dragging a viewport, as want to detect the window behind the one we are dragging.
                        // If you cannot easily access those viewport flags from your windowing/event code: you may manually synchronize its state e.g. in
                        // your main loop after calling UpdatePlatformWindows(). Iterate all viewports/platform windows and pass the flag to your windowing system.
                        if (viewport.Flags.HasFlag(ImGuiViewportFlags.NoInputs))
                        {
                            // https://docs.microsoft.com/en-us/windows/win32/inputdev/wm-nchittest
                            return (IntPtr)uint.MaxValue;
                        }

                        break;
                }
            }

            return User32.DefWindowProc(hWnd, msg, (IntPtr)wParam, (IntPtr)lParam);
        }

        #region IDisposable Support

        private bool disposedValue = false; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // TODO: dispose managed state (managed objects).
                    _cursors = null;
                }

                // TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
                // TODO: set large fields to null.

                if (_oldWndProcPtr != IntPtr.Zero)
                {
                    Win32.SetWindowLongPtr(_hWnd, WindowLongType.GWL_WNDPROC, _oldWndProcPtr);
                    _oldWndProcPtr = IntPtr.Zero;
                }

                if (_platformNamePtr != IntPtr.Zero)
                {
                    unsafe
                    {
                        ImGui.GetIO().NativePtr->BackendPlatformName = null;
                    }

                    Marshal.FreeHGlobal(_platformNamePtr);
                    _platformNamePtr = IntPtr.Zero;
                }

                if (_iniPathPtr != IntPtr.Zero)
                {
                    unsafe
                    {
                        ImGui.GetIO().NativePtr->IniFilename = null;
                    }

                    Marshal.FreeHGlobal(_iniPathPtr);
                    _iniPathPtr = IntPtr.Zero;
                }

                ImGui_ImplWin32_ShutdownPlatformInterface();

                disposedValue = true;
            }
        }

        ~ImGui_Input_Impl_Direct()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(false);
        }

        // This code added to correctly implement the disposable pattern.
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #endregion

        // Viewport support
        private CreateWindowDelegate createWindow;
        private DestroyWindowDelegate destroyWindow;
        private ShowWindowDelegate showWindow;
        private SetWindowPosDelegate setWindowPos;
        private GetWindowPosDelegate getWindowPos;
        private SetWindowSizeDelegate setWindowSize;
        private GetWindowSizeDelegate getWindowSize;
        private SetWindowFocusDelegate setWindowFocus;
        private GetWindowFocusDelegate getWindowFocus;
        private GetWindowMinimizedDelegate getWindowMinimized;
        private SetWindowTitleDelegate setWindowTitle;
        private SetWindowAlphaDelegate setWindowAlpha;
        private UpdateWindowDelegate updateWindow;
        // private SetImeInputPosDelegate setImeInputPos;
        // private GetWindowDpiScaleDelegate getWindowDpiScale;
        // private ChangedViewportDelegate changedViewport;

        private delegate void CreateWindowDelegate(ImGuiViewportPtr viewport);

        private delegate void DestroyWindowDelegate(ImGuiViewportPtr viewport);

        private delegate void ShowWindowDelegate(ImGuiViewportPtr viewport);

        private delegate void UpdateWindowDelegate(ImGuiViewportPtr viewport);

        private delegate Vector2* GetWindowPosDelegate(IntPtr unk, ImGuiViewportPtr viewport);

        private delegate void SetWindowPosDelegate(ImGuiViewportPtr viewport, Vector2 pos);

        private delegate Vector2* GetWindowSizeDelegate(IntPtr unk, ImGuiViewportPtr viewport);

        private delegate void SetWindowSizeDelegate(ImGuiViewportPtr viewport, Vector2 size);

        private delegate void SetWindowFocusDelegate(ImGuiViewportPtr viewport);

        private delegate bool GetWindowFocusDelegate(ImGuiViewportPtr viewport);

        private delegate byte GetWindowMinimizedDelegate(ImGuiViewportPtr viewport);

        private delegate void SetWindowTitleDelegate(ImGuiViewportPtr viewport, string title);

        private delegate void SetWindowAlphaDelegate(ImGuiViewportPtr viewport, float alpha);

        private delegate void SetImeInputPosDelegate(ImGuiViewportPtr viewport, Vector2 pos);

        private delegate float GetWindowDpiScaleDelegate(ImGuiViewportPtr viewport);

        private delegate void ChangedViewportDelegate(ImGuiViewportPtr viewport);

        // private bool wantUpdateMonitors = false;

        private void ImGui_ImplWin32_UpdateMonitors()
        {
            // Set up platformIO monitor structures
            // Here we use a manual ImVector overload, free the existing monitor data,
            // and allocate our own, as we are responsible for telling ImGui about monitors
            ImGuiPlatformIOPtr platformIO = ImGui.GetPlatformIO();
            int numMonitors = User32.GetSystemMetrics(User32.SystemMetric.SM_CMONITORS);
            IntPtr data = Marshal.AllocHGlobal(Marshal.SizeOf<ImGuiPlatformMonitor>() * numMonitors);
            platformIO.NativePtr->Monitors = new ImVector(numMonitors, numMonitors, data);

            // ImGuiPlatformIOPtr platformIO = ImGui.GetPlatformIO();
            // Marshal.FreeHGlobal(platformIO.NativePtr->Monitors.Data);
            // int numMonitors = User32.GetSystemMetrics(User32.SystemMetric.SM_CMONITORS);
            // IntPtr data = Marshal.AllocHGlobal(Marshal.SizeOf<ImGuiPlatformMonitor>() * numMonitors);
            // platformIO.NativePtr->Monitors = new ImVector(numMonitors, numMonitors, data);

            // Store an iterator for the enumeration function
            int* iterator = (int*)Marshal.AllocHGlobal(sizeof(int));
            *iterator = 0;

            User32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, ImGui_ImplWin32_UpdateMonitors_EnumFunc,
                                       new IntPtr(iterator));
            // this.wantUpdateMonitors = false;
        }

        private bool ImGui_ImplWin32_UpdateMonitors_EnumFunc(IntPtr nativeMonitor, IntPtr hdc, RECT* LPRECT,
                                                                    void* LPARAM)
        {
            // Get and increment iterator
            int monitorIndex = *(int*)LPARAM;
            *(int*)LPARAM = *(int*)LPARAM + 1;

            User32.MONITORINFO info = new User32.MONITORINFO();
            info.cbSize = Marshal.SizeOf(info);
            if (!User32.GetMonitorInfo(nativeMonitor, ref info))
                return true;

            // Give ImGui the info for this display
            ImGuiPlatformMonitorPtr imMonitor = ImGui.GetPlatformIO().Monitors[monitorIndex];
            imMonitor.MainPos = new Vector2(info.rcMonitor.left, info.rcMonitor.top);
            imMonitor.MainSize = new Vector2(info.rcMonitor.right - info.rcMonitor.left,
                                             info.rcMonitor.bottom - info.rcMonitor.top);
            imMonitor.WorkPos = new Vector2(info.rcWork.left, info.rcWork.top);
            imMonitor.WorkSize =
                new Vector2(info.rcWork.right - info.rcWork.left, info.rcWork.bottom - info.rcWork.top);
            imMonitor.DpiScale = 1f;

            return true;
        }

        // Helper structure we store in the void* RenderUserData field of each ImGuiViewport to easily retrieve our backend data->
        private struct ImGuiViewportDataWin32
        {
            public IntPtr Hwnd;
            public bool HwndOwned;
            public User32.WindowStyles DwStyle;
            public User32.WindowStylesEx DwExStyle;
        }

        private void ImGui_ImplWin32_GetWin32StyleFromViewportFlags(ImGuiViewportFlags flags,
                                                                    ref User32.WindowStyles outStyle,
                                                                    ref User32.WindowStylesEx outExStyle)
        {
            if (flags.HasFlag(ImGuiViewportFlags.NoDecoration))
                outStyle = User32.WindowStyles.WS_POPUP;
            else
                outStyle = User32.WindowStyles.WS_OVERLAPPEDWINDOW;

            if (flags.HasFlag(ImGuiViewportFlags.NoTaskBarIcon))
                outExStyle = User32.WindowStylesEx.WS_EX_TOOLWINDOW;
            else
                outExStyle = User32.WindowStylesEx.WS_EX_APPWINDOW;

            if (flags.HasFlag(ImGuiViewportFlags.TopMost))
                outExStyle |= User32.WindowStylesEx.WS_EX_TOPMOST;
        }

        private void ImGui_ImplWin32_CreateWindow(ImGuiViewportPtr viewport)
        {
            var data = (ImGuiViewportDataWin32*)Marshal.AllocHGlobal(Marshal.SizeOf<ImGuiViewportDataWin32>());
            viewport.PlatformUserData = (IntPtr)data;
            viewport.Flags =
                (
                    ImGuiViewportFlags.NoTaskBarIcon |
                    ImGuiViewportFlags.NoFocusOnClick |
                    ImGuiViewportFlags.NoRendererClear |
                    ImGuiViewportFlags.NoFocusOnAppearing |
                    viewport.Flags
                );
            ImGui_ImplWin32_GetWin32StyleFromViewportFlags(viewport.Flags, ref data->DwStyle, ref data->DwExStyle);

            IntPtr parentWindow = IntPtr.Zero;
            if (viewport.ParentViewportId != 0)
            {
                ImGuiViewportPtr parentViewport = ImGui.FindViewportByID(viewport.ParentViewportId);
                parentWindow = parentViewport.PlatformHandle;
            }

            // Create window
            var rect = MemUtil.Allocate<RECT>();
            rect->left = (int)viewport.Pos.X;
            rect->top = (int)viewport.Pos.Y;
            rect->right = (int)(viewport.Pos.X + viewport.Size.X);
            rect->bottom = (int)(viewport.Pos.Y + viewport.Size.Y);
            User32.AdjustWindowRectEx(rect, data->DwStyle, false, data->DwExStyle);

            data->Hwnd = User32.CreateWindowEx(
                data->DwExStyle, "ImGui Platform", "Untitled", data->DwStyle,
                rect->left, rect->top, rect->right - rect->left, rect->bottom - rect->top,
                parentWindow, IntPtr.Zero, Kernel32.GetModuleHandle(null),
                IntPtr.Zero);

            // User32.GetWindowThreadProcessId(data->Hwnd, out var windowProcessId);
            // var currentThreadId = Kernel32.GetCurrentThreadId();
            // var currentProcessId = Kernel32.GetCurrentProcessId();

            // Allow transparent windows
            // TODO: Eventually...
            ImGui_ImplWin32_EnableAlphaCompositing(data->Hwnd);

            data->HwndOwned = true;
            viewport.PlatformRequestResize = false;
            viewport.PlatformHandle = viewport.PlatformHandleRaw = data->Hwnd;
            Marshal.FreeHGlobal((IntPtr)rect);
        }

        private void ImGui_ImplWin32_DestroyWindow(ImGuiViewportPtr viewport)
        {
            // This is also called on the main viewport for some reason, and we never set that viewport's PlatformUserData
            if (viewport.PlatformUserData == IntPtr.Zero) return;

            var data = (ImGuiViewportDataWin32*)viewport.PlatformUserData;

            if (Win32.GetCapture() == data->Hwnd)
            {
                // Transfer capture so if we started dragging from a window that later disappears, we'll still receive the MOUSEUP event.
                User32.ReleaseCapture();
                User32.SetCapture(_hWnd);
            }

            if (data->Hwnd != IntPtr.Zero && data->HwndOwned)
            {
                var result = User32.DestroyWindow(data->Hwnd);

                if (result == false && Kernel32.GetLastError() == Win32ErrorCode.ERROR_ACCESS_DENIED)
                {
                    // We are disposing, and we're doing it from a different thread because of course we are
                    // Just send the window the close message
                    User32.PostMessage(data->Hwnd, User32.WindowMessage.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                }
            }

            data->Hwnd = IntPtr.Zero;
            Marshal.FreeHGlobal(viewport.PlatformUserData);
            viewport.PlatformUserData = viewport.PlatformHandle = IntPtr.Zero;
        }

        private void ImGui_ImplWin32_ShowWindow(ImGuiViewportPtr viewport)
        {
            var data = (ImGuiViewportDataWin32*)viewport.PlatformUserData;

            if (viewport.Flags.HasFlag(ImGuiViewportFlags.NoFocusOnAppearing))
                User32.ShowWindow(data->Hwnd, User32.WindowShowStyle.SW_SHOWNA);
            else
                User32.ShowWindow(data->Hwnd, User32.WindowShowStyle.SW_SHOW);
        }

        private void ImGui_ImplWin32_UpdateWindow(ImGuiViewportPtr viewport)
        {
            // (Optional) Update Win32 style if it changed _after_ creation.
            // Generally they won't change unless configuration flags are changed, but advanced uses (such as manually rewriting viewport flags) make this useful.
            var data = (ImGuiViewportDataWin32*)viewport.PlatformUserData;

            viewport.Flags =
                (
                    ImGuiViewportFlags.NoTaskBarIcon |
                    ImGuiViewportFlags.NoFocusOnClick |
                    ImGuiViewportFlags.NoRendererClear |
                    ImGuiViewportFlags.NoFocusOnAppearing |
                    viewport.Flags
                );
            User32.WindowStyles newStyle = 0;
            User32.WindowStylesEx newExStyle = 0;
            ImGui_ImplWin32_GetWin32StyleFromViewportFlags(viewport.Flags, ref newStyle, ref newExStyle);

            // Only reapply the flags that have been changed from our point of view (as other flags are being modified by Windows)
            if (data->DwStyle != newStyle || data->DwExStyle != newExStyle)
            {
                // (Optional) Update TopMost state if it changed _after_ creation
                bool topMostChanged = (data->DwExStyle & User32.WindowStylesEx.WS_EX_TOPMOST) !=
                                      (newExStyle & User32.WindowStylesEx.WS_EX_TOPMOST);

                IntPtr insertAfter = IntPtr.Zero;
                if (topMostChanged)
                {
                    if (viewport.Flags.HasFlag(ImGuiViewportFlags.TopMost))
                        insertAfter = User32.SpecialWindowHandles.HWND_TOPMOST;
                    else
                        insertAfter = User32.SpecialWindowHandles.HWND_NOTOPMOST;
                }

                User32.SetWindowPosFlags swpFlag = topMostChanged ? 0 : User32.SetWindowPosFlags.SWP_NOZORDER;

                // Apply flags and position (since it is affected by flags)
                data->DwStyle = newStyle;
                data->DwExStyle = newExStyle;

                User32.SetWindowLong(data->Hwnd, User32.WindowLongIndexFlags.GWL_STYLE,
                                     (User32.SetWindowLongFlags)data->DwStyle);
                User32.SetWindowLong(data->Hwnd, User32.WindowLongIndexFlags.GWL_EXSTYLE,
                                     (User32.SetWindowLongFlags)data->DwExStyle);

                // Create window
                var rect = MemUtil.Allocate<RECT>();
                rect->left = (int)viewport.Pos.X;
                rect->top = (int)viewport.Pos.Y;
                rect->right = (int)(viewport.Pos.X + viewport.Size.X);
                rect->bottom = (int)(viewport.Pos.Y + viewport.Size.Y);
                User32.AdjustWindowRectEx(rect, data->DwStyle, false, data->DwExStyle);
                User32.SetWindowPos(data->Hwnd, insertAfter,
                                    rect->left, rect->top, rect->right - rect->left, rect->bottom - rect->top,
                                    swpFlag |
                                    User32.SetWindowPosFlags.SWP_NOACTIVATE |
                                    User32.SetWindowPosFlags.SWP_FRAMECHANGED);

                // This is necessary when we alter the style
                User32.ShowWindow(data->Hwnd, User32.WindowShowStyle.SW_SHOWNA);
                viewport.PlatformRequestMove = viewport.PlatformRequestResize = true;
                Marshal.FreeHGlobal((IntPtr)rect);
            }
        }

        private Vector2* ImGui_ImplWin32_GetWindowPos(IntPtr unk, ImGuiViewportPtr viewport)
        {
            var data = (ImGuiViewportDataWin32*)viewport.PlatformUserData;
            var vec2 = MemUtil.Allocate<Vector2>();

            POINT pt = new POINT { x = 0, y = 0 };
            User32.ClientToScreen(data->Hwnd, ref pt);
            vec2->X = pt.x;
            vec2->Y = pt.y;

            return vec2;
        }

        private void ImGui_ImplWin32_SetWindowPos(ImGuiViewportPtr viewport, Vector2 pos)
        {
            var data = (ImGuiViewportDataWin32*)viewport.PlatformUserData;

            var rect = MemUtil.Allocate<RECT>();
            rect->left = (int)pos.X;
            rect->top = (int)pos.Y;
            rect->right = (int)pos.X;
            rect->bottom = (int)pos.Y;
            
            User32.AdjustWindowRectEx(rect, data->DwStyle, false, data->DwExStyle);
            User32.SetWindowPos(data->Hwnd, IntPtr.Zero,
                                rect->left, rect->top, 0, 0,
                                User32.SetWindowPosFlags.SWP_NOZORDER |
                                User32.SetWindowPosFlags.SWP_NOSIZE |
                                User32.SetWindowPosFlags.SWP_NOACTIVATE);
            Marshal.FreeHGlobal((IntPtr)rect);
        }

        private Vector2* ImGui_ImplWin32_GetWindowSize(IntPtr size, ImGuiViewportPtr viewport)
        {
            var data = (ImGuiViewportDataWin32*)viewport.PlatformUserData;
            var vec2 = MemUtil.Allocate<Vector2>();

            User32.GetClientRect(data->Hwnd, out var rect);
            vec2->X = rect.right - rect.left;
            vec2->Y = rect.bottom - rect.top;

            return vec2;
        }

        private void ImGui_ImplWin32_SetWindowSize(ImGuiViewportPtr viewport, Vector2 size)
        {
            var data = (ImGuiViewportDataWin32*)viewport.PlatformUserData;

            var rect = MemUtil.Allocate<RECT>();
            rect->left = 0;
            rect->top = 0;
            rect->right = (int)size.X;
            rect->bottom = (int)size.Y;
            
            User32.AdjustWindowRectEx(rect, data->DwStyle, false, data->DwExStyle);
            User32.SetWindowPos(data->Hwnd, IntPtr.Zero,
                                0, 0, rect->right - rect->left, rect->bottom - rect->top,
                                User32.SetWindowPosFlags.SWP_NOZORDER |
                                User32.SetWindowPosFlags.SWP_NOMOVE |
                                User32.SetWindowPosFlags.SWP_NOACTIVATE);
            Marshal.FreeHGlobal((IntPtr) rect);
        }

        private void ImGui_ImplWin32_SetWindowFocus(ImGuiViewportPtr viewport)
        {
            var data = (ImGuiViewportDataWin32*)viewport.PlatformUserData;

            Win32.BringWindowToTop(data->Hwnd);
            User32.SetForegroundWindow(data->Hwnd);
            Win32.SetFocus(data->Hwnd);
        }

        private bool ImGui_ImplWin32_GetWindowFocus(ImGuiViewportPtr viewport)
        {
            var data = (ImGuiViewportDataWin32*)viewport.PlatformUserData;
            return User32.GetForegroundWindow() == data->Hwnd;
        }

        private byte ImGui_ImplWin32_GetWindowMinimized(ImGuiViewportPtr viewport)
        {
            var data = (ImGuiViewportDataWin32*)viewport.PlatformUserData;
            return (byte)(User32.IsIconic(data->Hwnd) ? 1 : 0);
        }

        private void ImGui_ImplWin32_SetWindowTitle(ImGuiViewportPtr viewport, string title)
        {
            var data = (ImGuiViewportDataWin32*)viewport.PlatformUserData;
            User32.SetWindowText(data->Hwnd, title);
        }

        private void ImGui_ImplWin32_SetWindowAlpha(ImGuiViewportPtr viewport, float alpha)
        {
            var data = (ImGuiViewportDataWin32*)viewport.PlatformUserData;

            if (alpha < 1.0f)
            {
                User32.WindowStylesEx gwl =
                    (User32.WindowStylesEx)User32.GetWindowLong(data->Hwnd, User32.WindowLongIndexFlags.GWL_EXSTYLE);
                User32.WindowStylesEx style = gwl | User32.WindowStylesEx.WS_EX_LAYERED;
                User32.SetWindowLong(data->Hwnd, User32.WindowLongIndexFlags.GWL_EXSTYLE,
                                     (User32.SetWindowLongFlags)style);
                Win32.SetLayeredWindowAttributes(data->Hwnd, 0, (byte)(255 * alpha), 0x2); //0x2 = LWA_ALPHA
            }
            else
            {
                User32.WindowStylesEx gwl =
                    (User32.WindowStylesEx)User32.GetWindowLong(data->Hwnd, User32.WindowLongIndexFlags.GWL_EXSTYLE);
                User32.WindowStylesEx style = gwl & ~User32.WindowStylesEx.WS_EX_LAYERED;
                User32.SetWindowLong(data->Hwnd, User32.WindowLongIndexFlags.GWL_EXSTYLE,
                                     (User32.SetWindowLongFlags)style);
            }
        }

        // TODO: Decode why IME is miserable
        // private void ImGui_ImplWin32_SetImeInputPos(ImGuiViewportPtr viewport, Vector2 pos) {
        //     Win32.COMPOSITIONFORM cs = new Win32.COMPOSITIONFORM(
        //         0x20,
        //         new Win32.POINT(
        //             (int) (pos.X - viewport.Pos.X),
        //             (int) (pos.Y - viewport.Pos.Y)),
        //         new Win32.RECT(0, 0, 0, 0)
        //     );
        //     var hwnd = viewport.PlatformHandle;
        //     if (hwnd != IntPtr.Zero) {
        //         var himc = Win32.ImmGetContext(hwnd);
        //         if (himc != IntPtr.Zero) {
        //             Win32.ImmSetCompositionWindow(himc, ref cs);
        //             Win32.ImmReleaseContext(hwnd, himc);
        //         }
        //     }
        // }

        // TODO Alpha when it's no longer forced
        private void ImGui_ImplWin32_EnableAlphaCompositing(IntPtr hwnd)
        {
            Win32.DwmIsCompositionEnabled(out bool composition);

            if (!composition) return;

            if (DwmApi.DwmGetColorizationColor(out uint color, out bool opaque) == HResult.Code.S_OK && !opaque)
            {
                DwmApi.DWM_BLURBEHIND bb = new DwmApi.DWM_BLURBEHIND();
                bb.Enable = true;
                bb.dwFlags = DwmApi.DWM_BLURBEHINDFlags.DWM_BB_ENABLE;
                bb.hRgnBlur = IntPtr.Zero;
                DwmApi.DwmEnableBlurBehindWindow(hwnd, bb);
            }
        }

        private void ImGui_ImplWin32_InitPlatformInterface()
        {
            _classNamePtr = Marshal.StringToHGlobalUni("ImGui Platform");

            User32.WNDCLASSEX wcex = new User32.WNDCLASSEX();
            wcex.cbSize = Marshal.SizeOf(wcex);
            wcex.style = User32.ClassStyles.CS_HREDRAW | User32.ClassStyles.CS_VREDRAW;
            wcex.cbClsExtra = 0;
            wcex.cbWndExtra = 0;
            wcex.hInstance = Kernel32.GetModuleHandle(null);
            wcex.hIcon = IntPtr.Zero;
            wcex.hCursor = IntPtr.Zero;
            wcex.hbrBackground = new IntPtr(2); // COLOR_BACKGROUND is 1, so...
            wcex.lpfnWndProc = _wndProcDelegate;
            unsafe
            {
                wcex.lpszMenuName = null;
                wcex.lpszClassName = (char*)_classNamePtr;
            }

            wcex.hIconSm = IntPtr.Zero;
            User32.RegisterClassEx(ref wcex);

            ImGui_ImplWin32_UpdateMonitors();

            this.createWindow = ImGui_ImplWin32_CreateWindow;
            this.destroyWindow = ImGui_ImplWin32_DestroyWindow;
            this.showWindow = ImGui_ImplWin32_ShowWindow;
            this.setWindowPos = ImGui_ImplWin32_SetWindowPos;
            this.getWindowPos = ImGui_ImplWin32_GetWindowPos;
            this.setWindowSize = ImGui_ImplWin32_SetWindowSize;
            this.getWindowSize = ImGui_ImplWin32_GetWindowSize;
            this.setWindowFocus = ImGui_ImplWin32_SetWindowFocus;
            this.getWindowFocus = ImGui_ImplWin32_GetWindowFocus;
            this.getWindowMinimized = ImGui_ImplWin32_GetWindowMinimized;
            this.setWindowTitle = ImGui_ImplWin32_SetWindowTitle;
            this.setWindowAlpha = ImGui_ImplWin32_SetWindowAlpha;
            this.updateWindow = ImGui_ImplWin32_UpdateWindow;
            // this.setImeInputPos = ImGui_ImplWin32_SetImeInputPos;

            // Register platform interface (will be coupled with a renderer interface)
            ImGuiPlatformIOPtr io = ImGui.GetPlatformIO();
            io.Platform_CreateWindow = Marshal.GetFunctionPointerForDelegate(this.createWindow);
            io.Platform_DestroyWindow = Marshal.GetFunctionPointerForDelegate(this.destroyWindow);
            io.Platform_ShowWindow = Marshal.GetFunctionPointerForDelegate(this.showWindow);
            io.Platform_SetWindowPos = Marshal.GetFunctionPointerForDelegate(this.setWindowPos);
            io.Platform_GetWindowPos = Marshal.GetFunctionPointerForDelegate(this.getWindowPos);
            io.Platform_SetWindowSize = Marshal.GetFunctionPointerForDelegate(this.setWindowSize);
            io.Platform_GetWindowSize = Marshal.GetFunctionPointerForDelegate(this.getWindowSize);
            io.Platform_SetWindowFocus = Marshal.GetFunctionPointerForDelegate(this.setWindowFocus);
            io.Platform_GetWindowFocus = Marshal.GetFunctionPointerForDelegate(this.getWindowFocus);
            io.Platform_GetWindowMinimized = Marshal.GetFunctionPointerForDelegate(this.getWindowMinimized);
            io.Platform_SetWindowTitle = Marshal.GetFunctionPointerForDelegate(this.setWindowTitle);
            io.Platform_SetWindowAlpha = Marshal.GetFunctionPointerForDelegate(this.setWindowAlpha);
            io.Platform_UpdateWindow = Marshal.GetFunctionPointerForDelegate(this.updateWindow);
            // io.Platform_SetImeInputPos = Marshal.GetFunctionPointerForDelegate(this.setImeInputPos);

            // Register main window handle (which is owned by the main application, not by us)
            // This is mostly for simplicity and consistency, so that our code (e.g. mouse handling etc.) can use same logic for main and secondary viewports.
            ImGuiViewportPtr mainViewport = ImGui.GetMainViewport();

            var data = (ImGuiViewportDataWin32*)Marshal.AllocHGlobal(Marshal.SizeOf<ImGuiViewportDataWin32>());
            mainViewport.PlatformUserData = (IntPtr)data;
            data->Hwnd = _hWnd;
            data->HwndOwned = false;
            mainViewport.PlatformHandle = _hWnd;
        }

        private void ImGui_ImplWin32_ShutdownPlatformInterface()
        {
            Marshal.FreeHGlobal(_classNamePtr);

            // We allocated the platform monitor data in ImGui_ImplWin32_UpdateMonitors ourselves,
            // so we have to free it ourselves to ImGui doesn't try to, or else it will crash
            Marshal.FreeHGlobal(ImGui.GetPlatformIO().NativePtr->Monitors.Data);
            ImGui.GetPlatformIO().NativePtr->Monitors = new ImVector(0, 0, IntPtr.Zero);

            User32.UnregisterClass("ImGui Platform", Kernel32.GetModuleHandle(null));
        }
    }
}

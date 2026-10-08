// kazumi_helper.cs
//
// Kazumi 官方版伴生工具（不改官方任何文件）：
//  - 连接 mpv IPC 管道（由 kazumi-mpv-proxy 在 mpv 初始化时开启）
//  - 当 Kazumi 位于前台且有视频加载时，拦截 D / F / S：
//      D = 后退一帧 (frame-back-step)
//      F = 前进一帧 (frame-step)
//      S = 截图当前帧到剪贴板 (screenshot-to-file -> Clipboard)
//  - 托盘常驻：可临时停用热键、显示管道状态、退出
//
// 编译（Windows 自带 csc）：
//   csc /target:winexe /out:KazumiHelper.exe /r:System.dll /r:System.Core.dll
//       /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll
//       kazumi_helper.cs

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

static class Program
{
    internal static void Log(string msg)
    {
        try
        {
            File.AppendAllText(@"D:\KazumiHelper\helper_log.txt",
                DateTime.Now.ToString("HH:mm:ss.fff ") + msg + Environment.NewLine);
        }
        catch { }
    }

    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (s, e) => Log("UI exception: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            Log("Fatal: " + e.ExceptionObject);
        Application.Run(new HelperForm());
    }
}

class HelperForm : Form
{
    // ---------- Win32 ----------
    const int WH_KEYBOARD_LL = 13;
    const int WM_KEYDOWN = 0x0100;
    const int WM_SYSKEYDOWN = 0x0104;
    const int LLKHF_INJECTED = 0x10;

    delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);

    const int VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    // ---------- 状态 ----------
    const string PipeName = "kazumi-mpv-ipc";
    readonly JavaScriptSerializer _json = new JavaScriptSerializer();
    readonly object _pipeLock = new object();
    NamedPipeClientStream _pipe;
    StreamWriter _writer;
    volatile bool _quit;
    volatile bool _pipeConnected;
    volatile bool _hasVideo;          // mpv 已加载视频（path 非空）
    volatile bool _hotkeysEnabled = true;
    int _nextRequestId = 1;
    long _lastShotTicks;
    IntPtr _hook;
    LowLevelKeyboardProc _proc;
    NotifyIcon _tray;
    MenuItem _toggleItem;
    MenuItem _statusItem;

    // request_id -> 等待响应的信号与结果
    readonly Dictionary<int, ManualResetEvent> _pending = new Dictionary<int, ManualResetEvent>();
    readonly Dictionary<int, Dictionary<string, object>> _responses = new Dictionary<int, Dictionary<string, object>>();

    public HelperForm()
    {
        ShowInTaskbar = false;
        WindowState = FormWindowState.Minimized;
        FormBorderStyle = FormBorderStyle.None;
        Opacity = 0;
        Load += (s, e) => Hide();

        // 关键：强制创建原生句柄，否则后台线程 BeginInvoke 会抛异常
        var forceHandle = Handle;
        Program.Log("helper started, handle=" + forceHandle);

        _tray = new NotifyIcon();
        _tray.Icon = SystemIcons.Application;
        _tray.Text = "Kazumi Helper (D/F 逐帧, S 截图)";
        _tray.Visible = true;
        var menu = new ContextMenu();
        _statusItem = new MenuItem("管道: 连接中…", (s, e) => { });
        _statusItem.Enabled = false;
        _toggleItem = new MenuItem("停用热键", (s, e) => ToggleHotkeys());
        var exitItem = new MenuItem("退出", (s, e) => { _quit = true; Close(); });
        menu.MenuItems.Add(_statusItem);
        menu.MenuItems.Add("-");
        menu.MenuItems.Add(_toggleItem);
        menu.MenuItems.Add(exitItem);
        _tray.ContextMenu = menu;

        _proc = HookCallback;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);

        new Thread(ConnectLoop) { IsBackground = true }.Start();
        new Thread(PollLoop) { IsBackground = true }.Start();
    }

    void ToggleHotkeys()
    {
        _hotkeysEnabled = !_hotkeysEnabled;
        _toggleItem.Text = _hotkeysEnabled ? "停用热键" : "启用热键";
    }

    void UpdateStatus()
    {
        try
        {
            _statusItem.Text = "管道: " + (_pipeConnected ? "已连接" : "未连接 (等待播放)")
                + " | 视频: " + (_hasVideo ? "是" : "否");
        }
        catch { }
    }

    // ---------- 键盘钩子 ----------
    IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
            {
                var info = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                bool injected = (info.flags & LLKHF_INJECTED) != 0;
                if (!injected && _hotkeysEnabled && _hasVideo && NoModifiers() && IsKazumiForeground())
                {
                    if (info.vkCode == 'D' || info.vkCode == 'F')
                    {
                        SendFireAndForget(new object[] { info.vkCode == 'D' ? "frame-back-step" : "frame-step" });
                        return (IntPtr)1;
                    }
                    if (info.vkCode == 'S')
                    {
                        long now = DateTime.UtcNow.Ticks;
                        if (Interlocked.Read(ref _lastShotTicks) == 0 ||
                            now - Interlocked.Read(ref _lastShotTicks) > 800 * TimeSpan.TicksPerMillisecond)
                        {
                            Interlocked.Exchange(ref _lastShotTicks, now);
                            ScreenshotToClipboard();
                        }
                        return (IntPtr)1;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Program.Log("hook callback exception: " + ex);
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    static bool NoModifiers()
    {
        return (GetAsyncKeyState(VK_CONTROL) & 0x8000) == 0
            && (GetAsyncKeyState(VK_MENU) & 0x8000) == 0
            && (GetAsyncKeyState(VK_LWIN) & 0x8000) == 0
            && (GetAsyncKeyState(VK_RWIN) & 0x8000) == 0;
    }

    uint _lastFgPid;
    bool _lastFgIsKazumi;

    bool IsKazumiForeground()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        uint pid;
        GetWindowThreadProcessId(hwnd, out pid);
        if (pid == _lastFgPid) return _lastFgIsKazumi;
        bool isKazumi = false;
        try
        {
            using (var p = Process.GetProcessById((int)pid))
                isKazumi = string.Equals(p.ProcessName, "kazumi", StringComparison.OrdinalIgnoreCase);
        }
        catch { }
        _lastFgPid = pid;
        _lastFgIsKazumi = isKazumi;
        return isKazumi;
    }

    // ---------- IPC ----------
    void ConnectLoop()
    {
        while (!_quit)
        {
            if (_pipeConnected) { Thread.Sleep(500); continue; }
            try
            {
                var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                pipe.Connect(2000);
                lock (_pipeLock)
                {
                    _pipe = pipe;
                    _writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
                    _pipeConnected = true;
                }
                UpdateStatus();
                ReadLoop(pipe); // 阻塞直到断开
            }
            catch
            {
                Thread.Sleep(1500);
            }
            finally
            {
                CleanupPipe();
            }
        }
    }

    void CleanupPipe()
    {
        lock (_pipeLock)
        {
            _pipeConnected = false;
            _hasVideo = false;
            try { if (_writer != null) _writer.Dispose(); } catch { }
            try { if (_pipe != null) _pipe.Dispose(); } catch { }
            _writer = null;
            _pipe = null;
        }
        lock (_pending)
        {
            foreach (var ev in _pending.Values) ev.Set();
            _pending.Clear();
            _responses.Clear();
        }
        UpdateStatus();
    }

    void ReadLoop(NamedPipeClientStream pipe)
    {
        var buf = new StringBuilder();
        var bytes = new byte[4096];
        try
        {
            while (!_quit && _pipeConnected)
            {
                int n = pipe.Read(bytes, 0, bytes.Length);
                if (n <= 0) break;
                buf.Append(Encoding.UTF8.GetString(bytes, 0, n));
                int idx;
                while ((idx = buf.ToString().IndexOf('\n')) >= 0)
                {
                    string line = buf.ToString(0, idx).Trim();
                    buf.Remove(0, idx + 1);
                    if (line.Length > 0) HandleResponse(line);
                }
            }
        }
        catch { }
    }

    void HandleResponse(string line)
    {
        try
        {
            var dict = _json.Deserialize<Dictionary<string, object>>(line);
            if (dict == null || !dict.ContainsKey("request_id")) return;
            int id;
            if (!int.TryParse(dict["request_id"].ToString(), out id)) return;
            lock (_pending)
            {
                if (_pending.ContainsKey(id))
                {
                    _responses[id] = dict;
                    _pending[id].Set();
                }
            }
            // path 轮询响应 -> 更新“是否有视频”
            if (id == _pollRequestId)
            {
                bool has;
                object data;
                string err = dict["error"] as string;
                has = err == "success" && dict.TryGetValue("data", out data) && data is string && ((string)data).Length > 0;
                _hasVideo = has;
                UpdateStatus();
            }
        }
        catch { }
    }

    int _pollRequestId = -1;

    void PollLoop()
    {
        while (!_quit)
        {
            if (_pipeConnected)
            {
                int id = System.Threading.Interlocked.Increment(ref _nextRequestId);
                _pollRequestId = id;
                var ok = SendRaw(new Dictionary<string, object>
                {
                    { "command", new object[] { "get_property_string", "path" } },
                    { "request_id", id }
                });
                if (!ok) { Thread.Sleep(1500); continue; }
            }
            Thread.Sleep(1500);
        }
    }

    void SendFireAndForget(object[] command)
    {
        int id = System.Threading.Interlocked.Increment(ref _nextRequestId);
        SendRaw(new Dictionary<string, object> { { "command", command }, { "request_id", id } });
    }

    Dictionary<string, object> SendAndWait(object[] command, int timeoutMs)
    {
        int id = System.Threading.Interlocked.Increment(ref _nextRequestId);
        var ev = new ManualResetEvent(false);
        lock (_pending) _pending[id] = ev;
        if (!SendRaw(new Dictionary<string, object> { { "command", command }, { "request_id", id } }))
        {
            lock (_pending) _pending.Remove(id);
            return null;
        }
        if (!ev.WaitOne(timeoutMs))
        {
            lock (_pending) { _pending.Remove(id); _responses.Remove(id); }
            return null;
        }
        lock (_pending)
        {
            _pending.Remove(id);
            Dictionary<string, object> res;
            _responses.TryGetValue(id, out res);
            _responses.Remove(id);
            return res;
        }
    }

    bool SendRaw(Dictionary<string, object> msg)
    {
        try
        {
            lock (_pipeLock)
            {
                if (_writer == null) return false;
                _writer.WriteLine(_json.Serialize(msg));
                return true;
            }
        }
        catch { return false; }
    }

    // ---------- 截图到剪贴板 ----------
    void Ui(Action a)
    {
        try
        {
            if (IsHandleCreated) BeginInvoke(a);
            else Program.Log("Ui dispatch skipped, handle not created");
        }
        catch (Exception ex)
        {
            Program.Log("Ui dispatch failed: " + ex.Message);
        }
    }

    void ScreenshotToClipboard()
    {
        ThreadPool.QueueUserWorkItem(delegate
        {
            string file = Path.Combine(Path.GetTempPath(), "kazumi_shot_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".png");
            try
            {
                // mpv 接受正斜杠路径，免去 JSON 反斜杠转义
                string mpvPath = file.Replace('\\', '/');
                var res = SendAndWait(new object[] { "screenshot-to-file", mpvPath }, 4000);
                bool ok = res != null && "success".Equals(res.ContainsKey("error") ? res["error"] as string : null) && File.Exists(file);
                Program.Log("screenshot resp ok=" + ok + " file=" + (File.Exists(file) ? new FileInfo(file).Length + "B" : "none"));
                if (ok)
                {
                    Ui((Action)delegate
                    {
                        try
                        {
                            using (var img = Image.FromFile(file))
                            using (var bmp = new Bitmap(img))
                                Clipboard.SetImage(bmp);
                            _tray.ShowBalloonTip(1200, "Kazumi Helper", "截图已复制到剪贴板", ToolTipIcon.Info);
                        }
                        catch (Exception ex)
                        {
                            Program.Log("clipboard write failed: " + ex);
                            _tray.ShowBalloonTip(1500, "Kazumi Helper", "截图写入剪贴板失败: " + ex.Message, ToolTipIcon.Error);
                        }
                        finally
                        {
                            try { File.Delete(file); } catch { }
                        }
                    });
                }
                else
                {
                    Ui((Action)delegate
                    {
                        _tray.ShowBalloonTip(1500, "Kazumi Helper", "截图失败（mpv 未响应或视频不可用）", ToolTipIcon.Warning);
                    });
                    try { File.Delete(file); } catch { }
                }
            }
            catch (Exception ex)
            {
                Program.Log("screenshot flow exception: " + ex);
                try { File.Delete(file); } catch { }
            }
        });
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _tray.Visible = false;
        _tray.Dispose();
        base.OnFormClosed(e);
    }

    protected override void SetVisibleCore(bool value)
    {
        base.SetVisibleCore(false); // 永不显示主窗口
    }
}

// QuickLook.Plugin.QuickEdit
// Copyright (C) 2026 Ben Smart. Licensed under the GNU GPL v3 or later (see LICENSE).
//
// Edit a few words of a Markdown or plain text file straight in its QuickLook
// preview: double-click, type; Ctrl+S writes the file. Nothing is saved unless
// you press Ctrl+S.
//
// This plugin renders nothing itself. QuickLook's own viewers draw the preview
// as before:
//  - Markdown (MarkdownViewer): this plugin reaches the preview's WebView2,
//    adds QuickEdit.js to the page, and handles the page's messages.
//  - Plain text (TextViewer, an AvalonEdit editor): a double-click makes the
//    editor writable; Esc makes it read-only again; Ctrl+S saves.
// Either way, while you type it keeps QuickLook's keyboard hook from treating
// Space, Enter, Esc and the arrows as "close" / "open" / "next file".
//
// The per-preview hook is the "..." menu getter, which QuickLook calls for
// every plugin after the viewer has been created. Written in C# 5 so it builds
// with the csc that ships with the .NET Framework -- see build.ps1.

using QuickLook.Common.Helpers;
using QuickLook.Common.Plugin;
using QuickLook.Common.Plugin.MoreMenu;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace QuickLook.Plugin.QuickEdit
{
    public class Plugin : IViewer, IMoreMenuExtended
    {
        // MarkdownViewer's extensions whose page shows the file's own text.
        // (.mermaid and .mmd are wrapped in a code fence first, so the page's
        // source is not the file.)
        private static readonly string[] Extensions =
        {
            ".md", ".markdown", ".mdx", ".mkd", ".mdwn", ".mdown", ".mdc", ".qmd",
            ".rmd", ".rmarkdown", ".apib", ".mdtxt", ".mdtext",
        };

        private static readonly ConditionalWeakTable<object, object> Hooks = new ConditionalWeakTable<object, object>();
        private static string _script;
        private static readonly HashSet<string> Logged = new HashSet<string>();

        public int Priority { get { return int.MinValue; } }

        public void Init()
        {
            try
            {
                var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                _script = File.ReadAllText(Path.Combine(dir, "QuickEdit.js"), Encoding.UTF8);
            }
            catch (Exception e)
            {
                Log("script", "QuickEdit.js could not be read: " + e.Message);
            }
        }

        public bool CanHandle(string path) { return false; }
        public void Prepare(string path, ContextObject context) { }
        public void View(string path, ContextObject context) { }
        public void Cleanup() { }

        public IEnumerable<IMenuItem> MenuItems
        {
            get
            {
                try
                {
                    if (TryAttach())
                        return new IMenuItem[]
                        {
                            new MoreMenuItem
                            {
                                Header = "Edit: double-click text, Ctrl+S to save",
                                Icon = "",
                                IsEnabled = false,
                            },
                        };
                }
                catch (Exception e)
                {
                    Log("attach", "attach failed: " + e);
                }
                return new IMenuItem[0];
            }
        }

        // True when the current preview is a Markdown or text file this plugin edits.
        private static bool TryAttach()
        {
            var window = FindViewerWindow();
            if (window == null)
                return false;

            var path = GetField(window, "_path") as string;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return false;

            var contextProperty = window.GetType().GetProperty("ContextObject", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var context = contextProperty == null ? null : contextProperty.GetValue(window, null) as ContextObject;
            var panel = context == null ? null : context.ViewerContent as FrameworkElement;
            if (panel == null)
                return false;

            object existing;
            if (Hooks.TryGetValue(panel, out existing))
                return true;

            if (IsType(panel.GetType(), "QuickLook.Plugin.MarkdownViewer.MarkdownPanel"))
            {
                if (_script == null || !Extensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
                    return false;
                var webView = GetField(panel, "_webView");
                if (webView == null)
                    return false;                       // WebView2 missing: MarkdownViewer shows a download button
                var hook = new MarkdownHook(panel, webView, path);
                Hooks.Add(panel, hook);
                hook.Start();
                return true;
            }

            if (IsType(panel.GetType(), "QuickLook.Plugin.TextViewer.TextViewerPanel"))
            {
                var hook = new TextHook(panel, context, path);
                Hooks.Add(panel, hook);
                hook.Start();
                return true;
            }

            return false;
        }

        private static bool IsType(Type type, string fullName)
        {
            for (var t = type; t != null; t = t.BaseType)
                if (t.FullName == fullName)
                    return true;
            return false;
        }

        // QuickLook.ViewWindowManager.GetInstance()._viewerWindow
        private static Window FindViewerWindow()
        {
            var type = FindType("QuickLook.ViewWindowManager");
            if (type == null)
            {
                Log("vwm", "QuickLook.ViewWindowManager not found in this QuickLook version; the plugin is inactive.");
                return null;
            }
            var getInstance = type.GetMethod("GetInstance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var manager = getInstance == null ? null : getInstance.Invoke(null, null);
            return manager == null ? null : GetField(manager, "_viewerWindow") as Window;
        }

        internal static Type FindType(string name)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type;
                try { type = asm.GetType(name); }
                catch { continue; }
                if (type != null)
                    return type;
            }
            return null;
        }

        // An instance field declared on the object's type or any base type.
        internal static object GetField(object target, string name)
        {
            var field = FindField(target.GetType(), name, BindingFlags.Instance);
            return field == null ? null : field.GetValue(target);
        }

        internal static FieldInfo FindField(Type type, string name, BindingFlags kind)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                var field = t.GetField(name, kind | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null)
                    return field;
            }
            return null;
        }

        // Once per kind of problem, in QuickLook's log.
        internal static void Log(string kind, string message)
        {
            lock (Logged)
            {
                if (!Logged.Add(kind))
                    return;
            }
            try { ProcessHelper.WriteLog("QuickEdit: " + message); }
            catch { }
        }

        // Subscribe to an event of a type this assembly doesn't reference
        // (WebView2, AvalonEdit), with a handler taking (object, object).
        internal static void Subscribe(object target, string eventName, object handlerTarget, string method)
        {
            var info = target.GetType().GetEvent(eventName);
            var handler = handlerTarget.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (info == null || handler == null)
            {
                Log("event-" + eventName, "event " + eventName + " not found on " + target.GetType().FullName + ".");
                return;
            }
            info.AddEventHandler(target, Delegate.CreateDelegate(info.EventHandlerType, handlerTarget, handler));
        }

        internal static string Script { get { return _script; } }
    }

    // One Markdown preview: its WebView2, the file, and the page's messages.
    internal class MarkdownHook
    {
        private readonly FrameworkElement _panel;
        private readonly object _webView;
        private readonly string _path;
        private object _core;
        private byte[] _bytes;
        private Encoding _encoding;
        private readonly KeyGuard _guard;

        public MarkdownHook(FrameworkElement panel, object webView, string path)
        {
            _panel = panel;
            _webView = webView;
            _path = path;
            _guard = new KeyGuard(panel);
        }

        public void Start()
        {
            _panel.Unloaded += delegate { _guard.Stop(); };
            Plugin.Subscribe(_webView, "NavigationCompleted", this, "OnNavigationCompleted");

            object core = null;
            try { core = ((dynamic)_webView).CoreWebView2; }
            catch { }
            if (core != null)
                OnCore();
            else
                Plugin.Subscribe(_webView, "CoreWebView2InitializationCompleted", this, "OnInitialized");
        }

        private void OnInitialized(object sender, object e)
        {
            OnCore();
        }

        private void OnNavigationCompleted(object sender, object e)
        {
            if (_core == null)
                OnCore();
            else
                Inject();
        }

        private void OnCore()
        {
            if (_core != null)
                return;
            try { _core = ((dynamic)_webView).CoreWebView2; }
            catch { _core = null; }
            if (_core == null)
                return;
            Plugin.Subscribe(_core, "WebMessageReceived", this, "OnMessage");
            Inject();
        }

        // The script ignores pages that aren't the Markdown preview, and a
        // second injection into the same page.
        private void Inject()
        {
            try { ((dynamic)_core).ExecuteScriptAsync(Plugin.Script); }
            catch (Exception e) { Plugin.Log("inject", "could not add the editing script: " + e.Message); }
        }

        private void Post(string message)
        {
            try { ((dynamic)_core).PostWebMessageAsString(message); }
            catch (Exception e) { Plugin.Log("post", "could not message the page: " + e.Message); }
        }

        private void OnMessage(object sender, object e)
        {
            string message;
            try { message = ((dynamic)e).TryGetWebMessageAsString(); }
            catch { return; }
            if (message == null)
                return;

            try
            {
                if (message == "hello")
                    SendFile();
                else if (message == "editing:1")
                    _guard.Start();
                else if (message == "editing:0")
                    _guard.Stop();
                else if (message.StartsWith("save:", StringComparison.Ordinal))
                    Save(message.Substring(5));
            }
            catch (Exception ex)
            {
                Plugin.Log("message", "message handling failed: " + ex);
                if (message.StartsWith("save:", StringComparison.Ordinal))
                    Post("error:Couldn't save: " + ex.Message);
            }
        }

        // The file as MarkdownViewer decoded it, so the page can check that
        // what it shows is what is on disk.
        private void SendFile()
        {
            try
            {
                _bytes = File.ReadAllBytes(_path);
                _encoding = Detect(_bytes);
                Post("file:" + _encoding.GetString(_bytes));
            }
            catch (Exception e)
            {
                Post("nofile:Couldn't read the file: " + e.Message);
            }
            if (!KeyGuard.Available)
                Post("keys:missing");
        }

        private void Save(string text)
        {
            if (_bytes == null || _encoding == null)
            {
                Post("error:The file wasn't read yet; reopen the preview.");
                return;
            }

            byte[] disk;
            try { disk = File.ReadAllBytes(_path); }
            catch (Exception e)
            {
                Post("error:Couldn't read the file before saving: " + e.Message);
                return;
            }
            if (!disk.SequenceEqual(_bytes))
            {
                Post("error:The file changed on disk since it was opened, so it wasn't saved. Reopen it to edit.");
                return;
            }

            var content = RestoreLineEndings(_encoding.GetString(_bytes), text);
            var bytes = _encoding.GetBytes(content);
            if (_encoding.GetString(bytes) != content)
            {
                Post("error:Some characters can't be stored in this file's encoding (" + _encoding.WebName + "), so it wasn't saved.");
                return;
            }

            try { File.WriteAllBytes(_path, bytes); }
            catch (Exception e)
            {
                Post("error:Couldn't save: " + e.Message);
                return;
            }
            _bytes = bytes;
            Post("saved");
        }

        // MarkdownViewer: CharsetDetector.DetectFromBytes(bytes).Detected?.Encoding ?? Encoding.Default
        private static Encoding Detect(byte[] bytes)
        {
            try
            {
                var detector = Plugin.FindType("UtfUnknown.CharsetDetector");
                var method = detector == null ? null : detector.GetMethod("DetectFromBytes", new[] { typeof(byte[]) });
                if (method != null)
                {
                    dynamic result = method.Invoke(null, new object[] { bytes });
                    var detected = result.Detected;
                    if (detected != null)
                    {
                        Encoding encoding = detected.Encoding;
                        if (encoding != null)
                            return encoding;
                    }
                    return Encoding.Default;
                }
            }
            catch (Exception e)
            {
                Plugin.Log("detect", "charset detection failed: " + e.Message);
            }
            return Encoding.Default;
        }

        // The page works with LF line endings. Lines the edit didn't touch keep
        // their original ending; new or changed lines get the file's usual one.
        internal static string RestoreLineEndings(string original, string edited)
        {
            if (original.IndexOf('\r') < 0)
                return edited;

            var oldLines = original.Replace("\r\n", "\n").Split('\n');
            var crlf = new bool[oldLines.Length];
            int crlfCount = 0, lfCount = 0, line = 0;
            for (var i = 0; i < original.Length; i++)
            {
                if (original[i] != '\n')
                    continue;
                var isCrlf = i > 0 && original[i - 1] == '\r';
                crlf[line++] = isCrlf;
                if (isCrlf) crlfCount++; else lfCount++;
            }
            var usual = crlfCount >= lfCount;

            var newLines = edited.Split('\n');
            int same = 0;
            while (same < oldLines.Length - 1 && same < newLines.Length - 1 && oldLines[same] == newLines[same])
                same++;
            int tail = 0;
            while (tail < oldLines.Length - 1 - same && tail < newLines.Length - 1 - same
                   && oldLines[oldLines.Length - 1 - tail] == newLines[newLines.Length - 1 - tail])
                tail++;

            var sb = new StringBuilder(edited.Length + newLines.Length);
            for (var i = 0; i < newLines.Length; i++)
            {
                sb.Append(newLines[i]);
                if (i == newLines.Length - 1)
                    break;
                bool ending;
                if (i < same)
                    ending = crlf[i];
                else if (i >= newLines.Length - 1 - tail)
                    ending = crlf[i - newLines.Length + oldLines.Length];
                else
                    ending = usual;
                sb.Append(ending ? "\r\n" : "\n");
            }
            return sb.ToString();
        }
    }

    // QuickLook ignores Space, Enter, Esc and the arrows for a second after any
    // other key (KeystrokeDispatcher._lastInvalidKeyPressTick). Keeping that
    // timestamp current while you type stops it from closing the preview,
    // opening the file or switching files under you.
    internal class KeyGuard
    {
        private static FieldInfo _tickField;
        private static object _dispatcher;
        private static bool _looked;

        private readonly FrameworkElement _owner;
        private DispatcherTimer _timer;

        public KeyGuard(FrameworkElement owner)
        {
            _owner = owner;
        }

        public static bool Available
        {
            get
            {
                if (_looked)
                    return _dispatcher != null;
                _looked = true;
                try
                {
                    var type = Plugin.FindType("QuickLook.KeystrokeDispatcher");
                    var instance = type == null ? null : Plugin.FindField(type, "_instance", BindingFlags.Static);
                    _tickField = type == null ? null : Plugin.FindField(type, "_lastInvalidKeyPressTick", BindingFlags.Instance);
                    _dispatcher = instance == null ? null : instance.GetValue(null);
                    if (_tickField == null || _tickField.FieldType != typeof(long))
                        _dispatcher = null;
                }
                catch
                {
                    _dispatcher = null;
                }
                if (_dispatcher == null)
                    Plugin.Log("keys", "QuickLook's keystroke dispatcher not found; Space and Enter may act on the preview while editing.");
                return _dispatcher != null;
            }
        }

        public void Start()
        {
            if (!Available)
                return;
            if (_timer == null)
            {
                _timer = new DispatcherTimer(DispatcherPriority.Send, _owner.Dispatcher) { Interval = TimeSpan.FromMilliseconds(50) };
                _timer.Tick += delegate { Tick(); };
            }
            Tick();
            _timer.Start();
        }

        public void Stop()
        {
            if (_timer != null)
                _timer.Stop();
        }

        private void Tick()
        {
            if (!_owner.IsVisible)
            {
                Stop();
                return;
            }
            try { _tickField.SetValue(_dispatcher, DateTime.Now.Ticks); }
            catch { Stop(); }
        }
    }

    // One plain text preview: TextViewer's AvalonEdit editor, read-only until a
    // double-click. The title bar says when it is editable or unsaved.
    internal class TextHook
    {
        private readonly FrameworkElement _panel;
        private readonly ContextObject _context;
        private readonly string _path;
        private readonly KeyGuard _guard;
        private string _title;
        private bool _editing;
        private bool _blocked;
        private byte[] _bytes;
        private Encoding _encoding;
        private DispatcherTimer _flash;

        public TextHook(FrameworkElement panel, ContextObject context, string path)
        {
            _panel = panel;
            _context = context;
            _path = path;
            _guard = new KeyGuard(panel);
        }

        public void Start()
        {
            _panel.PreviewMouseLeftButtonDown += OnMouseDown;
            _panel.PreviewKeyDown += OnKeyDown;
            _panel.Unloaded += delegate { _guard.Stop(); };
            Plugin.Subscribe(_panel, "TextChanged", this, "OnTextChanged");
        }

        private dynamic Editor { get { return _panel; } }

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2 && !_editing)
                BeginEdit();                            // AvalonEdit still selects the word
        }

        private void BeginEdit()
        {
            try
            {
                if (_blocked)
                {
                    Flash("can't edit here: this preview isn't the file's exact text");
                    return;
                }
                object document = Editor.Document;
                if (document == null)
                    return;                             // still loading
                if (_bytes == null)
                {
                    string shown = Editor.Document.Text;
                    Encoding encoding = Editor.Encoding;
                    _encoding = encoding ?? Encoding.UTF8;
                    _bytes = File.ReadAllBytes(_path);
                    if (_encoding.GetString(_bytes) != shown)
                    {
                        // Over 5 MB (shown truncated) or converted for display.
                        _blocked = true;
                        _bytes = null;
                        Flash("can't edit here: this preview isn't the file's exact text");
                        return;
                    }
                    Editor.Document.UndoStack.MarkAsOriginalFile();
                }
                Editor.IsReadOnly = false;
                _editing = true;
                _guard.Start();
                UpdateTitle();
            }
            catch (Exception ex)
            {
                Plugin.Log("text-edit", "could not start editing: " + ex.Message);
            }
        }

        private void EndEdit()
        {
            try { Editor.IsReadOnly = true; }
            catch { }
            _editing = false;
            _guard.Stop();
            UpdateTitle();
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (ctrl && e.Key == Key.S)
            {
                e.Handled = true;
                Save();
            }
            else if (e.Key == Key.Escape && _editing)
            {
                e.Handled = true;                       // stop editing; a second Esc closes the preview
                EndEdit();
            }
        }

        private void OnTextChanged(object sender, object e)
        {
            UpdateTitle();
        }

        private bool Dirty
        {
            get
            {
                try { return _bytes != null && !(bool)Editor.Document.UndoStack.IsOriginalFile; }
                catch { return false; }
            }
        }

        private void Save()
        {
            if (_bytes == null || !Dirty)
            {
                if (_editing)
                    EndEdit();
                return;
            }
            try
            {
                string text = Editor.Document.Text;
                var disk = File.ReadAllBytes(_path);
                if (!disk.SequenceEqual(_bytes))
                {
                    Flash("not saved: the file changed on disk since it was opened");
                    return;
                }
                var bytes = _encoding.GetBytes(text);
                if (_encoding.GetString(bytes) != text)
                {
                    Flash("not saved: some characters can't be stored as " + _encoding.WebName);
                    return;
                }
                File.WriteAllBytes(_path, bytes);
                _bytes = bytes;
                Editor.Document.UndoStack.MarkAsOriginalFile();
                if (_editing)
                    EndEdit();
                Flash("saved");
            }
            catch (Exception ex)
            {
                Flash("not saved: " + ex.Message);
            }
        }

        private string BaseTitle
        {
            get
            {
                if (_title == null)
                    _title = _context.Title;
                return _title;
            }
        }

        private void UpdateTitle()
        {
            if (_flash != null && _flash.IsEnabled)
                return;
            var title = BaseTitle;
            if (_editing)
                title += Dirty ? "  ·  editing, unsaved: Ctrl+S saves, Esc stops" : "  ·  editing: Ctrl+S saves, Esc stops";
            else if (Dirty)
                title += "  ·  unsaved: Ctrl+S saves";
            _context.Title = title;
        }

        // A message in the title bar for a few seconds.
        private void Flash(string message)
        {
            _context.Title = BaseTitle + "  ·  " + message;
            if (_flash == null)
            {
                _flash = new DispatcherTimer(DispatcherPriority.Normal, _panel.Dispatcher) { Interval = TimeSpan.FromSeconds(3) };
                _flash.Tick += delegate
                {
                    _flash.Stop();
                    UpdateTitle();
                };
            }
            _flash.Stop();
            _flash.Start();
        }
    }
}

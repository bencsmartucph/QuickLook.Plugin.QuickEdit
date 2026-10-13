// QuickLook.Plugin.QuickEdit
// Copyright (C) 2026 Ben Smart. Licensed under the GNU GPL v3 or later (see LICENSE).
//
// Edit a few words of a Markdown file straight in its QuickLook preview:
// double-click rendered text, type, press Enter; Ctrl+S writes the file.
// Nothing is saved unless you press Ctrl+S.
//
// This plugin renders nothing itself. QuickLook's own MarkdownViewer draws the
// preview (table of contents, highlighting, diagrams) as before; for each
// Markdown preview this plugin reaches its WebView2, adds QuickEdit.js to the
// page, and handles the page's messages: it reads and writes the file, and
// while you type it keeps QuickLook's keyboard hook from treating Space,
// Enter, Esc and the arrows as "close" / "open" / "next file".
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

        private static readonly ConditionalWeakTable<object, Hook> Hooks = new ConditionalWeakTable<object, Hook>();
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

        // True when the current preview is a Markdown file this plugin edits.
        private static bool TryAttach()
        {
            if (_script == null)
                return false;

            var window = FindViewerWindow();
            if (window == null)
                return false;

            var path = GetField(window, "_path") as string;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return false;
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (!Extensions.Contains(ext))
                return false;

            var contextProperty = window.GetType().GetProperty("ContextObject", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var context = contextProperty == null ? null : contextProperty.GetValue(window, null) as ContextObject;
            var panel = context == null ? null : context.ViewerContent as FrameworkElement;
            if (panel == null || !IsMarkdownPanel(panel.GetType()))
                return false;

            Hook existing;
            if (Hooks.TryGetValue(panel, out existing))
                return true;

            var webView = GetField(panel, "_webView");
            if (webView == null)
                return false;                           // WebView2 missing: MarkdownViewer shows a download button

            var hook = new Hook(panel, webView, path);
            Hooks.Add(panel, hook);
            hook.Start();
            return true;
        }

        private static bool IsMarkdownPanel(Type type)
        {
            for (var t = type; t != null; t = t.BaseType)
                if (t.FullName == "QuickLook.Plugin.MarkdownViewer.MarkdownPanel")
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

        internal static string Script { get { return _script; } }
    }

    // One Markdown preview: its WebView2, the file, and the page's messages.
    internal class Hook
    {
        private static FieldInfo _tickField;
        private static object _dispatcher;
        private static bool _dispatcherLooked;

        private readonly FrameworkElement _panel;
        private readonly object _webView;
        private readonly string _path;
        private object _core;
        private byte[] _bytes;
        private Encoding _encoding;
        private DispatcherTimer _quiet;

        public Hook(FrameworkElement panel, object webView, string path)
        {
            _panel = panel;
            _webView = webView;
            _path = path;
        }

        public void Start()
        {
            _panel.Unloaded += delegate { StopQuiet(); };
            Subscribe(_webView, "NavigationCompleted", "OnNavigationCompleted");

            object core = null;
            try { core = ((dynamic)_webView).CoreWebView2; }
            catch { }
            if (core != null)
                OnCore();
            else
                Subscribe(_webView, "CoreWebView2InitializationCompleted", "OnInitialized");
        }

        private void Subscribe(object target, string eventName, string method)
        {
            var info = target.GetType().GetEvent(eventName);
            var handler = typeof(Hook).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic);
            if (info == null || handler == null)
            {
                Plugin.Log("event-" + eventName, "WebView2 event " + eventName + " not found.");
                return;
            }
            info.AddEventHandler(target, Delegate.CreateDelegate(info.EventHandlerType, this, handler));
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
            Subscribe(_core, "WebMessageReceived", "OnMessage");
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
                    StartQuiet();
                else if (message == "editing:0")
                    StopQuiet();
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
            if (!FindDispatcher())
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

        // QuickLook ignores Space, Enter, Esc and the arrows for a second after
        // any other key (KeystrokeDispatcher._lastInvalidKeyPressTick). Keeping
        // that timestamp current while you type stops it from closing the
        // preview, opening the file or switching files under you.
        private static bool FindDispatcher()
        {
            if (_dispatcherLooked)
                return _dispatcher != null;
            _dispatcherLooked = true;
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

        private void StartQuiet()
        {
            if (!FindDispatcher())
                return;
            if (_quiet == null)
            {
                _quiet = new DispatcherTimer(DispatcherPriority.Send, _panel.Dispatcher) { Interval = TimeSpan.FromMilliseconds(50) };
                _quiet.Tick += delegate { Quiet(); };
            }
            Quiet();
            _quiet.Start();
        }

        private void Quiet()
        {
            if (!_panel.IsVisible)
            {
                StopQuiet();
                return;
            }
            try { _tickField.SetValue(_dispatcher, DateTime.Now.Ticks); }
            catch { StopQuiet(); }
        }

        private void StopQuiet()
        {
            if (_quiet != null)
                _quiet.Stop();
        }
    }
}

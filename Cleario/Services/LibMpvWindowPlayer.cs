using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace Cleario.Services
{
    /// <summary>
    /// libmpv player that lets mpv render directly into a native child HWND.
    /// This keeps decoding/rendering on the GPU instead of copying every frame
    /// through a WinUI WriteableBitmap, which is required for stable 4K HDR/DV.
    /// </summary>
    public sealed class LibMpvWindowPlayer : IDisposable
    {
        private const string MpvDll = "libmpv-2.dll";

        private const int MPV_FORMAT_STRING = 1;
        private const int MPV_FORMAT_FLAG = 3;
        private const int MPV_FORMAT_INT64 = 4;
        private const int MPV_FORMAT_DOUBLE = 5;
        private const int MPV_FORMAT_NODE = 6;
        private const int MPV_FORMAT_NODE_ARRAY = 7;
        private const int MPV_FORMAT_NODE_MAP = 8;

        private const int MPV_EVENT_NONE = 0;
        private const int MPV_EVENT_SHUTDOWN = 1;
        private const int MPV_EVENT_END_FILE = 7;
        private const int MPV_EVENT_FILE_LOADED = 8;
        private const int MPV_EVENT_VIDEO_RECONFIG = 17;
        private const int MPV_EVENT_PROPERTY_CHANGE = 22;

        private const int MPV_END_FILE_REASON_EOF = 0;
        private const int MPV_END_FILE_REASON_STOP = 2;
        private const int MPV_END_FILE_REASON_QUIT = 3;
        private const int MPV_END_FILE_REASON_ERROR = 4;
        private const int MPV_END_FILE_REASON_REDIRECT = 5;

        private readonly object _mpvAccessLock = new();
        private readonly object _stateLock = new();
        private readonly IntPtr _windowHandle;

        private IntPtr _mpv = IntPtr.Zero;
        private bool _disposed;
        private bool _eventLoopRunning;
        private Thread? _eventThread;
        private volatile bool _shutdownRequested;

        private bool _startupReadyRaised;
        private bool _endReachedRaised;
        private bool _fileLoaded;
        private bool _voConfigured;
        private DateTime _lastPlaybackStateChangedUtc = DateTime.MinValue;

        private double _cachedTimeSeconds;
        private double _cachedDurationSeconds;
        private double _cachedVolume = 100;
        private bool _cachedPause = true;
        private bool _cachedEof;
        private bool _cachedIdle = true;
        private bool _cachedPausedForCache;
        private long _cachedCacheBufferingState = 100;
        private int _cachedAudioTrack = int.MinValue;
        private int _cachedSubtitleTrack = -1;
        private List<PlayerTrackChoice> _cachedAudioTracks = new();
        private List<PlayerTrackChoice> _cachedSubtitleTracks = new();

        static LibMpvWindowPlayer()
        {
            try
            {
                NativeLibrary.SetDllImportResolver(typeof(LibMpvWindowPlayer).Assembly, ResolveNativeLibrary);
            }
            catch (InvalidOperationException)
            {
                // Another mpv wrapper in this assembly already registered the resolver.
            }
        }

        public LibMpvWindowPlayer(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero)
                throw new ArgumentException("MPV video window handle is missing.", nameof(windowHandle));

            _windowHandle = windowHandle;
        }

        public event EventHandler? FirstFrameRendered;
        public event EventHandler? EndReached;
        public event EventHandler<string>? PlaybackError;
        public event EventHandler? PlaybackStateChanged;

        public bool HasRenderedFrame
        {
            get
            {
                lock (_stateLock)
                    return _startupReadyRaised;
            }
        }

        public bool IsPlaying
        {
            get
            {
                lock (_stateLock)
                    return !_cachedPause && !_cachedEof && !_cachedIdle && !_cachedPausedForCache;
            }
        }

        public bool IsBuffering
        {
            get
            {
                lock (_stateLock)
                    return _cachedPausedForCache;
            }
        }

        public long CacheBufferingState
        {
            get
            {
                lock (_stateLock)
                    return Math.Clamp(_cachedCacheBufferingState, 0, 100);
            }
        }

        public long Time
        {
            get
            {
                lock (_stateLock)
                    return SecondsToMilliseconds(_cachedTimeSeconds);
            }
            set
            {
                var seconds = Math.Max(0, value) / 1000.0;
                CommandAsyncNoThrow("seek", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "absolute", "keyframes");
            }
        }

        public long Length
        {
            get
            {
                lock (_stateLock)
                    return SecondsToMilliseconds(_cachedDurationSeconds);
            }
        }

        public int Volume
        {
            get
            {
                lock (_stateLock)
                    return (int)Math.Round(_cachedVolume);
            }
            set
            {
                var volume = Math.Clamp(value, 0, 200);
                lock (_stateLock)
                    _cachedVolume = volume;

                SetDoublePropertyNoThrow("volume", volume);
            }
        }

        public int AudioTrack
        {
            get
            {
                lock (_stateLock)
                    return _cachedAudioTrack;
            }
        }

        public int SubtitleTrack
        {
            get
            {
                lock (_stateLock)
                    return _cachedSubtitleTrack;
            }
        }

        public static string ExpectedLibraryFolder => Path.Combine(AppContext.BaseDirectory, "Players", "mpv");

        public static string? FindMpvLibraryPath()
        {
            var baseDir = AppContext.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(baseDir, "Players", "mpv", "libmpv-2.dll"),
                Path.Combine(baseDir, "Players", "mpv", "mpv-2.dll"),
                Path.Combine(baseDir, "libmpv-2.dll"),
                Path.Combine(baseDir, "mpv-2.dll")
            };

            return candidates.FirstOrDefault(File.Exists);
        }

        public static string BuildMpvLogPath()
        {
            try
            {
                var folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Cleario",
                    "Logs");
                Directory.CreateDirectory(folder);
                return Path.Combine(folder, "mpv-last.log");
            }
            catch
            {
                return Path.Combine(AppContext.BaseDirectory, "mpv-last.log");
            }
        }

        public void Start(string url, int volume, long startPositionMs = 0)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(LibMpvWindowPlayer));

            if (string.IsNullOrWhiteSpace(url))
                throw new ArgumentException("Playback URL is empty.", nameof(url));

            var libraryPath = FindMpvLibraryPath();
            if (string.IsNullOrWhiteSpace(libraryPath))
                throw new FileNotFoundException($"libmpv-2.dll was not found. Put the extracted libmpv files in: {ExpectedLibraryFolder}");

            try
            {
                lock (_mpvAccessLock)
                {
                    _mpv = mpv_create();
                    if (_mpv == IntPtr.Zero)
                        throw new InvalidOperationException("mpv_create failed.");

                    SetOptionString("config", "no");
                    SetOptionString("terminal", "no");
                    SetOptionString("osc", "no");
                    SetOptionString("input-default-bindings", "no");
                    SetOptionString("input-vo-keyboard", "no");
                    // Keep mpv aware of cursor movement so its native Win32 video window can honor
                    // cursor-autohide. Cleario still owns controls because OSC/default bindings/VO
                    // keyboard input are disabled.
                    TrySetOptionString("input-cursor", "yes");
                    TrySetOptionString("input-media-keys", "no");
                    TrySetOptionString("cursor-autohide", "always");
                    TrySetOptionString("cursor-autohide-fs-only", "no");
                    SetOptionString("wid", _windowHandle.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture));

                    // Use MPV's real GPU output. The bad 4K files reported by ffprobe are Dolby Vision
                    // Profile 5 / full-range HEVC Main10. They must stay on the GPU path; copy-back or
                    // WriteableBitmap rendering will lag badly or black-screen.
                    TrySetOptionString("log-file", BuildMpvLogPath());
                    TrySetOptionString("msg-level", "all=warn,vo/gpu-next=info,vd=info,vd_lavc=info,ffmpeg/video=info,demux=warn");

                    if (!TrySetOptionString("vo", "gpu-next"))
                        TrySetOptionString("vo", "gpu");

                    // Force the Windows D3D11 path so HEVC Main10/DV is decoded and presented without
                    // copying 4K 10-bit frames back through system RAM. If d3d11va is not usable on a
                    // machine, mpv can still fall back to its normal safe auto probing.
                    TrySetOptionString("gpu-context", "d3d11");
                    TrySetOptionString("gpu-api", "d3d11");
                    if (!TrySetOptionString("hwdec", "d3d11va"))
                        TrySetOptionString("hwdec", "auto-safe");
                    TrySetOptionString("gpu-hwdec-interop", "d3d11va");
                    TrySetOptionString("hwdec-codecs", "hevc,h264,vc1,mpeg2video,vp8,vp9,av1");
                    TrySetOptionString("vd-lavc-dr", "yes");

                    // Dolby Vision/HDR rendering hints. These do not transcode or downscale the stream;
                    // they only tell mpv/libplacebo to send the right output colorspace and avoid expensive
                    // per-frame peak analysis that can stutter on some DV Profile 5 files.
                    TrySetOptionString("target-colorspace-hint", "auto");
                    TrySetOptionString("target-colorspace-hint-mode", "target");
                    TrySetOptionString("target-colorspace-hint-strict", "yes");
                    TrySetOptionString("tone-mapping", "auto");
                    TrySetOptionString("gamut-mapping-mode", "auto");
                    TrySetOptionString("hdr-compute-peak", "no");

                    TrySetOptionString("hr-seek", "no");
                    TrySetOptionString("hr-seek-framedrop", "yes");
                    TrySetOptionString("video-sync", "audio");
                    TrySetOptionString("interpolation", "no");
                    TrySetOptionString("demuxer-max-bytes", "128MiB");
                    TrySetOptionString("demuxer-readahead-secs", "20");
                    TrySetOptionString("keep-open", "no");
                    TrySetOptionString("force-window", "yes");
                    TrySetOptionString("keepaspect", "yes");
                    TrySetOptionString("keepaspect-window", "yes");
                    TrySetOptionString("video-aspect-override", "-1");
                    TrySetOptionString("video-unscaled", "no");
                    TrySetOptionString("panscan", "0");

                    if (startPositionMs > 15_000)
                    {
                        var startSeconds = Math.Max(0, startPositionMs / 1000.0);
                        TrySetOptionString("start", startSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                    }

                    ThrowIfError(mpv_initialize(_mpv), "mpv_initialize");
                    ObserveProperties();
                }

                StartEventLoop();
                Volume = volume;
                CommandAsyncNoThrow("loadfile", url, "replace");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void RefreshCachedPlaybackState()
        {
            if (_disposed || _mpv == IntPtr.Zero)
                return;

            var time = GetDoublePropertyNoThrow("time-pos");
            if (time.HasValue)
            {
                lock (_stateLock)
                    _cachedTimeSeconds = time.Value;
            }

            var duration = GetDoublePropertyNoThrow("duration");
            if (duration.HasValue)
            {
                lock (_stateLock)
                    _cachedDurationSeconds = duration.Value;
            }
        }

        public void Pause()
        {
            SetPause(true);
        }

        public void SetPause(bool pause)
        {
            lock (_stateLock)
                _cachedPause = pause;

            SetFlagPropertyNoThrow("pause", pause);
        }

        public void Stop()
        {
            lock (_stateLock)
            {
                _cachedPause = true;
                _cachedEof = true;
                _cachedIdle = true;
            }

            // Queue the stop instead of synchronously blocking the UI on slow HDR/DV network teardown.
            CommandAsyncNoThrow("stop");
            CommandAsyncNoThrow("playlist-clear");
        }

        public void SetAudioTrack(int id)
        {
            lock (_stateLock)
                _cachedAudioTrack = id < 0 ? int.MinValue : id;

            if (id == int.MinValue || id < 0)
                CommandAsyncNoThrow("set", "aid", "auto");
            else
                CommandAsyncNoThrow("set", "aid", id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        public void SetSubtitleTrack(int id)
        {
            lock (_stateLock)
                _cachedSubtitleTrack = id < 0 ? -1 : id;

            if (id < 0)
                CommandAsyncNoThrow("set", "sid", "no");
            else
                CommandAsyncNoThrow("set", "sid", id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        public List<PlayerTrackChoice> GetAudioTracks()
        {
            lock (_stateLock)
                return _cachedAudioTracks.ToList();
        }

        public List<PlayerTrackChoice> GetSubtitleTracks()
        {
            lock (_stateLock)
            {
                var tracks = _cachedSubtitleTracks.ToList();
                if (!tracks.Any(x => x.Id < 0))
                    tracks.Insert(0, new PlayerTrackChoice(-1, "Off"));
                return tracks;
            }
        }

        private void ObserveProperties()
        {
            ObserveProperty(1, "time-pos", MPV_FORMAT_DOUBLE);
            ObserveProperty(2, "duration", MPV_FORMAT_DOUBLE);
            ObserveProperty(3, "volume", MPV_FORMAT_DOUBLE);
            ObserveProperty(4, "pause", MPV_FORMAT_FLAG);
            ObserveProperty(5, "eof-reached", MPV_FORMAT_FLAG);
            ObserveProperty(6, "core-idle", MPV_FORMAT_FLAG);
            ObserveProperty(7, "track-list", MPV_FORMAT_NODE);
            ObserveProperty(8, "aid", MPV_FORMAT_STRING);
            ObserveProperty(9, "sid", MPV_FORMAT_STRING);
            ObserveProperty(10, "vo-configured", MPV_FORMAT_FLAG);
            ObserveProperty(11, "paused-for-cache", MPV_FORMAT_FLAG);
            ObserveProperty(12, "cache-buffering-state", MPV_FORMAT_INT64);
        }

        private void ObserveProperty(ulong id, string name, int format)
        {
            _ = mpv_observe_property(_mpv, id, name, format);
        }

        private void StartEventLoop()
        {
            _eventLoopRunning = true;
            _eventThread = new Thread(EventLoop)
            {
                IsBackground = true,
                Name = "Cleario libmpv window event loop"
            };
            _eventThread.Start();
        }

        private void EventLoop()
        {
            while (_eventLoopRunning && !_disposed)
            {
                IntPtr eventPtr;
                try
                {
                    eventPtr = mpv_wait_event(_mpv, 0.25);
                }
                catch
                {
                    return;
                }

                if (eventPtr == IntPtr.Zero)
                    continue;

                MpvEvent ev;
                try
                {
                    ev = Marshal.PtrToStructure<MpvEvent>(eventPtr);
                }
                catch
                {
                    continue;
                }

                if (ev.EventId == MPV_EVENT_NONE)
                    continue;

                if (ev.EventId == MPV_EVENT_SHUTDOWN)
                    return;

                if (ev.EventId == MPV_EVENT_END_FILE)
                {
                    HandleEndFileEvent(ev.Data);
                    continue;
                }

                if (ev.EventId == MPV_EVENT_FILE_LOADED)
                {
                    lock (_stateLock)
                    {
                        _fileLoaded = true;
                        _cachedIdle = false;
                        _cachedEof = false;
                        _endReachedRaised = false;
                    }
                    RefreshCachedPlaybackState();
                    RaisePlaybackStateChanged(true);
                    MaybeRaiseStartupReady();
                    continue;
                }

                if (ev.EventId == MPV_EVENT_VIDEO_RECONFIG)
                {
                    lock (_stateLock)
                        _voConfigured = true;
                    RefreshCachedPlaybackState();
                    RaisePlaybackStateChanged(true);
                    MaybeRaiseStartupReady();
                    continue;
                }

                if (ev.EventId == MPV_EVENT_PROPERTY_CHANGE && ev.Data != IntPtr.Zero)
                    HandlePropertyChange(ev.Data);
            }
        }

        private void HandleEndFileEvent(IntPtr data)
        {
            if (_shutdownRequested || _disposed)
                return;

            var reason = MPV_END_FILE_REASON_EOF;
            var error = 0;
            if (data != IntPtr.Zero)
            {
                try
                {
                    var endFile = Marshal.PtrToStructure<MpvEventEndFile>(data);
                    reason = endFile.Reason;
                    error = endFile.Error;
                }
                catch
                {
                }
            }

            lock (_stateLock)
            {
                _cachedEof = true;
                _cachedIdle = true;
            }

            if (reason == MPV_END_FILE_REASON_EOF)
            {
                RaiseEndReachedOnce();
                return;
            }

            if (reason == MPV_END_FILE_REASON_ERROR)
            {
                var message = error != 0
                    ? Marshal.PtrToStringAnsi(mpv_error_string(error)) ?? "MPV could not open this stream."
                    : "MPV could not open this stream.";
                PlaybackError?.Invoke(this, message);
                return;
            }

            // STOP/QUIT/REDIRECT are not natural playback completion. Ignoring them prevents
            // Back/Stop/startup failures from navigating to the details page as if the movie ended.
            if (reason != MPV_END_FILE_REASON_STOP && reason != MPV_END_FILE_REASON_QUIT && reason != MPV_END_FILE_REASON_REDIRECT)
                PlaybackError?.Invoke(this, $"MPV stopped before playback finished. Reason code: {reason}.");
        }

        private void HandlePropertyChange(IntPtr dataPtr)
        {
            MpvEventProperty prop;
            try
            {
                prop = Marshal.PtrToStructure<MpvEventProperty>(dataPtr);
            }
            catch
            {
                return;
            }

            var name = Marshal.PtrToStringAnsi(prop.Name) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
                return;

            try
            {
                switch (name)
                {
                    case "time-pos":
                        if (prop.Format == MPV_FORMAT_DOUBLE && prop.Data != IntPtr.Zero)
                        {
                            var value = Marshal.PtrToStructure<double>(prop.Data);
                            if (!double.IsNaN(value) && !double.IsInfinity(value) && value >= 0)
                            {
                                lock (_stateLock)
                                    _cachedTimeSeconds = value;
                                RaisePlaybackStateChanged(false);
                                MaybeRaiseStartupReady();
                            }
                        }
                        break;

                    case "duration":
                        if (prop.Format == MPV_FORMAT_DOUBLE && prop.Data != IntPtr.Zero)
                        {
                            var value = Marshal.PtrToStructure<double>(prop.Data);
                            if (!double.IsNaN(value) && !double.IsInfinity(value) && value >= 0)
                            {
                                lock (_stateLock)
                                    _cachedDurationSeconds = value;
                                RaisePlaybackStateChanged(true);
                                MaybeRaiseStartupReady();
                            }
                        }
                        break;

                    case "volume":
                        if (prop.Format == MPV_FORMAT_DOUBLE && prop.Data != IntPtr.Zero)
                        {
                            var value = Marshal.PtrToStructure<double>(prop.Data);
                            if (!double.IsNaN(value) && !double.IsInfinity(value))
                            {
                                lock (_stateLock)
                                    _cachedVolume = value;
                            }
                        }
                        break;

                    case "pause":
                        if (prop.Format == MPV_FORMAT_FLAG && prop.Data != IntPtr.Zero)
                        {
                            lock (_stateLock)
                                _cachedPause = Marshal.ReadInt32(prop.Data) != 0;
                            RaisePlaybackStateChanged(true);
                        }
                        break;

                    case "eof-reached":
                        if (prop.Format == MPV_FORMAT_FLAG && prop.Data != IntPtr.Zero)
                        {
                            var eof = Marshal.ReadInt32(prop.Data) != 0;
                            lock (_stateLock)
                                _cachedEof = eof;
                            RaisePlaybackStateChanged(true);
                        }
                        break;

                    case "core-idle":
                        if (prop.Format == MPV_FORMAT_FLAG && prop.Data != IntPtr.Zero)
                        {
                            lock (_stateLock)
                                _cachedIdle = Marshal.ReadInt32(prop.Data) != 0;
                            RaisePlaybackStateChanged(true);
                        }
                        break;

                    case "paused-for-cache":
                        if (prop.Format == MPV_FORMAT_FLAG && prop.Data != IntPtr.Zero)
                        {
                            lock (_stateLock)
                                _cachedPausedForCache = Marshal.ReadInt32(prop.Data) != 0;
                            RaisePlaybackStateChanged(true);
                        }
                        break;

                    case "cache-buffering-state":
                        if (prop.Data != IntPtr.Zero)
                        {
                            long value;
                            if (prop.Format == MPV_FORMAT_INT64)
                                value = Marshal.ReadInt64(prop.Data);
                            else if (prop.Format == MPV_FORMAT_DOUBLE)
                                value = (long)Math.Round(Marshal.PtrToStructure<double>(prop.Data));
                            else
                                break;

                            lock (_stateLock)
                                _cachedCacheBufferingState = Math.Clamp(value, 0, 100);
                            RaisePlaybackStateChanged(false);
                        }
                        break;

                    case "vo-configured":
                        if (prop.Format == MPV_FORMAT_FLAG && prop.Data != IntPtr.Zero)
                        {
                            lock (_stateLock)
                                _voConfigured = Marshal.ReadInt32(prop.Data) != 0;
                            RaisePlaybackStateChanged(true);
                            MaybeRaiseStartupReady();
                        }
                        break;

                    case "aid":
                        if (prop.Format == MPV_FORMAT_STRING && prop.Data != IntPtr.Zero)
                        {
                            var id = ParseMpvTrackId(Marshal.PtrToStringAnsi(prop.Data), int.MinValue);
                            lock (_stateLock)
                                _cachedAudioTrack = id;
                        }
                        break;

                    case "sid":
                        if (prop.Format == MPV_FORMAT_STRING && prop.Data != IntPtr.Zero)
                        {
                            var id = ParseMpvTrackId(Marshal.PtrToStringAnsi(prop.Data), -1);
                            lock (_stateLock)
                                _cachedSubtitleTrack = id;
                        }
                        break;

                    case "track-list":
                        if (prop.Format == MPV_FORMAT_NODE && prop.Data != IntPtr.Zero)
                        {
                            var root = Marshal.PtrToStructure<MpvNode>(prop.Data);
                            UpdateCachedTracksFromNode(root);
                            RaisePlaybackStateChanged(true);
                        }
                        break;
                }
            }
            catch
            {
            }
        }

        private void MaybeRaiseStartupReady()
        {
            bool shouldRaise;
            lock (_stateLock)
            {
                if (_startupReadyRaised)
                    return;

                shouldRaise = _fileLoaded && (_voConfigured || _cachedTimeSeconds > 0.05 || _cachedDurationSeconds > 0);
                if (shouldRaise)
                    _startupReadyRaised = true;
            }

            if (!shouldRaise)
                return;

            RaisePlaybackStateChanged(true);
            FirstFrameRendered?.Invoke(this, EventArgs.Empty);
        }

        private void RaisePlaybackStateChanged(bool immediate)
        {
            var handler = PlaybackStateChanged;
            if (handler == null)
                return;

            if (!immediate)
            {
                var now = DateTime.UtcNow;
                if ((now - _lastPlaybackStateChangedUtc) < TimeSpan.FromMilliseconds(180))
                    return;

                _lastPlaybackStateChangedUtc = now;
            }
            else
            {
                _lastPlaybackStateChangedUtc = DateTime.UtcNow;
            }

            try
            {
                handler(this, EventArgs.Empty);
            }
            catch
            {
            }
        }

        private void RaiseEndReachedOnce()
        {
            var shouldRaise = false;
            lock (_stateLock)
            {
                if (!_endReachedRaised)
                {
                    _endReachedRaised = true;
                    shouldRaise = true;
                }
            }

            if (shouldRaise)
                EndReached?.Invoke(this, EventArgs.Empty);
        }

        private static int ParseMpvTrackId(string? value, int fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
                return fallback;

            value = value.Trim();
            if (value.Equals("no", StringComparison.OrdinalIgnoreCase) || value.Equals("auto", StringComparison.OrdinalIgnoreCase))
                return fallback;

            return int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var id)
                ? id
                : fallback;
        }

        private void UpdateCachedTracksFromNode(MpvNode root)
        {
            var audioTracks = new List<PlayerTrackChoice>();
            var subtitleTracks = new List<PlayerTrackChoice>();
            var selectedAudio = int.MinValue;
            var selectedSubtitle = -1;

            if (root.Format != MPV_FORMAT_NODE_ARRAY || root.Data == IntPtr.Zero)
                return;

            var list = Marshal.PtrToStructure<MpvNodeList>(root.Data);
            var nodeSize = Marshal.SizeOf<MpvNode>();

            for (var i = 0; i < list.Num; i++)
            {
                var itemPtr = IntPtr.Add(list.Values, i * nodeSize);
                var item = Marshal.PtrToStructure<MpvNode>(itemPtr);
                if (item.Format != MPV_FORMAT_NODE_MAP || item.Data == IntPtr.Zero)
                    continue;

                var map = ReadNodeMap(item.Data);
                if (!map.TryGetValue("type", out var typeNode))
                    continue;

                var type = NodeToString(typeNode);
                if (!string.Equals(type, "audio", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(type, "sub", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var id = map.TryGetValue("id", out var idNode) ? NodeToInt(idNode, -1) : -1;
                if (id < 0)
                    continue;

                var title = map.TryGetValue("title", out var titleNode) ? NodeToString(titleNode) : string.Empty;
                var lang = map.TryGetValue("lang", out var langNode) ? NodeToString(langNode) : string.Empty;
                var labelParts = new List<string>();
                if (!string.IsNullOrWhiteSpace(lang))
                    labelParts.Add(lang);
                if (!string.IsNullOrWhiteSpace(title))
                    labelParts.Add(title);

                var label = labelParts.Count > 0 ? string.Join(" - ", labelParts) : (type == "audio" ? $"Audio {id}" : $"Subtitle {id}");
                var selected = map.TryGetValue("selected", out var selectedNode) && NodeToBool(selectedNode);

                if (string.Equals(type, "audio", StringComparison.OrdinalIgnoreCase))
                {
                    audioTracks.Add(new PlayerTrackChoice(id, label));
                    if (selected)
                        selectedAudio = id;
                }
                else
                {
                    subtitleTracks.Add(new PlayerTrackChoice(id, label));
                    if (selected)
                        selectedSubtitle = id;
                }
            }

            lock (_stateLock)
            {
                _cachedAudioTracks = audioTracks;
                _cachedSubtitleTracks = subtitleTracks;
                if (selectedAudio != int.MinValue)
                    _cachedAudioTrack = selectedAudio;
                _cachedSubtitleTrack = selectedSubtitle;
            }
        }

        private static Dictionary<string, MpvNode> ReadNodeMap(IntPtr nodeListPtr)
        {
            var result = new Dictionary<string, MpvNode>(StringComparer.OrdinalIgnoreCase);
            var list = Marshal.PtrToStructure<MpvNodeList>(nodeListPtr);
            var nodeSize = Marshal.SizeOf<MpvNode>();

            for (var i = 0; i < list.Num; i++)
            {
                var keyPtr = Marshal.ReadIntPtr(list.Keys, i * IntPtr.Size);
                var key = Marshal.PtrToStringAnsi(keyPtr);
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                var valuePtr = IntPtr.Add(list.Values, i * nodeSize);
                result[key] = Marshal.PtrToStructure<MpvNode>(valuePtr);
            }

            return result;
        }

        private static string NodeToString(MpvNode node)
        {
            return node.Format switch
            {
                MPV_FORMAT_STRING => Marshal.PtrToStringAnsi(node.Data) ?? string.Empty,
                MPV_FORMAT_INT64 => node.Data.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),
                MPV_FORMAT_DOUBLE => Int64BitsToDouble(node.Data.ToInt64()).ToString(System.Globalization.CultureInfo.InvariantCulture),
                MPV_FORMAT_FLAG => node.Data.ToInt64() != 0 ? "yes" : "no",
                _ => string.Empty
            };
        }

        private static int NodeToInt(MpvNode node, int fallback)
        {
            if (node.Format == MPV_FORMAT_INT64)
                return unchecked((int)node.Data.ToInt64());

            if (node.Format == MPV_FORMAT_STRING && int.TryParse(Marshal.PtrToStringAnsi(node.Data), out var parsed))
                return parsed;

            return fallback;
        }

        private static bool NodeToBool(MpvNode node)
        {
            if (node.Format == MPV_FORMAT_FLAG)
                return node.Data.ToInt64() != 0;

            if (node.Format == MPV_FORMAT_STRING)
            {
                var value = Marshal.PtrToStringAnsi(node.Data) ?? string.Empty;
                return value.Equals("yes", StringComparison.OrdinalIgnoreCase) || value.Equals("true", StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        private static long SecondsToMilliseconds(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0)
                return 0;

            return (long)Math.Round(seconds * 1000.0);
        }

        private void SetOptionString(string name, string value)
        {
            ThrowIfError(mpv_set_option_string(_mpv, name, value), $"set option {name}");
        }

        private bool TrySetOptionString(string name, string value)
        {
            try
            {
                if (_mpv == IntPtr.Zero)
                    return false;

                return mpv_set_option_string(_mpv, name, value) >= 0;
            }
            catch
            {
                return false;
            }
        }

        private double? GetDoublePropertyNoThrow(string name)
        {
            if (_disposed || _mpv == IntPtr.Zero)
                return null;

            var ptr = Marshal.AllocHGlobal(sizeof(double));
            try
            {
                lock (_mpvAccessLock)
                {
                    if (_disposed || _mpv == IntPtr.Zero)
                        return null;

                    if (mpv_get_property(_mpv, name, MPV_FORMAT_DOUBLE, ptr) < 0)
                        return null;
                }

                var value = Marshal.PtrToStructure<double>(ptr);
                return double.IsNaN(value) || double.IsInfinity(value) || value < 0 ? null : value;
            }
            catch
            {
                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        private void SetPropertyStringNoThrow(string name, string value)
        {
            if (_disposed || _mpv == IntPtr.Zero)
                return;

            try
            {
                lock (_mpvAccessLock)
                {
                    if (!_disposed && _mpv != IntPtr.Zero)
                        _ = mpv_set_property_string(_mpv, name, value);
                }
            }
            catch
            {
            }
        }

        private void SetFlagPropertyNoThrow(string name, bool value)
        {
            if (_disposed || _mpv == IntPtr.Zero)
                return;

            var ptr = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                Marshal.WriteInt32(ptr, value ? 1 : 0);
                lock (_mpvAccessLock)
                {
                    if (!_disposed && _mpv != IntPtr.Zero)
                        _ = mpv_set_property(_mpv, name, MPV_FORMAT_FLAG, ptr);
                }
            }
            catch
            {
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        private void SetDoublePropertyNoThrow(string name, double value)
        {
            if (_disposed || _mpv == IntPtr.Zero)
                return;

            var ptr = Marshal.AllocHGlobal(sizeof(double));
            try
            {
                Marshal.StructureToPtr(value, ptr, false);
                lock (_mpvAccessLock)
                {
                    if (!_disposed && _mpv != IntPtr.Zero)
                        _ = mpv_set_property(_mpv, name, MPV_FORMAT_DOUBLE, ptr);
                }
            }
            catch
            {
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        private void Command(params string[] args)
        {
            if (_disposed || _mpv == IntPtr.Zero)
                return;

            var nativeStrings = new List<IntPtr>();
            var argv = IntPtr.Zero;
            try
            {
                argv = Marshal.AllocHGlobal((args.Length + 1) * IntPtr.Size);
                for (var i = 0; i < args.Length; i++)
                {
                    var argPtr = Marshal.StringToHGlobalAnsi(args[i]);
                    nativeStrings.Add(argPtr);
                    Marshal.WriteIntPtr(argv, i * IntPtr.Size, argPtr);
                }
                Marshal.WriteIntPtr(argv, args.Length * IntPtr.Size, IntPtr.Zero);

                lock (_mpvAccessLock)
                {
                    if (!_disposed && _mpv != IntPtr.Zero)
                        ThrowIfError(mpv_command(_mpv, argv), $"command {string.Join(" ", args)}");
                }
            }
            finally
            {
                foreach (var ptr in nativeStrings)
                    Marshal.FreeHGlobal(ptr);
                if (argv != IntPtr.Zero)
                    Marshal.FreeHGlobal(argv);
            }
        }

        private void CommandNoThrow(params string[] args)
        {
            try
            {
                Command(args);
            }
            catch
            {
            }
        }

        private void CommandAsyncNoThrow(params string[] args)
        {
            if (_disposed || _mpv == IntPtr.Zero)
                return;

            var nativeStrings = new List<IntPtr>();
            var argv = IntPtr.Zero;
            try
            {
                argv = Marshal.AllocHGlobal((args.Length + 1) * IntPtr.Size);
                for (var i = 0; i < args.Length; i++)
                {
                    var argPtr = Marshal.StringToHGlobalAnsi(args[i]);
                    nativeStrings.Add(argPtr);
                    Marshal.WriteIntPtr(argv, i * IntPtr.Size, argPtr);
                }
                Marshal.WriteIntPtr(argv, args.Length * IntPtr.Size, IntPtr.Zero);

                lock (_mpvAccessLock)
                {
                    if (!_disposed && _mpv != IntPtr.Zero)
                        _ = mpv_command_async(_mpv, 0, argv);
                }
            }
            catch
            {
            }
            finally
            {
                foreach (var ptr in nativeStrings)
                    Marshal.FreeHGlobal(ptr);
                if (argv != IntPtr.Zero)
                    Marshal.FreeHGlobal(argv);
            }
        }

        private static void ThrowIfError(int code, string operation)
        {
            if (code >= 0)
                return;

            var error = Marshal.PtrToStringAnsi(mpv_error_string(code)) ?? code.ToString(System.Globalization.CultureInfo.InvariantCulture);
            throw new InvalidOperationException($"libmpv {operation} failed: {error}");
        }

        private static double Int64BitsToDouble(long value)
        {
            return BitConverter.Int64BitsToDouble(value);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _shutdownRequested = true;
            _eventLoopRunning = false;

            try
            {
                CommandAsyncNoThrow("quit");
                if (_mpv != IntPtr.Zero)
                    mpv_wakeup(_mpv);
            }
            catch
            {
            }

            try
            {
                if (_eventThread != null && _eventThread.IsAlive && Thread.CurrentThread.ManagedThreadId != _eventThread.ManagedThreadId)
                    _eventThread.Join(300);
            }
            catch
            {
            }

            lock (_mpvAccessLock)
            {
                _disposed = true;

                try
                {
                    if (_mpv != IntPtr.Zero)
                    {
                        mpv_terminate_destroy(_mpv);
                        _mpv = IntPtr.Zero;
                    }
                }
                catch
                {
                    _mpv = IntPtr.Zero;
                }
            }
        }

        private static IntPtr ResolveNativeLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (!libraryName.Equals(MpvDll, StringComparison.OrdinalIgnoreCase))
                return IntPtr.Zero;

            var path = FindMpvLibraryPath();
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                return NativeLibrary.Load(path);

            return IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MpvNode
        {
            public IntPtr Data;
            public int Format;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MpvNodeList
        {
            public int Num;
            public IntPtr Values;
            public IntPtr Keys;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MpvEvent
        {
            public int EventId;
            public int Error;
            public ulong ReplyUserData;
            public IntPtr Data;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MpvEventEndFile
        {
            public int Reason;
            public int Error;
            public long PlaylistEntryId;
            public long PlaylistInsertId;
            public int PlaylistInsertNumEntries;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MpvEventProperty
        {
            public IntPtr Name;
            public int Format;
            public IntPtr Data;
        }

        [DllImport(MpvDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr mpv_create();

        [DllImport(MpvDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int mpv_initialize(IntPtr ctx);

        [DllImport(MpvDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void mpv_terminate_destroy(IntPtr ctx);

        [DllImport(MpvDll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int mpv_set_option_string(IntPtr ctx, string name, string data);

        [DllImport(MpvDll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int mpv_get_property(IntPtr ctx, string name, int format, IntPtr data);

        [DllImport(MpvDll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int mpv_set_property_string(IntPtr ctx, string name, string data);

        [DllImport(MpvDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int mpv_set_property(IntPtr ctx, [MarshalAs(UnmanagedType.LPStr)] string name, int format, IntPtr data);

        [DllImport(MpvDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int mpv_command(IntPtr ctx, IntPtr args);

        [DllImport(MpvDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int mpv_command_async(IntPtr ctx, ulong replyUserData, IntPtr args);

        [DllImport(MpvDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr mpv_error_string(int error);

        [DllImport(MpvDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int mpv_observe_property(IntPtr ctx, ulong replyUserData, [MarshalAs(UnmanagedType.LPStr)] string name, int format);

        [DllImport(MpvDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);

        [DllImport(MpvDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void mpv_wakeup(IntPtr ctx);
    }
}

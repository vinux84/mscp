using CommunitySDK;
using System;
using System.Collections.Generic;
using System.Linq;
using VideoOS.Platform;
using VideoOS.Platform.Background;
using VideoOS.Platform.Client;
using VideoOS.Platform.Messaging;

namespace ColoredTimeline.Background
{
    public class ColoredTimelineSmartClientBackgroundPlugin : BackgroundPlugin
    {
        // We may register multiple sources per pane: one Ribbon per rule, plus optional
        // Marker sources (Start + Stop) when the rule has UseMarkers turned on. Mixed list,
        // each source carries its own Dispose/UnregisterTimelineSequenceSource handling.
        private readonly Dictionary<ImageViewerAddOn, List<TimelineSequenceSource>> _sources
            = new Dictionary<ImageViewerAddOn, List<TimelineSequenceSource>>();

        private readonly object _lock = new object();
        private readonly PluginLog _log = new PluginLog("ColoredTimeline - SC BG");

        private List<RuleConfig> _rules = new List<RuleConfig>();
        private object _configChangedReg;

        public override Guid Id => ColoredTimelineDefinition.SmartClientBackgroundPluginId;
        public override string Name => "Colored Timeline Smart Client";
        public override List<EnvironmentType> TargetEnvironments { get; } = new List<EnvironmentType>
        {
            EnvironmentType.SmartClient
        };

        public override void Init()
        {
            _log.Info("Init - log file: %ProgramData%\\Milestone\\XProtect Smart Client\\MIPLog.txt");
            LoadRules();
            ClientControl.Instance.NewImageViewerControlEvent += OnNewImageViewer;
            _configChangedReg = EnvironmentManager.Instance.RegisterReceiver(
                OnConfigurationChanged,
                new MessageIdFilter(MessageId.Server.ConfigurationChangedIndication));
            _log.Info("Initialized - listening for image viewers and config changes");
        }

        public override void Close()
        {
            ClientControl.Instance.NewImageViewerControlEvent -= OnNewImageViewer;
            if (_configChangedReg != null)
            {
                EnvironmentManager.Instance.UnRegisterReceiver(_configChangedReg);
                _configChangedReg = null;
            }
            lock (_lock)
            {
                foreach (var kv in _sources)
                {
                    foreach (var src in kv.Value)
                    {
                        try { kv.Key.UnregisterTimelineSequenceSource(src); } catch { }
                        try { (src as IDisposable)?.Dispose(); } catch { }
                    }
                }
                _sources.Clear();
            }
            _log.Info("Closed");
        }

        private object OnConfigurationChanged(Message message, FQID destination, FQID sender)
        {
            try
            {
                if (message?.RelatedFQID?.Kind != ColoredTimelineDefinition.TimelineRuleKindId) return null;

                Configuration.Instance.RefreshConfiguration(
                    message.RelatedFQID.ServerId,
                    ColoredTimelineDefinition.TimelineRuleKindId);

                LoadRules();

                lock (_lock)
                {
                    foreach (var addon in _sources.Keys.ToList())
                        Reattach(addon);
                }
            }
            catch (Exception ex)
            {
                _log.Error($"OnConfigurationChanged failed: {ex.Message}");
            }
            return null;
        }

        private void LoadRules()
        {
            try
            {
                var items = Configuration.Instance.GetItemConfigurations(
                    ColoredTimelineDefinition.PluginId, null, ColoredTimelineDefinition.TimelineRuleKindId);

                var all = items.Select(RuleConfig.From).Where(r => r != null).ToList();
                _rules = all.Where(r => r.Enabled).ToList();
                _log.Info($"Loaded {_rules.Count} enabled rule(s) ({all.Count - _rules.Count} disabled)");
                foreach (var r in _rules)
                {
                    _log.Info($"  rule '{r.Name}': start='{r.StartEvent}' stop='{r.StopEvent}' " +
                              $"cameras={r.Cameras.Count} color=#{r.Color.R:X2}{r.Color.G:X2}{r.Color.B:X2}");
                }
            }
            catch (Exception ex)
            {
                _log.Error($"LoadRules failed: {ex.Message}");
                _rules = new List<RuleConfig>();
            }
        }

        private void OnNewImageViewer(ImageViewerAddOn addon)
        {
            if (addon == null) return;
            if (addon.ImageViewerType != ImageViewerType.CameraViewItem) return;

            addon.PropertyChangedEvent += OnImageViewerPropertyChanged;
            addon.CloseEvent += OnImageViewerClosed;
            Reattach(addon);
        }

        private void OnImageViewerPropertyChanged(object sender, EventArgs e)
        {
            if (sender is ImageViewerAddOn addon)
                Reattach(addon);
        }

        private void OnImageViewerClosed(object sender, EventArgs e)
        {
            if (!(sender is ImageViewerAddOn addon)) return;
            addon.PropertyChangedEvent -= OnImageViewerPropertyChanged;
            addon.CloseEvent -= OnImageViewerClosed;

            lock (_lock)
            {
                if (_sources.TryGetValue(addon, out var list))
                {
                    foreach (var src in list)
                    {
                        try { addon.UnregisterTimelineSequenceSource(src); } catch { }
                        try { (src as IDisposable)?.Dispose(); } catch { }
                    }
                    _sources.Remove(addon);
                }
            }
        }

        private void Reattach(ImageViewerAddOn addon)
        {
            lock (_lock)
            {
                if (_sources.TryGetValue(addon, out var existing))
                {
                    foreach (var src in existing)
                    {
                        try { addon.UnregisterTimelineSequenceSource(src); } catch { }
                        try { (src as IDisposable)?.Dispose(); } catch { }
                    }
                    existing.Clear();
                }
                else
                {
                    existing = new List<TimelineSequenceSource>();
                    _sources[addon] = existing;
                }

                if (addon.CameraFQID == null) return;
                var cameraId = addon.CameraFQID.ObjectId;
                string cameraName = null;
                try { cameraName = Configuration.Instance.GetItem(cameraId, Kind.Camera)?.Name; }
                catch { }

                int matched = 0;
                foreach (var rule in _rules)
                {
                    if (!rule.AppliesTo(cameraId)) continue;
                    try
                    {
                        matched++;

                        if (rule.AiClassMode)
                        {
                            // AI class detections: one marker source per enabled class, plus an
                            // optional ribbon source per class when that class's Ribbon box is checked.
                            foreach (var cls in rule.Classes)
                            {
                                try
                                {
                                    var classMarker = new ColoredTimelineMarkerSource(addon.CameraFQID, cameraName, rule, cls);
                                    addon.RegisterTimelineSequenceSource(classMarker);
                                    existing.Add(classMarker);
                                }
                                catch (Exception ex)
                                {
                                    _log.Error($"Failed to register class marker '{cls.Name}' for rule '{rule.Name}': {ex.GetType().FullName}: {ex.Message}");
                                }

                                if (cls.RibbonEnabled)
                                {
                                    try
                                    {
                                        var classRibbon = new ColoredTimelineSequenceSource(addon.CameraFQID, rule, cls);
                                        addon.RegisterTimelineSequenceSource(classRibbon);
                                        existing.Add(classRibbon);
                                    }
                                    catch (Exception ex)
                                    {
                                        _log.Error($"Failed to register class ribbon '{cls.Name}' for rule '{rule.Name}': {ex.GetType().FullName}: {ex.Message}");
                                    }
                                }
                            }
                            continue;
                        }

                        // MarkerOnly rules skip the ribbon source entirely - only the
                        // per-event marker sources below get registered.
                        if (!rule.MarkerOnly)
                        {
                            var src = new ColoredTimelineSequenceSource(addon.CameraFQID, rule);
                            addon.RegisterTimelineSequenceSource(src);
                            existing.Add(src);
                        }

                        if (rule.StartUseMarker && !string.IsNullOrEmpty(rule.StartEvent))
                        {
                            try
                            {
                                var startMarker = new ColoredTimelineMarkerSource(
                                    addon.CameraFQID, cameraName, rule, MarkerKind.Start, rule.StartEvent);
                                addon.RegisterTimelineSequenceSource(startMarker);
                                existing.Add(startMarker);
                            }
                            catch (Exception ex)
                            {
                                _log.Error($"Failed to register Start marker for rule '{rule.Name}': {ex.GetType().FullName}: {ex.Message}");
                            }
                        }
                        if (rule.StopUseMarker && !string.IsNullOrEmpty(rule.StopEvent))
                        {
                            try
                            {
                                var stopMarker = new ColoredTimelineMarkerSource(
                                    addon.CameraFQID, cameraName, rule, MarkerKind.Stop, rule.StopEvent);
                                addon.RegisterTimelineSequenceSource(stopMarker);
                                existing.Add(stopMarker);
                            }
                            catch (Exception ex)
                            {
                                _log.Error($"Failed to register Stop marker for rule '{rule.Name}': {ex.GetType().FullName}: {ex.Message}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.Error($"Failed to register source for rule '{rule.Name}': {ex.Message}");
                    }
                }
                _log.Info($"Pane cam={cameraId} attached: {matched}/{_rules.Count} rule(s) matched");
            }
        }
        internal class ClassMapping
        {
            public string Name;
            public string Icon;
            public string ColorHex;
            public bool RibbonEnabled;
            public string RibbonColorHex;
        }

        internal class RuleConfig
        {
            // Must match the 5 hardcoded classes in Admin/TimelineRuleUserControl.cs.
            private static readonly string[] ClassNames = { "Person", "Car", "Truck", "Tractor", "Van" };
            public string Name { get; private set; }
            public bool Enabled { get; private set; }
            public string StartEvent { get; private set; }
            public string StopEvent { get; private set; }
            public System.Drawing.Color Color { get; private set; }
            public HashSet<Guid> Cameras { get; private set; }
            public bool AutoCloseEnabled { get; private set; }
            public TimeSpan AutoCloseAfter { get; private set; }
            public bool StartUseMarker { get; private set; }
            public bool StopUseMarker { get; private set; }
            public string StartIcon { get; private set; }
            public string StopIcon { get; private set; }
            public string StartIconColor { get; private set; }
            public string StopIconColor { get; private set; }
            public bool MarkerOnly { get; private set; }
            public bool AiClassMode { get; private set; }
            public string ClassEvent { get; private set; }
            public TimeSpan ClassRibbonSeconds { get; private set; }
            public List<ClassMapping> Classes { get; private set; } = new List<ClassMapping>();

            public bool AppliesTo(Guid cameraId) => Cameras.Contains(cameraId);

            public static RuleConfig From(Item item)
            {
                if (item?.Properties == null) return null;
                try
                {
                    var enabled = !item.Properties.ContainsKey("Enabled") || item.Properties["Enabled"] != "No";
                    var start = item.Properties.ContainsKey("StartEvent") ? item.Properties["StartEvent"] : "";
                    var stop = item.Properties.ContainsKey("StopEvent") ? item.Properties["StopEvent"] : "";
                    var colorHex = item.Properties.ContainsKey("RibbonColor") ? item.Properties["RibbonColor"] : "#1E88E5";
                    var camIds = item.Properties.ContainsKey("CameraIds") ? item.Properties["CameraIds"] : "";
                    var autoCloseEnabled = item.Properties.ContainsKey("AutoCloseEnabled")
                        && item.Properties["AutoCloseEnabled"] == "Yes";
                    int autoCloseSecs = 10;
                    if (item.Properties.ContainsKey("AutoCloseSeconds"))
                        int.TryParse(item.Properties["AutoCloseSeconds"], out autoCloseSecs);
                    if (autoCloseSecs < 1) autoCloseSecs = 1;
                    if (autoCloseSecs > 3600) autoCloseSecs = 3600;

                    // Per-event marker flags (StartUseMarker/StopUseMarker). Fall back to
                    // legacy single "UseMarkers" key for rules saved before per-event support.
                    var legacyUseMarkers = item.Properties.ContainsKey("UseMarkers") && item.Properties["UseMarkers"] == "Yes";
                    var startUseMarker = item.Properties.ContainsKey("StartUseMarker")
                        ? item.Properties["StartUseMarker"] == "Yes"
                        : legacyUseMarkers;
                    var stopUseMarker = item.Properties.ContainsKey("StopUseMarker")
                        ? item.Properties["StopUseMarker"] == "Yes"
                        : legacyUseMarkers;
                    var startIcon = item.Properties.ContainsKey("StartIcon") ? item.Properties["StartIcon"] : "";
                    var stopIcon = item.Properties.ContainsKey("StopIcon") ? item.Properties["StopIcon"] : "";
                    var startIconColor = item.Properties.ContainsKey("StartIconColor") ? item.Properties["StartIconColor"] : "";
                    var stopIconColor = item.Properties.ContainsKey("StopIconColor") ? item.Properties["StopIconColor"] : "";
                    var markerOnly = item.Properties.ContainsKey("MarkerOnly") && item.Properties["MarkerOnly"] == "Yes";

                    // AI class detections: a rule can instead be driven by the CustomTag on
                    // events, with one marker + icon per enabled class,
                    // no Start/Stop event needed.
                    var aiClassMode = item.Properties.ContainsKey("AiClassMode") && item.Properties["AiClassMode"] == "Yes";
                    var classEvent = item.Properties.ContainsKey("ClassEvent") ? item.Properties["ClassEvent"] : "";
                    int classRibbonSecs = 5;
                    if (item.Properties.ContainsKey("ClassRibbonSeconds"))
                        int.TryParse(item.Properties["ClassRibbonSeconds"], out classRibbonSecs);
                    if (classRibbonSecs < 1) classRibbonSecs = 1;
                    if (classRibbonSecs > 3600) classRibbonSecs = 3600;
                    var classes = new List<ClassMapping>();
                    foreach (var className in ClassNames)
                    {
                        bool classEnabled = item.Properties.ContainsKey($"Class_{className}_Enabled")
                            && item.Properties[$"Class_{className}_Enabled"] == "Yes";
                        if (!classEnabled) continue;
                        var classIcon = item.Properties.ContainsKey($"Class_{className}_Icon")
                            ? item.Properties[$"Class_{className}_Icon"] : "";
                        var classColor = item.Properties.ContainsKey($"Class_{className}_Color") && !string.IsNullOrEmpty(item.Properties[$"Class_{className}_Color"])
                            ? item.Properties[$"Class_{className}_Color"] : "#1E88E5";
                        bool ribbonEnabled = item.Properties.ContainsKey($"Class_{className}_RibbonEnabled")
                            && item.Properties[$"Class_{className}_RibbonEnabled"] == "Yes";
                        var ribbonColor = item.Properties.ContainsKey($"Class_{className}_RibbonColor") && !string.IsNullOrEmpty(item.Properties[$"Class_{className}_RibbonColor"])
                            ? item.Properties[$"Class_{className}_RibbonColor"] : "#1E88E5";
                        classes.Add(new ClassMapping { Name = className, Icon = classIcon, ColorHex = classColor, RibbonEnabled = ribbonEnabled, RibbonColorHex = ribbonColor });
                    }

                    var cams = new HashSet<Guid>();
                    foreach (var s in camIds.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                        if (Guid.TryParse(s.Trim(), out var g)) cams.Add(g);

                    System.Drawing.Color color;
                    try { color = System.Drawing.ColorTranslator.FromHtml(colorHex); }
                    catch { color = System.Drawing.Color.DodgerBlue; }

                    return new RuleConfig
                    {
                        Name = item.Name,
                        Enabled = enabled,
                        StartEvent = start,
                        StopEvent = stop,
                        Color = color,
                        Cameras = cams,
                        AutoCloseEnabled = autoCloseEnabled,
                        AutoCloseAfter = TimeSpan.FromSeconds(autoCloseSecs),
                        StartUseMarker = startUseMarker,
                        StopUseMarker = stopUseMarker,
                        StartIcon = startIcon,
                        StopIcon = stopIcon,
                        StartIconColor = startIconColor,
                        StopIconColor = stopIconColor,
                        MarkerOnly = markerOnly,
                        AiClassMode = aiClassMode,
                        ClassEvent = classEvent,
                        ClassRibbonSeconds = TimeSpan.FromSeconds(classRibbonSecs),
                        Classes = classes
                    };
                }
                catch
                {
                    return null;
                }
            }
        }
    }
}

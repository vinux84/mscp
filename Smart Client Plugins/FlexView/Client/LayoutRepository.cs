using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using VideoOS.Platform;
using ConfigItems = VideoOS.Platform.ConfigurationItems;
using SdkRectangle = System.Drawing.Rectangle;

namespace FlexView.Client
{
    // Every Management Server layout operation FlexView performs. Layouts are the templates
    // offered in Smart Client setup mode under Add View, next to the built-in 1x1 / 2x2 grids.
    //
    // A Layout carries geometry and nothing else - the type exposes only Id, Name, Description
    // and DefinitionXml. There is no camera collection on it and no way to attach one, so a
    // layout saved from a populated FlexView arrangement is an empty template of the same shape.
    // Every user-facing string in the UI is worded to make that explicit; see SaveLayoutWindow.
    //
    // Layouts are system-wide Management Server configuration, not per-user client state. Adding
    // or removing one affects every operator on the site and needs configuration write rights that
    // a normal Smart Client account usually does not have. That is why CanRead() exists as a cheap
    // read-only probe the UI runs before offering either button, and why every failure path here
    // logs the underlying exception rather than only surfacing a message box.
    //
    // All logging uses the [FlexViewLayout] prefix, matching FederationWalker's [FlexViewFed], so a
    // customer MIPLog can be filtered down to just this feature.
    internal static class LayoutRepository
    {
        private const string P = "[FlexViewLayout]";

        // The layout XML coordinate space. Matches ViewAndLayoutItem.Layout / the SdkMax constant in
        // FlexViewViewItemWpfUserControl, which is why a pane rectangle needs no scaling on the way
        // out - both are 0..1000 on each axis.
        internal const int SdkMax = 1000;

        private static readonly TimeSpan TaskTimeout = TimeSpan.FromSeconds(30);

        private static CommunitySDK.PluginLog Log => FlexViewDefinition.Log;

        #region Models

        // A snapshot of one layout group and its layouts, detached from the config API objects so the
        // UI can bind to it after the background load completes. Group is kept because RemoveLayout
        // has to be invoked on the owning LayoutFolder.
        //
        // Properties rather than fields throughout both models, and this is load-bearing: WPF data
        // binding resolves through the property system only. A bound field does not raise an error,
        // it renders as an empty cell, so ManageLayoutsWindow's Layout and Description columns came
        // up blank while the computed-property columns beside them worked.
        internal sealed class LayoutGroupInfo
        {
            public string Name { get; set; }
            public string Id { get; set; }
            public string Path { get; set; }
            public ConfigItems.LayoutGroup Group { get; set; }
            public List<LayoutInfo> Layouts { get; } = new List<LayoutInfo>();

            // Set when this group's layouts could not be enumerated. One unreadable group must not
            // blank out the whole Manage Layouts list, so the failure is carried per group instead
            // of thrown.
            public string LoadError { get; set; }

            public override string ToString() => Name;
        }

        internal sealed class LayoutInfo
        {
            public string Name { get; set; }
            public string Id { get; set; }
            public string Path { get; set; }
            public string Description { get; set; }
            public int PaneCount { get; set; }
            public DateTime LastModified { get; set; }
            public LayoutGroupInfo Owner { get; set; }

            public string GroupName => Owner?.Name ?? "";
            public string PaneText => PaneCount > 0 ? PaneCount.ToString(CultureInfo.InvariantCulture) : "?";
            public string ModifiedText => LastModified == DateTime.MinValue
                ? ""
                : LastModified.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        #endregion

        #region Read

        // Enumerates every layout group and the layouts inside it. Throws only when the server
        // itself is unreachable; a group that fails to enumerate is returned with LoadError set.
        public static List<LayoutGroupInfo> LoadGroups()
        {
            var started = DateTime.UtcNow;
            Log.Info($"{P} LoadGroups: starting.");

            var master = EnvironmentManager.Instance.MasterSite;
            if (master == null)
                throw new InvalidOperationException("Master site is not available. Is the client still connecting?");

            var ms = new ConfigItems.ManagementServer(master);
            var groupFolder = ms.LayoutGroupFolder;
            if (groupFolder == null)
                throw new InvalidOperationException("The management server returned no layout group folder.");

            var groups = groupFolder.LayoutGroups;
            if (groups == null)
                throw new InvalidOperationException("The management server returned no layout groups.");

            var result = new List<LayoutGroupInfo>();
            foreach (var g in groups)
            {
                if (g == null) continue;

                var info = new LayoutGroupInfo
                {
                    Name = SafeGet(() => g.Name) ?? "(unnamed group)",
                    Id = SafeGet(() => g.Id),
                    Path = SafeGet(() => g.Path),
                    Group = g
                };

                try
                {
                    var folder = g.LayoutFolder;
                    if (folder == null)
                    {
                        info.LoadError = "Group exposes no layout folder.";
                        Log.Info($"{P} LoadGroups: group '{info.Name}' has no LayoutFolder.");
                    }
                    else
                    {
                        var layouts = folder.Layouts;
                        if (layouts != null)
                        {
                            foreach (var l in layouts)
                            {
                                if (l == null) continue;
                                var xml = SafeGet(() => l.DefinitionXml);
                                info.Layouts.Add(new LayoutInfo
                                {
                                    Name = SafeGet(() => l.Name) ?? "(unnamed layout)",
                                    Id = SafeGet(() => l.Id),
                                    Path = SafeGet(() => l.Path),
                                    Description = SafeGet(() => l.Description) ?? "",
                                    PaneCount = CountPanes(xml),
                                    LastModified = SafeGetDate(() => l.LastModified),
                                    Owner = info
                                });
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    info.LoadError = ex.Message;
                    Log.Error($"{P} LoadGroups: group '{info.Name}' failed to enumerate - {ex.GetType().Name}: {ex.Message}", ex);
                }

                Log.Info($"{P} LoadGroups: group '{info.Name}' id={info.Id} path={info.Path} layouts={info.Layouts.Count}"
                         + (info.LoadError != null ? $" error='{info.LoadError}'" : ""));
                result.Add(info);
            }

            var elapsed = (DateTime.UtcNow - started).TotalMilliseconds;
            Log.Info($"{P} LoadGroups: done - {result.Count} group(s), {result.Sum(g => g.Layouts.Count)} layout(s), {elapsed:F0} ms.");
            return result;
        }

        #endregion

        #region Write

        // Creates a layout in the given group. The name is not checked for collisions first: the
        // server enforces its own rules and reports them through the ServerTask, and reproducing
        // that check here would only add a second, weaker source of truth.
        public static void AddLayout(LayoutGroupInfo group, string name, string description, string definitionXml)
        {
            if (group == null) throw new ArgumentNullException(nameof(group));
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Layout name is required.", nameof(name));
            if (string.IsNullOrWhiteSpace(definitionXml)) throw new ArgumentException("Layout definition is required.", nameof(definitionXml));

            var folder = group.Group?.LayoutFolder;
            if (folder == null)
                throw new InvalidOperationException($"Layout group '{group.Name}' exposes no layout folder to write to.");

            Log.Info($"{P} AddLayout: group='{group.Name}' name='{name}' descriptionLength={description?.Length ?? 0} xmlLength={definitionXml.Length}");
            Log.Info($"{P} AddLayout: definition xml =\n{Truncate(definitionXml, 2000)}");

            var task = folder.AddLayout(name, description ?? "", definitionXml);
            WaitForTask(task, $"AddLayout '{name}'");

            // Without this the next Manage Layouts open would re-serve the pre-add cached collection
            // and the new layout would look like it had silently failed.
            TryClearCache(folder, "AddLayout");
            Log.Info($"{P} AddLayout: '{name}' created in group '{group.Name}'.");
        }

        // Deletes a layout. The server exposes this as a task whose ItemSelectionValues dictionary
        // lists what may be removed, so the selection key is resolved out of that dictionary rather
        // than assumed to be the layout path - the whole dictionary is logged, which is the only way
        // to diagnose a mismatch on a customer system.
        public static void RemoveLayout(LayoutInfo layout)
        {
            if (layout == null) throw new ArgumentNullException(nameof(layout));

            var folder = layout.Owner?.Group?.LayoutFolder;
            if (folder == null)
                throw new InvalidOperationException($"Layout '{layout.Name}' has no owning layout folder to remove it from.");

            Log.Info($"{P} RemoveLayout: name='{layout.Name}' id={layout.Id} path={layout.Path} group='{layout.GroupName}'");

            var task = folder.RemoveLayout();
            if (task == null)
                throw new InvalidOperationException("The management server returned no remove-layout task.");

            var choices = task.ItemSelectionValues;
            if (choices == null || choices.Count == 0)
            {
                Log.Error($"{P} RemoveLayout: server offered no removable items for group '{layout.GroupName}'.");
                throw new InvalidOperationException(
                    "The server did not offer this layout for removal. It may be a built-in layout, or the account may lack configuration rights.");
            }

            foreach (var kv in choices)
                Log.Info($"{P} RemoveLayout: candidate key='{kv.Key}' value='{kv.Value}'");

            var selection = ResolveSelection(choices, layout);
            if (selection == null)
            {
                Log.Error($"{P} RemoveLayout: no candidate matched layout '{layout.Name}' (id={layout.Id}, path={layout.Path}). Candidates logged above.");
                throw new InvalidOperationException(
                    $"The server's removable-layout list does not contain '{layout.Name}'. See MIPLog for the full list it returned.");
            }

            var side = choices.Any(kv => kv.Value == selection) ? "value" : "key";
            Log.Info($"{P} RemoveLayout: submitting ItemSelection='{selection}' (the {side} side of the pair).");
            task.ItemSelection = selection;

            var result = task.Execute();
            WaitForTask(result, $"RemoveLayout '{layout.Name}'");

            TryClearCache(folder, "RemoveLayout");
            Log.Info($"{P} RemoveLayout: '{layout.Name}' removed from group '{layout.GroupName}'.");
        }

        // Picks the token to hand back as ItemSelection.
        //
        // On 26.1 the dictionary is {display name -> item path}: key='FlexView 3 panes',
        // value='Layout[629cb03d-...]'. The server wants the path. Submitting the key gets
        // ArgumentMIPException("itemSelection") straight back.
        //
        // The orientation is not contractual across versions though, so rather than hard-coding
        // "always send the value", both sides are matched against the layout's own identifiers and
        // whichever side IS the identifier gets submitted. The display name is never submitted -
        // it is only ever used to find the pair.
        private static string ResolveSelection(IDictionary<string, string> choices, LayoutInfo layout)
        {
            foreach (var probe in new[] { layout.Path, layout.Id })
            {
                if (string.IsNullOrEmpty(probe)) continue;
                foreach (var kv in choices)
                {
                    if (string.Equals(kv.Value, probe, StringComparison.OrdinalIgnoreCase)) return kv.Value;
                    if (string.Equals(kv.Key, probe, StringComparison.OrdinalIgnoreCase)) return kv.Key;
                }
            }

            // Id embedded in a longer path-style token, e.g. "Layout[<guid>]".
            if (!string.IsNullOrEmpty(layout.Id))
            {
                foreach (var kv in choices)
                {
                    if (kv.Value != null && kv.Value.IndexOf(layout.Id, StringComparison.OrdinalIgnoreCase) >= 0) return kv.Value;
                    if (kv.Key != null && kv.Key.IndexOf(layout.Id, StringComparison.OrdinalIgnoreCase) >= 0) return kv.Key;
                }
            }

            // Name last: the weakest match, accepted only when unambiguous, and even then what gets
            // submitted is the opposite side of the pair - the identifier, not the name itself.
            if (!string.IsNullOrEmpty(layout.Name))
            {
                var byName = choices
                    .Where(kv => string.Equals(kv.Key, layout.Name, StringComparison.OrdinalIgnoreCase)
                              || string.Equals(kv.Value, layout.Name, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (byName.Count == 1)
                {
                    var kv = byName[0];
                    return string.Equals(kv.Key, layout.Name, StringComparison.OrdinalIgnoreCase) ? kv.Value : kv.Key;
                }

                if (byName.Count > 1)
                    Log.Info($"{P} RemoveLayout: name '{layout.Name}' matched {byName.Count} candidates, refusing to guess.");
            }

            return null;
        }

        // AddLayout / RemoveLayout run server-side and can still be in flight when the call returns.
        // Without polling, a server-side rejection (duplicate name, missing rights) would be reported
        // to the user as a success. FlexViewViewItemWpfUserControl has its own copy of this for the
        // federated AddView path; this one is kept separate because it logs each state transition,
        // which is what makes a customer-side failure diagnosable.
        private static void WaitForTask(ConfigItems.ServerTask task, string what)
        {
            if (task == null)
                throw new InvalidOperationException($"{what}: the server returned no task.");

            var deadline = DateTime.UtcNow + TaskTimeout;
            var polls = 0;
            var lastState = task.State;
            Log.Info($"{P} {what}: task state={task.State} progress={task.Progress} path={task.Path}");

            while (task.Progress < 100 && task.State != ConfigItems.StateEnum.Error && DateTime.UtcNow < deadline)
            {
                System.Threading.Thread.Sleep(200);
                polls++;
                try
                {
                    task.UpdateState();
                }
                catch (Exception ex)
                {
                    Log.Error($"{P} {what}: UpdateState threw after {polls} poll(s) - {ex.GetType().Name}: {ex.Message}", ex);
                    throw;
                }

                if (task.State != lastState)
                {
                    Log.Info($"{P} {what}: state {lastState} -> {task.State} (progress {task.Progress})");
                    lastState = task.State;
                }
            }

            if (task.State == ConfigItems.StateEnum.Error)
            {
                var detail = task.ErrorText ?? task.ErrorCode ?? "no error text supplied by the server";
                Log.Error($"{P} {what}: FAILED - state=Error code='{task.ErrorCode}' text='{task.ErrorText}'");
                throw new InvalidOperationException($"{what} failed: {detail}");
            }

            if (task.Progress < 100)
            {
                Log.Error($"{P} {what}: TIMED OUT after {TaskTimeout.TotalSeconds:F0}s at progress {task.Progress}, state {task.State}.");
                throw new TimeoutException(
                    $"{what} did not finish within {TaskTimeout.TotalSeconds:F0} seconds. The server may still be working - reopen Manage Layouts to check.");
            }

            Log.Info($"{P} {what}: completed - state={task.State} progress={task.Progress} after {polls} poll(s).");
        }

        // Add and Remove write straight to the Management Server through the configuration API, a
        // side channel the running Smart Client's own configuration is never notified about. Until
        // the client re-reads it, a new layout is missing from Add View and a deleted one is still
        // offered - which reads as the operation having silently failed.
        //
        // Reaching for something narrower than a full reload is the obvious instinct here. There
        // isn't one, and the reason is worth writing down so it does not get re-litigated:
        //
        //   - ClearChildrenCache above clears the cache on our own ManagementServer instance. That
        //     is what makes Manage Layouts show the truth. It says nothing to the client.
        //   - Configuration.RefreshConfiguration(Kind.Layout) compiles - Kind.Layout exists - but
        //     Milestone Development have confirmed it is a no-op for built-in kinds: "RefreshConfiguration
        //     only works for plugin configurations... Any build-in item configuration is maintained
        //     by the environment and thus cannot be refreshed through MIP."
        //     https://forum.milestonesys.com/t/12005/6
        //   - Even if it did work it would flush the wrong cache. It clears Configuration.Instance,
        //     the MIP item cache a plugin reads through GetItemsByKind. The Add View picker is
        //     native Smart Client UI reading the client's own configuration, which is a different
        //     cache. ColoredTimeline's RefreshConfiguration call is not a counter-example: it passes
        //     a plugin-defined kind and then re-reads through Configuration.Instance itself, so it
        //     refreshes and reads the same cache.
        //
        // The client-wide reload is the only thing that reaches the cache the picker actually uses,
        // and it is precisely what the operator would otherwise trigger by hand through Reload
        // Configuration. It is coarse by necessity, not by shortcut - so callers should send it once
        // per batch of changes rather than once per change.
        public static void RequestClientConfigurationReload()
        {
            try
            {
                Log.Info($"{P} Requesting a Smart Client configuration reload so the layout picker picks the change up.");
                EnvironmentManager.Instance.SendMessage(
                    new VideoOS.Platform.Messaging.Message(
                        VideoOS.Platform.Messaging.MessageId.SmartClient.ReloadConfigurationCommand));
                Log.Info($"{P} Reload command sent.");
            }
            catch (Exception ex)
            {
                // Not fatal: the write already succeeded on the server. It only means the operator
                // has to reload by hand before the change shows up in Add View.
                Log.Error($"{P} Reload command failed - {ex.GetType().Name}: {ex.Message}. "
                        + "Add View may stay stale until the client reloads its configuration manually.", ex);
            }
        }

        private static void TryClearCache(ConfigItems.LayoutFolder folder, string what)
        {
            try
            {
                folder.ClearChildrenCache();
            }
            catch (Exception ex)
            {
                // Non-fatal: the write already succeeded, this only affects what the next read sees.
                Log.Info($"{P} {what}: ClearChildrenCache threw ({ex.GetType().Name}: {ex.Message}); list may be stale until reconnect.");
            }
        }

        #endregion

        #region Definition XML

        // Builds the layout definition document. Shape and coordinate space are taken from the MIP SDK
        // AddLayout sample: a flat list of ViewItem rectangles in a 1000x1000 space, with the picker
        // thumbnail carried as a base64 PNG in ViewLayoutIcon.
        //
        //   <ViewLayout>
        //     <ViewItems>
        //       <ViewItem><Position><X>0</X><Y>0</Y></Position><Size><Width>1000</Width><Height>200</Height></Size></ViewItem>
        //     </ViewItems>
        //     <ViewLayoutIcon>iVBORw0…</ViewLayoutIcon>
        //   </ViewLayout>
        //
        // Rectangles arrive straight from ConvertPanesToSdkLayout, already in that space, so nothing
        // is scaled or rounded here - the arrangement the operator drew is what gets stored.
        public static string BuildDefinitionXml(SdkRectangle[] rects, string iconBase64)
        {
            if (rects == null || rects.Length == 0)
                throw new ArgumentException("A layout needs at least one pane.", nameof(rects));

            var settings = new XmlWriterSettings
            {
                Indent = true,
                IndentChars = "  ",
                OmitXmlDeclaration = true,
                Encoding = new UTF8Encoding(false)
            };

            var sb = new StringBuilder();
            using (var writer = XmlWriter.Create(sb, settings))
            {
                writer.WriteStartElement("ViewLayout");
                writer.WriteStartElement("ViewItems");

                foreach (var r in rects)
                {
                    writer.WriteStartElement("ViewItem");

                    writer.WriteStartElement("Position");
                    WriteInt(writer, "X", r.X);
                    WriteInt(writer, "Y", r.Y);
                    writer.WriteEndElement();

                    writer.WriteStartElement("Size");
                    WriteInt(writer, "Width", r.Width);
                    WriteInt(writer, "Height", r.Height);
                    writer.WriteEndElement();

                    writer.WriteEndElement();
                }

                writer.WriteEndElement();

                if (!string.IsNullOrEmpty(iconBase64))
                    writer.WriteElementString("ViewLayoutIcon", iconBase64);

                writer.WriteEndElement();
                writer.Flush();
            }

            return sb.ToString();
        }

        private static void WriteInt(XmlWriter writer, string name, int value)
        {
            writer.WriteElementString(name, value.ToString(CultureInfo.InvariantCulture));
        }

        // Pane count for the Manage Layouts list. Best-effort: a layout authored by another
        // integration may not parse, and an unreadable count must not hide the row.
        private static int CountPanes(string definitionXml)
        {
            if (string.IsNullOrWhiteSpace(definitionXml)) return 0;
            try
            {
                var doc = new XmlDocument { XmlResolver = null };
                using (var sr = new StringReader(definitionXml))
                using (var xr = XmlReader.Create(sr, new XmlReaderSettings { XmlResolver = null, DtdProcessing = DtdProcessing.Prohibit }))
                {
                    doc.Load(xr);
                }
                return doc.SelectNodes("/ViewLayout/ViewItems/ViewItem")?.Count ?? 0;
            }
            catch (Exception ex)
            {
                Log.Info($"{P} CountPanes: could not parse definition xml ({ex.GetType().Name}: {ex.Message}).");
                return 0;
            }
        }

        #endregion

        #region Helpers

        private static T SafeGet<T>(Func<T> get) where T : class
        {
            try { return get(); } catch { return null; }
        }

        private static DateTime SafeGetDate(Func<DateTime> get)
        {
            try { return get(); } catch { return DateTime.MinValue; }
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
            return s.Substring(0, max) + $" ...[truncated, {s.Length} chars total]";
        }

        #endregion
    }
}

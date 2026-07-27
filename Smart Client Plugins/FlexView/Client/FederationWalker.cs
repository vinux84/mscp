using System;
using System.Collections.Generic;
using System.Xml.Linq;
using VideoOS.Platform;
using VideoOS.Platform.Client;
using VideoOS.Platform.ConfigurationItems;

namespace FlexView.Client
{
    // Reads views from the master site and, in a Milestone Federated Architecture, from its child
    // sites too - straight off each site's Management Server configuration.
    //
    // Views persist on the Management Server (ViewGroupFolder -> ViewGroup -> ViewFolder -> View),
    // exactly like Recording Servers do (RecordingServerFolder -> RecordingServer). So the same
    // per-site pattern that made System Status federation-aware works here: construct a
    // ManagementServer from each site's FQID and enumerate its ViewGroupFolder. This is a
    // configuration-plane read - it does NOT depend on a client session, so it is not subject to the
    // "Cannot work with View Groups in standalone SDK" limitation that ClientControl hits.
    //
    // A child-site view is never resolved back to a live ViewAndLayoutItem (Configuration.Instance
    // .GetItem always returns null for it - Views are not part of MFA's federated client-session
    // model, only devices/cameras/alarms/access are). Instead the raw LayoutViewItems XML and the
    // handful of other fields ViewFolder.AddView needs are captured here directly from the
    // configuration-plane View object, so the caller can recreate the view via that same config API
    // on a site it can write to, without ever needing a client-side handle to the source view.
    //
    // Everything is logged with the [FlexViewFed] prefix (including a sample view's LayoutViewItems
    // XML) so a customer test run can be diagnosed.
    internal static class FederationWalker
    {
        // A view read from a site's Management Server configuration - enough to recreate it
        // elsewhere via ViewFolder.AddView(name, shortcut, viewLayoutType, layoutCustomId,
        // layoutIcon, layoutViewItems).
        internal sealed class FedView
        {
            public string SiteName;
            public string GroupPath;         // "Group / Subgroup"
            public string Name;
            public string Id;
            public string LayoutType;
            public string Shortcut;
            public string LayoutCustomId;
            public string LayoutIcon;
            public string LayoutViewItemsXml;
            public bool HasLayoutXml;

            // Used only if/when this view is actually copied - see ReadCameraItems. Deliberately not
            // read during the browse walk itself: fetching ViewItemChildItems for every view (rather
            // than just the one the user picks) roughly doubled the walk's cost across a site with
            // thousands of views, for data the browse tree never displays.
            public ServerId SourceServerId;
            public string SourcePath;
        }

        // One pane's content, parsed from a ViewItemChildItem's ViewItemDefinitionXml.
        internal sealed class FedViewItem
        {
            public int Position;
            public Guid? CameraId;
        }

        internal sealed class SiteViews
        {
            public string Label;                 // "Master: HQ" / "Site: Branch-1"
            public bool IsMaster;

            // Master only: the client-runtime view roots, still used for the working same-site Open.
            public List<Item> ClientViewRoots = new List<Item>();

            // All sites: views read from the Management Server config (federation-capable proof path).
            public List<FedView> FedViews = new List<FedView>();
        }

        // One site in the (possibly federated) hierarchy: the master or a child site.
        private sealed class SiteRef
        {
            public string Name;
            public FQID Fqid;
            public ServerId ServerId;
        }

        // Session-long cache: the walk costs real seconds even after skipping the master's wasted
        // config-plane read (still several seconds per child site over the network), and views rarely
        // change site-to-site within a single Smart Client session. Cached until ForceRefresh clears
        // it (wired to a Refresh button in ViewBrowserWindow) - there's no automatic expiry, by design
        // (the user asked for session-long caching with a manual refresh, not a time-based one).
        // Guarded by a lock since CollectAllSiteViews runs on a background thread.
        private static readonly object _cacheLock = new object();
        private static List<SiteViews> _cache;

        public static void ForceRefresh()
        {
            lock (_cacheLock) { _cache = null; }
        }

        public static List<SiteViews> CollectAllSiteViews()
        {
            lock (_cacheLock)
            {
                if (_cache != null) return _cache;
            }

            var log = FlexViewDefinition.Log;
            var result = new List<SiteViews>();

            List<SiteRef> sites;
            try { sites = EnumerateManagementServers(); }
            catch (Exception ex) { log.Info($"[FlexViewFed] EnumerateManagementServers failed: {ex.Message}"); sites = new List<SiteRef>(); }

            log.Info($"[FlexViewFed] Management-server sites: {sites.Count}");

            ServerId masterServerId = null;
            try { masterServerId = EnvironmentManager.Instance.MasterSite?.ServerId; } catch { }

            foreach (var site in sites)
            {
                bool isMaster = masterServerId != null && site.ServerId != null &&
                                site.ServerId.Id == masterServerId.Id;

                var sv = new SiteViews
                {
                    Label = (isMaster ? "Master: " : "Site: ") + site.Name,
                    IsMaster = isMaster
                };

                // Master already has a fully working client-runtime tree (below) - PopulateFederatedTree
                // renders the master from ClientViewRoots, never FedViews, so walking the master's
                // config-plane view tree here is pure wasted work. Confirmed against a real system:
                // that walk alone (3411 views) took ~73 seconds of the ~90 second total load time, for
                // data that was computed and then never used. Only child sites need the config-plane
                // walk - they have no working client-session tree at all (that's the original bug this
                // whole file exists to work around).
                if (isMaster)
                {
                    try
                    {
                        var groups = ClientControl.Instance.GetViewGroupItems();
                        if (groups != null) sv.ClientViewRoots.AddRange(groups);
                        log.Info($"[FlexViewFed] [{site.Name}] (master) ClientControl roots: {sv.ClientViewRoots.Count}");
                    }
                    catch (Exception ex) { log.Info($"[FlexViewFed] [{site.Name}] GetViewGroupItems failed: {ex.Message}"); }
                }
                else
                {
                    try
                    {
                        var ms = new ManagementServer(site.Fqid);
                        var vgf = ms.ViewGroupFolder;
                        if (vgf?.ViewGroups == null)
                            log.Info($"[FlexViewFed] [{site.Name}] ViewGroupFolder/ViewGroups is null");
                        else
                            foreach (var vg in vgf.ViewGroups)
                                WalkViewGroup(vg, site.Name, "", sv.FedViews, 0);
                    }
                    catch (Exception ex)
                    {
                        log.Info($"[FlexViewFed] [{site.Name}] ManagementServer ViewGroup read failed: {ex.GetType().Name}: {ex.Message}");
                    }
                    log.Info($"[FlexViewFed] [{site.Name}] views via ManagementServer config: {sv.FedViews.Count}");
                }

                result.Add(sv);
            }

            lock (_cacheLock) { _cache = result; }
            return result;
        }

        // Recurse a ViewGroup: collect its Views, then descend into nested ViewGroups. The first view
        // encountered has its LayoutViewItems XML dumped (truncated) so we capture the layout format.
        private static void WalkViewGroup(VideoOS.Platform.ConfigurationItems.ViewGroup vg, string siteName, string parentPath, List<FedView> acc, int depth)
        {
            if (vg == null || depth > 8) return;
            var log = FlexViewDefinition.Log;

            string path = string.IsNullOrEmpty(parentPath) ? vg.Name : parentPath + " / " + vg.Name;

            try
            {
                var vf = vg.ViewFolder;
                if (vf?.Views != null)
                {
                    foreach (var v in vf.Views)
                    {
                        if (v == null) continue;
                        string xml = null;
                        try { xml = v.LayoutViewItems; } catch { }

                        var fv = new FedView
                        {
                            SiteName = siteName,
                            GroupPath = path,
                            Name = v.Name,
                            Id = v.Id,
                            LayoutType = SafeGet(() => v.ViewLayoutType),
                            Shortcut = SafeGet(() => v.Shortcut),
                            LayoutCustomId = SafeGet(() => v.LayoutCustomId),
                            LayoutIcon = SafeGet(() => v.LayoutIcon),
                            LayoutViewItemsXml = xml,
                            HasLayoutXml = !string.IsNullOrEmpty(xml),
                            // Cheap - ServerId/Path are already-loaded fields on v, not a network call.
                            // Used to re-fetch just this one view's ViewItemChildItems later, at copy
                            // time, instead of every view paying that cost during the browse walk (that
                            // used to double the walk's cost across thousands of views for no benefit -
                            // only the one view actually being copied ever needs its camera content).
                            SourceServerId = SafeGetServerId(() => v.ServerId),
                            SourcePath = SafeGet(() => v.Path)
                        };
                        acc.Add(fv);

                        // Dump the first view's layout so we know the XML shape for recreation.
                        if (acc.Count == 1 && !string.IsNullOrEmpty(xml))
                        {
                            var preview = xml.Length > 800 ? xml.Substring(0, 800) + " ...[truncated]" : xml;
                            log.Info($"[FlexViewFed] [{siteName}] sample view '{v.Name}' layoutType={fv.LayoutType} LayoutViewItems=\n{preview}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log.Info($"[FlexViewFed] [{siteName}] ViewFolder read failed under '{path}': {ex.Message}");
            }

            try
            {
                var nested = vg.ViewGroupFolder;
                if (nested?.ViewGroups != null)
                    foreach (var child in nested.ViewGroups)
                        WalkViewGroup(child, siteName, path, acc, depth + 1);
            }
            catch (Exception ex)
            {
                log.Info($"[FlexViewFed] [{siteName}] nested ViewGroupFolder read failed under '{path}': {ex.Message}");
            }
        }

        private static string SafeGet(Func<string> f)
        {
            try { return f() ?? ""; } catch { return ""; }
        }

        private static ServerId SafeGetServerId(Func<ServerId> f)
        {
            try { return f(); } catch { return null; }
        }

        // Re-fetches ViewItemChildItems for exactly one view - called only when that view is actually
        // being copied (see FedView.SourceServerId/SourcePath). A camera slot's ViewItemDefinitionXml
        // looks like (confirmed from a real dump):
        //   <viewitem type="...CameraContentType.CameraViewItem, VideoOS.RemoteClient.Application">
        //     <iteminfo cameraid="{guid}" .../>
        //   </viewitem>
        // Other view item types (maps, HTML, plugins) aren't parsed yet - camera is the common case
        // this plugin needs to carry across a federated copy.
        internal static List<FedViewItem> ReadCameraItems(FedView fv)
        {
            var result = new List<FedViewItem>();
            if (fv?.SourceServerId == null || string.IsNullOrEmpty(fv.SourcePath)) return result;

            var log = FlexViewDefinition.Log;
            try
            {
                var v = new VideoOS.Platform.ConfigurationItems.View(fv.SourceServerId, fv.SourcePath);
                var children = v.ViewItemChildItems;
                if (children != null)
                {
                    foreach (var vi in children)
                    {
                        if (vi == null) continue;
                        var camId = ParseCameraId(vi.ViewItemDefinitionXml);
                        if (camId != null)
                            result.Add(new FedViewItem { Position = vi.ViewItemPosition, CameraId = camId });
                    }
                }
                log.Info($"[FlexViewFed] ReadCameraItems('{fv.Name}'): {children?.Count ?? 0} view item(s), {result.Count} camera(s) parsed");
            }
            catch (Exception ex)
            {
                log.Info($"[FlexViewFed] ReadCameraItems failed for '{fv.Name}': {ex.Message}");
            }
            return result;
        }

        // Extracts the camera GUID from a single view item's definition XML, when it's a camera
        // slot: <viewitem type="...CameraViewItem..."><iteminfo cameraid="{guid}" ... /></viewitem>.
        // Returns null for every other view item type (empty, map, HTML, plugin, ...) - those are
        // left as empty panes on copy rather than guessed at.
        private static Guid? ParseCameraId(string viewItemDefinitionXml)
        {
            if (string.IsNullOrEmpty(viewItemDefinitionXml)) return null;
            try
            {
                var el = XElement.Parse(viewItemDefinitionXml);
                var type = (string)el.Attribute("type") ?? "";
                if (type.IndexOf("CameraViewItem", StringComparison.OrdinalIgnoreCase) < 0) return null;

                var camIdStr = (string)el.Element("iteminfo")?.Attribute("cameraid");
                if (Guid.TryParse(camIdStr, out var camId) && camId != Guid.Empty) return camId;
            }
            catch { }
            return null;
        }

        // Finds a ViewGroup config object anywhere under a ViewGroupFolder tree by matching its Id,
        // walking properties rather than constructing a ViewGroup(FQID) directly from a client Item's
        // FQID - that constructor validates the FQID came from the same configuration-API object
        // graph (it throws "Invalid kind for this constructor" otherwise), which a client-session
        // Item's FQID never satisfies. Every existing working read in this file reaches a ViewGroup
        // the same way: navigate down from a genuine site FQID via .ViewGroupFolder/.ViewFolder.
        internal static VideoOS.Platform.ConfigurationItems.ViewGroup FindViewGroupById(ViewGroupFolder folder, Guid id)
        {
            if (folder?.ViewGroups == null) return null;
            foreach (var vg in folder.ViewGroups)
            {
                if (Guid.TryParse(vg.Id, out var vgId) && vgId == id) return vg;
                var found = FindViewGroupById(vg.ViewGroupFolder, id);
                if (found != null) return found;
            }
            return null;
        }

        // ── Federated site enumeration (proven pattern from System Status) ─────────────────────

        // Keep only entries whose Management Server configuration is actually readable: the raw walk
        // can surface a Recording Server's own FQID as a distinct ServerId, and probing
        // RecordingServerFolder throws for anything that is not a real Management Server.
        private static List<SiteRef> EnumerateManagementServers()
        {
            var log = FlexViewDefinition.Log;
            var all = EnumerateSites();
            log.Info($"[FlexViewFed] Sites discovered (raw walk): {all.Count}");

            var result = new List<SiteRef>();
            foreach (var s in all)
            {
                if (s?.Fqid == null) continue;
                try
                {
                    var management = new ManagementServer(s.Fqid);
                    var probe = management.RecordingServerFolder?.RecordingServers; // throws if not a real MS
                    result.Add(s);
                }
                catch (Exception ex)
                {
                    log.Info($"[FlexViewFed] Excluded non-management-server site '{s.Name}': {ex.GetType().Name}");
                }
            }
            return result;
        }

        private static List<SiteRef> EnumerateSites()
        {
            var list = new List<SiteRef>();
            var masterFqid = EnvironmentManager.Instance.MasterSite;
            if (masterFqid == null)
            {
                FlexViewDefinition.Log.Info("[FlexViewFed] MasterSite not available - no sites to enumerate");
                return list;
            }

            Item masterItem = null;
            try { masterItem = Configuration.Instance.GetItem(masterFqid); }
            catch (Exception ex) { FlexViewDefinition.Log.Info($"[FlexViewFed] GetItem(MasterSite) failed: {ex.Message}"); }

            if (masterItem == null)
            {
                if (masterFqid.ServerId != null)
                    list.Add(new SiteRef { Name = masterFqid.ServerId.ServerHostname, Fqid = masterFqid, ServerId = masterFqid.ServerId });
                return list;
            }

            CollectSites(masterItem, list, new HashSet<Guid>(), 0);
            return list;
        }

        private static void CollectSites(Item site, List<SiteRef> acc, HashSet<Guid> seen, int depth)
        {
            var fqid = site?.FQID;
            if (fqid?.ServerId == null || depth > 8) return;
            var sid = fqid.ServerId;
            if (!seen.Add(sid.Id)) return;

            acc.Add(new SiteRef
            {
                Name = string.IsNullOrWhiteSpace(site.Name) ? sid.ServerHostname : site.Name,
                Fqid = fqid,
                ServerId = sid
            });

            bool hasKids;
            try { hasKids = site.HasChildren != HasChildren.No; } catch { hasKids = true; }
            if (!hasKids) return;

            List<Item> kids = null;
            try { kids = site.GetChildren(); }
            catch (Exception ex) { FlexViewDefinition.Log.Info($"[FlexViewFed] GetChildren failed for site '{site.Name}': {ex.Message}"); }
            if (kids == null) return;

            foreach (var k in kids)
                CollectSites(k, acc, seen, depth + 1);
        }
    }
}

using System;
using System.Collections.Generic;
using VideoOS.Platform;
using VideoOS.Platform.Client;
using VideoOS.Platform.ConfigurationItems;

namespace FlexView.Client
{
    // TEST / DIAGNOSTIC (federated views): reads views from the master site and, in a Milestone
    // Federated Architecture, from its child sites too - straight off each site's Management Server
    // configuration.
    //
    // Views persist on the Management Server (ViewGroupFolder -> ViewGroup -> ViewFolder -> View),
    // exactly like Recording Servers do (RecordingServerFolder -> RecordingServer). So the same
    // per-site pattern that made System Status federation-aware works here: construct a
    // ManagementServer from each site's FQID and enumerate its ViewGroupFolder. This is a
    // configuration-plane read - it does NOT depend on a client session, so it is not subject to the
    // "Cannot work with View Groups in standalone SDK" limitation that ClientControl hits.
    //
    // Everything is logged with the [FlexViewFed] prefix (including a sample view's LayoutViewItems
    // XML) so a customer test run can be diagnosed - and so we capture the layout format needed to
    // recreate a child-site view on the parent.
    internal static class FederationWalker
    {
        // A view read from a site's Management Server configuration.
        internal sealed class FedView
        {
            public string SiteName;
            public string GroupPath;         // "Group / Subgroup"
            public string Name;
            public string Id;
            public string LayoutType;
            public bool HasLayoutXml;
            // Client FQID rebuilt from the view's ServerId + Id, so the view can be resolved back to a
            // ViewAndLayoutItem via Configuration.Instance.GetItem and opened with the normal pipeline.
            public FQID Fqid;
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

        public static List<SiteViews> CollectAllSiteViews()
        {
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

                // Config-plane read: works for master and every child, off the Management Server.
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

                // Master also keeps its client-runtime roots so same-site Open/edit stays fully working.
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

                result.Add(sv);
            }

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
                            HasLayoutXml = !string.IsNullOrEmpty(xml),
                            Fqid = BuildViewFqid(v)
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

        // Rebuild a client FQID for a view from its config ServerId + Id, so it can be resolved back
        // to a ViewAndLayoutItem with Configuration.Instance.GetItem - the same trick used to resolve
        // a camera FQID elsewhere. Returns null if the id/server is not usable.
        private static FQID BuildViewFqid(VideoOS.Platform.ConfigurationItems.View v)
        {
            try
            {
                if (v?.ServerId == null || string.IsNullOrEmpty(v.Id)) return null;
                if (!Guid.TryParse(v.Id, out var objId)) return null;
                return new FQID(v.ServerId, Guid.Empty, objId, FolderType.No, Kind.View);
            }
            catch (Exception ex)
            {
                FlexViewDefinition.Log.Info($"[FlexViewFed] BuildViewFqid failed for '{v?.Name}': {ex.Message}");
                return null;
            }
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

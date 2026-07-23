using System;
using System.Collections.Generic;
using VideoOS.Platform;
using VideoOS.Platform.Client;
using VideoOS.Platform.ConfigurationItems;

namespace FlexView.Client
{
    // TEST / DIAGNOSTIC (federated views): walks the master site and, in a Milestone Federated
    // Architecture, its child sites, and collects the top-level view-group Items of each site so the
    // Open dialog can present one merged, per-site tree. Master views come from the proven
    // ClientControl API; child-site views are discovered by walking the site Item's configuration
    // children looking for Kind.View roots.
    //
    // Everything is logged with the [FlexViewFed] prefix so a customer test run can be diagnosed from
    // MIPLog.txt even when nothing appears in the tree - the log tells us whether child sites were
    // enumerated at all, what each site exposed, and whether any view items were reachable.
    //
    // The site-walk (EnumerateSites/CollectSites/SiteRef) is copied from the working federated
    // System Status implementation so this reuses a proven pattern.
    internal static class FederationWalker
    {
        internal sealed class SiteViews
        {
            public string Label;          // e.g. "Master: HQ" or "Site: Branch-1"
            public List<Item> ViewRoots;  // top-level view-group Items for this site
            public bool IsMaster;
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
                var roots = new List<Item>();

                // The config-plane walk (Configuration.Instance.GetItem(site.Fqid) -> Kind.View) is
                // the SAME accessor for master and child, because views persist on each site's
                // management server, not only in the client runtime. Running it on the master too is
                // the decisive test: if it finds the master's own views (which we KNOW exist), the
                // approach is proven and child sites should behave identically.
                var walked = new List<Item>();
                try
                {
                    var siteItem = Configuration.Instance.GetItem(site.Fqid);
                    if (siteItem == null)
                        log.Info($"[FlexViewFed] [{site.Name}] GetItem(site) returned null - cannot walk views");
                    else
                        CollectViewRoots(siteItem, site.ServerId, walked, site.Name, new HashSet<Guid>(), 0);
                }
                catch (Exception ex)
                {
                    log.Info($"[FlexViewFed] [{site.Name}] config-walk failed: {ex.Message}");
                }
                log.Info($"[FlexViewFed] [{site.Name}] config-walk (GetItem+Kind.View) roots: {walked.Count}");

                if (isMaster)
                {
                    // Baseline comparison: the known-good client accessor. If the config-walk count
                    // matches this, the config-plane path is confirmed equivalent on the master.
                    int clientCount = -1;
                    try
                    {
                        var groups = ClientControl.Instance.GetViewGroupItems();
                        clientCount = groups?.Count ?? 0;
                    }
                    catch (Exception ex) { log.Info($"[FlexViewFed] [{site.Name}] GetViewGroupItems failed: {ex.Message}"); }
                    log.Info($"[FlexViewFed] [{site.Name}] (master) ClientControl baseline: {clientCount} group(s) vs config-walk {walked.Count}");

                    // Use whichever accessor actually produced roots so the Open tree still works today:
                    // prefer the proven config-walk, fall back to ClientControl if it found nothing.
                    if (walked.Count > 0)
                        roots.AddRange(walked);
                    else
                    {
                        try
                        {
                            var groups = ClientControl.Instance.GetViewGroupItems();
                            if (groups != null) roots.AddRange(groups);
                            log.Info($"[FlexViewFed] [{site.Name}] config-walk empty on master - using ClientControl ({roots.Count}).");
                        }
                        catch { }
                    }
                }
                else
                {
                    roots.AddRange(walked);
                }

                result.Add(new SiteViews
                {
                    Label = (isMaster ? "Master: " : "Site: ") + site.Name,
                    ViewRoots = roots,
                    IsMaster = isMaster
                });
            }

            return result;
        }

        // DFS from a site Item collecting the top-most Kind.View items. Descent stops once a Kind.View
        // item is found on a branch (the browser recurses into it via GetChildren), and never crosses
        // into a nested child site (a different ServerId) - those are separate site entries. Shallow
        // levels are logged so we can see exactly what each site exposes.
        private static void CollectViewRoots(Item node, ServerId siteServerId, List<Item> roots,
                                             string siteName, HashSet<Guid> seen, int depth)
        {
            if (node?.FQID == null || depth > 6) return;
            if (!seen.Add(node.FQID.ObjectId)) return;

            List<Item> kids = null;
            try { kids = node.GetChildren(); }
            catch (Exception ex)
            {
                FlexViewDefinition.Log.Info($"[FlexViewFed] [{siteName}] GetChildren failed at d{depth} on '{node.Name}': {ex.Message}");
                return;
            }
            if (kids == null) return;

            foreach (var k in kids)
            {
                if (k?.FQID == null) continue;

                // A nested child site (different ServerId) is its own entry - do not descend.
                if (k.FQID.ServerId != null && siteServerId != null && k.FQID.ServerId.Id != siteServerId.Id)
                    continue;

                if (depth <= 2)
                    FlexViewDefinition.Log.Info($"[FlexViewFed] [{siteName}] d{depth} child: name='{k.Name}' kind={k.FQID.Kind} folder={k.FQID.FolderType}");

                if (k.FQID.Kind == Kind.View)
                {
                    roots.Add(k);   // top-most view item on this branch; browser recurses into it
                    continue;
                }

                CollectViewRoots(k, siteServerId, roots, siteName, seen, depth + 1);
            }
        }

        // ── Proven federated site-walk (copied from System Status) ─────────────────────────────

        // The raw site walk can surface FQIDs that are not genuine Management Servers (e.g. a
        // Recording Server's own FQID that appears as a distinct ServerId). Keep only entries whose
        // Management Server configuration is actually readable - probing RecordingServerFolder throws
        // for anything that is not a real Management Server. Same filter as System Status.
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
                    var _ = management.RecordingServerFolder?.RecordingServers; // throws if not a real MS
                    result.Add(s);
                }
                catch (Exception ex)
                {
                    // Not a genuine Management Server (e.g. a Recording Server's own FQID) - excluded.
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

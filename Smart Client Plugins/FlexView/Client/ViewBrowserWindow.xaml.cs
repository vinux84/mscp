using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VideoOS.Platform;
using VideoOS.Platform.Client;

namespace FlexView.Client
{
    public enum BrowseMode { SelectFolder, SelectView }

    public partial class ViewBrowserWindow : Window
    {
        private readonly BrowseMode _mode;
        // TEST (federated views): when true, the Open tree is populated from every site (master +
        // federated children) via FederationWalker instead of the master-only client view tree.
        private readonly bool _federated;

        public Item SelectedItem { get; private set; }
        public Item SelectedParent { get; private set; }

        // Set when the selection is a child-site view read from the Management Server config
        // (rather than a master client Item). Carries its captured layout XML for copying.
        internal FederationWalker.FedView SelectedFedView { get; private set; }

        // Set instead of SelectedFedView when one or more checkboxes are checked - batch copy mode.
        // Takes priority over SelectedFedView/SelectedItem when populated.
        internal List<FederationWalker.FedView> SelectedFedViews { get; private set; }

        // Tracks which federated views are checked, across every site's subtree in the tree at once.
        private readonly HashSet<FederationWalker.FedView> _checkedFedViews = new HashSet<FederationWalker.FedView>();

        public ViewBrowserWindow(BrowseMode mode) : this(mode, false) { }

        public ViewBrowserWindow(BrowseMode mode, bool federated)
        {
            _mode = mode;
            _federated = federated;
            InitializeComponent();
            headerText.Text = mode == BrowseMode.SelectFolder
                ? "Select a folder to save the view in"
                : "Select a view to edit";
            searchBox.ToolTip = "Type to filter by name";
            refreshButton.Visibility = (_federated && _mode == BrowseMode.SelectView) ? Visibility.Visible : Visibility.Collapsed;
            LoadTree();
        }

        // The federated site/view list is cached for the session (see FederationWalker) - re-walking
        // every site on every Open View click was the original ~90-second-per-open complaint. Refresh
        // is the manual escape hatch for the rare case something changed on another site since.
        private void OnRefreshClick(object sender, RoutedEventArgs e)
        {
            FederationWalker.ForceRefresh();
            tree.Items.Clear();
            LoadTreeFederatedAsync();
        }

        private void LoadTree()
        {
            if (_federated && _mode == BrowseMode.SelectView)
            {
                LoadTreeFederatedAsync();
                return;
            }
            LoadTreeClassic();
        }

        // Master-only tree from the client view-group API (original behavior).
        private void LoadTreeClassic()
        {
            List<Item> groups;
            try
            {
                groups = ClientControl.Instance.GetViewGroupItems();
            }
            catch
            {
                return;
            }

            if (groups == null) return;

            foreach (var group in groups)
            {
                var node = CreateTreeNode(group);
                if (node != null)
                    tree.Items.Add(node);
            }
        }

        // One non-selectable header node per site, each holding that site's view roots. On a
        // non-federated system (only the master) this falls back to the classic flat tree so
        // behavior is unchanged. CollectAllSiteViews walks every site's config over the network -
        // it previously ran synchronously in the constructor and froze the whole Smart Client host
        // for as long as that took (over a minute on a real hierarchy), so it's offloaded to a
        // background thread here and the tree is only populated once it completes.
        private async void LoadTreeFederatedAsync()
        {
            var loadingNode = new TreeViewItem
            {
                Header = "Loading sites...",
                Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E)),
                IsEnabled = false
            };
            tree.Items.Add(loadingNode);
            searchBox.IsEnabled = false;
            refreshButton.IsEnabled = false;

            List<FederationWalker.SiteViews> siteViews = null;
            try
            {
                siteViews = await Task.Run(() => FederationWalker.CollectAllSiteViews());
            }
            catch (Exception ex)
            {
                FlexViewDefinition.Log.Info($"[FlexViewFed] CollectAllSiteViews failed: {ex.Message}");
            }

            tree.Items.Clear();
            searchBox.IsEnabled = true;
            refreshButton.IsEnabled = true;

            if (siteViews == null || siteViews.Count <= 1)
            {
                FlexViewDefinition.Log.Info(siteViews == null
                    ? "[FlexViewFed] CollectAllSiteViews failed - using classic flat tree."
                    : "[FlexViewFed] Single site (no children) - using classic flat tree.");
                LoadTreeClassic();
                return;
            }

            PopulateFederatedTree(siteViews);
        }

        private void PopulateFederatedTree(List<FederationWalker.SiteViews> siteViews)
        {
            var dim = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));

            foreach (var sv in siteViews)
            {
                var header = new TreeViewItem
                {
                    Header = "\U0001F310 " + sv.Label,
                    Tag = null,                 // site header is not selectable
                    IsExpanded = true,
                    FontWeight = FontWeights.Bold
                };

                int added = 0;

                if (sv.IsMaster)
                {
                    // Master: real client Items, fully selectable/openable - built from the pre-fetched
                    // ClientTree (no GetChildren() calls here) so a cache hit is pure in-memory work.
                    foreach (var rootNode in sv.ClientTree)
                    {
                        var node = CreateTreeNodeFromClientNode(rootNode);
                        if (node != null) { header.Items.Add(node); added++; }
                    }
                }
                else
                {
                    // Child site: views read from the Management Server config, each carrying its own
                    // captured layout XML so it can be copied into a folder on this site without ever
                    // needing a client-side handle to the source view. Views whose layout XML could
                    // not be read are shown but not selectable. Openable ones get a checkbox so several
                    // can be picked at once for a batch copy, in addition to the existing single-click
                    // Select flow.
                    foreach (var fv in sv.FedViews)
                    {
                        bool openable = fv.HasLayoutXml;
                        var label = $"{fv.GroupPath} / {fv.Name}" + (openable ? "" : "   [not resolvable]");

                        object headerContent;
                        if (openable)
                        {
                            var panel = new StackPanel { Orientation = Orientation.Horizontal };
                            var checkBox = new CheckBox
                            {
                                Tag = fv,
                                VerticalAlignment = VerticalAlignment.Center,
                                Margin = new Thickness(0, 0, 6, 0),
                                IsChecked = _checkedFedViews.Contains(fv)
                            };
                            checkBox.Checked += OnFedViewCheckChanged;
                            checkBox.Unchecked += OnFedViewCheckChanged;
                            panel.Children.Add(checkBox);
                            panel.Children.Add(new TextBlock { Text = "\U0001F4CB " + label, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
                            headerContent = panel;
                        }
                        else
                        {
                            headerContent = "\U0001F4CB " + label;
                        }

                        header.Items.Add(new TreeViewItem
                        {
                            Header = headerContent,
                            Foreground = openable ? Brushes.White : dim,
                            Tag = openable ? fv : null,     // FedView tag = selectable child view
                            FontWeight = FontWeights.Normal,
                            IsExpanded = false
                        });
                        added++;
                    }
                }

                if (added == 0)
                {
                    header.Items.Add(new TreeViewItem
                    {
                        Header = "   (no views reachable)",
                        Foreground = dim,
                        Tag = null,
                        FontWeight = FontWeights.Normal
                    });
                }

                tree.Items.Add(header);
            }
        }

        // Builds a TreeViewItem straight from a pre-fetched ClientNode - no GetChildren() calls, since
        // those were already resolved once when the site walk ran (see FederationWalker.BuildClientNode).
        // This is what makes a cache hit actually fast: without it, rebuilding the master's ~113+ view
        // group tree via live GetChildren() calls was still costing ~10 seconds on every Open View
        // click, even after CollectAllSiteViews itself started returning instantly from cache.
        private TreeViewItem CreateTreeNodeFromClientNode(FederationWalker.ClientNode node)
        {
            var item = node?.Item;
            if (item == null) return null;

            bool isFolder;
            try { isFolder = item.FQID.FolderType != FolderType.No; } catch { isFolder = false; }

            var treeViewItem = new TreeViewItem
            {
                Header = (isFolder ? "\U0001F4C1 " : "\U0001F4CB ") + item.Name,
                Tag = item,
                IsExpanded = true,
            };

            foreach (var child in node.Children)
            {
                var childNode = CreateTreeNodeFromClientNode(child);
                if (childNode != null) treeViewItem.Items.Add(childNode);
            }

            return treeViewItem;
        }

        private TreeViewItem CreateTreeNode(Item item)
        {
            bool isFolder = item.FQID.FolderType != FolderType.No;

            // In folder mode, skip views (leaves)
            if (_mode == BrowseMode.SelectFolder && !isFolder)
                return null;

            string icon = isFolder ? "\U0001F4C1 " : "\U0001F4CB ";
            var node = new TreeViewItem
            {
                Header = icon + item.Name,
                Tag = item,
                IsExpanded = true,
            };

            if (isFolder)
            {
                var configItem = item as ConfigItem;
                if (configItem != null)
                {
                    var children = configItem.GetChildren();
                    if (children != null)
                    {
                        foreach (var child in children)
                        {
                            var childNode = CreateTreeNode(child);
                            if (childNode != null)
                                node.Items.Add(childNode);
                        }
                    }
                }
            }

            return node;
        }

        private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            // While any checkboxes are checked, the button is in batch-copy mode and its enabled
            // state/label are driven by the checked count, not by whatever row happens to be
            // highlighted - see OnFedViewCheckChanged.
            if (_checkedFedViews.Count > 0) return;
            RefreshSelectButtonForSingleSelection();
        }

        private void RefreshSelectButtonForSingleSelection()
        {
            var selected = tree.SelectedItem as TreeViewItem;
            if (selected == null)
            {
                btnSelect.IsEnabled = false;
                return;
            }

            // Federated child-site view (read from the Management Server config): selectable only
            // when opening a view, never as a save folder.
            if (selected.Tag is FederationWalker.FedView)
            {
                btnSelect.IsEnabled = _mode == BrowseMode.SelectView;
                return;
            }

            var item = selected.Tag as Item;
            if (item == null)
            {
                btnSelect.IsEnabled = false;
                return;
            }

            bool isFolder = item.FQID.FolderType != FolderType.No;

            if (_mode == BrowseMode.SelectFolder)
                btnSelect.IsEnabled = isFolder;
            else
                btnSelect.IsEnabled = !isFolder;
        }

        // A checkbox next to an openable federated view was toggled. While one or more are checked,
        // btnSelect switches to "Copy N Selected" and closing the dialog returns SelectedFedViews
        // instead of the single-selection properties - see OnSelectClick.
        private void OnFedViewCheckChanged(object sender, RoutedEventArgs e)
        {
            var checkBox = sender as CheckBox;
            if (!(checkBox?.Tag is FederationWalker.FedView fv)) return;

            if (checkBox.IsChecked == true) _checkedFedViews.Add(fv);
            else _checkedFedViews.Remove(fv);

            if (_checkedFedViews.Count > 0)
            {
                btnSelect.Content = $"Copy {_checkedFedViews.Count} Selected";
                btnSelect.IsEnabled = true;
            }
            else
            {
                btnSelect.Content = "Select";
                RefreshSelectButtonForSingleSelection();
            }
        }

        private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
        {
            var query = (searchBox.Text ?? string.Empty).Trim();
            ApplyFilter(tree.Items, query);
        }

        // Returns true if the subtree rooted at one of these items has any visible match.
        // A node is visible when it (or any descendant) matches the query; folders auto-expand
        // while a query is active so matches are revealed.
        private bool ApplyFilter(ItemCollection items, string query)
        {
            bool anyVisible = false;
            foreach (var obj in items)
            {
                var node = obj as TreeViewItem;
                if (node == null) continue;

                bool selfMatches = string.IsNullOrEmpty(query) || NodeMatches(node, query);
                bool childMatches = ApplyFilter(node.Items, query);

                bool visible = selfMatches || childMatches;
                node.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

                if (!string.IsNullOrEmpty(query) && childMatches)
                    node.IsExpanded = true;

                if (visible) anyVisible = true;
            }
            return anyVisible;
        }

        private static bool NodeMatches(TreeViewItem node, string query)
        {
            var item = node.Tag as Item;
            if (item != null)
                return (item.Name ?? string.Empty).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

            // Openable federated views carry a FedView tag - their Header is a StackPanel (checkbox +
            // text), not a plain string, since the checkbox was added for batch selection.
            if (node.Tag is FederationWalker.FedView fv)
            {
                var text = $"{fv.GroupPath} / {fv.Name}";
                return text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
            }

            // Site headers / non-resolvable child views carry no Tag at all; match their plain-string text.
            var header = node.Header as string;
            return header != null && header.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void OnSelectClick(object sender, RoutedEventArgs e)
        {
            // Batch mode: one or more checkboxes are checked - return all of them regardless of
            // whatever row is currently highlighted in the tree.
            if (_checkedFedViews.Count > 0)
            {
                SelectedFedViews = _checkedFedViews.ToList();
                SelectedFedView = null;
                SelectedItem = null;
                SelectedParent = null;
                FlexViewDefinition.Log.Info($"[FlexViewFed] Selected {SelectedFedViews.Count} child-site view(s) for batch copy.");
                DialogResult = true;
                return;
            }

            var selected = tree.SelectedItem as TreeViewItem;
            if (selected == null) return;

            // A child-site view carries a FedView tag, not a client Item.
            if (selected.Tag is FederationWalker.FedView fv)
            {
                SelectedFedView = fv;
                SelectedItem = null;
                SelectedParent = null;
                FlexViewDefinition.Log.Info($"[FlexViewFed] Selected child-site view '{fv.Name}' on '{fv.SiteName}'");
                DialogResult = true;
                return;
            }

            SelectedItem = selected.Tag as Item;
            SelectedFedView = null;

            var parentNode = selected.Parent as TreeViewItem;
            if (parentNode != null)
                SelectedParent = parentNode.Tag as Item;

            DialogResult = true;
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}

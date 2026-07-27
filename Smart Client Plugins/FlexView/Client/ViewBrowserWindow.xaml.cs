using System;
using System.Collections.Generic;
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
            LoadTree();
        }

        private void LoadTree()
        {
            if (_federated && _mode == BrowseMode.SelectView)
            {
                LoadTreeFederated();
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

        // TEST (federated views): one non-selectable header node per site, each holding that site's
        // view roots. On a non-federated system (only the master) this falls back to the classic
        // flat tree so behavior is unchanged.
        private void LoadTreeFederated()
        {
            List<FederationWalker.SiteViews> siteViews;
            try { siteViews = FederationWalker.CollectAllSiteViews(); }
            catch (Exception ex)
            {
                FlexViewDefinition.Log.Info($"[FlexViewFed] CollectAllSiteViews failed: {ex.Message}");
                LoadTreeClassic();
                return;
            }

            if (siteViews == null || siteViews.Count <= 1)
            {
                FlexViewDefinition.Log.Info("[FlexViewFed] Single site (no children) - using classic flat tree.");
                LoadTreeClassic();
                return;
            }

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
                    // Master: real client Items - fully selectable/openable, same as today.
                    foreach (var root in sv.ClientViewRoots)
                    {
                        var node = CreateTreeNode(root);
                        if (node != null) { header.Items.Add(node); added++; }
                    }
                }
                else
                {
                    // Child site: views read from the Management Server config, each carrying its own
                    // captured layout XML so it can be copied into a folder on this site without ever
                    // needing a client-side handle to the source view. Views whose layout XML could
                    // not be read are shown but not selectable.
                    foreach (var fv in sv.FedViews)
                    {
                        bool openable = fv.HasLayoutXml;
                        header.Items.Add(new TreeViewItem
                        {
                            Header = $"\U0001F4CB {fv.GroupPath} / {fv.Name}" + (openable ? "" : "   [not resolvable]"),
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

            // Federated site-header / read-only child-view nodes carry no Item tag; match their text.
            var header = node.Header as string;
            return header != null && header.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void OnSelectClick(object sender, RoutedEventArgs e)
        {
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

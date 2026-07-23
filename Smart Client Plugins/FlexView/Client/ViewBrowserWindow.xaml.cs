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

        // TEST (federated views): true when the selected view lives on a different site than the
        // master we are logged into. The caller then loads it as a new copy so it can be saved into
        // a parent (master) folder rather than written back to the child site.
        public bool SelectedIsCrossSite { get; private set; }

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
                if (sv.ViewRoots != null)
                {
                    foreach (var root in sv.ViewRoots)
                    {
                        var node = CreateTreeNode(root);
                        if (node != null) { header.Items.Add(node); added++; }
                    }
                }

                if (added == 0)
                {
                    header.Items.Add(new TreeViewItem
                    {
                        Header = "   (no views reachable)",
                        Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E)),
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
            if (item == null) return false;
            return (item.Name ?? string.Empty).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void OnSelectClick(object sender, RoutedEventArgs e)
        {
            var selected = tree.SelectedItem as TreeViewItem;
            if (selected == null) return;

            SelectedItem = selected.Tag as Item;

            var parentNode = selected.Parent as TreeViewItem;
            if (parentNode != null)
                SelectedParent = parentNode.Tag as Item;

            // TEST (federated views): flag selections that live on a child site so the caller loads
            // them as a copy targeted at a parent folder.
            SelectedIsCrossSite = false;
            try
            {
                var masterSid = EnvironmentManager.Instance.MasterSite?.ServerId?.Id;
                var selSid = SelectedItem?.FQID?.ServerId?.Id;
                if (masterSid != null && selSid != null && masterSid != selSid)
                    SelectedIsCrossSite = true;
                FlexViewDefinition.Log.Info($"[FlexViewFed] Selected '{SelectedItem?.Name}' kind={SelectedItem?.FQID?.Kind} crossSite={SelectedIsCrossSite}");
            }
            catch { }

            DialogResult = true;
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}

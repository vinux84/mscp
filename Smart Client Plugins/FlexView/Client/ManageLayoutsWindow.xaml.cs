using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace FlexView.Client
{
    // Lists every layout on the site and deletes the selected one.
    //
    // Both the load and the delete are management-server round trips, so both run on a background
    // thread with the buttons disabled while in flight - the config API objects used here are the
    // raw ConfigurationItems types, which FederationWalker already established are safe off the UI
    // thread (unlike client-session objects such as ViewAndLayoutItem).
    public partial class ManageLayoutsWindow : Window
    {
        private List<LayoutRepository.LayoutGroupInfo> _groups = new List<LayoutRepository.LayoutGroupInfo>();
        private bool _busy;

        public ManageLayoutsWindow()
        {
            InitializeComponent();
            Loaded += async (s, e) => await LoadAsync();
        }

        private async Task LoadAsync()
        {
            SetBusy(true, "Loading layouts...");
            ShowPlaceholder("Loading layouts from the management server...");
            layoutList.ItemsSource = null;

            try
            {
                var groups = await Task.Run(() => LayoutRepository.LoadGroups());
                _groups = groups ?? new List<LayoutRepository.LayoutGroupInfo>();

                var layouts = _groups.SelectMany(g => g.Layouts)
                                     .OrderBy(l => l.GroupName, StringComparer.CurrentCultureIgnoreCase)
                                     .ThenBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase)
                                     .ToList();

                layoutList.ItemsSource = layouts;

                if (layouts.Count == 0)
                {
                    ShowPlaceholder(_groups.Count == 0
                        ? "This server exposes no layout groups."
                        : "No layouts defined yet. Build an arrangement in FlexView and use Save as Layout.");
                }
                else
                {
                    HidePlaceholder();
                }

                // A group that failed to enumerate is reported rather than silently dropped: the
                // list would otherwise look complete while missing entries.
                var failed = _groups.Where(g => g.LoadError != null).ToList();
                var summary = $"{layouts.Count} layout{(layouts.Count == 1 ? "" : "s")} in {_groups.Count} group{(_groups.Count == 1 ? "" : "s")}.";
                if (failed.Count > 0)
                {
                    summary += $" {failed.Count} group(s) could not be read: "
                             + string.Join("; ", failed.Select(g => $"{g.Name} ({g.LoadError})"))
                             + " See MIPLog for details.";
                    statusText.Foreground = System.Windows.Media.Brushes.Orange;
                }
                else
                {
                    statusText.ClearValue(ForegroundProperty);
                    statusText.Foreground = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0x8B, 0x94, 0x9E));
                }

                SetBusy(false, summary);
            }
            catch (Exception ex)
            {
                FlexViewDefinition.Log.Error($"[FlexViewLayout] ManageLayouts load failed - {ex.GetType().Name}: {ex.Message}", ex);
                ShowPlaceholder("Layouts could not be loaded.");
                SetBusy(false, $"Load failed: {ex.Message} See MIPLog for details.");
                statusText.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xF8, 0x51, 0x49));
            }

            UpdateDeleteEnabled();
        }

        private async void OnRefreshClick(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            await LoadAsync();
        }

        private async void OnDeleteClick(object sender, RoutedEventArgs e)
        {
            if (_busy) return;

            var layout = layoutList.SelectedItem as LayoutRepository.LayoutInfo;
            if (layout == null) return;

            var confirmed = MessageDialog.Confirm(
                "Delete Layout",
                $"Delete the layout \"{layout.Name}\" from group \"{layout.GroupName}\"?\n\n"
                + "It disappears from Add View for every operator on this site. This cannot be undone.",
                okText: "Delete",
                cancelText: "Cancel",
                owner: this);

            if (!confirmed)
            {
                FlexViewDefinition.Log.Info($"[FlexViewLayout] Delete of '{layout.Name}' cancelled by the operator.");
                return;
            }

            SetBusy(true, $"Deleting \"{layout.Name}\"...");

            try
            {
                await Task.Run(() => LayoutRepository.RemoveLayout(layout));
                FlexViewDefinition.Log.Info($"[FlexViewLayout] Delete of '{layout.Name}' succeeded.");

                // Reload rather than removing the row locally, so the list reflects what the server
                // actually holds - a partially applied delete would otherwise stay invisible.
                await LoadAsync();
                statusText.Text = $"Deleted \"{layout.Name}\". " + statusText.Text;
            }
            catch (Exception ex)
            {
                FlexViewDefinition.Log.Error($"[FlexViewLayout] Delete of '{layout.Name}' failed - {ex.GetType().Name}: {ex.Message}", ex);
                SetBusy(false, null);
                MessageDialog.ShowError("Delete Failed",
                    $"Could not delete \"{layout.Name}\":\n\n{ex.Message}\n\nSee MIPLog for the full detail.", this);
                statusText.Text = $"Delete failed: {ex.Message}";
                statusText.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xF8, 0x51, 0x49));
            }
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateDeleteEnabled();
        }

        private void UpdateDeleteEnabled()
        {
            deleteButton.IsEnabled = !_busy && layoutList.SelectedItem is LayoutRepository.LayoutInfo;
        }

        private void SetBusy(bool busy, string status)
        {
            _busy = busy;
            refreshButton.IsEnabled = !busy;
            layoutList.IsEnabled = !busy;
            if (status != null) statusText.Text = status;
            UpdateDeleteEnabled();
        }

        private void ShowPlaceholder(string message)
        {
            listPlaceholder.Text = message;
            listPlaceholder.Visibility = Visibility.Visible;
        }

        private void HidePlaceholder()
        {
            listPlaceholder.Visibility = Visibility.Collapsed;
        }
    }
}

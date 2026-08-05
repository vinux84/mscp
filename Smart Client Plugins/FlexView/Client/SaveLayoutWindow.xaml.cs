using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using SdkRectangle = System.Drawing.Rectangle;

namespace FlexView.Client
{
    // Name / description / group picker for "Save as Layout", with a preview rendered from the very
    // rectangles that will be written, so what the operator approves is what gets stored.
    //
    // The caller resolves the layout groups before opening this window: enumerating them is a
    // management-server round trip, and doing it here would freeze the dialog as it appears.
    public partial class SaveLayoutWindow : Window
    {
        private readonly List<LayoutRepository.LayoutGroupInfo> _groups;

        // Internal, not public: these surface LayoutRepository's internal types, and the window
        // class itself has to stay public for the XAML-generated partial to match.
        internal string LayoutName => nameBox.Text?.Trim();
        internal LayoutRepository.LayoutGroupInfo SelectedGroup =>
            groupCombo.SelectedItem as LayoutRepository.LayoutGroupInfo;

        public SaveLayoutWindow()
        {
            InitializeComponent();
        }

        internal SaveLayoutWindow(string defaultName,
                                  List<LayoutRepository.LayoutGroupInfo> groups,
                                  SdkRectangle[] rects) : this()
        {
            _groups = groups ?? new List<LayoutRepository.LayoutGroupInfo>();

            if (!string.IsNullOrEmpty(defaultName))
                nameBox.Text = defaultName;

            groupCombo.ItemsSource = _groups;
            groupCombo.DisplayMemberPath = nameof(LayoutRepository.LayoutGroupInfo.Name);
            if (_groups.Count > 0) groupCombo.SelectedIndex = 0;

            // No writable group means Save can never succeed. Say so on the dialog rather than
            // letting the operator fill it in and fail on submit.
            if (_groups.Count == 0)
            {
                ShowError("This server exposes no layout group to save into.");
                saveButton.IsEnabled = false;
                groupCombo.IsEnabled = false;
            }

            var count = rects?.Length ?? 0;
            paneSummary.Text = count == 1
                ? "1 pane will be stored."
                : $"{count} panes will be stored.";

            previewImage.Source = LayoutIconRenderer.RenderPreview(rects, 150, 120);

            Loaded += (s, e) =>
            {
                nameBox.Focus();
                nameBox.SelectAll();
            };
        }

        private void OnNameChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            // Clear a stale "name is required" as soon as the operator starts typing.
            if (errorText.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(nameBox.Text))
                errorText.Visibility = Visibility.Collapsed;
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(LayoutName))
            {
                ShowError("Enter a name for the layout.");
                nameBox.Focus();
                return;
            }

            if (SelectedGroup == null)
            {
                ShowError("Select a layout group.");
                return;
            }

            // Local duplicate check purely so the operator gets an instant answer instead of a
            // round trip. The server remains the authority and its rejection is still surfaced.
            var clash = SelectedGroup.Layouts
                .FirstOrDefault(l => string.Equals(l.Name, LayoutName, StringComparison.OrdinalIgnoreCase));
            if (clash != null)
            {
                ShowError($"'{clash.Name}' already exists in this group. Choose another name.");
                nameBox.Focus();
                nameBox.SelectAll();
                return;
            }

            DialogResult = true;
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void ShowError(string message)
        {
            errorText.Text = message;
            errorText.Visibility = Visibility.Visible;
        }
    }
}

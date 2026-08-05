using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Threading.Tasks;
using FlexView.Models;
using VideoOS.Platform;
using VideoOS.Platform.Client;
using SdkRectangle = System.Drawing.Rectangle;

namespace FlexView.Client
{
    public partial class FlexViewViewItemWpfUserControl : ViewItemWpfUserControl
    {
        private const int GridCols = 60;
        private const int GridRows = 60;
        private const double CanvasWidth = 800.0;
        private const double CanvasHeight = 450.0;
        private const double CellWidth = CanvasWidth / GridCols;   // 50.0
        private const double CellHeight = CanvasHeight / GridRows; // 50.0
        private const int SdkMax = 1000;
        private const double ResizeThreshold = 8.0;

        // Brushes
        private static readonly SolidColorBrush GridLineBrush = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255));
        private static readonly SolidColorBrush GridLineAccentBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
        private static readonly SolidColorBrush PaneFill = new SolidColorBrush(Color.FromArgb(50, 40, 40, 40));
        private static readonly SolidColorBrush PaneBorder = new SolidColorBrush(Color.FromRgb(80, 80, 80));
        private static readonly SolidColorBrush PaneHoverFill = new SolidColorBrush(Color.FromArgb(70, 60, 60, 60));
        private static readonly SolidColorBrush PaneHoverBorder = new SolidColorBrush(Color.FromRgb(110, 110, 110));
        private static readonly SolidColorBrush SelectedFill = new SolidColorBrush(Color.FromArgb(80, 80, 80, 80));
        private static readonly SolidColorBrush SelectedBorder = new SolidColorBrush(Color.FromRgb(140, 140, 140));
        private static readonly SolidColorBrush PreviewFill = new SolidColorBrush(Color.FromArgb(60, 88, 166, 255));
        private static readonly SolidColorBrush PreviewBorder = new SolidColorBrush(Color.FromRgb(88, 166, 255));
        private static readonly SolidColorBrush OverlapFill = new SolidColorBrush(Color.FromArgb(80, 248, 81, 73));
        private static readonly SolidColorBrush OverlapBorder = new SolidColorBrush(Color.FromRgb(248, 81, 73));
        private static readonly SolidColorBrush CameraLabelBrush = new SolidColorBrush(Color.FromRgb(88, 166, 255));
        private static readonly SolidColorBrush ResizeHandleFill = new SolidColorBrush(Color.FromRgb(160, 160, 160));
        private static readonly SolidColorBrush ResizeHandleBorder = new SolidColorBrush(Color.FromRgb(100, 100, 100));
        private static readonly SolidColorBrush CopyIconBrush = new SolidColorBrush(Color.FromRgb(170, 170, 170));
        private static readonly SolidColorBrush CopyIconHoverBrush = new SolidColorBrush(Color.FromRgb(88, 166, 255));

        // Pane state
        private readonly List<GridPane> _panes = new List<GridPane>();
        private GridPane _selectedPane;
        private GridPane _hoveredPane;
        private int _nextPaneId = 1;

        // Drag state
        private enum DragMode { None, Creating, Moving, Resizing }
        private DragMode _dragMode = DragMode.None;
        private int _dragStartCol, _dragStartRow;
        private int _createEndCol, _createEndRow;

        // Move state
        private int _moveOrigCol, _moveOrigRow;

        // Resize state
        private enum ResizeEdge { None, Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }
        private ResizeEdge _resizeEdge;
        private int _resizeOrigCol, _resizeOrigRow, _resizeOrigColSpan, _resizeOrigRowSpan;

        // Edit mode
        private ViewAndLayoutItem _editingView;
        private Item _editingParent;
        private bool _isEditMode;
        private bool _isDirty;

        // Save target
        private Item _targetFolder;

        public FlexViewViewItemWpfUserControl()
        {
            InitializeComponent();
        }

        public override void Init()
        {
            FlexViewDefinition.Log.Info("ViewItemWpfUserControl Init called");
            RedrawCanvas();
            UpdateStatus();
            FlexViewDefinition.Log.Info("ViewItemWpfUserControl Init completed");
        }

        public override void Close() { }

        public override bool Maximizable => true;
        public override bool Selectable => false;
        public override bool ShowToolbar => false;

        #region Canvas Drawing

        private void RedrawCanvas()
        {
            gridCanvas.Children.Clear();
            DrawGridLines();
            DrawDragPreview();
            DrawPanes();
            hintOverlay.Visibility = _panes.Count == 0 && _dragMode == DragMode.None
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private void DrawGridLines()
        {
            for (int i = 0; i <= GridCols; i++)
            {
                double x = i * CellWidth;
                bool isMajor = i % 4 == 0;
                gridCanvas.Children.Add(new Line
                {
                    X1 = x, Y1 = 0, X2 = x, Y2 = CanvasHeight,
                    Stroke = isMajor ? GridLineAccentBrush : GridLineBrush,
                    StrokeThickness = 0.5
                });
            }
            for (int i = 0; i <= GridRows; i++)
            {
                double y = i * CellHeight;
                bool isMajor = i % 3 == 0;
                gridCanvas.Children.Add(new Line
                {
                    X1 = 0, Y1 = y, X2 = CanvasWidth, Y2 = y,
                    Stroke = isMajor ? GridLineAccentBrush : GridLineBrush,
                    StrokeThickness = 0.5
                });
            }
        }

        private void DrawPanes()
        {
            foreach (var pane in _panes)
            {
                bool isSelected = pane == _selectedPane;
                bool isHovered = pane == _hoveredPane && !isSelected;
                bool hasOverlap = _panes.Any(other => other != pane && pane.Overlaps(other));

                double x = pane.Col * CellWidth;
                double y = pane.Row * CellHeight;
                double w = pane.ColSpan * CellWidth;
                double h = pane.RowSpan * CellHeight;

                SolidColorBrush fill, border;
                if (hasOverlap)
                {
                    fill = OverlapFill;
                    border = OverlapBorder;
                }
                else if (isSelected)
                {
                    fill = SelectedFill;
                    border = SelectedBorder;
                }
                else if (isHovered)
                {
                    fill = PaneHoverFill;
                    border = PaneHoverBorder;
                }
                else
                {
                    fill = PaneFill;
                    border = PaneBorder;
                }

                var rect = new Rectangle
                {
                    Width = w - 2,
                    Height = h - 2,
                    Fill = fill,
                    Stroke = border,
                    StrokeThickness = 1
                };
                Canvas.SetLeft(rect, x + 1);
                Canvas.SetTop(rect, y + 1);
                gridCanvas.Children.Add(rect);

                // Slot label: camera name for camera slots, plugin name for
                // non-camera plugin view items (e.g. "Metadata Display"). Both
                // are populated by TryReadSlotLabels when editing an existing
                // view; new panes have neither and stay unlabeled.
                string slotLabel = !string.IsNullOrEmpty(pane.CameraName) ? pane.CameraName : pane.PluginName;
                if (!string.IsNullOrEmpty(slotLabel) && w > 60 && h > 40)
                {
                    var label = new TextBlock
                    {
                        Text = slotLabel,
                        Foreground = CameraLabelBrush,
                        FontSize = 10,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxWidth = w - 12
                    };
                    Canvas.SetLeft(label, x + 6);
                    Canvas.SetTop(label, y + h - 20);
                    gridCanvas.Children.Add(label);
                }

                // Size label (center)
                if (w > 60 && h > 40)
                {
                    var sizeLabel = new TextBlock
                    {
                        Text = $"{pane.ColSpan}x{pane.RowSpan}",
                        Foreground = Brushes.White,
                        FontSize = 11,
                        Opacity = 0.4,
                        TextAlignment = TextAlignment.Center
                    };
                    sizeLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    Canvas.SetLeft(sizeLabel, x + (w - sizeLabel.DesiredSize.Width) / 2);
                    Canvas.SetTop(sizeLabel, y + (h - sizeLabel.DesiredSize.Height) / 2);
                    gridCanvas.Children.Add(sizeLabel);
                }

                // Resize handle (bottom-right corner only)
                if (isSelected || isHovered)
                {
                    DrawResizeHandle(x + w - 7, y + h - 7);
                }

                // Copy button (top-right corner). Shown on every pane large enough
                // to hold it, but only while a same-size duplicate can still fit
                // somewhere on the grid. When the grid fills up the button vanishes.
                if (w > 30 && h > 22 && TryFindCopySlot(pane, out _, out _))
                {
                    DrawCopyButton(pane, x + w - 20, y + 4);
                }
            }
        }

        private void DrawResizeHandle(double x, double y)
        {
            var handle = new Polygon
            {
                Points = new PointCollection
                {
                    new Point(0, 5),
                    new Point(5, 5),
                    new Point(5, 0)
                },
                Fill = ResizeHandleFill,
                Stroke = ResizeHandleBorder,
                StrokeThickness = 0.5,
                Opacity = 0.7
            };
            Canvas.SetLeft(handle, x);
            Canvas.SetTop(handle, y);
            gridCanvas.Children.Add(handle);
        }

        // A bare copy glyph rendered on top of the pane (no box or border). The
        // transparent background gives it a clickable bounds, and it turns blue while
        // hovered. It handles its own MouseLeftButtonDown (and marks it handled) so the
        // canvas create/move/resize logic never sees the click. The pane it copies is
        // carried in Tag.
        private void DrawCopyButton(GridPane pane, double left, double top)
        {
            var icon = new TextBlock
            {
                Text = "⧉",
                Foreground = CopyIconBrush,
                Background = Brushes.Transparent,
                FontSize = 10.8,
                Padding = new Thickness(2, 0, 2, 0),
                Cursor = Cursors.Hand,
                ToolTip = "Copy this pane to a free spot",
                Tag = pane
            };
            icon.MouseEnter += (s, e) => ((TextBlock)s).Foreground = CopyIconHoverBrush;
            icon.MouseLeave += (s, e) => ((TextBlock)s).Foreground = CopyIconBrush;
            icon.MouseLeftButtonDown += CopyButton_MouseLeftButtonDown;
            Canvas.SetLeft(icon, left);
            Canvas.SetTop(icon, top);
            gridCanvas.Children.Add(icon);
        }

        private void CopyButton_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if ((sender as TextBlock)?.Tag is GridPane src)
                CopyPane(src);
        }

        private void DrawDragPreview()
        {
            if (_dragMode != DragMode.Creating) return;

            int startCol = Math.Min(_dragStartCol, _createEndCol);
            int startRow = Math.Min(_dragStartRow, _createEndRow);
            int endCol = Math.Max(_dragStartCol, _createEndCol);
            int endRow = Math.Max(_dragStartRow, _createEndRow);

            double x = startCol * CellWidth;
            double y = startRow * CellHeight;
            double w = (endCol - startCol + 1) * CellWidth;
            double h = (endRow - startRow + 1) * CellHeight;

            var preview = new GridPane
            {
                Col = startCol, Row = startRow,
                ColSpan = endCol - startCol + 1,
                RowSpan = endRow - startRow + 1
            };
            bool overlaps = _panes.Any(p => preview.Overlaps(p));

            var rect = new Rectangle
            {
                Width = w - 2,
                Height = h - 2,
                Fill = overlaps ? OverlapFill : PreviewFill,
                Stroke = overlaps ? OverlapBorder : PreviewBorder,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 4, 2 }
            };
            Canvas.SetLeft(rect, x + 1);
            Canvas.SetTop(rect, y + 1);
            gridCanvas.Children.Add(rect);

            var label = new TextBlock
            {
                Text = $"{endCol - startCol + 1}x{endRow - startRow + 1}",
                Foreground = Brushes.White,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Opacity = 0.8
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, x + (w - label.DesiredSize.Width) / 2);
            Canvas.SetTop(label, y + (h - label.DesiredSize.Height) / 2);
            gridCanvas.Children.Add(label);
        }

        #endregion

        #region Mouse Event Handlers

        private (int col, int row) GetGridPosition(Point canvasPos)
        {
            int col = (int)(canvasPos.X / CellWidth);
            int row = (int)(canvasPos.Y / CellHeight);
            col = Math.Max(0, Math.Min(col, GridCols - 1));
            row = Math.Max(0, Math.Min(row, GridRows - 1));
            return (col, row);
        }

        private GridPane GetPaneAt(int col, int row)
        {
            for (int i = _panes.Count - 1; i >= 0; i--)
            {
                if (_panes[i].Contains(col, row))
                    return _panes[i];
            }
            return null;
        }

        private ResizeEdge GetResizeEdge(GridPane pane, Point canvasPos)
        {
            double px = pane.Col * CellWidth;
            double py = pane.Row * CellHeight;
            double pw = pane.ColSpan * CellWidth;
            double ph = pane.RowSpan * CellHeight;

            bool nearLeft = Math.Abs(canvasPos.X - px) < ResizeThreshold;
            bool nearRight = Math.Abs(canvasPos.X - (px + pw)) < ResizeThreshold;
            bool nearTop = Math.Abs(canvasPos.Y - py) < ResizeThreshold;
            bool nearBottom = Math.Abs(canvasPos.Y - (py + ph)) < ResizeThreshold;

            if (nearTop && nearLeft) return ResizeEdge.TopLeft;
            if (nearTop && nearRight) return ResizeEdge.TopRight;
            if (nearBottom && nearLeft) return ResizeEdge.BottomLeft;
            if (nearBottom && nearRight) return ResizeEdge.BottomRight;
            if (nearLeft) return ResizeEdge.Left;
            if (nearRight) return ResizeEdge.Right;
            if (nearTop) return ResizeEdge.Top;
            if (nearBottom) return ResizeEdge.Bottom;

            return ResizeEdge.None;
        }

        private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var pos = e.GetPosition(gridCanvas);
            var (col, row) = GetGridPosition(pos);

            var pane = GetPaneAt(col, row);

            if (pane != null)
            {
                _selectedPane = pane;

                var edge = GetResizeEdge(pane, pos);
                if (edge != ResizeEdge.None)
                {
                    _dragMode = DragMode.Resizing;
                    _resizeEdge = edge;
                    _resizeOrigCol = pane.Col;
                    _resizeOrigRow = pane.Row;
                    _resizeOrigColSpan = pane.ColSpan;
                    _resizeOrigRowSpan = pane.RowSpan;
                    _dragStartCol = col;
                    _dragStartRow = row;
                }
                else
                {
                    _dragMode = DragMode.Moving;
                    _moveOrigCol = pane.Col;
                    _moveOrigRow = pane.Row;
                    _dragStartCol = col;
                    _dragStartRow = row;
                }
            }
            else
            {
                _selectedPane = null;
                _dragMode = DragMode.Creating;
                _dragStartCol = col;
                _dragStartRow = row;
                _createEndCol = col;
                _createEndRow = row;
            }

            _hoveredPane = null;
            gridCanvas.CaptureMouse();
            RedrawCanvas();
            e.Handled = true;
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            var pos = e.GetPosition(gridCanvas);
            var (col, row) = GetGridPosition(pos);

            if (_dragMode == DragMode.Creating)
            {
                _createEndCol = col;
                _createEndRow = row;
                RedrawCanvas();
            }
            else if (_dragMode == DragMode.Moving && _selectedPane != null)
            {
                int deltaCol = col - _dragStartCol;
                int deltaRow = row - _dragStartRow;
                int newCol = _moveOrigCol + deltaCol;
                int newRow = _moveOrigRow + deltaRow;

                newCol = Math.Max(0, Math.Min(newCol, GridCols - _selectedPane.ColSpan));
                newRow = Math.Max(0, Math.Min(newRow, GridRows - _selectedPane.RowSpan));

                _selectedPane.Col = newCol;
                _selectedPane.Row = newRow;
                RedrawCanvas();
            }
            else if (_dragMode == DragMode.Resizing && _selectedPane != null)
            {
                ApplyResize(col, row);
                RedrawCanvas();
            }
            else
            {
                // Hover tracking
                var pane = GetPaneAt(col, row);
                if (pane != _hoveredPane)
                {
                    _hoveredPane = pane;
                    RedrawCanvas();
                }

                if (pane != null)
                {
                    var edge = GetResizeEdge(pane, pos);
                    gridCanvas.Cursor = GetCursorForEdge(edge);
                }
                else
                {
                    gridCanvas.Cursor = Cursors.Cross;
                }
            }
        }

        private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            gridCanvas.ReleaseMouseCapture();

            if (_dragMode == DragMode.Creating)
            {
                FinalizeCreate();
                _selectedPane = null;
            }
            else if (_dragMode == DragMode.Moving && _selectedPane != null)
            {
                FinalizeMove();
                _selectedPane = null;
            }
            else if (_dragMode == DragMode.Resizing && _selectedPane != null)
            {
                FinalizeResize();
                _selectedPane = null;
            }

            _dragMode = DragMode.None;
            _hoveredPane = null;
            RedrawCanvas();
            UpdateStatus();
            FireClickEvent();
        }

        private void Canvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            var pos = e.GetPosition(gridCanvas);
            var (col, row) = GetGridPosition(pos);
            var pane = GetPaneAt(col, row);

            if (pane != null)
            {
                _panes.Remove(pane);
                if (_selectedPane == pane) _selectedPane = null;
                if (_hoveredPane == pane) _hoveredPane = null;
                RenumberPanes();
                _isDirty = true;
                RedrawCanvas();
                UpdateStatus();
            }

            e.Handled = true;
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete && _selectedPane != null)
            {
                _panes.Remove(_selectedPane);
                _selectedPane = null;
                RenumberPanes();
                _isDirty = true;
                RedrawCanvas();
                UpdateStatus();
                e.Handled = true;
            }
        }

        #endregion

        #region Drag Finalization

        private void FinalizeCreate()
        {
            int startCol = Math.Min(_dragStartCol, _createEndCol);
            int startRow = Math.Min(_dragStartRow, _createEndRow);
            int endCol = Math.Max(_dragStartCol, _createEndCol);
            int endRow = Math.Max(_dragStartRow, _createEndRow);

            int colSpan = endCol - startCol + 1;
            int rowSpan = endRow - startRow + 1;
            if (colSpan < 2 || rowSpan < 2)
                return;

            var newPane = new GridPane
            {
                Id = _nextPaneId++,
                Col = startCol,
                Row = startRow,
                ColSpan = colSpan,
                RowSpan = rowSpan
            };

            if (!IsInBounds(newPane) || HasOverlap(newPane))
                return;

            _panes.Add(newPane);
            _selectedPane = newPane;
            _isDirty = true;
        }

        private void FinalizeMove()
        {
            if (HasOverlap(_selectedPane))
            {
                _selectedPane.Col = _moveOrigCol;
                _selectedPane.Row = _moveOrigRow;
            }
            else if (_selectedPane.Col != _moveOrigCol || _selectedPane.Row != _moveOrigRow)
            {
                _isDirty = true;
            }
        }

        private void FinalizeResize()
        {
            if (!IsInBounds(_selectedPane) || HasOverlap(_selectedPane))
            {
                _selectedPane.Col = _resizeOrigCol;
                _selectedPane.Row = _resizeOrigRow;
                _selectedPane.ColSpan = _resizeOrigColSpan;
                _selectedPane.RowSpan = _resizeOrigRowSpan;
            }
            else if (_selectedPane.Col != _resizeOrigCol || _selectedPane.Row != _resizeOrigRow ||
                     _selectedPane.ColSpan != _resizeOrigColSpan || _selectedPane.RowSpan != _resizeOrigRowSpan)
            {
                _isDirty = true;
            }
        }

        private void ApplyResize(int currentCol, int currentRow)
        {
            var pane = _selectedPane;
            int deltaCol = currentCol - _dragStartCol;
            int deltaRow = currentRow - _dragStartRow;

            switch (_resizeEdge)
            {
                case ResizeEdge.Right:
                    pane.ColSpan = Math.Max(2, _resizeOrigColSpan + deltaCol);
                    break;
                case ResizeEdge.Bottom:
                    pane.RowSpan = Math.Max(2, _resizeOrigRowSpan + deltaRow);
                    break;
                case ResizeEdge.BottomRight:
                    pane.ColSpan = Math.Max(2, _resizeOrigColSpan + deltaCol);
                    pane.RowSpan = Math.Max(2, _resizeOrigRowSpan + deltaRow);
                    break;
                case ResizeEdge.Left:
                    {
                        int newCol = _resizeOrigCol + deltaCol;
                        int newSpan = _resizeOrigColSpan - deltaCol;
                        if (newCol >= 0 && newSpan >= 2)
                        {
                            pane.Col = newCol;
                            pane.ColSpan = newSpan;
                        }
                    }
                    break;
                case ResizeEdge.Top:
                    {
                        int newRow = _resizeOrigRow + deltaRow;
                        int newSpan = _resizeOrigRowSpan - deltaRow;
                        if (newRow >= 0 && newSpan >= 2)
                        {
                            pane.Row = newRow;
                            pane.RowSpan = newSpan;
                        }
                    }
                    break;
                case ResizeEdge.TopLeft:
                    {
                        int newCol = _resizeOrigCol + deltaCol;
                        int newColSpan = _resizeOrigColSpan - deltaCol;
                        int newRow = _resizeOrigRow + deltaRow;
                        int newRowSpan = _resizeOrigRowSpan - deltaRow;
                        if (newCol >= 0 && newColSpan >= 2 && newRow >= 0 && newRowSpan >= 2)
                        {
                            pane.Col = newCol;
                            pane.ColSpan = newColSpan;
                            pane.Row = newRow;
                            pane.RowSpan = newRowSpan;
                        }
                    }
                    break;
                case ResizeEdge.TopRight:
                    {
                        int newRow = _resizeOrigRow + deltaRow;
                        int newRowSpan = _resizeOrigRowSpan - deltaRow;
                        if (newRow >= 0 && newRowSpan >= 2)
                        {
                            pane.ColSpan = Math.Max(2, _resizeOrigColSpan + deltaCol);
                            pane.Row = newRow;
                            pane.RowSpan = newRowSpan;
                        }
                    }
                    break;
                case ResizeEdge.BottomLeft:
                    {
                        int newCol = _resizeOrigCol + deltaCol;
                        int newColSpan = _resizeOrigColSpan - deltaCol;
                        if (newCol >= 0 && newColSpan >= 2)
                        {
                            pane.Col = newCol;
                            pane.ColSpan = newColSpan;
                            pane.RowSpan = Math.Max(2, _resizeOrigRowSpan + deltaRow);
                        }
                    }
                    break;
            }

            pane.Col = Math.Max(0, pane.Col);
            pane.Row = Math.Max(0, pane.Row);
            pane.ColSpan = Math.Min(pane.ColSpan, GridCols - pane.Col);
            pane.RowSpan = Math.Min(pane.RowSpan, GridRows - pane.Row);
        }

        #endregion

        #region Helpers

        private bool IsInBounds(GridPane pane)
        {
            return pane.Col >= 0 && pane.Row >= 0 &&
                   pane.Col + pane.ColSpan <= GridCols &&
                   pane.Row + pane.RowSpan <= GridRows;
        }

        private bool HasOverlap(GridPane pane)
        {
            return _panes.Any(other => other != pane && pane.Overlaps(other));
        }

        // Find where a same-size duplicate of src can be dropped without overlapping
        // any existing pane. Prefers the cell immediately to the right, then directly
        // below, then a row-major first-fit scan of the whole grid. Returns false when
        // nothing of that size fits anywhere (grid is full for this size).
        private bool TryFindCopySlot(GridPane src, out int foundCol, out int foundRow)
        {
            int cs = src.ColSpan, rs = src.RowSpan;

            bool Fits(int col, int row)
            {
                if (col < 0 || row < 0 || col + cs > GridCols || row + rs > GridRows)
                    return false;
                var candidate = new GridPane { Col = col, Row = row, ColSpan = cs, RowSpan = rs };
                return !_panes.Any(p => candidate.Overlaps(p));
            }

            // Preferred adjacent placements first.
            if (Fits(src.Col + cs, src.Row)) { foundCol = src.Col + cs; foundRow = src.Row; return true; }
            if (Fits(src.Col, src.Row + rs)) { foundCol = src.Col; foundRow = src.Row + rs; return true; }

            // First-fit scan, top-left to bottom-right.
            for (int row = 0; row + rs <= GridRows; row++)
            {
                for (int col = 0; col + cs <= GridCols; col++)
                {
                    if (Fits(col, row)) { foundCol = col; foundRow = row; return true; }
                }
            }

            foundCol = 0;
            foundRow = 0;
            return false;
        }

        // Duplicate a pane's shape (size only) into the first free slot. The copy is a
        // fresh, empty pane (OriginalSlotIndex = -1) just like a hand-drawn one, so it
        // carries no camera assignment.
        private void CopyPane(GridPane src)
        {
            if (!TryFindCopySlot(src, out int col, out int row))
                return;

            var copy = new GridPane
            {
                Id = _nextPaneId++,
                Col = col,
                Row = row,
                ColSpan = src.ColSpan,
                RowSpan = src.RowSpan
            };
            _panes.Add(copy);
            _selectedPane = copy;
            _hoveredPane = null;
            _isDirty = true;
            RedrawCanvas();
            UpdateStatus();
        }

        // Find the grid column whose SDK X (SdkMax * col / GridCols) is closest to sdkX
        private static int ClosestGridCol(int sdkX)
        {
            int best = 0;
            int bestDist = int.MaxValue;
            for (int c = 0; c <= GridCols; c++)
            {
                int dist = Math.Abs(SdkMax * c / GridCols - sdkX);
                if (dist < bestDist) { bestDist = dist; best = c; }
            }
            return Math.Min(best, GridCols);
        }

        private static int ClosestGridRow(int sdkY)
        {
            int best = 0;
            int bestDist = int.MaxValue;
            for (int r = 0; r <= GridRows; r++)
            {
                int dist = Math.Abs(SdkMax * r / GridRows - sdkY);
                if (dist < bestDist) { bestDist = dist; best = r; }
            }
            return Math.Min(best, GridRows);
        }

        private void RenumberPanes()
        {
            for (int i = 0; i < _panes.Count; i++)
                _panes[i].Id = i + 1;
            _nextPaneId = _panes.Count + 1;
        }

        private Cursor GetCursorForEdge(ResizeEdge edge)
        {
            switch (edge)
            {
                case ResizeEdge.Left:
                case ResizeEdge.Right: return Cursors.SizeWE;
                case ResizeEdge.Top:
                case ResizeEdge.Bottom: return Cursors.SizeNS;
                case ResizeEdge.TopLeft:
                case ResizeEdge.BottomRight: return Cursors.SizeNWSE;
                case ResizeEdge.TopRight:
                case ResizeEdge.BottomLeft: return Cursors.SizeNESW;
                default: return Cursors.SizeAll;
            }
        }

        private void UpdateStatus()
        {
            string mode = _isEditMode ? "Edit" : "New";
            statusText.Text = $"{mode} | {_panes.Count} pane{(_panes.Count != 1 ? "s" : "")} | {GridCols}x{GridRows} grid";
        }

        private void ShowSavedStatus(string viewName)
        {
            FlashStatus($"Saved \"{viewName}\"");
        }

        // Green confirmation in the status corner that reverts to the normal pane/grid readout
        // after three seconds.
        private void FlashStatus(string message)
        {
            var original = statusText.Foreground;
            statusText.Text = message;
            statusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF4CAF50"));

            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                statusText.Foreground = original;
                UpdateStatus();
            };
            timer.Start();
        }

        #endregion

        #region SDK Coordinate Conversion

        private SdkRectangle[] ConvertPanesToSdkLayout()
        {
            var ordered = _panes
                .Where(p => p.OriginalSlotIndex >= 0)
                .OrderBy(p => p.OriginalSlotIndex)
                .Concat(_panes.Where(p => p.OriginalSlotIndex < 0))
                .ToList();

            var rects = new SdkRectangle[ordered.Count];
            for (int i = 0; i < ordered.Count; i++)
            {
                var p = ordered[i];
                int x = SdkMax * p.Col / GridCols;
                int y = SdkMax * p.Row / GridRows;
                int x2 = SdkMax * (p.Col + p.ColSpan) / GridCols;
                int y2 = SdkMax * (p.Row + p.RowSpan) / GridRows;
                rects[i] = new SdkRectangle(x, y, x2 - x, y2 - y);
            }
            return rects;
        }

        private void LoadFromSdkLayout(SdkRectangle[] layout)
        {
            _panes.Clear();
            _nextPaneId = 1;

            for (int i = 0; i < layout.Length; i++)
            {
                var rect = layout[i];
                // Find closest grid column/row whose SDK coordinate matches
                int col = ClosestGridCol(rect.X);
                int row = ClosestGridRow(rect.Y);
                int colEnd = ClosestGridCol(rect.X + rect.Width);
                int rowEnd = ClosestGridRow(rect.Y + rect.Height);
                int colSpan = Math.Max(2, colEnd - col);
                int rowSpan = Math.Max(2, rowEnd - row);

                col = Math.Max(0, Math.Min(col, GridCols - 1));
                row = Math.Max(0, Math.Min(row, GridRows - 1));
                colSpan = Math.Min(colSpan, GridCols - col);
                rowSpan = Math.Min(rowSpan, GridRows - row);

                _panes.Add(new GridPane
                {
                    Id = _nextPaneId++,
                    Col = col,
                    Row = row,
                    ColSpan = colSpan,
                    RowSpan = rowSpan,
                    OriginalSlotIndex = i
                });
            }
        }

        #endregion

        #region View Save / Load

        // Returns the freshly-created view on success, null on failure.
        // If slotContent is provided, the new view's slots are populated in the same
        // order ConvertPanesToSdkLayout produces: built-ins via InsertBuiltinViewItem
        // and plugin view items (e.g. Metadata Display) via InsertViewItemPlugin.
        private ViewAndLayoutItem SaveNewView(string name, Item folder, List<SlotSnapshot> slotContent = null)
        {
            try
            {
                var configFolder = folder as ConfigItem;
                if (configFolder == null)
                {
                    MessageDialog.ShowError("Save Failed", "Selected folder is not valid.", Window.GetWindow(this));
                    return null;
                }

                var rects = ConvertPanesToSdkLayout();
                var view = configFolder.AddChild(name, Kind.View, FolderType.No) as ViewAndLayoutItem;
                if (view == null)
                {
                    MessageDialog.ShowError("Save Failed", "Failed to create view. Check folder permissions.", Window.GetWindow(this));
                    return null;
                }

                view.Layout = rects;
                view.Save();
                if (slotContent != null && slotContent.Count > 0)
                {
                    RestoreSlotContent(view, slotContent);
                    view.Save();
                }
                configFolder.PropertiesModified();
                ShowSavedStatus(name);
                MessageDialog.ShowSuccess("View Saved", $"View \"{name}\" was saved successfully.", Window.GetWindow(this));
                return view;
            }
            catch (Exception ex)
            {
                FlexViewDefinition.Log.Info($"SaveNewView failed: {ex}");
                MessageDialog.ShowError("Save Failed", $"Failed to save view:\n{ex.Message}", Window.GetWindow(this));
                return null;
            }
        }

        // ViewAndLayoutItem.Layout is one-shot: its setter throws "Cannot change layout
        // on existing View" on any saved view. Every edit must delete the existing view
        // and create a new one. We snapshot each slot's content (built-ins like camera
        // or hotspot, and plugin view items like Metadata Display) before deletion and
        // re-attach on the new view via InsertBuiltinViewItem or InsertViewItemPlugin.
        // Plain Properties writes on slot children silently revert to "Empty ViewItem"
        // because the owning plugin doesn't persist them.
        private void SaveEditedView()
        {
            if (_editingView == null) return;

            if (!_isDirty)
            {
                ShowSavedStatus(_editingView.Name);
                return;
            }

            var parentConfig = _editingParent as ConfigItem;
            if (parentConfig == null)
            {
                MessageDialog.ShowError("Save Failed", "Cannot update view: parent folder is not valid.", Window.GetWindow(this));
                return;
            }

            try
            {
                var rects = ConvertPanesToSdkLayout();
                var viewName = _editingView.Name;

                FlexViewDefinition.Log.Info($"SaveEditedView: name='{viewName}', panes={_panes.Count}, rects={rects.Length}");

                var slotContent = SnapshotSlotContent(_editingView);

                parentConfig.RemoveChild(_editingView);
                var newView = parentConfig.AddChild(viewName, Kind.View, FolderType.No) as ViewAndLayoutItem;
                if (newView == null)
                {
                    FlexViewDefinition.Log.Info("SaveEditedView: AddChild returned null/non-ViewAndLayoutItem");
                    MessageDialog.ShowError("Save Failed", "Failed to recreate view.", Window.GetWindow(this));
                    return;
                }

                newView.Layout = rects;
                newView.Save();
                RestoreSlotContent(newView, slotContent);
                newView.Save();

                parentConfig.PropertiesModified();

                _editingView = newView;
                _isDirty = false;
                ShowSavedStatus(viewName);
                FlexViewDefinition.Log.Info("SaveEditedView: success");
                MessageDialog.ShowSuccess("View Saved", $"View \"{viewName}\" was saved successfully.", Window.GetWindow(this));
            }
            catch (Exception ex)
            {
                FlexViewDefinition.Log.Info($"SaveEditedView failed: {ex}");
                MessageDialog.ShowError("Save Failed", $"Failed to update view:\n{ex.Message}", Window.GetWindow(this));
            }
        }

        private class SlotSnapshot
        {
            public Guid ViewItemId;
            public Dictionary<string, string> Properties;
        }

        // One entry per pane in ConvertPanesToSdkLayout order: original slots first
        // (sorted by OriginalSlotIndex), then newly-added panes get null entries.
        private List<SlotSnapshot> SnapshotSlotContent(ViewAndLayoutItem view)
        {
            var children = (view as ConfigItem)?.GetChildren();
            var ordered = _panes
                .Where(p => p.OriginalSlotIndex >= 0)
                .OrderBy(p => p.OriginalSlotIndex)
                .Concat(_panes.Where(p => p.OriginalSlotIndex < 0));

            var result = new List<SlotSnapshot>();
            foreach (var pane in ordered)
            {
                SlotSnapshot snap = null;
                if (pane.OriginalSlotIndex >= 0 && children != null && pane.OriginalSlotIndex < children.Count)
                {
                    var src = children[pane.OriginalSlotIndex];
                    if (src?.Properties != null &&
                        src.Properties.TryGetValue("ViewItemId", out var vid) &&
                        Guid.TryParse(vid, out var viewItemId))
                    {
                        snap = new SlotSnapshot { ViewItemId = viewItemId, Properties = new Dictionary<string, string>() };
                        foreach (var key in src.Properties.Keys)
                        {
                            if (key == "ViewItemId" || key == "Index" || key == "Builtin") continue;
                            snap.Properties[key] = src.Properties[key];
                        }
                    }
                }
                result.Add(snap);
            }
            return result;
        }

        private void RestoreSlotContent(ViewAndLayoutItem view, List<SlotSnapshot> snapshots)
        {
            Guid emptyBuiltinId;
            try { emptyBuiltinId = ViewAndLayoutItem.EmptyBuiltinId; }
            catch { emptyBuiltinId = new Guid("57abe978-8861-4577-8a09-5fa6e43c4109"); }

            var pluginsById = BuildViewItemPluginLookup();

            for (int i = 0; i < snapshots.Count; i++)
            {
                var snap = snapshots[i];
                if (snap == null || snap.ViewItemId == emptyBuiltinId) continue;

                try
                {
                    if (pluginsById.TryGetValue(snap.ViewItemId, out var plugin))
                    {
                        view.InsertViewItemPlugin(i, plugin, snap.Properties);
                        FlexViewDefinition.Log.Info($"slot[{i}]: restored plugin '{plugin.Name}' ({snap.ViewItemId}) props={snap.Properties.Count}");
                    }
                    else
                    {
                        view.InsertBuiltinViewItem(i, snap.ViewItemId, snap.Properties);
                        FlexViewDefinition.Log.Info($"slot[{i}]: restored builtin={snap.ViewItemId} props={snap.Properties.Count}");
                    }
                }
                catch (Exception ex)
                {
                    FlexViewDefinition.Log.Info($"slot[{i}]: restore failed for {snap.ViewItemId}: {ex.Message}");
                }
            }
        }

        // Enumerate every ViewItemPlugin registered in the current Smart Client so we
        // can dispatch saved-slot GUIDs to InsertViewItemPlugin. Without this, plugin
        // view items (Metadata Display, Remote Manager, etc.) silently disappear after
        // edit/save-as because InsertBuiltinViewItem rejects non-builtin GUIDs.
        private static Dictionary<Guid, ViewItemPlugin> BuildViewItemPluginLookup()
        {
            var lookup = new Dictionary<Guid, ViewItemPlugin>();
            try
            {
                var defs = EnvironmentManager.Instance.AllPluginDefinitions;
                if (defs == null) return lookup;
                foreach (var def in defs)
                {
                    if (def?.ViewItemPlugins == null) continue;
                    foreach (var vp in def.ViewItemPlugins)
                    {
                        if (vp == null) continue;
                        lookup[vp.Id] = vp;
                    }
                }
            }
            catch (Exception ex)
            {
                FlexViewDefinition.Log.Info($"Failed to enumerate ViewItemPlugins: {ex.Message}");
            }
            return lookup;
        }

        private void LoadViewForEditing(ViewAndLayoutItem view, Item parent)
        {
            _isEditMode = true;
            _editingView = view;
            _editingParent = parent;
            _selectedPane = null;
            _hoveredPane = null;

            var layout = view.Layout;
            if (layout == null || layout.Length == 0)
            {
                MessageBox.Show("Selected view has no layout data.", "FlexView", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            FlexViewDefinition.Log.Info($"LoadViewForEditing: name='{view.Name}', slots={layout.Length}");

            LoadFromSdkLayout(layout);
            TryReadSlotLabels(view);

            _targetFolder = parent;
            viewNameLabel.Text = view.Name;
            _isDirty = false;
            saveAsButton.Visibility = Visibility.Visible;

            RedrawCanvas();
            UpdateStatus();
        }

        // Copies a child-site view straight from its captured Management Server configuration (name,
        // layout type, and the raw LayoutViewItems XML - the same wire format Get/AddView both read
        // and write) into a folder on this site. This never routes through Configuration.Instance
        // .GetItem for the source view - that lookup only resolves items the client session actually
        // federates, and Views are not part of MFA's federated resource model (same limitation
        // ViewSync's README documents for View Groups). The destination write goes through the same
        // ConfigurationItems API (ViewGroup.ViewFolder.AddView), just targeted at this, locally
        // connected/writable site instead of the source.

        // Result of the background phase: everything RestoreCameraSlots needs, plus what's needed to
        // report the outcome. Deliberately carries no ViewAndLayoutItem/client-session object across
        // the Task.Run boundary - see the thread-affinity note on RestoreCameraSlots below.
        private class PreparedCopy
        {
            public string NewViewPath;
            public ServerId MasterServerId;
            public List<FederationWalker.FedViewItem> CameraItems;
        }

        // Resolved once per destination folder, then reused for every view in a batch copy - avoids
        // re-walking the whole ViewGroupFolder tree (FindViewGroupById) once per selected view, and
        // ExistingNames lets every view in the batch get a unique name up front instead of each one
        // finding out about a collision only when AddView itself rejects it.
        private class DestinationInfo
        {
            public VideoOS.Platform.ConfigurationItems.ViewGroup ViewGroup;
            public ServerId MasterServerId;
            public HashSet<string> ExistingNames;
        }

        // Background-thread phase: raw config-API only, no client-session objects touched.
        private static DestinationInfo ResolveDestination(Item destFolder)
        {
            var masterFqid = EnvironmentManager.Instance.MasterSite;
            if (masterFqid == null) throw new InvalidOperationException("Master site is not available.");

            var ms = new VideoOS.Platform.ConfigurationItems.ManagementServer(masterFqid);
            var viewGroup = FederationWalker.FindViewGroupById(ms.ViewGroupFolder, destFolder.FQID.ObjectId);
            if (viewGroup == null)
                throw new InvalidOperationException($"Could not locate '{destFolder.Name}' in the site configuration.");

            var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var views = viewGroup.ViewFolder?.Views;
                if (views != null) foreach (var v in views) existingNames.Add(v.Name);
            }
            catch { }

            return new DestinationInfo { ViewGroup = viewGroup, MasterServerId = masterFqid.ServerId, ExistingNames = existingNames };
        }

        // Appends " (n)" until the name doesn't collide with anything already in ExistingNames -
        // covers both views already in the destination folder and other views earlier in this same
        // batch (ExistingNames is mutated as each name is claimed).
        private static string UniqueName(HashSet<string> existingNames, string baseName)
        {
            var candidate = baseName;
            var n = 1;
            while (existingNames.Contains(candidate))
                candidate = $"{baseName} ({++n})";
            existingNames.Add(candidate);
            return candidate;
        }

        // AddView + WaitForServerTask + reading the source's camera items are all raw config-API
        // calls (VideoOS.Platform.ConfigurationItems) - thread-agnostic, confirmed safe on a
        // background thread. Restoring those cameras onto the new view is NOT: that touches a
        // ViewAndLayoutItem, a client-session object, on the UI thread only (see RestoreCameraSlots).
        // So the slow network-bound part runs via Task.Run, and only the final restore step - bounded
        // to a few seconds by RestoreCameraSlots' own retry cap - runs on the UI thread.
        private async void CopyFederatedViewToLocal(FederationWalker.FedView fv)
        {
            if (fv == null || !fv.HasLayoutXml)
            {
                MessageDialog.ShowError("Open Failed",
                    "This view's layout could not be read from its site's configuration. See MIPLog for details.",
                    Window.GetWindow(this));
                return;
            }

            var dlg = new SaveViewWindow(fv.Name, null);
            dlg.Owner = Application.Current.MainWindow;
            if (dlg.ShowDialog() != true) return;

            var destFolder = dlg.SelectedFolder;
            var newName = dlg.ViewName;

            try
            {
                var prep = await Task.Run(() =>
                {
                    var dest = ResolveDestination(destFolder);
                    return PrepareFederatedCopy(fv, dest.ViewGroup, dest.MasterServerId, newName);
                });

                int restored = 0, attempted = prep.CameraItems.Count;
                if (attempted > 0)
                    restored = await RestoreCameraSlots(prep.MasterServerId, prep.NewViewPath, prep.CameraItems);

                FlexViewDefinition.Log.Info($"[FlexViewFed] Copied '{fv.Name}' from site '{fv.SiteName}' into '{destFolder.Name}' as '{newName}' ({restored}/{attempted} camera(s) restored).");
                var cameraNote = attempted == 0 ? "" : restored == attempted
                    ? $" All {attempted} camera(s) were carried over."
                    : $" {restored} of {attempted} camera(s) were carried over - see MIPLog for the rest.";
                MessageDialog.ShowSuccess("View Copied",
                    $"\"{fv.Name}\" was copied from {fv.SiteName} into \"{destFolder.Name}\" as \"{newName}\".{cameraNote}",
                    Window.GetWindow(this));
            }
            catch (Exception ex)
            {
                FlexViewDefinition.Log.Info($"[FlexViewFed] CopyFederatedViewToLocal failed: {ex}");
                MessageDialog.ShowError("Copy Failed", $"Failed to copy the view:\n{ex.Message}", Window.GetWindow(this));
            }
        }

        // Batch version: one destination folder for every selected view (picked once, not per view),
        // each view keeps its own name - auto-deduped via UniqueName rather than prompted per view -
        // and one summary dialog covers the whole batch instead of one dialog per view. A failure on
        // one view is recorded in the summary and does not stop the rest of the batch.
        private async void CopyMultipleFederatedViewsToLocal(List<FederationWalker.FedView> views)
        {
            if (views == null || views.Count == 0) return;

            var folderPicker = new ViewBrowserWindow(BrowseMode.SelectFolder);
            folderPicker.Owner = Application.Current.MainWindow;
            if (folderPicker.ShowDialog() != true || folderPicker.SelectedItem == null) return;
            var destFolder = folderPicker.SelectedItem;

            DestinationInfo dest;
            try
            {
                dest = await Task.Run(() => ResolveDestination(destFolder));
            }
            catch (Exception ex)
            {
                FlexViewDefinition.Log.Info($"[FlexViewFed] CopyMultipleFederatedViewsToLocal failed to resolve destination: {ex}");
                MessageDialog.ShowError("Copy Failed", $"Failed to copy views:\n{ex.Message}", Window.GetWindow(this));
                return;
            }

            var lines = new List<string>();
            int succeeded = 0;

            foreach (var fv in views)
            {
                var newName = UniqueName(dest.ExistingNames, fv.Name);
                try
                {
                    var prep = await Task.Run(() => PrepareFederatedCopy(fv, dest.ViewGroup, dest.MasterServerId, newName));

                    int restored = 0, attempted = prep.CameraItems.Count;
                    if (attempted > 0)
                        restored = await RestoreCameraSlots(prep.MasterServerId, prep.NewViewPath, prep.CameraItems);

                    lines.Add(attempted == 0
                        ? $"✓ \"{newName}\" (from {fv.SiteName})"
                        : $"✓ \"{newName}\" (from {fv.SiteName}) - {restored}/{attempted} camera(s)");
                    succeeded++;
                }
                catch (Exception ex)
                {
                    FlexViewDefinition.Log.Info($"[FlexViewFed] Batch copy failed for '{fv.Name}' ({fv.SiteName}): {ex}");
                    lines.Add($"✗ \"{fv.Name}\" (from {fv.SiteName}) - {ex.Message}");
                }
            }

            FlexViewDefinition.Log.Info($"[FlexViewFed] Batch copy into '{destFolder.Name}': {succeeded}/{views.Count} view(s) copied.");
            var title = succeeded == views.Count ? "Views Copied" : "Some Views Failed";
            MessageDialog.ShowSuccess(title,
                $"{succeeded} of {views.Count} view(s) copied into \"{destFolder.Name}\":\n\n" + string.Join("\n", lines),
                Window.GetWindow(this));
        }

        // Background-thread phase: raw config-API only, no client-session objects touched.
        private static PreparedCopy PrepareFederatedCopy(FederationWalker.FedView fv, VideoOS.Platform.ConfigurationItems.ViewGroup viewGroup, ServerId masterServerId, string newName)
        {
            var task = viewGroup.ViewFolder.AddView(
                newName,
                fv.Shortcut ?? "",
                fv.LayoutType ?? "",
                fv.LayoutCustomId ?? "",
                fv.LayoutIcon ?? "",
                fv.LayoutViewItemsXml);
            WaitForServerTask(task, "AddView");

            return new PreparedCopy
            {
                NewViewPath = task.Path,
                MasterServerId = masterServerId,
                CameraItems = FederationWalker.ReadCameraItems(fv)
            };
        }

        // Restores camera slots onto the view AddView just created. The destination is always local
        // (this site), so - unlike the source - Configuration.Instance.GetItem resolves it fine once
        // we know its Id: ServerTask.Path (from AddView) is the new view's config-API path, used to
        // read its raw View object and pull out the Id needed to rebuild a client FQID. From there
        // it's the exact same InsertBuiltinViewItem pipeline the same-site copy already uses.
        // Per-slot failures are logged and skipped rather than failing the whole copy - the view and
        // its layout already exist at this point regardless.
        //
        // MUST be called on Smart Client's own UI thread. Confirmed against a real run: calling this
        // from a background thread throws "The calling thread cannot access this object because a
        // different thread owns it" on Save - the ViewAndLayoutItem returned by Configuration.Instance
        // .GetItem is a client-session object with thread affinity, unlike the raw ConfigurationItems.*
        // objects used elsewhere in this file, which have no such restriction.
        //
        // Uses await Task.Delay (not Thread.Sleep) between retry attempts so the UI stays responsive
        // while waiting - confirmed against a real system that the client cache can take 45+ seconds
        // to notice a view created through the raw config API side channel, and each GetItem attempt
        // itself (not just the delay between attempts) can take 2-3 seconds since it's a real network
        // call that must run on this thread. Delay-based yielding can't eliminate that per-attempt
        // cost, but it does mean Smart Client's message pump isn't dead for the whole wait - just
        // briefly busy during each individual attempt.
        private static async Task<int> RestoreCameraSlots(ServerId masterServerId, string newViewPath, List<FederationWalker.FedViewItem> items)
        {
            if (string.IsNullOrEmpty(newViewPath)) return 0;

            VideoOS.Platform.ConfigurationItems.View newConfigView;
            try { newConfigView = new VideoOS.Platform.ConfigurationItems.View(masterServerId, newViewPath); }
            catch (Exception ex)
            {
                FlexViewDefinition.Log.Info($"[FlexViewFed] Could not read the new view at '{newViewPath}': {ex.Message}");
                return 0;
            }

            if (!Guid.TryParse(newConfigView.Id, out var newViewObjectId))
            {
                FlexViewDefinition.Log.Info($"[FlexViewFed] New view Id '{newConfigView.Id}' is not a GUID - cannot restore camera content.");
                return 0;
            }

            // The client session's own item cache for built-in kinds (View included) doesn't see a
            // just-created item immediately - it was written through the raw config API, a side
            // channel the client cache isn't notified about synchronously. Configuration.RefreshConfiguration
            // explicitly does not apply here (SDK docs: "Only works for plug-in defined configurations
            // ... built-in item types ... cannot be refreshed"), so the only option is to wait the
            // environment's own propagation out with a bounded retry.
            var newViewFqid = new FQID(masterServerId, Guid.Empty, newViewObjectId, FolderType.No, Kind.View);
            ViewAndLayoutItem newClientItem = null;
            var deadline = DateTime.UtcNow.AddSeconds(90);
            int attempt = 0;
            while (newClientItem == null && DateTime.UtcNow < deadline)
            {
                attempt++;
                if (attempt > 1) await Task.Delay(500);
                newClientItem = Configuration.Instance.GetItem(newViewFqid) as ViewAndLayoutItem;
            }
            if (newClientItem == null)
            {
                FlexViewDefinition.Log.Info($"[FlexViewFed] Could not resolve the newly created view via the client session after {attempt} attempt(s) - camera content not restored.");
                return 0;
            }

            int restored = 0;
            foreach (var item in items)
            {
                if (item.CameraId == null) continue;
                try
                {
                    newClientItem.InsertBuiltinViewItem(item.Position, ViewAndLayoutItem.CameraBuiltinId,
                        new Dictionary<string, string> { ["CameraId"] = item.CameraId.Value.ToString() });
                    restored++;
                }
                catch (Exception ex)
                {
                    FlexViewDefinition.Log.Info($"[FlexViewFed] slot[{item.Position}]: restore failed for camera {item.CameraId}: {ex.Message}");
                }
            }

            // Confirmed against a real run: InsertBuiltinViewItem's changes stuck (the resulting view
            // genuinely had working cameras) even on a run where this Save() call itself failed (it was
            // called from the wrong thread, before this method was fixed to always run on the UI
            // thread). So a Save() failure here is logged but does not roll the already-successful
            // restored count back to 0 - that undercounted a copy that had actually worked.
            if (restored > 0)
            {
                try { newClientItem.Save(); }
                catch (Exception ex)
                {
                    FlexViewDefinition.Log.Info($"[FlexViewFed] Save after camera restore failed (restored slots may already be persisted regardless): {ex.Message}");
                }
            }
            return restored;
        }

        // AddView/AddViewGroup run as a server-side task that may not be finished when the call
        // returns (ServerTask.Progress < 100) - the SDK docs say to poll UpdateState() until it
        // completes. Without this, a server-side failure (e.g. a duplicate name) would otherwise be
        // reported back as a success.
        private static void WaitForServerTask(VideoOS.Platform.ConfigurationItems.ServerTask task, string what)
        {
            if (task == null) throw new InvalidOperationException($"{what}: server returned no task.");

            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (task.Progress < 100 && task.State != VideoOS.Platform.ConfigurationItems.StateEnum.Error && DateTime.UtcNow < deadline)
            {
                System.Threading.Thread.Sleep(200);
                task.UpdateState();
            }

            if (task.State == VideoOS.Platform.ConfigurationItems.StateEnum.Error)
                throw new InvalidOperationException($"{what} failed: {task.ErrorText ?? task.ErrorCode ?? "unknown error"}");
        }

        private void TryReadSlotLabels(ViewAndLayoutItem view)
        {
            try
            {
                var configItem = view as ConfigItem;
                if (configItem == null) return;

                var children = configItem.GetChildren();
                if (children == null || children.Count == 0) return;

                // Plugin lookup is built lazily on the first non-camera slot
                // we encounter - most edited views are camera grids, so this
                // avoids enumerating AllPluginDefinitions for nothing.
                Dictionary<Guid, ViewItemPlugin> pluginsById = null;

                for (int i = 0; i < children.Count && i < _panes.Count; i++)
                {
                    var child = children[i];
                    if (child?.Properties == null) continue;

                    // Camera slot: resolve via CameraId -> camera item name.
                    if (child.Properties.TryGetValue("CameraId", out var camIdStr)
                        && Guid.TryParse(camIdStr, out var camId)
                        && camId != Guid.Empty)
                    {
                        _panes[i].CameraId = camId;
                        _panes[i].CameraName = ResolveCameraName(camId);
                        continue;
                    }

                    // Non-camera slot: resolve via ViewItemId -> plugin name.
                    // Built-in view items (Hotspot, Carousel, etc.) are not in
                    // AllPluginDefinitions, so the lookup misses and the pane
                    // stays unlabeled - matches the prior camera-only behavior.
                    if (child.Properties.TryGetValue("ViewItemId", out var vidStr)
                        && Guid.TryParse(vidStr, out var viewItemId)
                        && viewItemId != Guid.Empty)
                    {
                        if (pluginsById == null) pluginsById = BuildViewItemPluginLookup();
                        if (pluginsById.TryGetValue(viewItemId, out var plugin)
                            && !string.IsNullOrEmpty(plugin?.Name))
                        {
                            _panes[i].PluginName = plugin.Name;
                        }
                    }
                }
            }
            catch
            {
            }
        }

        private static string ResolveCameraName(Guid camId)
        {
            // Try FQID lookup first (fast path).
            try
            {
                var serverId = EnvironmentManager.Instance.MasterSite.ServerId;
                var fqid = new FQID(serverId, Guid.Empty, camId, FolderType.No, Kind.Camera);
                var camItem = Configuration.Instance.GetItem(fqid);
                if (camItem != null && !string.IsNullOrEmpty(camItem.Name))
                    return camItem.Name;
            }
            catch { }

            // Fallback: scan every camera in the configuration.
            try
            {
                var allCams = Configuration.Instance.GetItemsByKind(Kind.Camera);
                if (allCams != null)
                {
                    foreach (var cam in allCams)
                    {
                        if (cam.FQID.ObjectId == camId) return cam.Name;
                    }
                }
            }
            catch { }

            // Last resort: the abbreviated GUID so the operator still sees
            // *something* identifying the slot.
            return camId.ToString().Substring(0, 8) + "...";
        }

        #endregion

        #region Button Handlers

        private void OnNewClick(object sender, RoutedEventArgs e)
        {
            _panes.Clear();
            _selectedPane = null;
            _hoveredPane = null;
            _nextPaneId = 1;
            _isEditMode = false;
            _editingView = null;
            _editingParent = null;
            _targetFolder = null;
            _isDirty = false;
            saveAsButton.Visibility = Visibility.Collapsed;
            viewNameLabel.Text = "";
            RedrawCanvas();
            UpdateStatus();
        }

        private void OnOpenClick(object sender, RoutedEventArgs e)
        {
            try
            {
                bool proceed = MessageDialog.Confirm(
                    "Edit Existing View",
                    "Saving will recreate the view. Name and folder stay the same, but the internal ID changes.",
                    okText: "Continue",
                    cancelText: "Cancel",
                    owner: Window.GetWindow(this));

                if (!proceed) return;

                // Browse views across all sites (master + federated children).
                var browser = new ViewBrowserWindow(BrowseMode.SelectView, federated: true);
                browser.Owner = Application.Current.MainWindow;
                if (browser.ShowDialog() != true) return;

                // Multiple checked child-site views: one folder picker, one batch copy, one summary.
                if (browser.SelectedFedViews != null && browser.SelectedFedViews.Count > 0)
                {
                    CopyMultipleFederatedViewsToLocal(browser.SelectedFedViews);
                    return;
                }

                // Child-site view: copy its captured layout XML straight into a folder on this site,
                // rather than trying to resolve it to a live ViewAndLayoutItem - Views are not part of
                // MFA's federated client-session model, so that resolve always returns null.
                if (browser.SelectedFedView != null)
                {
                    CopyFederatedViewToLocal(browser.SelectedFedView);
                    return;
                }

                if (browser.SelectedItem == null) return;

                var view = browser.SelectedItem as ViewAndLayoutItem;
                if (view == null)
                {
                    FlexViewDefinition.Log.Info($"[FlexViewFed] Selected item '{browser.SelectedItem.Name}' is not a ViewAndLayoutItem (kind={browser.SelectedItem.FQID?.Kind}).");
                    MessageDialog.ShowError("Open Failed", "Selected item is not a view layout.", Window.GetWindow(this));
                    return;
                }

                LoadViewForEditing(view, browser.SelectedParent);
            }
            catch (Exception ex)
            {
                MessageDialog.ShowError("Open Failed", $"Failed to open view:\n{ex.Message}", Window.GetWindow(this));
            }
        }

        private void OnClearClick(object sender, RoutedEventArgs e)
        {
            if (_panes.Count == 0) return;

            if (MessageDialog.Confirm("Clear All Panes",
                    "Remove every pane from the current layout?",
                    okText: "Clear", cancelText: "Cancel",
                    owner: Window.GetWindow(this)))
            {
                _panes.Clear();
                _selectedPane = null;
                _hoveredPane = null;
                _nextPaneId = 1;
                _isDirty = true;
                RedrawCanvas();
                UpdateStatus();
            }
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            if (_panes.Count == 0)
            {
                MessageDialog.ShowError("Cannot Save", "Please create at least one pane.", Window.GetWindow(this));
                return;
            }

            if (_isEditMode)
            {
                SaveEditedView();
            }
            else
            {
                var dlg = new SaveViewWindow(null, _targetFolder);
                dlg.Owner = Application.Current.MainWindow;
                if (dlg.ShowDialog() == true)
                {
                    _targetFolder = dlg.SelectedFolder;
                    SaveNewView(dlg.ViewName, dlg.SelectedFolder);
                }
            }
        }

        // Save As: always prompts the folder/name picker and creates a fresh view, even
        // when editing an existing one. Camera assignments from the source view are
        // carried over via the same slot-content snapshot/restore used by SaveEditedView.
        private void OnSaveAsClick(object sender, RoutedEventArgs e)
        {
            if (_panes.Count == 0)
            {
                MessageDialog.ShowError("Cannot Save", "Please create at least one pane.", Window.GetWindow(this));
                return;
            }

            string defaultName = null;
            List<SlotSnapshot> slotContent = null;
            if (_isEditMode && _editingView != null)
            {
                defaultName = _editingView.Name + " (copy)";
                slotContent = SnapshotSlotContent(_editingView);
            }

            var dlg = new SaveViewWindow(defaultName, _targetFolder);
            dlg.Owner = Application.Current.MainWindow;
            if (dlg.ShowDialog() != true) return;

            var newView = SaveNewView(dlg.ViewName, dlg.SelectedFolder, slotContent);
            if (newView == null) return;

            // Switch to editing the freshly-created copy so subsequent Saves target it
            // (and don't try to recreate the source view).
            _editingView = newView;
            _editingParent = dlg.SelectedFolder;
            _targetFolder = dlg.SelectedFolder;
            _isEditMode = true;
            _isDirty = false;
            viewNameLabel.Text = newView.Name;
            UpdateStatus();
        }

        #endregion

        #region Layouts

        // Stores the current arrangement as a layout: a reusable template in Smart Client's Add View
        // picker, alongside the built-in 1x1 / 2x2 grids.
        //
        // A layout is geometry and nothing else - the SDK's Layout type carries only Id, Name,
        // Description and DefinitionXml, with no way to attach cameras. Views created from this
        // layout therefore start empty. SaveLayoutWindow states that on the dialog, and the button
        // is worded "Save as Layout" rather than "Save View as Layout" for the same reason.
        //
        // The rectangles come from ConvertPanesToSdkLayout, the same method the view save path uses,
        // and land in the definition XML unscaled: both sides are the 0..1000 coordinate space, so
        // what the operator drew is stored exactly.
        private async void OnSaveAsLayoutClick(object sender, RoutedEventArgs e)
        {
            if (_panes.Count == 0)
            {
                MessageDialog.ShowError("Cannot Save Layout",
                    "Create at least one pane before saving a layout.", Window.GetWindow(this));
                return;
            }

            // Panes cannot overlap by construction - every create, move and resize is rejected on
            // overlap. Checked anyway because an overlapping layout would be accepted by the server
            // and only misbehave later, in the picker, far from here.
            var overlapping = _panes.Where(HasOverlap).ToList();
            if (overlapping.Count > 0)
            {
                FlexViewDefinition.Log.Error(
                    $"[FlexViewLayout] Save as Layout refused: {overlapping.Count} overlapping pane(s) - "
                    + string.Join(", ", overlapping.Select(p => $"#{p.Id}({p.Col},{p.Row},{p.ColSpan}x{p.RowSpan})")));
                MessageDialog.ShowError("Cannot Save Layout",
                    "Some panes overlap. Separate them before saving as a layout.", Window.GetWindow(this));
                return;
            }

            var rects = ConvertPanesToSdkLayout();
            FlexViewDefinition.Log.Info(
                $"[FlexViewLayout] Save as Layout: {rects.Length} pane(s) - "
                + string.Join(" ", rects.Select(r => $"({r.X},{r.Y},{r.Width}x{r.Height})")));

            List<LayoutRepository.LayoutGroupInfo> groups;
            SetLayoutButtonsEnabled(false);
            try
            {
                groups = await Task.Run(() => LayoutRepository.LoadGroups());
            }
            catch (Exception ex)
            {
                FlexViewDefinition.Log.Error($"[FlexViewLayout] Save as Layout: loading groups failed - {ex.GetType().Name}: {ex.Message}", ex);
                MessageDialog.ShowError("Cannot Save Layout",
                    $"The layout groups could not be read from the management server:\n\n{ex.Message}\n\n"
                    + "Saving a layout needs configuration rights that a standard operator account usually does not have. "
                    + "See MIPLog for details.",
                    Window.GetWindow(this));
                return;
            }
            finally
            {
                SetLayoutButtonsEnabled(true);
            }

            var defaultName = _isEditMode && _editingView != null && !string.IsNullOrWhiteSpace(_editingView.Name)
                ? _editingView.Name
                : $"FlexView {_panes.Count} pane{(_panes.Count != 1 ? "s" : "")}";

            var dlg = new SaveLayoutWindow(defaultName, groups, rects) { Owner = Application.Current.MainWindow };
            if (dlg.ShowDialog() != true)
            {
                FlexViewDefinition.Log.Info("[FlexViewLayout] Save as Layout cancelled by the operator.");
                return;
            }

            var group = dlg.SelectedGroup;
            var name = dlg.LayoutName;

            // Rendered here, on the UI thread: RenderTargetBitmap needs a Dispatcher, so it cannot
            // move into the Task.Run below.
            var icon = LayoutIconRenderer.RenderBase64(rects);

            string definitionXml;
            try
            {
                definitionXml = LayoutRepository.BuildDefinitionXml(rects, icon);
            }
            catch (Exception ex)
            {
                FlexViewDefinition.Log.Error($"[FlexViewLayout] Save as Layout: building definition xml failed - {ex.GetType().Name}: {ex.Message}", ex);
                MessageDialog.ShowError("Cannot Save Layout",
                    $"The layout definition could not be built:\n\n{ex.Message}", Window.GetWindow(this));
                return;
            }

            SetLayoutButtonsEnabled(false);
            statusText.Text = $"Saving layout \"{name}\"...";
            try
            {
                // AddLayout's description is required by the SDK signature but nothing in the
                // picker surfaces it, so it is not worth a field on the dialog.
                await Task.Run(() => LayoutRepository.AddLayout(group, name, "", definitionXml));

                // Without this the layout is on the server but absent from Add View until the
                // operator reloads the client by hand, which reads as the save having failed.
                LayoutRepository.RequestClientConfigurationReload();

                FlashStatus($"Saved layout \"{name}\"");
                MessageDialog.ShowSuccess("Layout Saved",
                    $"\"{name}\" was added to the \"{group.Name}\" group.\n\n"
                    + "It is now available in Smart Client setup mode under Add View. Views created from it "
                    + "start empty - the arrangement is stored, the cameras are not.",
                    Window.GetWindow(this));
            }
            catch (Exception ex)
            {
                FlexViewDefinition.Log.Error($"[FlexViewLayout] Save as Layout: AddLayout failed - {ex.GetType().Name}: {ex.Message}", ex);
                UpdateStatus();
                MessageDialog.ShowError("Save Layout Failed",
                    $"\"{name}\" could not be saved:\n\n{ex.Message}\n\nSee MIPLog for the full detail.",
                    Window.GetWindow(this));
            }
            finally
            {
                SetLayoutButtonsEnabled(true);
            }
        }

        private void OnManageLayoutsClick(object sender, RoutedEventArgs e)
        {
            try
            {
                FlexViewDefinition.Log.Info("[FlexViewLayout] Manage Layouts opened.");
                var dlg = new ManageLayoutsWindow { Owner = Application.Current.MainWindow };
                dlg.ShowDialog();
                FlexViewDefinition.Log.Info("[FlexViewLayout] Manage Layouts closed.");
            }
            catch (Exception ex)
            {
                FlexViewDefinition.Log.Error($"[FlexViewLayout] Manage Layouts failed to open - {ex.GetType().Name}: {ex.Message}", ex);
                MessageDialog.ShowError("Manage Layouts Failed",
                    $"The layout manager could not be opened:\n\n{ex.Message}\n\nSee MIPLog for details.",
                    Window.GetWindow(this));
            }
        }

        private void SetLayoutButtonsEnabled(bool enabled)
        {
            saveLayoutButton.IsEnabled = enabled;
            manageLayoutsButton.IsEnabled = enabled;
        }

        #endregion
    }
}

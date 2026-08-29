using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace CodeLensOopProviderVsix.Blame
{
    /// <summary>
    /// Left-side editor margin that renders per-line P4 blame
    /// (changelist / user / date), git-blame style. Created for every text view
    /// (collapsed); made visible by the "Toggle P4 Blame" command.
    /// </summary>
    internal sealed class BlameMargin : FrameworkElement, IWpfTextViewMargin
    {
        public const string MarginName = "P4BlameMargin";

        private static readonly Typeface TextTypeface = new Typeface("Consolas");
        private static readonly Brush StaleBrush = MakeBrush(0x77, 0x77, 0x77);
        private static readonly Brush UnknownBrush = MakeBrush(0x66, 0x66, 0x66);
        private static readonly Brush HintBrush = MakeBrush(0x80, 0x80, 0x80);

        private readonly IWpfTextView _view;
        private bool _disposed;
        private bool _enabled;
        private bool _loading;
        private bool _stale;
        private string _error;
        private IList<BlameLineInfo> _lines;

        private readonly Dictionary<int, Brush> _brushCache = new Dictionary<int, Brush>();
        private DateTime _newest = DateTime.MinValue;
        private DateTime _oldest = DateTime.MaxValue;

        public BlameMargin(IWpfTextView view)
        {
            _view = view;
            Width = 0;
            Visibility = Visibility.Collapsed;

            _view.LayoutChanged += OnLayoutChanged;
            _view.Closed += OnViewClosed;
            _view.TextBuffer.Changed += OnBufferChanged;
        }

        internal bool IsBlameVisible
        {
            get { return _enabled; }
        }

        #region Toggle / data loading

        internal void Toggle()
        {
            if (_enabled)
            {
                DisableBlame();
            }
            else
            {
                EnableBlame();
            }
        }

        private void EnableBlame()
        {
            _enabled = true;
            _error = null;
            _stale = false;
            Visibility = Visibility.Visible;
            Width = 60; // placeholder until data arrives and real width is measured
            _ = LoadAsync();
        }

        private void DisableBlame()
        {
            _enabled = false;
            _loading = false;
            _stale = false;
            _error = null;
            _lines = null;
            Visibility = Visibility.Collapsed;
            Width = 0;
        }

        private async System.Threading.Tasks.Task LoadAsync()
        {
            _loading = true;
            InvalidateVisual();

            string filePath = GetFilePath();
            IList<BlameLineInfo> data = null;
            string error = null;

            try
            {
                if (Environment.GetEnvironmentVariable("P4VS_BLAME_FAKE") != null)
                {
                    // Debug/geometry mode: synthetic per-line data from the LIVE buffer
                    // (no P4 needed). CL number encodes the buffer line number, so wrong
                    // anchoring is visible at a glance.
                    data = MakeFakeData();
                }
                else
                {
                    data = await System.Threading.Tasks.Task.Run(
                        () => P4BlameService.GetBlame(filePath));
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            // Continuation runs on the UI thread (LoadAsync was started there).
            if (_disposed || !_enabled)
            {
                return;
            }

            _loading = false;

            if (error != null)
            {
                _error = error;
                _lines = null;
            }
            else
            {
                _error = null;
                _lines = data;
                _stale = false;
                ComputeStatsAndWidth();
            }

            InvalidateVisual();
        }

        private IList<BlameLineInfo> MakeFakeData()
        {
            ITextSnapshot snapshot = _view.TextSnapshot;
            List<BlameLineInfo> list = new List<BlameLineInfo>(snapshot.LineCount);
            DateTime baseDate = new DateTime(2026, 1, 1);
            foreach (ITextSnapshotLine line in snapshot.Lines)
            {
                int n = line.LineNumber;
                list.Add(new BlameLineInfo
                {
                    ChangeNumber = 10000 + n,
                    User = "L" + n.ToString(CultureInfo.InvariantCulture),
                    Date = baseDate.AddDays(n % 180),
                    Description = "fake line " + n,
                });
            }
            return list;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            // Take the full height the margin host offers; a 0-height DesiredSize would
            // clip away rows near the bottom of the viewport.
            double h = double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height;
            return new Size(Width, h);
        }

        private string GetFilePath()
        {
            ITextDocument doc;
            if (_view.TextBuffer.Properties.TryGetProperty(typeof(ITextDocument), out doc)
                && doc != null
                && !string.IsNullOrEmpty(doc.FilePath))
            {
                return doc.FilePath;
            }

            return null;
        }

        #endregion

        #region Layout bookkeeping

        private void ComputeStatsAndWidth()
        {
            _brushCache.Clear();
            _newest = DateTime.MinValue;
            _oldest = DateTime.MaxValue;

            if (_lines != null)
            {
                foreach (BlameLineInfo info in _lines)
                {
                    if (!info.Date.HasValue)
                    {
                        continue;
                    }

                    if (info.Date.Value > _newest) _newest = info.Date.Value;
                    if (info.Date.Value < _oldest) _oldest = info.Date.Value;
                }
            }

            double pixelsPerDip = GetPixelsPerDip();
            double fontSize = GetFontSize();
            double max = 40;
            if (_lines != null)
            {
                foreach (BlameLineInfo info in _lines)
                {
                    string text = FormatLine(info);
                    if (text.Length == 0)
                    {
                        continue;
                    }

                    FormattedText ft = new FormattedText(text, CultureInfo.CurrentUICulture,
                        FlowDirection.LeftToRight, TextTypeface, fontSize, UnknownBrush, pixelsPerDip);
                    max = Math.Max(max, ft.Width);
                }
            }

            Width = Math.Ceiling(max) + 10;
        }

        private double GetFontSize()
        {
            return Math.Max(9.0, 11.0 * _view.ZoomLevel / 100.0);
        }

        private double GetPixelsPerDip()
        {
            try
            {
                return VisualTreeHelper.GetDpi(this).PixelsPerDip;
            }
            catch
            {
                return 1.0;
            }
        }

        #endregion

        #region Rendering

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            if (!_enabled)
            {
                return;
            }

            if (_loading)
            {
                DrawHint(dc, "Loading P4 blame...");
                return;
            }

            if (_error != null)
            {
                DrawHint(dc, _error);
                return;
            }

            if (_lines == null || _view.TextViewLines == null)
            {
                return;
            }

            double pixelsPerDip = GetPixelsPerDip();
            double fontSize = GetFontSize();
            // TextViewLines geometry is in LOGICAL (unzoomed) units, but the editor renders
            // text scaled by ZoomLevel while margins are NOT scaled. Convert to the margin's
            // coordinate space or rows bunch up / drift off their lines.
            double zoom = _view.ZoomLevel / 100.0;
            ITextSnapshot snapshot = _view.TextSnapshot;

            if (DebugEnabled)
            {
                DebugDumpHeader();
            }

            foreach (ITextViewLine line in _view.TextViewLines)
            {
                if (line.VisibilityState == VisibilityState.Hidden)
                {
                    continue;
                }

                // Word-wrap continuation segments share the same buffer line; drawing on
                // every segment makes one blame entry appear on each wrapped row (misaligned
                // "extra rows"). Only draw on the first segment of each buffer line.
                if (!line.IsFirstTextViewLineForSnapshotLine)
                {
                    if (DebugEnabled) DebugDump(line, -1);
                    continue;
                }

                int lineNumber = snapshot.GetLineNumberFromPosition(line.Start.Position);
                if (lineNumber < 0 || lineNumber >= _lines.Count)
                {
                    continue;
                }

                BlameLineInfo info = _lines[lineNumber];
                string text = FormatLine(info);
                if (text.Length == 0)
                {
                    continue;
                }

                FormattedText ft = new FormattedText(text, CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight, TextTypeface, fontSize, BrushFor(info), pixelsPerDip);
                dc.DrawText(ft, new Point(4, (line.TextTop - _view.ViewportTop) * zoom));

                if (DebugEnabled)
                {
                    DebugDump(line, lineNumber);
                }
            }
        }

        private static bool DebugEnabled
        {
            get { return Environment.GetEnvironmentVariable("P4VS_BLAME_DEBUG") != null; }
        }

        private void DebugDumpHeader()
        {
            try
            {
                string msg = string.Format(CultureInfo.InvariantCulture,
                    "=== view={0} lines={1} viewport w={2:F0} h={3:F0} zoom={4:F0} t={5:HH:mm:ss.fff}",
                    _view.GetHashCode(), _view.TextViewLines.Count, _view.ViewportWidth,
                    _view.ViewportHeight, _view.ZoomLevel, DateTime.Now);
                System.IO.File.AppendAllText(@"C:\Windows\Temp\blame_geo.txt", msg + Environment.NewLine);
            }
            catch { }
        }

        private void DebugDump(ITextViewLine line, int lineNumber)
        {
            try
            {
                string msg = string.Format(CultureInfo.InvariantCulture,
                    "  v{8} n={0} first={1} top={2:F1} texttop={3:F1} height={4:F1} texth={5:F1} vtop={6:F1} state={7}",
                    lineNumber, line.IsFirstTextViewLineForSnapshotLine, line.Top, line.TextTop,
                    line.Height, line.TextHeight, _view.ViewportTop, line.VisibilityState,
                    _view.GetHashCode() % 10000);
                System.IO.File.AppendAllText(@"C:\Windows\Temp\blame_geo.txt", msg + Environment.NewLine);
            }
            catch { }
        }

        private void DrawHint(DrawingContext dc, string message)
        {
            if (message == null)
            {
                return;
            }

            // Keep it short: a narrow margin cannot wrap text.
            if (message.Length > 60)
            {
                message = message.Substring(0, 57) + "...";
            }

            FormattedText ft = new FormattedText(message, CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, TextTypeface, GetFontSize(), HintBrush, GetPixelsPerDip());
            dc.DrawText(ft, new Point(4, 4));
        }

        private static string FormatLine(BlameLineInfo info)
        {
            if (info.ChangeNumber <= 0)
            {
                return "-";
            }

            string date = info.Date.HasValue
                ? info.Date.Value.ToString("MM-dd", CultureInfo.InvariantCulture)
                : string.Empty;

            return string.Format(CultureInfo.InvariantCulture, "{0} {1} {2}",
                info.ChangeNumber, info.User, date).TrimEnd();
        }

        private Brush BrushFor(BlameLineInfo info)
        {
            if (_stale)
            {
                return StaleBrush;
            }

            if (info.ChangeNumber <= 0)
            {
                return UnknownBrush;
            }

            Brush cached;
            if (_brushCache.TryGetValue(info.ChangeNumber, out cached))
            {
                return cached;
            }

            // Recency gradient: newest = VS blue (#569CD6), oldest = gray (#7A7A7A).
            double t = AgeFraction(info);
            byte r = (byte)(0x56 + (0x7A - 0x56) * t);
            byte g = (byte)(0x9C + (0x7A - 0x9C) * t);
            byte b = (byte)(0xD6 + (0x7A - 0xD6) * t);
            Brush brush = MakeBrush(r, g, b);
            _brushCache[info.ChangeNumber] = brush;
            return brush;
        }

        private double AgeFraction(BlameLineInfo info)
        {
            if (!info.Date.HasValue || _newest <= _oldest)
            {
                return 0.5;
            }

            double span = (_newest - _oldest).TotalDays;
            if (span <= 0)
            {
                return 0;
            }

            double age = (_newest - info.Date.Value).TotalDays;
            return Math.Min(1.0, Math.Max(0.0, age / span));
        }

        private static Brush MakeBrush(byte r, byte g, byte b)
        {
            Brush brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        #endregion

        #region Tooltip

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (!_enabled || _lines == null || _view.TextViewLines == null)
            {
                ToolTip = null;
                return;
            }

            Point p = e.GetPosition(this);
            double zoom = _view.ZoomLevel / 100.0;
            double y = p.Y / zoom + _view.ViewportTop;
            BlameLineInfo hit = null;

            foreach (ITextViewLine line in _view.TextViewLines)
            {
                if (line.VisibilityState == VisibilityState.Hidden)
                {
                    continue;
                }

                if (y >= line.Top && y <= line.Bottom)
                {
                    int n = _view.TextSnapshot.GetLineNumberFromPosition(line.Start.Position);
                    if (n >= 0 && n < _lines.Count)
                    {
                        hit = _lines[n];
                    }

                    break;
                }
            }

            if (hit == null || hit.ChangeNumber <= 0)
            {
                ToolTip = null;
                return;
            }

            string description = (hit.Description ?? string.Empty).Trim();
            if (description.Length > 400)
            {
                description = description.Substring(0, 397) + "...";
            }

            ToolTip = string.Format(CultureInfo.InvariantCulture,
                "CL {0} - {1} - {2:yyyy/MM/dd HH:mm}\n{3}{4}",
                hit.ChangeNumber, hit.User, hit.Date,
                description,
                _stale ? "\n(stale - file edited; toggle off/on to refresh)" : string.Empty);
        }

        #endregion

        #region Event handlers / lifetime

        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            if (_enabled)
            {
                InvalidateVisual();
            }
        }

        private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            // Line numbers may have shifted; keep showing but dimmed as stale.
            if (_enabled && _lines != null && e.Changes != null && e.Changes.Count > 0)
            {
                _stale = true;
                InvalidateVisual();
            }
        }

        private void OnViewClosed(object sender, EventArgs e)
        {
            Dispose();
        }

        #endregion

        #region IWpfTextViewMargin

        public FrameworkElement VisualElement
        {
            get { return this; }
        }

        public double MarginSize
        {
            get { return _enabled ? Width : 0; }
        }

        public bool Enabled
        {
            get { return _enabled; }
        }

        public ITextViewMargin GetTextViewMargin(string marginName)
        {
            return marginName == MarginName ? this : null;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _enabled = false;
            _view.LayoutChanged -= OnLayoutChanged;
            _view.Closed -= OnViewClosed;
            _view.TextBuffer.Changed -= OnBufferChanged;
        }

        #endregion
    }
}

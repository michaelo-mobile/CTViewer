#nullable disable
using CTViewer.Tools;
using CTViewer.Views;
//using Dicom;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using Microsoft.Win32;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;  // for InkCanvas bits
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Runtime.Intrinsics.X86;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Linq;

namespace CTViewer.Views
{
    public partial class MainWindow : Window
    {
        // ===== Image state (LEFT) =====
        private ushort[] _raw16;
        private int _width, _height;
        private DicomDataset _ds;
        private int _wl, _ww;

        // ===== Image state (RIGHT) =====
        private ushort[] _rightRaw16;
        private int _rightWidth, _rightHeight;
        private int _rightwl, _rightww;
        private DicomFile _rightDicomFile;
        private string _rightDicomPath;

        // ===== Common / defaults =====
        private int _defaultwl, _defaultww;
        private bool _suppressSliderEvents;

        // ===== Undo per pane (replace your single _undo) =====
        private readonly Stack<Stroke> _leftUndo = new();
        private readonly Stack<Stroke> _rightUndo = new();

        // ===== Current files =====
        private DicomFile _currentDicomFile;
        private string _currentDicomPath;

        // ===== Active-pane + mode =====
        private enum Pane { Left, Right }
        private PaneState _left, _right;
        private Pane _activePane = Pane.Left;
        private bool _twoPlayerMode = false;
        private bool _drawingEnabled = false;   // your Draw button toggles this

        // ===== Current stroke style (shared UI, applied to active pane) =====
        // Drawing defaults + UI guard
        private const double DEFAULT_STROKE_WIDTH = 2.0;
        private static readonly System.Windows.Media.Color DEFAULT_STROKE_COLOR = System.Windows.Media.Colors.Black;
        private bool _suppressDrawingUi = false;   // prevents feedback loops when we set the toolbar programmatically

        [Conditional("DEBUG")]
        static void D(string msg) => System.Diagnostics.Debug.WriteLine(msg);

        // Convenience accessors
        private InkCanvas ActiveInk() => _activePane == Pane.Left ? InkCanvas : RightInkCanvas;
        private int ActiveWL { get => _activePane == Pane.Left ? _wl : _rightwl; set { if (_activePane == Pane.Left) _wl = value; else _rightwl = value; } }
        private int ActiveWW { get => _activePane == Pane.Left ? _ww : _rightww; set { if (_activePane == Pane.Left) _ww = value; else _rightww = value; } }
        private bool _patientInfoVisible = true;
        public MainWindow()
        {
            InitializeComponent();

            _left = new PaneState
            {
                Image = MainImage,
                Ink = InkCanvas,
                Surface = LeftSurface,
                WlWwLabel = WlWwText,
                XYHUD = XYHU
            };

            _right = new PaneState
            {
                Image = RightImage,
                Ink = RightInkCanvas,
                Surface = RightSurface,
                WlWwLabel = RightWlWwText,
                XYHUD = RightXYHU
            };
            // ---- FileButtons wiring (your UserControl) ----
            Loaded += (_, __) => _suppressSliderEvents = false;
            FileButtons.FileOpened += FileButtons_FileOpened;
            FileButtons.TwoPlayerModeChanged += OnTwoPlayerModeChanged;
            // If FileButtons raises SaveAsClicked (routed), you can also handle it here if needed:
            // AddHandler(FileButtons.SaveAsClickedEvent, new RoutedEventHandler(SaveAs_ClickedFromToolbar));

            // ---- Drawing toolbar wiring (your DrawingCanvas control) ----
            DrawingCanvas.StrokeSizeChanged += OnStrokeSizeChanged;   // (object?, double) OR (object?, RoutedPropertyChangedEventArgs<double>)
            DrawingCanvas.StrokeColorChanged += OnStrokeColorChanged;  // (object?, Color)   OR (object?, SelectionChangedEventArgs / custom)
            DrawingCanvas.UndoClicked += OnUndoClicked;         // (object?, EventArgs)
            DrawingCanvas.ClearClicked += OnClearClicked;        // (object?, EventArgs)
            DrawingCanvas.HideClicked += OnHideClicked;         // (object?, string "Annotations"/"PatientInfo")
            DrawingCanvas.DrawModeToggled += OnDrawModeToggled;     // (object?, bool enabled)

            // ---- Track strokes for Undo per pane ----
            InkCanvas.StrokeCollected += (s, e) => { _leftUndo.Push(e.Stroke); D($"[INK] Left stroke count={InkCanvas.Strokes.Count}"); };
            RightInkCanvas.StrokeCollected += (s, e) => { _rightUndo.Push(e.Stroke); D($"[INK] Right stroke count={RightInkCanvas.Strokes.Count}"); };

            // ---- XY/HU hover readouts for BOTH images ----
            LeftSurface.MouseMove += (s, e) => PaneMouseMove(Pane.Left, e);
            MainImage.MouseLeave += (_, __) => XYHU.Text = "X: —   Y: —    HU: —";
            RightSurface.MouseMove += (s, e) => PaneMouseMove(Pane.Right, e);
            RightImage.MouseLeave += (_, __) => RightXYHU.Text = "X: —   Y: —    HU: —";

            // ---- Make “surfaces” clickable no matter what overlays do ----
            LeftSurface.Background = System.Windows.Media.Brushes.Transparent;
            RightSurface.Background = System.Windows.Media.Brushes.Transparent;

            // Hook mouse logging (optional but helpful)
            HookMouse("LEFT-VIEWBOX", LeftViewbox);
            HookMouse("LEFT-SURFACE", LeftSurface);
            HookMouse("LEFT-INK", InkCanvas);
            HookMouse("RIGHT-VIEWBOX", RightViewbox);
            HookMouse("RIGHT-SURFACE", RightSurface);
            HookMouse("RIGHT-INK", RightInkCanvas);

            // Ensure we see clicks even if children mark handled
            LeftSurface.AddHandler(UIElement.MouseDownEvent, new MouseButtonEventHandler(LeftSurface_MouseDown), true);
            RightSurface.AddHandler(UIElement.MouseDownEvent, new MouseButtonEventHandler(RightSurface_MouseDown), true);

            // Start with LEFT active; make both canvases use current stroke style
            SetActivePane(Pane.Left);
            //OnStrokeSizeChanged(InkCanvas);
           // OnStrokeSizeChanged(RightInkCanvas);
        }

        private sealed class PaneState
        {
            public ushort[] Raw16;
            public int Width, Height;
            public int WL, WW;
            public DicomFile File;
            public string Path;

            // UI for this pane
            public System.Windows.Controls.Image Image;
            public InkCanvas Ink;
            public Grid Surface;
            public TextBlock WlWwLabel;
            public TextBlock XYHUD;

            public bool DrawEnabled = false;                 // draw toggle
            public bool InkVisible = true;                   // hide/show annotations
            public double StrokeWidth = DEFAULT_STROKE_WIDTH;                 // default Stroke Width
            public System.Windows.Media.Color StrokeColor = DEFAULT_STROKE_COLOR;

            // === Dirty-tracking baseline ===
            public int WL0, WW0;       // WL/WW when last loaded/saved
            public string InkHash0;    // strokes checksum when last loaded/saved
        }
        private void HookMouse(string tag, UIElement el)
        {
            el.PreviewMouseDown += (s, e) => LogMouse("PreviewDown", tag, s, e);
            el.MouseDown += (s, e) => LogMouse("Down", tag, s, e);
            el.PreviewMouseUp += (s, e) => LogMouse("PreviewUp", tag, s, e);
            el.MouseUp += (s, e) => LogMouse("Up", tag, s, e);
        }
        private void LogMouse(string phase, string tag, object sender, MouseButtonEventArgs e)
        {
            var pL = LeftSurface != null ? e.GetPosition(LeftSurface) : new System.Windows.Point(double.NaN, double.NaN);
            var pR = RightSurface != null ? e.GetPosition(RightSurface) : new System.Windows.Point(double.NaN, double.NaN);
            D($"[MOUSE] {phase,-11} tag={tag,-14} btn={e.ChangedButton,-5} clicks={e.ClickCount} handled={e.Handled} " +
              $"posL=({pL.X:0},{pL.Y:0}) posR=({pR.X:0},{pR.Y:0}) sender={sender.GetType().Name}");
        }
        // method for the two player mode toggle
        private void OnTwoPlayerModeChanged(object sender, bool on)
        {
            D($"[2P] toggle -> {on}");

            // Confirm if there are unsaved edits before switching modes
            
            SetUiEnabled(false);                  // 1) freeze UI during the switch

            // Show/hide right column; ViewBox does the shrinking
            RightViewbox.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            ImagesLayout.ColumnDefinitions[1].Width = on
                ? new GridLength(1, GridUnitType.Star)
                : new GridLength(0);
            SetSinglePaneControlsEnabled(true); // always enable single-pane controls
            // 2) Re-render LEFT exactly with its current WL/WW (do NOT recompute)
            RenderPaneBitmap(_left);                         // uses _wl/_ww as-is

            if (on)
            {
                // 3) Load RIGHT exactly like a normal open (restores saved WL/WL + ink if present)
                var next = GetNextDicomPath(_currentDicomPath);
                if (!string.IsNullOrEmpty(next))
                    LoadRightPane(next);          // your working method from earlier
            }
            else
            {
                // collapse to single pane
                RightImage.Source = null;
                RightInkCanvas.Strokes.Clear();
                RightImageFile.Text = "—";
                _activePane = Pane.Left; // so sliders are allowed to move
                InitializeWlWwUI(_left, _left.WL, _left.WW, alsoMoveSharedSliders: true);
                RenderPaneBitmap(_left); // draw using preserved WL/WW
            }

            SetUiEnabled(true);                   // 5) thaw UI
            ApplyInkEditingModeToActive();   // <- re-apply edit mode after layout/loads
            UpdateActiveHighlight();
            
        }


        private void LoadDicomIntoPane(string filepath, Pane pane, bool preferSavedWlWw = true)
        {
            var P = pane == Pane.Left ? _left : _right;

            try
            {
                D($"[OPEN:{pane}] start {filepath}");
                P.Ink.Strokes = new StrokeCollection();

                var dicomFile = DicomFile.Open(filepath);
                var ds = dicomFile.Dataset;
                #if DEBUG
                DumpPrivateGroup0011(ds);
                #endif

                var pixelData = DicomPixelData.Create(ds);
                int width = pixelData.Width;
                int height = pixelData.Height;

                byte[] frame = pixelData.GetFrame(0).Data;
                P.Raw16 = new ushort[frame.Length / 2];
                Buffer.BlockCopy(frame, 0, P.Raw16, 0, frame.Length);

                ComputeAutoWindowLevel(P.Raw16, out var autoWL, out var autoWW);

                // try to restore WL/WW + strokes
                LoadInkAndWwWlFromDicom(ds, P.Ink, out double? savedWW, out double? savedWL);
                bool edited = savedWL.HasValue && savedWW.HasValue;

                P.WL = (preferSavedWlWw && edited) ? (int)savedWL.Value : autoWL;
                P.WW = (preferSavedWlWw && edited) ? (int)savedWW.Value : autoWW;

                P.Width = width;
                P.Height = height;
                P.File = dicomFile;
                P.Path = filepath;

                // fixed design surface; Viewbox scales everything uniformly
                P.Surface.Width = width;
                P.Surface.Height = height;
                P.Ink.Width = width;
                P.Ink.Height = height;

                // render bitmap for this pane
                RenderPaneBitmap(P);

                // patient info (use pane-aware setter if you split left/right)
                SetPatientInfo(ds, pane);
                ResetDrawingForPane(P, syncToolbar: _activePane == pane);
                // If this pane is the active pane, sync the shared sliders/labels
                if (_activePane == pane)
                    InitializeWlWwUI(P, P.WL, P.WW, alsoMoveSharedSliders: true);
                ApplyInkEditingModeToActive();   // <- keep edit mode consistent
                D($"[OPEN:{pane}] done wl/ww={P.WL}/{P.WW} edited={edited}");
            }
            catch (Exception ex)
            {
                D($"[OPEN:{pane}] EXCEPTION: {ex}");
                MessageBox.Show($"Error loading DICOM file: {ex.Message}");
            }
            D($"[OPEN:{pane}] done wl/ww={P.WL}/{P.WW} edited={CheckIfEdited}");
            DumpBoth($"AfterLoad({pane})");
            if (_activePane == pane) UpdateActiveHighlight();   // <—
            P.Path = filepath;
            UpdateFileLabelForPane(pane, filepath);     // <—                               
        }
        private void RenderPaneBitmap(PaneState P)
        {
            if (P.Raw16 == null) return;

            var scaled = ApplyWindowLevelTo16Bit(P.Raw16, P.WL, P.WW);
            var bmp = BitmapSource.Create(
                P.Width, P.Height,
                96, 96, PixelFormats.Gray16, null,
                scaled, P.Width * 2);

            P.Image.Source = bmp;
            if (P.WlWwLabel != null) P.WlWwLabel.Text = $"WL: {P.WL}   WW: {P.WW}";
        }
        /// <summary> Start of Code for opening Dicom Image with clarity and optimized pixels
        /// Event handler triggered when a file is opened from FileButtons.
        /// Loads and displays a 16-bit grayscale DICOM image with auto window/level adjustment.
        /// </summary>
        
      
        
        private void FileButtons_FileOpened(object sender, string filepath)
        {
            _activePane = Pane.Left;
            LoadDicomIntoPane(filepath, Pane.Left, preferSavedWlWw: true);

            _currentDicomFile = _left.File;
            _currentDicomPath = _left.Path;

            _drawingEnabled = false;
            SetDrawMode(false);                 // or just remove the call entirely
            ApplyInkEditingModeToActive();     // keep active pane in the right mode
        }

        private void LoadRightPane(string filepath)
        {
            LoadDicomIntoPane(filepath, Pane.Right, preferSavedWlWw: true);
            _rightDicomFile = _right.File;
            _rightDicomPath = _right.Path;
        }
        private DicomDataset _currentDataset => _currentDicomFile?.Dataset;
        private PaneState Active() => _activePane == Pane.Left ? _left : _right;



        static class InkTags
        {
            public const string CreatorValue = "CTViewer";          // single source of truth

            public static readonly DicomTag CreatorTag = new DicomTag(0x0011, 0x0010); // Private Creator (LO)
            public static readonly DicomTag StrokesTag = new DicomTag(0x0011, 0x1001, CreatorValue); // OB: Ink ISF bytes
            public static readonly DicomTag WindowWidthTag = DicomTag.WindowWidth;   // (0028,1051) do not need explicitly define, use the standard DICOM tag
            public static readonly DicomTag WindowCenterTag = DicomTag.WindowCenter;  // (0028,1050) do not need explicitly define, use the standard DICOM tag
            // Enhancement option for later, ignore for now. public static readonly DicomTag AppStateTag = new DicomTag(0x0011, 0x1002); // UT/LT: optional JSON
        }

        private static bool CheckIfEdited(DicomDataset ds)
        {
            if (ds == null)
                return false;

            // Check if the private creator tag exists and matches our value
            if (ds.TryGetSingleValue(InkTags.CreatorTag, out string creator) &&
                string.Equals(creator, InkTags.CreatorValue, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Estimates optimal window center and width by excluding extreme outliers
        /// based on the 1st and 99th percentile of the pixel histogram.
        /// </summary>
        private static void ComputeAutoWindowLevel(ushort[] pixels, out int windowCenter, out int windowWidth)
        {
            // Defensive: empty input
            if (pixels == null || pixels.Length == 0)
            {
                windowCenter = 0; windowWidth = 1;
                return;
            }

            var sorted = (ushort[])pixels.Clone();
            Array.Sort(sorted);

            int lowerIndex = Math.Clamp((int)(sorted.Length * 0.01), 0, sorted.Length - 1);
            int upperIndex = Math.Clamp((int)(sorted.Length * 0.99), 0, sorted.Length - 1);

            ushort minVal = sorted[Math.Min(lowerIndex, upperIndex)];
            ushort maxVal = sorted[Math.Max(lowerIndex, upperIndex)];

            windowWidth = Math.Max(1, maxVal - minVal);          // avoid divide-by-zero
            windowCenter = (maxVal + minVal) / 2;
            
            
        }

        /// <summary>
        /// Applies linear scaling to 16-bit grayscale pixel data using window center and width.
        /// Maps values to a visible contrast range (0 - 65535).
        /// </summary>
        private static ushort[] ApplyWindowLevelTo16Bit(ushort[] pixelData, int windowCenter, int windowWidth)
        {
            if (pixelData == null || pixelData.Length == 0)
                return Array.Empty<ushort>();

            int length = pixelData.Length;
            var output = new ushort[length];

            // Defensive clamps
            if (windowWidth < 1) windowWidth = 1;

            double min = windowCenter - windowWidth / 2.0;
            double max = windowCenter + windowWidth / 2.0;

            for (int i = 0; i < length; i++)
            {
                double v = pixelData[i];
                if (v <= min) output[i] = 0;
                else if (v >= max) output[i] = 65535;
                else output[i] = (ushort)(((v - min) / windowWidth) * 65535.0);
            }
            return output;
        }

        /// <summary>
        /// Builds the four lines of overlay text for either the top or bottom patient info box.
        /// Uses the DICOM dataset to pull relevant tag values.
        /// </summary>
        /// <param //name="ds">DICOM dataset containing patient and study metadata.</param>
        /// <param //name="top">If true, returns the Patient/ID/Clinic/Physician lines. 
        /// If false, returns the Date/Time, Modality/Study, Image Index, Body Area/Laterality lines.</param>
        /// <returns>Tuple of four strings representing overlay lines (L1–L4).</returns>
        private static (string L1, string L2, string L3, string L4)
            BuildOverlayLines(DicomDataset ds, bool top)
        {
            // Helper to safely get a DICOM tag value as a string, with fallback if empty or missing
            string Get(DicomTag tag, string fb = "—")
                => ds?.GetSingleValueOrDefault<string>(tag, fb)?.Trim() ?? fb;

            // Helper to clean a Person Name (PN) field by replacing separators and trimming
            string CleanPN(string pn) =>
                string.IsNullOrWhiteSpace(pn) ? "—" : pn.Replace('^', ' ').Replace("  ", " ").Trim();

            // Helper to return the first non-empty, non-placeholder value from a set of options
            string First(params string[] vals)
            {
                foreach (var v in vals)
                    if (!string.IsNullOrWhiteSpace(v) && v != "—")
                        return v;
                return "—";
            }

            // If this is the TOP overlay (patient info)
            if (top)
            {
                // Patient name (cleaned from PN format)
                var patient = CleanPN(Get(DicomTag.PatientName));
                // Patient ID
                var id = Get(DicomTag.PatientID);
                // Clinic / Institution
                var clinic = Get(DicomTag.InstitutionName);

                // Physician with fallback tags (referring → performing → requesting)
                var physician = CleanPN(Get(DicomTag.ReferringPhysicianName));
                if (physician == "—") physician = CleanPN(Get(DicomTag.PerformingPhysicianName));
                if (physician == "—") physician = CleanPN(Get(DicomTag.RequestingPhysician));

                // Return four lines for top overlay
                return ($"Patient: {patient}",
                        $"ID: {id}",
                        $"Clinic: {clinic}",
                        $"Physician: {physician}");
            }

            // If this is the BOTTOM overlay (study/image info)

            // Date (prefer StudyDate, fall back to Series/Acquisition/Content dates)
            var date = First(Get(DicomTag.StudyDate, ""), Get(DicomTag.SeriesDate, ""),
                             Get(DicomTag.AcquisitionDate, ""), Get(DicomTag.ContentDate, ""));
            // Time (prefer StudyTime, fall back to Series/Acquisition/Content times)
            var time = First(Get(DicomTag.StudyTime, ""), Get(DicomTag.SeriesTime, ""),
                             Get(DicomTag.AcquisitionTime, ""), Get(DicomTag.ContentTime, ""));
            // Nicely formatted date/time
            var dateTimeNice = FormatDicomDateTime(date, time);

            // Modality (e.g., CT, MR) and Study/Series/Protocol name
            var modality = Get(DicomTag.Modality);
            var studyName = First(Get(DicomTag.StudyDescription, ""),
                                   Get(DicomTag.SeriesDescription, ""),
                                   Get(DicomTag.ProtocolName, ""));

            // Image index (InstanceNumber preferred, fallbacks to other sequence positions)
            var imageIndex = First(Get(DicomTag.InstanceNumber, ""),
                                   Get(DicomTag.TemporalPositionIndex, ""),
                                   Get(DicomTag.AcquisitionNumber, ""));

            // Body area and laterality (side)
            var bodyArea = Get(DicomTag.BodyPartExamined);
            var laterality = First(Get(DicomTag.ImageLaterality, ""),
                                   Get(DicomTag.Laterality, ""));

            // Return four lines for bottom overlay
            return ($"Date/Time: {dateTimeNice}",
                    $"Modality/Study: {modality} / {studyName}",
                    $"Image Index: {imageIndex}",
                    $"Body Area/Laterality: {bodyArea} {laterality}");
        }

        /// <summary>
        /// Converts DICOM date/time strings into a human-readable format.
        /// Handles missing or partial date/time values gracefully.
        /// </summary>
        private static string FormatDicomDateTime(string dicomDate, string dicomTime)
        {
            DateTime? date = null;

            // Parse DICOM date (YYYYMMDD) if available
            if (!string.IsNullOrWhiteSpace(dicomDate) && dicomDate.Length >= 8 &&
                DateTime.TryParseExact(dicomDate[..8], "yyyyMMdd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                date = d;

            TimeSpan? time = null;

            // Parse DICOM time (HHMMSS[.fraction]) if available
            if (!string.IsNullOrWhiteSpace(dicomTime))
            {
                var t = dicomTime.Split('.')[0]; // drop fractional seconds
                var hh = t.Length >= 2 ? t[..2] : "00";
                var mm = t.Length >= 4 ? t.Substring(2, 2) : "00";
                var ss = t.Length >= 6 ? t.Substring(4, 2) : "00";
                if (int.TryParse(hh, out var H) && int.TryParse(mm, out var M) && int.TryParse(ss, out var S))
                    time = new TimeSpan(H, M, S);
            }

            // Format based on what data is present
            if (date.HasValue && time.HasValue)
                return date.Value.Add(time.Value).ToString("MMM d, yyyy h:mm tt", CultureInfo.InvariantCulture);
            if (date.HasValue)
                return date.Value.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
            if (time.HasValue)
                return DateTime.Today.Add(time.Value).ToString("h:mm tt", CultureInfo.InvariantCulture);

            return "—"; // fallback if nothing is available
        }

        /// <summary>
        /// Populates both top and bottom patient info TextBlocks from a DICOM dataset.
        /// </summary>
        private void SetPatientInfo(DicomDataset ds, Pane pane)
        {
            if (ds == null) return;

            // Pick the correct text blocks
            TextBlock topBlock = (pane == Pane.Left) ? PatientInfoTop : RightPatientInfoTop;
            TextBlock bottomBlock = (pane == Pane.Left) ? PatientInfoBottom : RightPatientInfoBottom;

            var (t1, t2, t3, t4) = BuildOverlayLines(ds, top: true);
            topBlock.Text = $"{t1}\n{t2}\n{t3}\n{t4}";

            var (b1, b2, b3, b4) = BuildOverlayLines(ds, top: false);
            bottomBlock.Text = $"{b1}\n{b2}\n{b3}\n{b4}";
        }
        // Hide the patient info overlays, 1 button to turn off both, makes it simpler
        private void HidePatientInfoButton_Click(object sender, RoutedEventArgs e)
        {
            _patientInfoVisible = !_patientInfoVisible;
            SetPatientInfoVisibility(_patientInfoVisible);
            HidePatientInfoButton.Content = _patientInfoVisible ? "🔒  Hide Patient Info" : "🔓  Show Patient Info";
        }

        private void SetPatientInfoVisibility(bool visible)
        {
            var vis = visible ? Visibility.Visible : Visibility.Collapsed;

            // Hide/show both overlay grids as a unit
            PatientInfoOverlay.Visibility = vis;        // left overlay grid
            RightPatientInfoOverlay.Visibility = vis;   // right overlay grid
        }

        // ==========================
        // Event: Updates "X: Y: HU:" display in real-time when mouse moves over the image
        // ==========================
        private void PaneMouseMove(Pane pane, MouseEventArgs e)
        {
            // DO NOT early-return while drawing – we want tracking even when inking.
            var P = (pane == Pane.Left) ? _left : _right;
            var surface = (pane == Pane.Left) ? (IInputElement)LeftSurface : RightSurface;
            var xyhu = (pane == Pane.Left) ? XYHU : RightXYHU;

            var pos = e.GetPosition(surface);
            var hit = GetPixelFromSurface(pane, pos);

            if (P?.Raw16 == null || P.File?.Dataset == null || hit is null)
            {
                xyhu.Text = "X: —   Y: —    HU: —";
                return;
            }

            int idx = hit.Value.py * P.Width + hit.Value.px;

            var ds = P.File.Dataset;
            double m = ds.GetSingleValueOrDefault(FellowOakDicom.DicomTag.RescaleSlope, 1.0);
            double b = ds.GetSingleValueOrDefault(FellowOakDicom.DicomTag.RescaleIntercept, 0.0);
            double hu = P.Raw16[idx] * m + b;

            xyhu.Text = $"X: {hit.Value.px}   Y: {hit.Value.py}    HU: {hu:0}";
        }



        private void SetDrawMode(bool on)
        {
            _drawingEnabled = on;

            var activeInk = (_activePane == Pane.Left) ? InkCanvas : RightInkCanvas;
            var inactiveInk = (_activePane == Pane.Left) ? RightInkCanvas : InkCanvas;

            if (on)
            {
                activeInk.IsHitTestVisible = true;
                activeInk.EditingMode = InkCanvasEditingMode.Ink;

                // ensure the other side cannot draw
                inactiveInk.EditingMode = InkCanvasEditingMode.None;
                inactiveInk.IsHitTestVisible = false;

                Mouse.OverrideCursor = Cursors.Pen;
            }
            else
            {
                // turn off both
                InkCanvas.EditingMode = InkCanvasEditingMode.None;
                InkCanvas.IsHitTestVisible = false;

                RightInkCanvas.EditingMode = InkCanvasEditingMode.None;
                RightInkCanvas.IsHitTestVisible = false;

                Mouse.OverrideCursor = Cursors.Arrow;
            }
        }

        // ==========================
        // Helper: Converts mouse position in the control into pixel coordinates in the image
        // Handles "Stretch=Uniform" scaling so the mapping is correct
        // ==========================
        // Returns pixel coords for the requested pane, or null if out of bounds/not ready
        private (int px, int py)? GetPixelFromSurface(Pane pane, System.Windows.Point posOnSurface)
        {
            var P = (pane == Pane.Left) ? _left : _right;
            if (P == null || P.Width <= 0 || P.Height <= 0) return null;

            int px = (int)Math.Floor(posOnSurface.X);
            int py = (int)Math.Floor(posOnSurface.Y);
            if (px < 0 || py < 0 || px >= P.Width || py >= P.Height) return null;

            return (px, py);
        }

        // Call this from a MouseMove/MouseDown handler if you want the active pane’s pixel
        private (int px, int py)? GetPixelFromMouseActive(MouseEventArgs e)
        {
            var surface = (_activePane == Pane.Left) ? (IInputElement)LeftSurface
                                                     : (IInputElement)RightSurface;
            var p = e.GetPosition(surface);
            return GetPixelFromSurface(_activePane, p);
        }


        private void WLSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressSliderEvents) return;
            var p = Active();
            if (p == null || p.Raw16 == null) return;   // <= guard both

            p.WL = (int)e.NewValue;
            RenderPaneBitmap(p);       // redraw image with new WL/WW
            UpdateWlWwLabel(p);        // keep the WL/WW overlay in sync
        }

        private void WWSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressSliderEvents) return;
            var p = Active();
            if (p == null || p.Raw16 == null) return;   // <= guard both

            p.WW = (int)e.NewValue;
            RenderPaneBitmap(p);
            UpdateWlWwLabel(p);
        }



        private void InitializeWlWwUI(PaneState pane, int wl, int ww, bool alsoMoveSharedSliders)
        {
            pane.WL = wl;
            pane.WW = ww;
            UpdateWlWwLabel(pane);

            if (alsoMoveSharedSliders && Active() == pane)
            {
                _suppressSliderEvents = true;
                WLSlider.Value = wl;
                WWSlider.Value = ww;
                _suppressSliderEvents = false;
            }
        }

        //private void RenderPaneBitmap(PaneState p)
        //{
        //    if (p.Raw16 == null || p.Width <= 0 || p.Height <= 0) return;

        //    ushort[] scaled = ApplyWindowLevelTo16Bit(p.Raw16, p.WL, p.WW);
        //    var bmp = BitmapSource.Create(
        //        p.Width, p.Height,
        //        96, 96,
        //        PixelFormats.Gray16,
        //        null,
        //        scaled,
        //        p.Width * 2);

        //    p.Image.Source = bmp;
        //    UpdateWlWwLabel(p);
        //}
        
        private void UpdateWlWwLabel(PaneState p)
        {
            if (p.WlWwLabel != null)
                p.WlWwLabel.Text = $"WL: {p.WL}   WW: {p.WW}";
        }

        private void SetActivePane(Pane pane)
        {
            _activePane = pane;

            // Load pane’s draw toggle into your global + apply
            _drawingEnabled = Active().DrawEnabled;
            ApplyInkEditingModeToActive();

            // Sync shared UI controls to this pane
            SyncUIFromActivePane();

            // WL/WW sliders already handled by your code
            _suppressSliderEvents = true;
            WLSlider.Value = Active().WL;
            WWSlider.Value = Active().WW;
            _suppressSliderEvents = false;

            UpdateWlWwLabel(Active());
            UpdateActiveHighlight(); // update the active highlight border
            //D($"[STATE:SetActivePane] " + DumpPaneStates());
        }
        private void UpdateActiveHighlight()
        {
            LeftActiveHighlight.Visibility = (_activePane == Pane.Left) ? Visibility.Visible : Visibility.Collapsed;
            RightActiveHighlight.Visibility = (_activePane == Pane.Right) ? Visibility.Visible : Visibility.Collapsed;

            // make it pop a bit
            LeftActiveHighlight.BorderThickness = (_activePane == Pane.Left) ? new Thickness(5) : new Thickness(0);
            RightActiveHighlight.BorderThickness = (_activePane == Pane.Right) ? new Thickness(5) : new Thickness(0);
        }
        private void SyncUIFromActivePane()
        {
            var p = Active();

            // drawing toggle button text/state
            DrawingCanvas.DrawingToggleButton.Content = p.DrawEnabled;
            DrawingCanvas.DrawingToggleButton.Content = p.DrawEnabled ? "⛔ Stop Drawing" : "✏️ Draw";

            // stroke size slider
            DrawingCanvas.StrokeSizeSlider.Value = Math.Max(1, p.StrokeWidth);

            // stroke color combo selection (match by color name you use)
            // adjust to your actual items if needed
            var hex = ColorToHex(p.StrokeColor);
            if (hex == "#FF0000") DrawingCanvas.StrokeColorComboBox.SelectedIndex = 1; // Red
            else if (hex == "#0000FF") DrawingCanvas.StrokeColorComboBox.SelectedIndex = 2; // Blue
            else if (hex == "#008000" || hex == "#00FF00") DrawingCanvas.StrokeColorComboBox.SelectedIndex = 3; // Green
            else if (hex == "#FFFF00") DrawingCanvas.StrokeColorComboBox.SelectedIndex = 4; // Yellow
            else DrawingCanvas.StrokeColorComboBox.SelectedIndex = 0; // Black

            // hide/show annotations button
            DrawingCanvas.HideButton.Content = p.InkVisible ? "🙈  Hide Annotations" : "👁️  Show Annotations";
        }


        private readonly Dictionary<string, (int wl, int ww)> _presets = new()
        {
            ["default"] = (0, 0),        // placeholder; we’ll replace with _defaultWl/_defaultWw at runtime
            ["bone"] = (300, 1500),
            ["soft"] = (50, 400),     // abdomen/soft tissue
            ["lung"] = (-600, 1500),
            ["brain"] = (40, 80),
            ["mediastinum"] = (40, 350),
        };

        private void PresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;

            var p = Active();
            if (p.Raw16 == null) return;

            var key = (PresetComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            if (string.IsNullOrWhiteSpace(key)) return;

            int wl, ww;

            if (key == "default")
            {
                // per-pane defaults (populate these when you load each image)
                ComputeAutoWindowLevel(p.Raw16, out var wlAuto, out var wwAuto);
                
                wl = wlAuto;
                ww = Math.Max(1, wwAuto);
            }
            else if (_presets.TryGetValue(key, out var preset))
            {
                wl = preset.wl;
                ww = preset.ww;
            }
            else
            {
                return;
            }

            // Move sliders (which drive the active pane) and render
            _suppressSliderEvents = true;
            WLSlider.Value = wl;
            WWSlider.Value = Math.Max(1, ww);
            _suppressSliderEvents = false;

            p.WL = wl;
            p.WW = Math.Max(1, ww);
            RenderPaneBitmap(p);
            UpdateWlWwLabel(p);
        }


        // “Back to Default” button -> just set sliders
        private void BtnResetAuto_Click(object sender, RoutedEventArgs e)
        {
            var p = Active();                   // pane-aware state
            if (p.Raw16 == null) return;

            // Use per-pane defaults (populate when each image is loaded)
            _suppressSliderEvents = true;
            ComputeAutoWindowLevel(p.Raw16, out var wlAuto, out var wwAuto);
            WLSlider.Value = wlAuto;
            WWSlider.Value = Math.Max(1, wwAuto);
            _suppressSliderEvents = false;

            p.WL = wlAuto;
            p.WW = Math.Max(1, wwAuto);
            RenderPaneBitmap(p);
            UpdateWlWwLabel(p);
        }
        private void OnStrokeSizeChanged(double size)
        {
            if (_suppressDrawingUi) return;
            var p = Active();
            p.StrokeWidth = Math.Max(1, size);
            ApplyInkStyle(p);
            DumpBoth($"StrokeSizeChanged({size:0.##})");
        }

        private void ApplyInkStyle(PaneState p)
        {
            var da = p.Ink.DefaultDrawingAttributes.Clone();
            da.Width = da.Height = Math.Max(1, p.StrokeWidth);
            da.Color = p.StrokeColor;
            p.Ink.DefaultDrawingAttributes = da;
        }
        private void OnStrokeColorChanged(System.Windows.Media.Brush brush)
        {
            if (_suppressDrawingUi) return;
            if (brush is not SolidColorBrush scb) return;
            var p = Active();
            p.StrokeColor = scb.Color;
            ApplyInkStyle(p);
            DumpBoth($"StrokeColorChanged({ColorToHex(scb.Color)})");
        }


        private void OnUndoClicked()
        {
            if (_activePane == Pane.Left)
            {
                if (_leftUndo.Count == 0) return;
                var last = _leftUndo.Pop();
                if (InkCanvas.Strokes.Contains(last))
                    InkCanvas.Strokes.Remove(last);
            }
            else
            {
                if (_rightUndo.Count == 0) return;
                var last = _rightUndo.Pop();
                if (RightInkCanvas.Strokes.Contains(last))
                    RightInkCanvas.Strokes.Remove(last);
            }
        }
        

        private void OnClearClicked()
        {
            if (_activePane == Pane.Left)
            {
                InkCanvas.Strokes.Clear();
                _leftUndo.Clear();
            }
            else
            {
                RightInkCanvas.Strokes.Clear();
                _rightUndo.Clear();
            }
        }


        private void OnHideClicked()
        {
            var p = Active();
            var to = (p.Ink.Visibility == Visibility.Visible) ? Visibility.Collapsed : Visibility.Visible;

            p.Ink.Visibility = to;
            p.Ink.IsHitTestVisible = (to == Visibility.Visible) && p.DrawEnabled; // pane-specific
            p.InkVisible = (to == Visibility.Visible);

            // If this lives inside a toolbar/usercontrol, point at the right button name
            DrawingCanvas.HideButton.Content = p.InkVisible ? "🙈  Hide Annotations" : "👁️  Show Annotations";

            Mouse.OverrideCursor = p.InkVisible && p.DrawEnabled ? Cursors.Pen : Cursors.Arrow;

           // D($"[STATE:HideClicked] " + DumpPaneStates());
        }



        private void OnDrawModeToggled(bool enabled)
        {
            if (_suppressDrawingUi) return;
            _drawingEnabled = enabled;
            var p = Active();
            p.DrawEnabled = enabled;
            ApplyInkEditingModeToActive();
            DumpBoth($"DrawModeToggled({enabled})");
        }


        private void SaveAs_Clicked(object sender, RoutedEventArgs e)
        {
            var p = Active(); // your PaneState for Left/Right
            if (p.File == null)
            {
                MessageBox.Show("Open a DICOM first.");
                return;
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "DICOM file (*.dcm)|*.dcm",
                FileName = MakeNextVersionName(p.Path)
            };

            if (dlg.ShowDialog() == true)
                SaveActivePaneToPath(p, dlg.FileName);
        }

        private void SaveActivePaneToPath(PaneState p, string outPath)
        {
            // Cast ints to nullable doubles if your WL/WW are ints
            double? ww = p.WW;
            double? wl = p.WL;

            SaveWorkingDicomWithInk(
                p.File,       // DicomFile for this pane
                ww, wl,       // WW/WL for this pane
                p.Ink,        // InkCanvas for this pane
                outPath);
        }

        // start of methods for save functionality
        public void SaveWorkingDicomWithInk(
    DicomFile sourceFile, double? ww, double? wl,
    InkCanvas inkCanvas, string outPath)
        {
            var ds = sourceFile.Dataset.Clone();

            // new SOP Instance UID so this is a distinct object
            var newSop = DicomUIDGenerator.GenerateDerivedFromUUID();
            ds.AddOrUpdate(DicomTag.SOPInstanceUID, newSop);

            // Write current VOI parameters if provided
            if (ww.HasValue) ds.AddOrUpdate(DicomTag.WindowWidth, Math.Round(ww.Value, 0));
            if (wl.HasValue) ds.AddOrUpdate(DicomTag.WindowCenter, Math.Round(wl.Value, 0));

            // Private creator + strokes (write creator first)
            ds.AddOrUpdate(new DicomLongString(InkTags.CreatorTag, InkTags.CreatorValue));

            using (var ms = new MemoryStream())
            {
                inkCanvas.Strokes.Save(ms);
                var bytes = ms.ToArray();
                if (bytes.Length > 0)
                    ds.AddOrUpdate(new DicomOtherByte(InkTags.StrokesTag, bytes));
            }

            new DicomFile(ds).Save(outPath);
            // Decide which pane we just saved based on the InkCanvas instance
            PaneState p = (inkCanvas == RightInkCanvas) ? _right : _left;

           
        }

        private void ResetDrawingForPane(PaneState p, bool syncToolbar)
        {
            // model
            p.DrawEnabled = false;
            p.StrokeWidth = DEFAULT_STROKE_WIDTH;
            p.StrokeColor = DEFAULT_STROKE_COLOR;

            // apply to InkCanvas
            var da = p.Ink.DefaultDrawingAttributes.Clone();
            da.Width = da.Height = p.StrokeWidth;
            da.Color = p.StrokeColor;
            p.Ink.DefaultDrawingAttributes = da;
            p.Ink.EditingMode = InkCanvasEditingMode.None;
            p.Ink.IsHitTestVisible = false;

            // cursor for the active pane
            if (Active() == p) Mouse.OverrideCursor = Cursors.Arrow;

            // reflect on the toolbar (if your DrawingButtons exposes these parts)
            if (syncToolbar)
            {
                _suppressDrawingUi = true;
                DrawingCanvas.DrawingToggleButton.Content = false;
                DrawingCanvas.DrawingToggleButton.Content = "✏️  Draw";
                DrawingCanvas.StrokeSizeSlider.Value = DEFAULT_STROKE_WIDTH;
                DrawingCanvas.StrokeColorComboBox.SelectedIndex = 0;   // Black
                _suppressDrawingUi = false;
            }
        }


        public void LoadInkAndWwWlFromDicom(
    DicomDataset ds, InkCanvas ink,
    out double? ww, out double? wl)
        {
            if (ds.TryGetValues(InkTags.WindowWidthTag, out double[] wws) && wws.Length > 0)
                ww = wws[0];
            else
                ww = null;

            if (ds.TryGetValues(InkTags.WindowCenterTag, out double[] wls) && wls.Length > 0)
                wl = wls[0];
            else
                wl = null;

            D($"[LOAD] WW={ww?.ToString() ?? "<null>"}  WL={wl?.ToString() ?? "<null>"}");

            ink.Strokes = new StrokeCollection();

            ds.TryGetSingleValue<string>(InkTags.CreatorTag, out var creator);
            ds.TryGetValues<byte>(InkTags.StrokesTag, out var bytes);
            D($"[LOAD] Creator='{creator ?? "<null>"}'  bytes={(bytes?.Length ?? 0)}");

            if (!string.IsNullOrEmpty(creator) &&
                string.Equals(creator, InkTags.CreatorValue, StringComparison.OrdinalIgnoreCase) &&
                bytes?.Length > 0)
            {
                using var ms = new MemoryStream(bytes, writable: false);
                ink.Strokes = new StrokeCollection(ms);
                D($"[LOAD] Ink strokes reloaded: {ink.Strokes.Count}");
            }
            else
            {
                D("[LOAD] No ink strokes loaded.");
            }
        }



        private string MakeNextVersionName(string currentPath)
        {
            if (string.IsNullOrWhiteSpace(currentPath))
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "image_v1.dcm");

            var dir = Path.GetDirectoryName(currentPath) ?? "";
            var name = Path.GetFileNameWithoutExtension(currentPath);
            var ext = Path.GetExtension(currentPath);

            var m = Regex.Match(name, @"^(.*)_v(\d+)$");
            return m.Success
                ? Path.Combine(dir, $"{m.Groups[1].Value}_v{int.Parse(m.Groups[2].Value) + 1}{ext}")
                : Path.Combine(dir, $"{name}_v1{ext}");
        }

        private void SaveAsClickedEvent(object sender, RoutedEventArgs e)
        {
            // Active pane info
            var file = (_activePane == Pane.Left) ? _currentDicomFile : _rightDicomFile;
            var basePath = (_activePane == Pane.Left) ? _currentDicomPath : _rightDicomPath;

            if (file == null)
            {
                MessageBox.Show("No DICOM loaded for the active pane.");
                return;
            }

            // If basePath is unknown, fall back to Documents
            var suggested = !string.IsNullOrWhiteSpace(basePath)
                ? MakeNextVersionName(basePath)
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "image_v1.dcm");

            var dlg = new SaveFileDialog
            {
                Filter = "DICOM file (*.dcm)|*.dcm|All files (*.*)|*.*",
                FileName = suggested,
                InitialDirectory = Path.GetDirectoryName(suggested)
            };

            if (dlg.ShowDialog() == true)
            {
                try
                {
                    // Pull WL/WW + Ink from the active pane
                    double? ww = (_activePane == Pane.Left) ? _ww : _rightww;
                    double? wl = (_activePane == Pane.Left) ? _wl : _rightwl;
                    var ink = (_activePane == Pane.Left) ? InkCanvas : RightInkCanvas;

                    SaveWorkingDicomWithInk(file, ww, wl, ink, dlg.FileName);
                    D($"[SAVE-AS] pane={_activePane} WL={wl} WW={ww} -> {dlg.FileName}");

                    // Optional: make the new file/path the pane’s current “opened” file
                    if (_activePane == Pane.Left)
                    {
                        _currentDicomPath = dlg.FileName;
                        _currentDicomFile = DicomFile.Open(dlg.FileName); // keep dataset in sync if you rely on it later
                    }
                    else
                    {
                        _rightDicomPath = dlg.FileName;
                        _rightDicomFile = DicomFile.Open(dlg.FileName);
                    }
                }
                catch (Exception ex)
                {
                    D($"[SAVE-AS] EXCEPTION: {ex}");
                    MessageBox.Show($"Failed to save: {ex.Message}");
                }
            }
        }




        #region Debug/Diagnostics
        [Conditional("DEBUG")]
        private static void DumpPrivateGroup0011(DicomDataset ds)
        {
            foreach (var item in ds)
            {
                if (item.Tag.Group != 0x0011) continue;

                var owner = item.Tag.PrivateCreator?.ToString() ?? "<none>";
                var vr = item.ValueRepresentation?.ToString() ?? "<none>";

                string lenStr = "-";
                if (item is DicomElement el)
                    lenStr = (el.Buffer != null) ? el.Buffer.Size.ToString() : "0";
                else if (item is DicomSequence seq)
                    lenStr = $"seq:{(seq.Items?.Count ?? 0)}";

                Debug.WriteLine($"[DUMP] {item.Tag} VR={vr} Len={lenStr} Owner={owner}");
            }
        }

        #endregion
        // Add the missing method definition for SetSinglePaneControlsEnabled.
        // This method is likely intended to enable or disable controls when switching between single-pane and two-pane modes.

        private void SetSinglePaneControlsEnabled(bool enabled)
        {
            // Keep these always usable (they target the active pane)
            WLSlider.IsEnabled = true;
            WWSlider.IsEnabled = true;
            PresetComboBox.IsEnabled = true;

            // OPTIONAL: only if you truly want to lock these in 2-player
            //DrawingCanvas.IsEnabled = enabled;  // one switch for Draw/Undo/Clear/Hide
            //SaveAsButton.IsEnabled = enabled;
        }


        private string GetNextDicomPath(string currentPath)
        {
            if (string.IsNullOrWhiteSpace(currentPath)) return null;
            var dir = Path.GetDirectoryName(currentPath);
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return null;

            var files = Directory.GetFiles(dir, "*.dcm");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            var idx = Array.FindIndex(files, f =>
                string.Equals(f, currentPath, StringComparison.OrdinalIgnoreCase));
            return (idx >= 0 && idx < files.Length - 1) ? files[idx + 1] : null;
        }



        //start of methods for two-player mode, need to be able to open the folder so you can view the next image ahead
        private void SetUiEnabled(bool on)
        {
            // choose what to lock; keep sliders enabled if you want pane-by-pane edits
            FileButtons.IsEnabled = on;
            DrawingCanvas.IsEnabled = on;
            PresetComboBox.IsEnabled = on;
            WLSlider.IsEnabled = on;
            WWSlider.IsEnabled = on;
        }

        // the save as feature works, so how do you load the image, be able to make edits, then have all of edits, stay on the image when
        // you move to the left, and when you load the next image to the right,
        // it has to go through the exact same methods the right when it loaded because that has been test and it works, also turn off all available buttons when you do go into 2 player mode,
        //for when you are loading the new image, you are loading it the same way but it is to the right and it is shrunk, and the left image then is shrunk too and moved to the left side, this is how it should work
        private void LeftSurface_MouseDown(object sender, MouseButtonEventArgs e)
        {
            D($"[SELECT] LeftSurface MouseDown btn={e.ChangedButton} handled={e.Handled}");
            SetActivePane(Pane.Left);
        }

        private void RightSurface_MouseDown(object sender, MouseButtonEventArgs e)
        {
            D($"[SELECT] RightSurface MouseDown btn={e.ChangedButton} handled={e.Handled}");
            SetActivePane(Pane.Right);
        }




        private void ApplyInkEditingModeToActive()
        {
            var modeActive = _drawingEnabled ? InkCanvasEditingMode.Ink : InkCanvasEditingMode.None;
            var modeInactive = InkCanvasEditingMode.None;

            // editing mode per pane
            InkCanvas.EditingMode = (_activePane == Pane.Left) ? modeActive : modeInactive;
            RightInkCanvas.EditingMode = (_activePane == Pane.Right) ? modeActive : modeInactive;

            // hit test per pane (this is what lets the right side receive input)
            InkCanvas.IsHitTestVisible = _drawingEnabled && _activePane == Pane.Left;
            RightInkCanvas.IsHitTestVisible = _drawingEnabled && _activePane == Pane.Right;

            // (optional) cursor feedback
            Mouse.OverrideCursor = _drawingEnabled ? Cursors.Pen : Cursors.Arrow;

           
            D($"[INK] L:{InkCanvas.EditingMode}  R:{RightInkCanvas.EditingMode}");
            DumpBoth("ApplyInkEditingModeToActive");
        }


        // WL/WW (get/set)

        private void ApplyPreset(int wl, int ww)
        {
            var p = Active();
            if (p.Raw16 == null) return;

            // update model
            p.WL = wl;
            p.WW = Math.Max(1, ww);

            // sync sliders without re-rendering twice
            _suppressSliderEvents = true;
            WLSlider.Value = wl;
            WWSlider.Value = p.WW;
            _suppressSliderEvents = false;

            RenderPaneBitmap(p);
        }

        private void ResetWlWw_Click(object sender, RoutedEventArgs e)
        {
            var p = Active();
            if (p.Raw16 == null) return;

            ComputeAutoWindowLevel(p.Raw16, out var wlAuto, out var wwAuto);
            p.WL = wlAuto;
            p.WW = Math.Max(1, wwAuto);

            _suppressSliderEvents = true;
            WLSlider.Value = p.WL;
            WWSlider.Value = p.WW;
            _suppressSliderEvents = false;

            RenderPaneBitmap(p);
        }

        private void SelectMode_Click(object s, RoutedEventArgs e)
        {
            _drawingEnabled = true;               // still “editing” but as selection
            InkCanvas.EditingMode = (_activePane == Pane.Left) ? InkCanvasEditingMode.Select : InkCanvasEditingMode.None;
            RightInkCanvas.EditingMode = (_activePane == Pane.Right) ? InkCanvasEditingMode.Select : InkCanvasEditingMode.None;
        }

        private void UpdateFileLabelForPane(Pane pane, string path)
        {
            string name = System.IO.Path.GetFileName(path) ?? "—";
            if (pane == Pane.Left)
                LeftImageFile.Text = name;
            else
                RightImageFile.Text = name;
        }
        private static string ComputeInkHash(StrokeCollection strokes)
        {
            if (strokes == null || strokes.Count == 0) return "empty";
            using var ms = new MemoryStream();
            strokes.Save(ms);
            var bytes = ms.ToArray();
            using var sha1 = SHA1.Create();
            return BitConverter.ToString(sha1.ComputeHash(bytes));
        }

        
 

    private static string ColorToHex(System.Windows.Media.Color c)
            => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        #region DEBUG DUMPS
        [Conditional("DEBUG")]
        private void DumpInk(string label, Pane pane)
        {
            var P = (pane == Pane.Left) ? _left : _right;
            var active = (_activePane == pane);
            var da = P?.Ink?.DefaultDrawingAttributes;

            D($"[STATE:{label}] pane={pane} active={active} " +
              $"drawToggle={_drawingEnabled} " +
              $"inkVisible={P?.Ink?.Visibility} mode={P?.Ink?.EditingMode} " +
              $"DA(w={da?.Width:0.##},h={da?.Height:0.##},color={(da == null ? "<n/a>" : ColorToHex(da.Color))}) " +
              $"WL/WW={P?.WL}/{P?.WW} strokes={(P?.Ink?.Strokes?.Count ?? 0)}");
        }

        [Conditional("DEBUG")]
        private void DumpBoth(string label)
        {
            DumpInk(label, Pane.Left);
            DumpInk(label, Pane.Right);
        }
        #endregion


        // default directory/name based on the active pane's path



        // for the right hand image, these features do not work: X: Y; HU: tracking, Patient Info Pop, and Hide Button, Clear All, Save as, Drawing Buttons

    }
}
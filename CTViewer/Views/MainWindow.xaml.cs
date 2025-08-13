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

namespace CTViewer.Views
{
    public partial class MainWindow : Window
    {
        // Fields to hold the image data and metadata
        private ushort[] _raw16;       // Original 16-bit pixels
        private int _width, _height;   // Image dimensions
        private DicomDataset _ds;      // DICOM dataset for metadata
        private int _wl, _ww;
        private int _rightwl, _rightww;// Current Window Level / Width
        private int _rightWidth, _rightHeight;
        private ushort[] _rightRaw16;
        private int _defaultwl, _defaultww; // Default WL/WW for reset
        private bool _suppressSliderEvents; // To prevents double renders when sliders are moved across
        private readonly Stack<Stroke> _undo = new();
        private bool _isDrawMode;
        private DicomFile _currentDicomFile; // Current DICOM file being viewed
        private string _currentDicomPath; // Path of the currently opened DICOM file
        private DicomFile _rightDicomFile; // path for the right pane in two-player mode
        private string _rightDicomPath; // path for the right pane in two-player mode
        [Conditional("DEBUG")]
        static void D(string msg) => Debug.WriteLine(msg);
        private enum Pane { Left, Right }
        private Pane _activePane = Pane.Left;
        private bool _twoPlayerMode = false;
        private bool _drawingEnabled = false; // whatever your Draw button toggles


        public MainWindow()
        {
            InitializeComponent();
            InkCanvas.StrokeCollected += (s, e) => D($"[INK] Left stroke count={InkCanvas.Strokes.Count}");
            RightInkCanvas.StrokeCollected += (s, e) => D($"[INK] Right stroke count={RightInkCanvas.Strokes.Count}");
            FileButtons.FileOpened += FileButtons_FileOpened;
            FileButtons.TwoPlayerModeChanged += OnTwoPlayerModeChanged;
            // Hook events from your DrawingButtons control
            DrawingCanvas.StrokeSizeChanged += OnStrokeSizeChanged;
            DrawingCanvas.StrokeColorChanged += OnStrokeColorChanged;
            DrawingCanvas.UndoClicked += OnUndoClicked;
            DrawingCanvas.ClearClicked += OnClearClicked;
            DrawingCanvas.HideClicked += OnHideClicked;
            DrawingCanvas.DrawModeToggled += OnDrawModeToggled;

            // Track strokes for Undo
            InkCanvas.StrokeCollected += (s, e) => _undo.Push(e.Stroke);
            // Hover readout wiring
            MainImage.MouseMove += MainImage_MouseMove;
            MainImage.MouseLeave += (_, __) => XYHU.Text = "X: —   Y: —    HU: —";
            // debuggin for the pane selection
            // Make sure these elements can receive clicks
            LeftSurface.Background = System.Windows.Media.Brushes.Transparent;
            RightSurface.Background = System.Windows.Media.Brushes.Transparent;

            // Attach both Preview* (tunneling) and non-Preview (bubbling)
            HookMouse("LEFT-VIEWBOX", LeftViewbox);
            HookMouse("LEFT-SURFACE", LeftSurface);
            HookMouse("LEFT-INK", InkCanvas);

            HookMouse("RIGHT-VIEWBOX", RightViewbox);
            HookMouse("RIGHT-SURFACE", RightSurface);
            HookMouse("RIGHT-INK", RightInkCanvas);

            // Also guarantee we see events even if a child marks them handled
            LeftSurface.AddHandler(UIElement.MouseDownEvent,
                new MouseButtonEventHandler(LeftSurface_MouseDown), /*handledEventsToo*/ true);
            RightSurface.AddHandler(UIElement.MouseDownEvent,
                new MouseButtonEventHandler(RightSurface_MouseDown), true);


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
            // show/hide right viewer + give/take its column space (ImagesLayout: 0=left, 1=right)
            RightViewbox.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            ImagesLayout.ColumnDefinitions[1].Width =
                on ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

            // keep controls enabled; they route via _activePane
            SetSinglePaneControlsEnabled(true);
            WLSlider.IsEnabled = WWSlider.IsEnabled = true;

            // pick which pane the controls target (default to Left on entry)
            SetActivePane(Pane.Left);

            // ensure left tone mapping sticks after layout change
            RenderLeftWithCurrentWlWw();

            if (on)
            {
                // auto-load the “next” file for right side (your existing helper)
                var next = GetNextDicomPath(_currentDicomPath);
                if (!string.IsNullOrEmpty(next)) LoadRightPane(next);
            }
            else
            {

                RightImage.Source = null;
                RightInkCanvas.Strokes = new StrokeCollection();
                if (RightWlWwText != null) RightWlWwText.Text = "WL: —   WW: —";
            }

            // route ink input to active side (Left by default here)
            ApplyInkEditingModeToActive();
        }



        /// <summary> Start of Code for opening Dicom Image with clarity and optimized pixels
        /// Event handler triggered when a file is opened from FileButtons.
        /// Loads and displays a 16-bit grayscale DICOM image with auto window/level adjustment.
        /// </summary>
        private void FileButtons_FileOpened(object sender, string filepath)
        {
            try
            {
                D($"[OPEN] File open started: {filepath}");

                // Load the DICOM file using fo-dicom
                //clear strokes and undo stack when a new file is opened
                InkCanvas.Strokes = new StrokeCollection();
                var dicomFile = DicomFile.Open(filepath);
                var pixelData = DicomPixelData.Create(dicomFile.Dataset);
                var dset = dicomFile.Dataset;

                #if DEBUG
                DumpPrivateGroup0011(dset);
                #endif

                D($"[OPEN] DICOM loaded. Size: {pixelData.Width}x{pixelData.Height}");

                int width = pixelData.Width;
                int height = pixelData.Height;

                // Extract raw pixel bytes from the first frame (DICOM may contain multiple frames)
                byte[] pixels = pixelData.GetFrame(0).Data;

                // Convert byte[] to ushort[] since grayscale images are stored as 16-bit integers
                ushort[] rawPixels = new ushort[pixels.Length / 2];
                Buffer.BlockCopy(pixels, 0, rawPixels, 0, pixels.Length);

                // Automatically compute optimal window center and width using histogram percentiles (1% to 99%)
                int wl, ww;

                ComputeAutoWindowLevel(rawPixels, out wl, out ww);

                // set the private parameters for future use
                _ds = dicomFile.Dataset;
                _raw16 = rawPixels;
                _width = width;
                _height = height;
                _wl = wl;
                _ww = ww;
                LeftSurface.Width = _width;
                LeftSurface.Height = _height;
                InkCanvas.Width = _width;
                InkCanvas.Height = _height;

                // Apply linear window/level scaling to enhance contrast based on wc/ww
                ushort[] scaledPixels = ApplyWindowLevelTo16Bit(rawPixels, wl, ww);

                var edited = CheckIfEdited(dset);
                D($"[OPEN] CheckIfEdited: {edited}");

                if (edited.Equals(true))
                {
                    LoadInkAndWwWlFromDicom(dset, InkCanvas, out double? savedWw, out double? savedWl);
                    D($"[OPEN] LoadInkAndWwWlFromDicom returned: savedWL={savedWl?.ToString() ?? "<null>"}, savedWW={savedWw?.ToString() ?? "<null>"}");

                    if (savedWw.HasValue && savedWl.HasValue)
                    {
                        _wl = (int)savedWl.Value;
                        _ww = (int)savedWw.Value;
                    }
                    else
                    {
                        _wl = wl;
                        _ww = ww;
                    }

                    // 🔹 Sync sliders & overlay here
                    InitializeWlWwUI(_wl, _ww);



                    ushort[] scaledPixelsEdited = ApplyWindowLevelTo16Bit(rawPixels, _wl, _ww);

                    // start to load the saved strokes into the InkCanvas
                    InkCanvas.Strokes = new StrokeCollection();   // reset

                    if (dset.TryGetValues<byte>(InkTags.StrokesTag, out var isf) && isf != null && isf.Length > 0)
                    {
                        using var ms = new MemoryStream(isf);
                        // if this code can run off the UI thread, marshal it:
                        Dispatcher.Invoke(() => InkCanvas.Strokes = new StrokeCollection(ms));
                    }
                    else
                    {
                        D("[OPEN] No strokes in file.");
                    }
                    var bitmap = BitmapSource.Create(
                        width,
                        height,
                        96, 96, PixelFormats.Gray16,
                        null,
                        scaledPixelsEdited,
                        width * 2
                    );
                    // native pixel size of the bitmap you just created
                    LeftSurface.Width = _width;
                    LeftSurface.Height = _height;

                    InkCanvas.Width = _width;
                    InkCanvas.Height = _height;

                    // image just gets the source; Stretch="Fill" makes it match LeftSurface
                    MainImage.Source = bitmap;
                }
                else if (edited.Equals(false))
                {

                    // 🔹 Sync sliders & overlay here
                    InitializeWlWwUI(_wl, _ww);

                    var bitmap = BitmapSource.Create(
                        width,
                        height,
                        96, 96, PixelFormats.Gray16,
                        null,
                        scaledPixels,
                        width * 2
                    );

                    MainImage.Source = bitmap;
                }

                _currentDicomFile = dicomFile;
                _currentDicomPath = filepath;

                SetPatientInfo(dset);
                _drawingEnabled = true;

            }
            catch (Exception ex)
            {
                D($"[OPEN] EXCEPTION: {ex}");
                MessageBox.Show($"Error loading DICOM file: {ex.Message}");
            }
        } private DicomDataset _currentDataset => _currentDicomFile?.Dataset;




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
        private void ComputeAutoWindowLevel(ushort[] pixels, out int windowCenter, out int windowWidth)
        {
            ushort[] sorted = (ushort[])pixels.Clone();
            Array.Sort(sorted);

            // Find pixel values at the 1st and 99th percentiles
            int lowerIndex = (int)(sorted.Length * 0.01);
            int upperIndex = (int)(sorted.Length * 0.99);

            ushort minVal = sorted[lowerIndex];
            ushort maxVal = sorted[upperIndex];

            // Calculate window width and center
            windowWidth = maxVal - minVal;
            windowCenter = (maxVal + minVal) / 2;
            // set default ww and wc values for future use and initalize them
            _defaultwl = windowCenter;
            _defaultww = windowWidth;
            InitializeWlWwUI(windowCenter, windowWidth);
        }

        /// <summary>
        /// Applies linear scaling to 16-bit grayscale pixel data using window center and width.
        /// Maps values to a visible contrast range (0 - 65535).
        /// </summary>
        private ushort[] ApplyWindowLevelTo16Bit(ushort[] pixelData, int windowCenter, int windowWidth)
        {
            int length = pixelData.Length;
            ushort[] output = new ushort[length];

            // Define window min and max bounds
            double min = windowCenter - windowWidth / 2.0;
            double max = windowCenter + windowWidth / 2.0;

            for (int i = 0; i < length; i++)
            {
                double value = pixelData[i];
                double scaled;

                if (value <= min)
                    scaled = 0;             // Below window: black
                else if (value >= max)
                    scaled = 65535;         // Above window: white
                else
                    scaled = ((value - min) / windowWidth) * 65535.0; // Linear scale in between

                output[i] = (ushort)scaled;
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
        private void SetPatientInfo(DicomDataset dataset)
        {
            // Build top overlay lines and join them into multiline string
            var top = BuildOverlayLines(dataset, top: true);
            PatientInfoTop.Text = $"{top.L1}\n{top.L2}\n{top.L3}\n{top.L4}";

            // Build bottom overlay lines and join them into multiline string
            var bottom = BuildOverlayLines(dataset, top: false);
            PatientInfoBottom.Text = $"{bottom.L1}\n{bottom.L2}\n{bottom.L3}\n{bottom.L4}";
        }

        // Hide the patient info overlays, 1 button to turn off both, makes it simpler
        private void HidePatientInfoButton_Click(object sender, RoutedEventArgs e)
        {
            // Toggle visibility of both TextBlocks
            bool isVisible = PatientInfoTop.Visibility == Visibility.Visible;
            PatientInfoTop.Visibility = isVisible ? Visibility.Collapsed : Visibility.Visible;
            PatientInfoBottom.Visibility = isVisible ? Visibility.Collapsed : Visibility.Visible;
        }

        // ==========================
        // Event: Updates "X: Y: HU:" display in real-time when mouse moves over the image
        // ==========================
        private void MainImage_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDrawMode) return; // pause while drawing
            // Convert the mouse's on-screen position into image pixel coordinates
            // 'GetPosition(MainImage)' gives mouse coordinates in WPF space (System.Windows.Point)
            var hit = GetPixelFromMouse(e.GetPosition(MainImage));

            // If we are outside the image bounds, or we have no loaded pixel data, show blanks
            if (hit is null || _raw16 == null)
            {
                XYHU.Text = "X: —   Y: —    HU: —";
                return;
            }

            // Pixel coordinates within the image
            int px = hit.Value.px; // X coordinate in pixels
            int py = hit.Value.py; // Y coordinate in pixels

            // Convert (x, y) into a single index in our 1D pixel array
            // Row-major order: index = row * width + column
            int idx = py * _width + px;

            // Get the raw 16-bit pixel value from our stored DICOM pixel data
            ushort raw = _raw16[idx];

            // Convert raw pixel value into Hounsfield Units (HU)
            // Formula: HU = raw * slope + intercept
            // RescaleSlope and RescaleIntercept come from the DICOM metadata (dataset)
            // If tags are missing, defaults are slope=1.0 and intercept=0.0
            double slope = _ds?.GetSingleValueOrDefault(FellowOakDicom.DicomTag.RescaleSlope, 1.0) ?? 1.0;
            double intercept = _ds?.GetSingleValueOrDefault(FellowOakDicom.DicomTag.RescaleIntercept, 0.0) ?? 0.0;
            double hu = raw * slope + intercept;

            // Update the overlay TextBlock with formatted coordinates and HU
            // '0' after the colon in {hu:0} means no decimal places
            XYHU.Text = $"X: {px}   Y: {py}    HU: {hu:0}";
        }

        private void SetDrawMode(bool on)
        {
            _isDrawMode = on;

            if (_isDrawMode)
            {
                // Drawing ON: InkCanvas owns input, tracking pauses
                InkCanvas.IsHitTestVisible = true;
                InkCanvas.EditingMode = InkCanvasEditingMode.Ink;   // or your selected tool
                Mouse.OverrideCursor = Cursors.Pen;
            }
            else
            {
                // Drawing OFF: mouse passes through, tracking resumes
                InkCanvas.EditingMode = InkCanvasEditingMode.None;
                InkCanvas.IsHitTestVisible = false;                  // <- key line
                Mouse.OverrideCursor = Cursors.Arrow;
            }
        }
        // ==========================
        // Helper: Converts mouse position in the control into pixel coordinates in the image
        // Handles "Stretch=Uniform" scaling so the mapping is correct
        // ==========================
        private (int px, int py)? GetPixelFromMouse(System.Windows.Point pos)
        {
            // If no valid image dimensions or no image loaded, exit
            if (_width <= 0 || _height <= 0 || MainImage.Source == null)
                return null;

            // Scale factor: how much the image was scaled to fit into the Image control
            double scale = Math.Min(
                MainImage.ActualWidth / _width,
                MainImage.ActualHeight / _height
            );

            // The actual drawn size of the image (could be smaller than the control if aspect ratio preserved)
            double drawW = _width * scale;
            double drawH = _height * scale;

            // Offsets: how far from the top-left corner the drawn image starts
            // (used for centering when "Stretch=Uniform" is set)
            double offX = (MainImage.ActualWidth - drawW) / 2.0;
            double offY = (MainImage.ActualHeight - drawH) / 2.0;

            // Convert mouse coordinates to pixel coordinates inside the image
            int px = (int)Math.Floor((pos.X - offX) / scale);
            int py = (int)Math.Floor((pos.Y - offY) / scale);

            // Check bounds — if the mouse is outside the image, return null
            if (px < 0 || py < 0 || px >= _width || py >= _height)
                return null;

            // Return pixel coordinates
            return (px, py);
        }


        private void WLSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressSliderEvents || !ActiveHasRaw()) return;
            SetActiveWlWw((int)e.NewValue, ActiveWW());
            RenderActive();
            // update left/right WL/WW labels if you have two
            WlWwText.Text = $"WL: {_wl}   WW: {_ww}";
            if (RightWlWwText != null) RightWlWwText.Text = $"WL: {_rightwl}   WW: {_rightww}";
        }

        private void WWSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressSliderEvents || !ActiveHasRaw()) return;
            SetActiveWlWw(ActiveWL(), (int)e.NewValue);
            RenderActive();
            WlWwText.Text = $"WL: {_wl}   WW: {_ww}";
            if (RightWlWwText != null) RightWlWwText.Text = $"WL: {_rightwl}   WW: {_rightww}";
        }



        private void InitializeWlWwUI(int wl, int ww)
        {
            _wl = wl;
            _ww = Math.Max(1, ww);

            _suppressSliderEvents = true;          // avoid ValueChanged recursion
            WLSlider.Value = _wl;
            WWSlider.Value = _ww;
            _suppressSliderEvents = false;

            RenderWithWlWw(_wl, _ww);              // your render function
            UpdateWlWwOverlay();                   // e.g., WlWwText.Text = $"WL: {_wl}   WW: {_ww}";
        }
        private void RenderWithWlWw(int wl, int ww)
        {
            if (_raw16 == null) return;
            ushort[] scaled = ApplyWindowLevelTo16Bit(_raw16, wl, ww);
            var bmp = BitmapSource.Create(_width, _height, 96, 96, PixelFormats.Gray16, null, scaled, _width * 2);
            MainImage.Source = bmp;
        }
        private void UpdateWlWwOverlay()
        {
            // Only if you have the bottom-left label; otherwise safe to no-op
            if (WlWwText != null)
                WlWwText.Text = $"WL: {_wl}   WW: {_ww}";
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
            if (_raw16 == null || !IsLoaded) return;

            var key = (PresetComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            if (string.IsNullOrWhiteSpace(key)) return;

            int wl, ww;
            if (key == "default")
            {
                wl = _defaultwl;
                ww = _defaultww;
            }
            else if (_presets.TryGetValue(key, out var p))
            {
                wl = p.wl;
                ww = p.ww;
            }
            else return;

            // Setting sliders will trigger your existing ValueChanged handlers
            WLSlider.Value = wl;
            WWSlider.Value = Math.Max(1, ww);
        }

        // “Back to Default” button -> just set sliders
        private void BtnResetAuto_Click(object sender, RoutedEventArgs e)
        {
            if (_raw16 == null) return;
            WLSlider.Value = _defaultwl;
            WWSlider.Value = Math.Max(1, _defaultww);
        }
        private void OnStrokeSizeChanged(double size)
        {
            var da = InkCanvas.DefaultDrawingAttributes;
            da.Width = Math.Max(1, size);
            da.Height = Math.Max(1, size);
        }

        private void OnStrokeColorChanged(System.Windows.Media.Brush brush)
        {
            if (brush is SolidColorBrush scb)
                InkCanvas.DefaultDrawingAttributes.Color = scb.Color;
        }

        private void OnUndoClicked()
        {
            if (_undo.Count == 0) return;
            var last = _undo.Pop();
            if (InkCanvas.Strokes.Contains(last))
                InkCanvas.Strokes.Remove(last);
        }

        private void OnClearClicked()
        {
            InkCanvas.Strokes.Clear();
            _undo.Clear();
        }

        private void OnHideClicked()
        {
            InkCanvas.Visibility = InkCanvas.Visibility == Visibility.Visible
                ? Visibility.Collapsed : Visibility.Visible;

            InkCanvas.IsHitTestVisible = InkCanvas.Visibility == Visibility.Visible;
        }

        private void OnDrawModeToggled(bool enabled)
        {
            _isDrawMode = enabled;
            SetDrawMode(enabled);
            InkCanvas.EditingMode = enabled ? InkCanvasEditingMode.Ink
                                              : InkCanvasEditingMode.None;
            InkCanvas.Cursor = enabled ? Cursors.Pen : Cursors.Arrow;
        }

        // start of methods for save functionality
        public void SaveWorkingDicomWithInk(
    DicomFile sourceFile, double? ww, double? wl,
    InkCanvas inkCanvas, string outPath)
        {
            var ds = sourceFile.Dataset.Clone();

            // New instance UID
            var newSop = DicomUIDGenerator.GenerateDerivedFromUUID();
            ds.AddOrUpdate(DicomTag.SOPInstanceUID, newSop);

            // --- Write *the passed-in* WW/WL (not globals) ---
            if (ww.HasValue)
            {
                ds.AddOrUpdate(InkTags.WindowWidthTag, ww.Value);      // (0028,1051)
                D($"[SAVE] WW (arg) saved: {ww.Value}");
            }
            else D("[SAVE] WW arg is null — not saving");

            if (wl.HasValue)
            {
                ds.AddOrUpdate(InkTags.WindowCenterTag, wl.Value);     // (0028,1050)
                D($"[SAVE] WL (arg) saved: {wl.Value}");
            }
            else D("[SAVE] WL arg is null — not saving");

            // Private creator + strokes
            ds.AddOrUpdate(new DicomLongString(InkTags.CreatorTag, InkTags.CreatorValue)); // (0011,0010)
            using var ms = new MemoryStream();
            inkCanvas.Strokes.Save(ms);
            var isf = ms.ToArray();

            if (isf.Length > 0)
            {
                ds.AddOrUpdate(new DicomOtherByte(InkTags.StrokesTag, isf));               // (0011,1001)
                D($"[SAVE] Strokes saved: {isf.Length} bytes");
            }
            else D("[SAVE] No strokes to save");

            new DicomFile(ds).Save(outPath);
            D($"[SAVE] Saved DICOM with ink to: {outPath}");
        }


        public void LoadInkAndWwWlFromDicom(DicomDataset ds, InkCanvas ink,
                                    out double? ww, out double? wl)
        {
            ww = ds.TryGetSingleValue(InkTags.WindowWidthTag, out double wv) ? wv : (double?)null;
            wl = ds.TryGetSingleValue(InkTags.WindowCenterTag, out double lv) ? lv : (double?)null;
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
            var dir = Path.GetDirectoryName(currentPath)!;
            var name = Path.GetFileNameWithoutExtension(currentPath);
            var ext = Path.GetExtension(currentPath);

            var m = Regex.Match(name, @"^(.*)_v(\d+)$");
            if (m.Success) return Path.Combine(dir, $"{m.Groups[1].Value}_v{int.Parse(m.Groups[2].Value) + 1}{ext}");
            return Path.Combine(dir, $"{name}_v1{ext}");
        }
        private void SaveAsClickedEvent(object sender, RoutedEventArgs e)
        {
            // pick the active pane’s file & path
            var file = (_activePane == Pane.Left) ? _currentDicomFile : _rightDicomFile;
            var basePath = (_activePane == Pane.Left) ? _currentDicomPath : _rightDicomPath;

            if (file == null)
            {
                MessageBox.Show("No DICOM loaded for the active pane.");
                return;
            }

            var dlg = new SaveFileDialog
            {
                Filter = "DICOM file (*.dcm)|*.dcm|All files (*.*)|*.*",
                FileName = MakeNextVersionName(basePath),
                InitialDirectory = string.IsNullOrEmpty(basePath) ? null : Path.GetDirectoryName(basePath)
            };

            if (dlg.ShowDialog() == true)
            {
                // Use ACTIVE pane state, not slider helpers
                SaveWorkingDicomWithInk(
                    file,
                    (double)ActiveWW(), (double)ActiveWL(),
                    ActiveInk(),
                    dlg.FileName);

                D($"[SAVE-AS] pane={_activePane} WL={ActiveWL()} WW={ActiveWW()} -> {dlg.FileName}");
            }
        }

        private double getCurrentCurrentWL()
        {
            // Get the current values from the sliders
            return (double)WLSlider.Value;
        }
        private double getCurrentCurrentWW()
        {
            // Get the current values from the sliders
            return (double)WWSlider.Value;
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
            // Keep WL/WW and presets usable in 2-player
            WLSlider.IsEnabled = true;
            WWSlider.IsEnabled = true;
            PresetComboBox.IsEnabled = true;

            // Disable the risky stuff while in 2-player
            //DrawButton.IsEnabled = enabled;
            //EraseButton.IsEnabled = enabled;
            //ClearButton.IsEnabled = enabled;
            //SaveAsButton.IsEnabled = enabled;
        }

        private string GetNextDicomPath(string currentPath)
        {
            if (string.IsNullOrEmpty(currentPath) || !File.Exists(currentPath))
                return null;

            var directory = Path.GetDirectoryName(currentPath);
            var files = Directory.GetFiles(directory, "*.dcm"); // Assuming DICOM files have a .dcm extension
            Array.Sort(files); // Sort files alphabetically

            var currentIndex = Array.IndexOf(files, currentPath);
            if (currentIndex >= 0 && currentIndex < files.Length - 1)
            {
                return files[currentIndex + 1]; // Return the next file in the list
            }

            return null; // No next file found
        }
        // Add the missing method definition for LoadRightPane.
        // This method is likely intended to load the next DICOM file into the right pane in two-player mode.

        private void LoadRightPane(string dicomPath)
        {
            var dicom = DicomFile.Open(dicomPath);
            var ds = dicom.Dataset;
            var px = DicomPixelData.Create(ds);

            // cache file + path for Save As
            _rightDicomFile = dicom;
            _rightDicomPath = dicomPath;

            // cache native size + raw pixels
            _rightWidth = px.Width;
            _rightHeight = px.Height;

            byte[] frame = px.GetFrame(0).Data;
            _rightRaw16 = new ushort[frame.Length / 2];
            Buffer.BlockCopy(frame, 0, _rightRaw16, 0, frame.Length);

            // baseline auto WL/WW
            ComputeAutoWindowLevel(_rightRaw16, out int wlAuto, out int wwAuto);

            // load saved WL/WW + ink directly into RightInkCanvas
            LoadInkAndWwWlFromDicom(ds, RightInkCanvas, out double? savedWw, out double? savedWl);
            _rightwl = savedWl.HasValue ? (int)savedWl.Value : wlAuto;
            _rightww = savedWw.HasValue ? (int)savedWw.Value : wwAuto;

            // Viewbox design surface must match native pixels
            RightSurface.Width = _rightWidth;
            RightSurface.Height = _rightHeight;
            RightInkCanvas.Width = _rightWidth;
            RightInkCanvas.Height = _rightHeight;

            // render with current right WL/WW
            RenderRightWithCurrentWlWw();
        }

        //start of methods for two-player mode, need to be able to open the folder so you can view the next image ahead
        private void RenderLeftWithCurrentWlWw()
        {
            if (_raw16 == null) return;
            var scaled = ApplyWindowLevelTo16Bit(_raw16, _wl, _ww);
            var bmp = BitmapSource.Create(_width, _height, 96, 96, PixelFormats.Gray16, null, scaled, _width * 2);
            MainImage.Source = bmp;
            if (WlWwText != null) WlWwText.Text = $"WL: {_wl}   WW: {_ww}";
        }

        private void RenderRightWithCurrentWlWw()
        {
            if (_rightRaw16 == null) return;
            var scaled = ApplyWindowLevelTo16Bit(_rightRaw16, _rightwl, _rightww);
            var bmp = BitmapSource.Create(_rightWidth, _rightHeight, 96, 96, PixelFormats.Gray16, null, scaled, _rightWidth * 2);
            RightImage.Source = bmp;
            if (RightWlWwText != null) RightWlWwText.Text = $"WL: {_rightwl}   WW: {_rightww}";
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

        private void SetActivePane(Pane pane)
        {
            D($"[ACTIVE] -> {pane}   LEFT wl/ww={_wl}/{_ww}   RIGHT wl/ww={_rightwl}/{_rightww}");
            _activePane = pane;

            // visual hint
            LeftActiveHighlight.Visibility = (pane == Pane.Left) ? Visibility.Visible : Visibility.Collapsed;
            RightActiveHighlight.Visibility = (pane == Pane.Right) ? Visibility.Visible : Visibility.Collapsed;

            // sliders reflect the active pane's current values (keep them enabled even in 2-player)
            _suppressSliderEvents = true;
            if (pane == Pane.Left) { WLSlider.Value = _wl; WWSlider.Value = _ww; }
            else { WLSlider.Value = _rightwl; WWSlider.Value = _rightww; }
            _suppressSliderEvents = false;

            // ink input goes only to the active pane
            ApplyInkEditingModeToActive();
            D($"[ACTIVE] sliders now WL={WLSlider.Value} WW={WWSlider.Value} suppress={_suppressSliderEvents}");
        }

        private void ApplyInkEditingModeToActive()
        {
            var mode = _drawingEnabled ? InkCanvasEditingMode.Ink : InkCanvasEditingMode.None;
            InkCanvas.EditingMode = (_activePane == Pane.Left) ? mode : InkCanvasEditingMode.None;
            RightInkCanvas.EditingMode = (_activePane == Pane.Right) ? mode : InkCanvasEditingMode.None;
            D($"[INK] L:{InkCanvas.EditingMode}  R:{RightInkCanvas.EditingMode}");
        }

        // methods for toggling back and forward between images in the folder
        // visuals
        private InkCanvas ActiveInk() => _activePane == Pane.Left ? InkCanvas : RightInkCanvas;
        private System.Windows.Controls.Image ActiveImage() => _activePane == Pane.Left ? MainImage : RightImage;
        private Grid ActiveOverlay() => _activePane == Pane.Left ? PatientInfoOverlay : RightPatientInfoOverlay;

        // pixels + sizes (assumes you already cache both)
        private bool ActiveHasRaw() => _activePane == Pane.Left ? _raw16 != null : _rightRaw16 != null;
        private ushort[] ActiveRaw() => _activePane == Pane.Left ? _raw16 : _rightRaw16;
        private int ActiveW() => _activePane == Pane.Left ? _width : _rightWidth;
        private int ActiveH() => _activePane == Pane.Left ? _height : _rightHeight;

        // WL/WW (get/set)
        private int ActiveWL() => _activePane == Pane.Left ? _wl : _rightwl;
        private int ActiveWW() => _activePane == Pane.Left ? _ww : _rightww;
        private void SetActiveWlWw(int wl, int ww)
        {
            if (_activePane == Pane.Left) { _wl = wl; _ww = ww; } else { _rightwl = wl; _rightww = ww; }
        }
        private void RenderActive()
        {
            if (!ActiveHasRaw()) return;
            var scaled = ApplyWindowLevelTo16Bit(ActiveRaw(), ActiveWL(), ActiveWW());
            var bmp = BitmapSource.Create(ActiveW(), ActiveH(), 96, 96, PixelFormats.Gray16, null, scaled, ActiveW() * 2);
            ActiveImage().Source = bmp;
        }
        private void ApplyPreset(int wl, int ww)
        {
            SetActiveWlWw(wl, ww);
            RenderActive();
            _suppressSliderEvents = true;
            WLSlider.Value = wl; WWSlider.Value = ww;
            _suppressSliderEvents = false;
        }
        private void ResetWlWw_Click(object sender, RoutedEventArgs e)
        {
            if (!ActiveHasRaw()) return;
            ComputeAutoWindowLevel(ActiveRaw(), out int wlAuto, out int wwAuto);
            SetActiveWlWw(wlAuto, wwAuto);
            RenderActive();
            _suppressSliderEvents = true; WLSlider.Value = wlAuto; WWSlider.Value = wwAuto; _suppressSliderEvents = false;
        }
        private void DrawButton_Click(object sender, RoutedEventArgs e)
        {
            _drawingEnabled = !_drawingEnabled;
            D($"[DRAW] toggle -> {(_drawingEnabled ? "ON" : "OFF")}  active={_activePane}");
            ApplyInkEditingModeToActive();
        }
        private void ClearAll_Click(object s, RoutedEventArgs e)
        {
            var before = ActiveInk().Strokes.Count;
            ActiveInk().Strokes.Clear();
            D($"[CLEAR] active={_activePane} {before}→{ActiveInk().Strokes.Count}");
        }

        private void HideAnnotations_Click(object s, RoutedEventArgs e)
        {
            var ink = ActiveInk();
            ink.Visibility = ink.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        }
        private void HidePatientInfo_Click(object s, RoutedEventArgs e)
        {
            var ov = ActiveOverlay();
            ov.Visibility = ov.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        }
        private void SaveAs_Click(object sender, RoutedEventArgs e)
        {
            // pick active inputs
            var file = (_activePane == Pane.Left) ? _currentDicomFile : _rightDicomFile;
            var ink = ActiveInk();               // from the router helpers
            var wl = ActiveWL();                // int
            var ww = ActiveWW();                // int

            if (file == null)
            {
                MessageBox.Show("No DICOM loaded for the active pane.");
                return;
            }

            // default directory/name based on the active pane's path
            string defPath = (_activePane == Pane.Left) ? _currentDicomPath : _rightDicomPath;
            string defName = string.IsNullOrEmpty(defPath) ? "image" : System.IO.Path.GetFileNameWithoutExtension(defPath) + "_v1";

            var sfd = new SaveFileDialog
            {
                Filter = "DICOM files (*.dcm)|*.dcm|All files (*.*)|*.*",
                FileName = defName + ".dcm",
                InitialDirectory = string.IsNullOrEmpty(defPath) ? null : System.IO.Path.GetDirectoryName(defPath)
            };

            if (sfd.ShowDialog() != true) return;

            // your saver takes (DicomFile, double? ww, double? wl, InkCanvas, string)
            SaveWorkingDicomWithInk(file, (double)ww, (double)wl, ink, sfd.FileName);
            D($"[SAVE-AS] Active={_activePane}  WL={wl} WW={ww}  -> {sfd.FileName}");
        }

        // for the right hand image, these features do not work: X: Y; HU: tracking, Patient Info Pop, and Hide Button, Clear All, Save as, Drawing Buttons

    }
}
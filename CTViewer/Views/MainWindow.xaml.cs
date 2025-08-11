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

namespace CTViewer.Views
{
    public partial class MainWindow : Window
    {
        // Fields to hold the image data and metadata
        private ushort[] _raw16;       // Original 16-bit pixels
        private int _width, _height;   // Image dimensions
        private DicomDataset _ds;      // DICOM dataset for metadata
        private int _wl, _ww;          // Current Window Level / Width
        private int _defaultwl, _defaultww; // Default WL/WW for reset
        private bool _suppressSliderEvents; // To prevents double renders when sliders are moved across
        private readonly Stack<Stroke> _undo = new();
        private bool _isDrawMode;
        private DicomFile _currentDicomFile; // Current DICOM file being viewed
        private string _currentDicomPath; // Path of the currently opened DICOM file
        
        private DicomDataset _currentDataset => _currentDicomFile?.Dataset;

        public MainWindow()
        {
            InitializeComponent();
            FileButtons.FileOpened += FileButtons_FileOpened;
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

        }
        /// <summary> Start of Code for opening Dicom Image with clarity and optimized pixels
        /// Event handler triggered when a file is opened from FileButtons.
        /// Loads and displays a 16-bit grayscale DICOM image with auto window/level adjustment.
        /// </summary>
        private void FileButtons_FileOpened(object sender, string filepath)
        {
            try
            {
                // Load the DICOM file using fo-dicom
                var dicomFile = DicomFile.Open(filepath);
                var pixelData = DicomPixelData.Create(dicomFile.Dataset);
                var dset = dicomFile.Dataset;

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
                // set the private perameters for future use
                _ds = dicomFile.Dataset;
                _raw16 = rawPixels;
                _width = width;
                _height = height;
                _wl = wl;
                _ww = ww;

                // Apply linear window/level scaling to enhance contrast based on wc/ww
                ushort[] scaledPixels = ApplyWindowLevelTo16Bit(rawPixels, wl, ww);

                // Create a WPF-compatible image source using the scaled pixel data
                var bitmap = BitmapSource.Create(
                    width,
                    height,
                    96, 96,                    // Dots per inch (DPI)
                    PixelFormats.Gray16,       // Keep 16-bit grayscale for higher fidelity
                    null,                      // No color palette for grayscale
                    scaledPixels,
                    width * 2                  // Stride = width * bytes per pixel (2 for Gray16)
                );

                // Display the final image
                _currentDicomFile = dicomFile; // Store the current DICOM file for later use
                _currentDicomPath = filepath; // Store the path of the current DICOM file
                MainImage.Source = bitmap;
                SetPatientInfo(dset);
                SetDrawMode(false); // default = tracking, drawing off

                //LoadInkAndWwWlFromDicom(_currentDataset, InkCanvas, out var ww, out var wl, out var state);

                //apply only if present
                //(CurrentWW, CurrentWL) = (ww ?? CurrentWW, wl ?? CurrentWL);

                //if changing WW/WL needs a redraw, call once here:
                //    ApplyWindowLevel(CurrentWW, CurrentWL);

            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading DICOM file: {ex.Message}");
            }
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

        private void WWSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_raw16 == null) return; // No image loaded
            if (_suppressSliderEvents) return; // check if there is preset being applied
            _ww = (int)e.NewValue;
            // Re-apply window/level scaling with new width
            ushort[] scaledPixels = ApplyWindowLevelTo16Bit(_raw16, _wl, _ww);
            // Update the displayed image
            var bitmap = BitmapSource.Create(
                _width,
                _height,
                96, 96,
                PixelFormats.Gray16,
                null,
                scaledPixels,
                _width * 2
            );
            MainImage.Source = bitmap;
            WlWwText.Text = $"WL: {_wl}   WW: {_ww}";
        }

        private void WLSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_raw16 == null) return; // No image loaded
            if (_suppressSliderEvents) return; // check if there is preset being applied
            _wl = (int)e.NewValue;
            // Re-apply window/level scaling with new level
            ushort[] scaledPixels = ApplyWindowLevelTo16Bit(_raw16, _wl, _ww);
            // Update the displayed image
            var bitmap = BitmapSource.Create(
                _width,
                _height,
                96, 96,
                PixelFormats.Gray16,
                null,
                scaledPixels,
                _width * 2
            );
            MainImage.Source = bitmap;
            WlWwText.Text = $"WL: {_wl}   WW: {_ww}";
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
    InkCanvas inkCanvas, string outPath, string optionalAppStateJson = null)
        {
            var ds = sourceFile.Dataset.Clone();

            // New IDs so it’s a distinct instance/series (study stays the same)
            var newSeries = DicomUIDGenerator.GenerateDerivedFromUUID();
            var newSop = DicomUIDGenerator.GenerateDerivedFromUUID();
            ds.AddOrUpdate(DicomTag.SeriesInstanceUID, newSeries);
            ds.AddOrUpdate(DicomTag.SOPInstanceUID, newSop);
            ds.AddOrUpdate(DicomTag.SeriesDescription, "Working copy (Ink)");

            // Save current display defaults
            if (ww.HasValue) ds.AddOrUpdate(DicomTag.WindowWidth, ww.Value);
            if (wl.HasValue) ds.AddOrUpdate(DicomTag.WindowCenter, wl.Value);

            // ---- Private creator + private elements (explicit VRs!) ----
            var creatorTag = new DicomTag(0x0011, 0x0010); // Private Creator (LO)
            var strokesTag = new DicomTag(0x0011, 0x1001); // our ISF bytes (OB)
            var appStateTag = new DicomTag(0x0011, 0x1002); // optional JSON (UT/LT)

            ds.AddOrUpdate(new DicomLongString(creatorTag, "CTViewer")); // register creator FIRST

            using (var ms = new MemoryStream())
            {
                inkCanvas.Strokes.Save(ms);                                // ISF vector data
                ds.AddOrUpdate(new DicomOtherByte(strokesTag, ms.ToArray())); // OB VR → no exception
            }

            if (!string.IsNullOrWhiteSpace(optionalAppStateJson))
            {
                // If your build lacks DicomUnlimitedText, use DicomLongText instead.
                ds.AddOrUpdate(new DicomUnlimitedText(appStateTag, optionalAppStateJson));
                // ds.AddOrUpdate(new DicomLongText(appStateTag, optionalAppStateJson));
            }

            new DicomFile(ds).Save(outPath);
        }
        public void LoadInkAndWwWlFromDicom(
    DicomDataset ds, InkCanvas inkCanvas,
    out double? ww, out double? wl, out string appStateJson)
        {
            ww = wl = null; appStateJson = null;

            if (ds.TryGetSingleValue(DicomTag.WindowWidth, out double wwVal)) ww = wwVal;
            if (ds.TryGetSingleValue(DicomTag.WindowCenter, out double wlVal)) wl = wlVal;

            // Private creator + our private tags
            var PrivateCreatorTag = new DicomTag(0x0011, 0x0010);
            var InkIsfTag = new DicomTag(0x0011, 0x1001); // ISF bytes
            var AppStateJsonTag = new DicomTag(0x0011, 0x1002); // optional JSON

            ds.AddOrUpdate(PrivateCreatorTag, "CTViewer"); // must come first

            using (var ms = new MemoryStream())
            {
                inkCanvas.Strokes.Save(ms);                     // ISF vector data
                ds.AddOrUpdate(new DicomOtherByte(InkIsfTag, ms.ToArray()));   // <-- OB VR
            }

            //if (!string.IsNullOrWhiteSpace(optionalAppStateJson))
            //{
            //    ds.AddOrUpdate(new DicomUnlimitedText(AppStateJsonTag, optionalAppStateJson)); // <-- UT VR
            //}
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
            if (_currentDicomFile == null)
            {
                MessageBox.Show("Open a DICOM first.");
                return;
            }
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "DICOM file (*.dcm)|*.dcm",
                FileName = MakeNextVersionName(_currentDicomPath)
            };
            if (dlg.ShowDialog() == true)
            {
                SaveWorkingDicomWithInk(
                    _currentDicomFile,    // the original you opened
                    getCurrentCurrentWW(), getCurrentCurrentWL(), // from your sliders
                    InkCanvas,            // current strokes
                    dlg.FileName,
                    optionalAppStateJson: null // optional: tool, zoom, etc.
                );
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
            return (double)WWSlider.Value; ;
        }

    }
}
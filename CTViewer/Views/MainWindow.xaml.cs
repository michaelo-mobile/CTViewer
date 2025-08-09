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
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;


namespace CTViewer.Views
{
    public partial class MainWindow : Window
    {

        public MainWindow()
        {
            InitializeComponent();
            FileButtons.FileOpened += FileButtons_FileOpened;

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
                MainImage.Source = bitmap;
                SetPatientInfo(dset);
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
        /// <param name="ds">DICOM dataset containing patient and study metadata.</param>
        /// <param name="top">If true, returns the Patient/ID/Clinic/Physician lines. 
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
    }
}
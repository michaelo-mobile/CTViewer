#nullable disable
using FellowOakDicom;
using FellowOakDicom.Imaging;
using Microsoft.Win32;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CTViewer.Tools
{
    public class ImageTool 
    {
        private System.Windows.Controls.Image _imageControl;
        private Canvas _overlayCanvas;

        public string Name => "DICOM Loader";

        public WriteableBitmap LoadedImage { get; private set; }

        public void Attach(System.Windows.Controls.Image image, Canvas overlay)
        {
            _imageControl = image;
            _overlayCanvas = overlay;

            OpenDicomImage(); // Prompt user when this tool is activated
        }

        public void Detach()
        {
            _imageControl = null;
            _overlayCanvas = null;
            LoadedImage = null;
        }

        private void OpenDicomImage()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "DICOM Files|*.dcm"
            };

            if (dialog.ShowDialog() != true)
                return;

            try
            {
                var dicomFile = DicomFile.Open(dialog.FileName);
                var dataset = dicomFile.Dataset;

                string patientName = dataset.GetSingleValueOrDefault(DicomTag.PatientName, "<unknown>");
                string patientID = dataset.GetSingleValueOrDefault(DicomTag.PatientID, "<unknown>");
                string clinicName = dataset.GetSingleValueOrDefault(DicomTag.InstitutionName, "<unknown>");
                string physicianName = dataset.GetSingleValueOrDefault(DicomTag.PerformingPhysicianName, "<unknown>");
                string studyDate = dataset.GetSingleValueOrDefault(DicomTag.StudyDate, "<unknown>");
                string modality = dataset.GetSingleValueOrDefault(DicomTag.Modality, "<unknown>");
                string studyDescription = dataset.GetSingleValueOrDefault(DicomTag.StudyDescription, "<unknown>");
                string seriesDescription = dataset.GetSingleValueOrDefault(DicomTag.SeriesDescription, "<unknown>");
                string imageLaterality = dataset.GetSingleValueOrDefault(DicomTag.ImageLaterality, "<unknown>");
                string bodyPart = dataset.GetSingleValueOrDefault(DicomTag.BodyPartExamined, "<unknown>");
                int imageIndex = dataset.GetSingleValueOrDefault(DicomTag.InstanceNumber, 0);
                int totalImages = dataset.GetSingleValueOrDefault(DicomTag.ImagesInAcquisition, 0);
                string rawTime = dataset.GetSingleValueOrDefault(DicomTag.StudyTime, "");
                string formattedTime = "<unknown>";

                if (!string.IsNullOrEmpty(rawTime) && rawTime.Length >= 4)
                {
                    try
                    {
                        // Try to parse using format: "HHmm" (24-hour time)
                        DateTime parsedTime = DateTime.ParseExact(rawTime.Substring(0, 4), "HHmm", null);
                        formattedTime = parsedTime.ToString("h:mm tt"); // → e.g., "12:00 PM"
                    }
                    catch (FormatException)
                    {
                        // fallback if parsing fails
                        formattedTime = "<invalid time>";
                    }
                }

                System.Diagnostics.Debug.WriteLine("Formatted Study Time: " + formattedTime);

                // For debugging / verification
                System.Diagnostics.Debug.WriteLine($"Patient: {patientName} ({patientID})");
                System.Diagnostics.Debug.WriteLine($"Clinic: {clinicName} | Physician: {physicianName}");
                System.Diagnostics.Debug.WriteLine($"Image: {imageIndex}/{totalImages} | Body: {bodyPart} ({imageLaterality})");

                var pixelData = DicomPixelData.Create(dataset);
                int width = pixelData.Width;
                int height = pixelData.Height;
                var buffer = pixelData.GetFrame(0);
                byte[] bytes = buffer.Data;

                double intercept = dataset.GetSingleValueOrDefault(DicomTag.RescaleIntercept, 0.0);
                double slope = dataset.GetSingleValueOrDefault(DicomTag.RescaleSlope, 1.0);
                double windowCenter = dataset.GetSingleValueOrDefault(DicomTag.WindowCenter, 40.0);
                double windowWidth = dataset.GetSingleValueOrDefault(DicomTag.WindowWidth, 400.0);

                byte[] normalized = new byte[width * height];
                for (int i = 0; i < width * height; i++)
                {
                    int offset = i * 2;
                    ushort raw = BitConverter.ToUInt16(bytes, offset);
                    int hu = (int)(slope * raw + intercept);

                    double min = windowCenter - windowWidth / 2.0;
                    double max = windowCenter + windowWidth / 2.0;
                    double scaled = (hu - min) * 255.0 / (max - min);
                    byte value = (byte)Math.Clamp(scaled, 0, 255);
                    value = (byte)(255 - value); // invert grayscale

                    normalized[i] = value;
                }

                int stride = width;
                LoadedImage = new WriteableBitmap(width, height, 96, 96, PixelFormats.Gray8, null);
                LoadedImage.WritePixels(new Int32Rect(0, 0, width, height), normalized, stride, 0);

                _imageControl.Source = LoadedImage;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Failed to load DICOM: " + ex.Message);
            }
        }
    }
}

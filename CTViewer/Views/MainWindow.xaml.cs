#nullable disable
using CTViewer.Tools;
using CTViewer.Views;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using Microsoft.Win32;
using System;
using System.Drawing;
using System.Drawing.Imaging;
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
        /// <summary>
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

    }
}
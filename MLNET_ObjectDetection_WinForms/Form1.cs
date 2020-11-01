using Microsoft.ML;
using Microsoft.ML.Transforms.Image;
using MLNET_ObjectDetection_WinForms.Models;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MLNET_ObjectDetection_WinForms
{
    public partial class Form1 : Form
    {
        public const int rowCount = 13, columnCount = 13;

        public const int featuresPerBox = 5;

        private static readonly (float x, float y)[] boxAnchors = { (0.573f, 0.677f), (1.87f, 2.06f), (3.34f, 5.47f), (7.88f, 3.53f), (9.77f, 9.17f) };

        private PredictionEngine<WineInput, WinePredictions> _predictionEngine;

        public Form1()
        {
            InitializeComponent();

            picPrediction.Visible = false;
            btnNewPrediction.Visible = false;

            var context = new MLContext();

            var emptyData = new List<WineInput>();

            var data = context.Data.LoadFromEnumerable(emptyData);

            var pipeline = context.Transforms.ResizeImages(resizing: ImageResizingEstimator.ResizingKind.Fill, outputColumnName: "data", imageWidth: ImageSettings.imageWidth, imageHeight: ImageSettings.imageHeight, inputColumnName: nameof(WineInput.Image))
                            .Append(context.Transforms.ExtractPixels(outputColumnName: "data"))
                            .Append(context.Transforms.ApplyOnnxModel(modelFile: "./MLModel/model.onnx", outputColumnName: "model_outputs0", inputColumnName: "data"));

            var model = pipeline.Fit(data);

            _predictionEngine = context.Model.CreatePredictionEngine<WineInput, WinePredictions>(model);
        }

        private void btnSelectImage_Click(object sender, EventArgs e)
        {
            if (fileDialog.ShowDialog() == DialogResult.OK)
            {
                var image = (Bitmap)Image.FromFile(fileDialog.FileName);

                var prediction = _predictionEngine.Predict(new WineInput { Image = image });

                var labels = File.ReadAllLines("./MLModel/labels.txt");

                var boundingBoxes = ParseOutputs(prediction.WineType, labels);

                var originalWidth = image.Width;
                var originalHeight = image.Height;

                if (boundingBoxes.Count > 1)
                {
                    var maxConfidence = boundingBoxes.Max(b => b.Confidence);
                    var topBoundingBox = boundingBoxes.FirstOrDefault(b => b.Confidence == maxConfidence);

                    boundingBoxes.Clear();

                    boundingBoxes.Add(topBoundingBox);
                }
                else
                {
                    MessageBox.Show("No prediction for image");
                    return;
                }

                foreach (var boundingBox in boundingBoxes)
                {
                    float x = Math.Max(boundingBox.Dimensions.X, 0);
                    float y = Math.Max(boundingBox.Dimensions.Y, 0);
                    float width = Math.Min(originalWidth - x, boundingBox.Dimensions.Width);
                    float height = Math.Min(originalHeight - y, boundingBox.Dimensions.Height);

                    // fit to current image size
                    x = originalWidth * x / ImageSettings.imageWidth;
                    y = originalHeight * y / ImageSettings.imageHeight;
                    width = originalWidth * width / ImageSettings.imageWidth;
                    height = originalHeight * height / ImageSettings.imageHeight;

                    using (var graphics = Graphics.FromImage(image))
                    {
                        graphics.DrawRectangle(new Pen(Color.Red, 3), x, y, width, height);
                        graphics.DrawString(boundingBox.Description, new Font(FontFamily.Families[0], 30f), Brushes.Red, x + 5, y + 5);
                    }
                }

                picPrediction.Image = image;
                picPrediction.SizeMode = PictureBoxSizeMode.AutoSize;
                picPrediction.Visible = true;
                btnSelectImage.Visible = false;
                btnNewPrediction.Visible = true;
            }
        }

        public static List<BoundingBox> ParseOutputs(float[] modelOutput, string[] labels, float probabilityThreshold = .5f)
        {
            var boxes = new List<BoundingBox>();

            for (int row = 0; row < rowCount; row++)
            {
                for (int column = 0; column < columnCount; column++)
                {
                    for (int box = 0; box < boxAnchors.Length; box++)
                    {
                        var channel = box * (labels.Length + featuresPerBox);

                        var boundingBoxPrediction = ExtractBoundingBoxPrediction(modelOutput, row, column, channel);

                        var mappedBoundingBox = MapBoundingBoxToCell(row, column, box, boundingBoxPrediction);

                        if (boundingBoxPrediction.Confidence < probabilityThreshold)
                            continue;

                        float[] classProbabilities = ExtractClassProbabilities(modelOutput, row, column, channel, boundingBoxPrediction.Confidence, labels);

                        var (topProbability, topIndex) = classProbabilities.Select((probability, index) => (Score: probability, Index: index)).Max();

                        if (topProbability < probabilityThreshold)
                            continue;

                        boxes.Add(new BoundingBox
                        {
                            Dimensions = mappedBoundingBox,
                            Confidence = topProbability,
                            Label = labels[topIndex]
                        });
                    }
                }
            }

            return boxes;
        }

        private static BoundingBoxDimensions MapBoundingBoxToCell(int row, int column, int box, BoundingBoxPrediction boxDimensions)
        {
            const float cellWidth = ImageSettings.imageWidth / columnCount;
            const float cellHeight = ImageSettings.imageHeight / rowCount;

            var mappedBox = new BoundingBoxDimensions
            {
                X = (row + Sigmoid(boxDimensions.X)) * cellWidth,
                Y = (column + Sigmoid(boxDimensions.Y)) * cellHeight,
                Width = (float)Math.Exp(boxDimensions.Width) * cellWidth * boxAnchors[box].x,
                Height = (float)Math.Exp(boxDimensions.Height) * cellHeight * boxAnchors[box].y,
            };

            // The x,y coordinates from the (mapped) bounding box prediction represent the center
            // of the bounding box. We adjust them here to represent the top left corner.
            mappedBox.X -= mappedBox.Width / 2;
            mappedBox.Y -= mappedBox.Height / 2;

            return mappedBox;
        }

        private static BoundingBoxPrediction ExtractBoundingBoxPrediction(float[] modelOutput, int row, int column, int channel)
        {
            return new BoundingBoxPrediction
            {
                X = modelOutput[GetOffset(row, column, channel++)],
                Y = modelOutput[GetOffset(row, column, channel++)],
                Width = modelOutput[GetOffset(row, column, channel++)],
                Height = modelOutput[GetOffset(row, column, channel++)],
                Confidence = Sigmoid(modelOutput[GetOffset(row, column, channel++)])
            };
        }

        public static float[] ExtractClassProbabilities(float[] modelOutput, int row, int column, int channel, float confidence, string[] labels)
        {
            var classProbabilitiesOffset = channel + featuresPerBox;
            float[] classProbabilities = new float[labels.Length];
            for (int classProbability = 0; classProbability < labels.Length; classProbability++)
                classProbabilities[classProbability] = modelOutput[GetOffset(row, column, classProbability + classProbabilitiesOffset)];
            return Softmax(classProbabilities).Select(p => p * confidence).ToArray();
        }

        private static float Sigmoid(float value)
        {
            var k = (float)Math.Exp(value);
            return k / (1.0f + k);
        }

        private static float[] Softmax(float[] classProbabilities)
        {
            var max = classProbabilities.Max();
            var exp = classProbabilities.Select(v => Math.Exp(v - max));
            var sum = exp.Sum();
            return exp.Select(v => (float)v / (float)sum).ToArray();
        }

        private void btnNewPrediction_Click(object sender, EventArgs e)
        {
            btnNewPrediction.Visible = false;
            picPrediction.Visible = false;
            btnSelectImage.Visible = true;
        }

        private static int GetOffset(int row, int column, int channel)
        {
            const int channelStride = rowCount * columnCount;
            return (channel * channelStride) + (column * columnCount) + row;
        }
    }

    class BoundingBoxPrediction : BoundingBoxDimensions
    {
        public float Confidence { get; set; }
    }
}

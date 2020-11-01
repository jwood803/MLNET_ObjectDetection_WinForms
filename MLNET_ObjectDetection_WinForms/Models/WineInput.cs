using Microsoft.ML.Transforms.Image;
using System.Drawing;

namespace MLNET_ObjectDetection_WinForms.Models
{
    public class WineInput
    {
        [ImageType(ImageSettings.imageHeight, ImageSettings.imageWidth)]
        public Bitmap Image { get; set; }
    }
}

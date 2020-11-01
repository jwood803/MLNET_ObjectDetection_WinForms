using Microsoft.ML.Data;

namespace MLNET_ObjectDetection_WinForms.Models
{
    public class WinePredictions
    {
        [ColumnName("model_outputs0")]
        public float[] WineType { get; set; }
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HDRGammaController
{
    /// <summary>
    /// The Gloam brand mark (S-curve disc) for window title bars. Set <see cref="GapBrush"/>
    /// to the colour of whatever sits behind it so the diagonal S-cut reads as transparent.
    /// </summary>
    public partial class BrandMark : UserControl
    {
        public BrandMark() => InitializeComponent();

        public static readonly DependencyProperty GapBrushProperty =
            DependencyProperty.Register(nameof(GapBrush), typeof(Brush), typeof(BrandMark),
                new PropertyMetadata(null));

        public Brush? GapBrush
        {
            get => (Brush?)GetValue(GapBrushProperty);
            set => SetValue(GapBrushProperty, value);
        }
    }
}

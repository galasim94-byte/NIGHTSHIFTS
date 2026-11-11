using Nightshift.Core.Color;
using Xunit;

namespace Nightshift.Core.Tests;

public class ColorTemperatureTests
{
    [Fact]
    public void NeutralTemperature_ReturnsExactNeutralWhite()
    {
        WhitePoint white = ColorTemperature.WhitePointFor(6500);

        Assert.True(white.IsNeutral);
        Assert.Equal(1.0, white.R, 12);
        Assert.Equal(1.0, white.G, 12);
        Assert.Equal(1.0, white.B, 12);
    }

    [Fact]
    public void CoolerThanNeutral_IsClampedToNeutral()
    {
        // Un écran sRGB ne peut pas être plus froid que son blanc : 8000 K = 6500 K.
        WhitePoint cool = ColorTemperature.WhitePointFor(8000);

        Assert.True(cool.IsNeutral);
    }

    [Theory]
    [InlineData(6000)]
    [InlineData(5000)]
    [InlineData(4500)]
    [InlineData(4000)]
    [InlineData(3500)]
    [InlineData(3000)]
    [InlineData(2700)]
    [InlineData(2200)]
    [InlineData(2000)]
    public void WarmWhite_KeepsRedMaxAndReducesBlue(int kelvin)
    {
        WhitePoint white = ColorTemperature.WhitePointFor(kelvin);

        Assert.Equal(1.0, white.R, 6);
        Assert.InRange(white.G, 0.05, 1.0);
        Assert.InRange(white.B, 0.0, 1.0);
        Assert.True(white.G >= white.B, $"{kelvin}K : le vert doit rester au-dessus du bleu");
    }

    [Fact]
    public void BlueChannel_DecreasesMonotonically_AsTemperatureDrops()
    {
        int[] sequence = { 6500, 5000, 4500, 4000, 3500, 3000, 2700, 2200, 2000 };
        double previous = double.MaxValue;

        foreach (int kelvin in sequence)
        {
            double blue = ColorTemperature.WhitePointFor(kelvin).B;
            Assert.True(blue <= previous + 1e-12, $"{kelvin}K : bleu non décroissant ({blue} > {previous})");
            previous = blue;
        }
    }

    [Fact]
    public void ExtremeWarmTemperature_StaysInGamut()
    {
        WhitePoint white = ColorTemperature.WhitePointFor(1000);

        Assert.InRange(white.R, 0.0, 1.0);
        Assert.InRange(white.G, 0.0, 1.0);
        Assert.InRange(white.B, 0.0, 1.0);
        Assert.True(white.R <= 1.0 + 1e-12);
    }
}

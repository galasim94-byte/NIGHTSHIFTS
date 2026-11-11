using Nightshift.Core.Color;
using Xunit;

namespace Nightshift.Core.Tests;

public sealed class EffectiveTemperatureTests
{
    [Fact]
    public void NeutralTableIsNeutral()
    {
        ushort[] neutral = RampBuilder.Neutral();

        Assert.Equal(ColorTemperature.NeutralKelvin, EffectiveTemperature.Estimate(neutral, neutral));
    }

    [Fact]
    public void NonIdentityOriginalTableStillReadsNeutral()
    {
        // Table neutre d'un pilote réel : i×257, pas i×256.
        var original = new ushort[RampBuilder.RampLength];
        for (int channel = 0; channel < 3; channel++)
        {
            for (int i = 0; i < 256; i++)
            {
                original[(channel * 256) + i] = (ushort)(i * 257);
            }
        }

        Assert.Equal(ColorTemperature.NeutralKelvin, EffectiveTemperature.Estimate(original, original));
    }

    [Theory]
    [InlineData(6500)]
    [InlineData(5000)]
    [InlineData(4000)]
    [InlineData(3500)]
    [InlineData(3000)]
    [InlineData(2700)]
    [InlineData(2200)]
    public void FullStrengthRecoversRequestedTemperature(int kelvin)
    {
        ushort[] neutral = RampBuilder.Neutral();
        ushort[] applied = RampBuilder.Build(kelvin, 100.0);

        int estimated = EffectiveTemperature.Estimate(applied, neutral);

        Assert.InRange(estimated, kelvin - 30, kelvin + 30);
    }

    [Fact]
    public void ZeroStrengthIsNeutral()
    {
        ushort[] neutral = RampBuilder.Neutral();
        ushort[] blend = RampBuilder.Blend(neutral, RampBuilder.Build(2700), 0.0);

        Assert.Equal(ColorTemperature.NeutralKelvin, EffectiveTemperature.Estimate(blend, neutral));
    }

    [Fact]
    public void ReducedStrengthRaisesEffectiveTemperature()
    {
        ushort[] neutral = RampBuilder.Neutral();
        ushort[] target = RampBuilder.Build(2700, 100.0);

        int full = EffectiveTemperature.Estimate(target, neutral);
        int reduced = EffectiveTemperature.Estimate(RampBuilder.Blend(neutral, target, 0.7), neutral);

        Assert.Equal(2700, full);
        Assert.True(reduced > full + 200, $"attendu nettement plus froid que {full}, obtenu {reduced}");
        Assert.True(reduced < ColorTemperature.NeutralKelvin);
    }

    [Fact]
    public void StrengthIsMonotonicInEffectiveTemperature()
    {
        ushort[] neutral = RampBuilder.Neutral();
        ushort[] target = RampBuilder.Build(2200, 100.0);

        int previous = EffectiveTemperature.Estimate(RampBuilder.Blend(neutral, target, 0.6), neutral);

        for (double strength = 0.7; strength <= 1.0; strength += 0.1)
        {
            int current = EffectiveTemperature.Estimate(RampBuilder.Blend(neutral, target, strength), neutral);
            Assert.True(current <= previous, $"intensité {strength:0.0} : {current} K > {previous} K");
            previous = current;
        }
    }

    [Fact]
    public void RejectsWrongLength()
    {
        // 768 = longueur d'une table valide (3 × 256) : on teste bien un écart.
        Assert.Equal(768, RampBuilder.RampLength);

        Assert.Throws<ArgumentException>(() =>
            EffectiveTemperature.Estimate(new ushort[767], RampBuilder.Neutral()));

        Assert.Throws<ArgumentException>(() =>
            EffectiveTemperature.Estimate(RampBuilder.Neutral(), new ushort[769]));
    }
}

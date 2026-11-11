using Nightshift.Core.Color;
using Nightshift.Core.Transitions;
using Xunit;

namespace Nightshift.Core.Tests;

public class RampBuilderTests
{
    [Fact]
    public void NeutralRamp_IsDriverStyleIdentity()
    {
        ushort[] ramp = RampBuilder.Neutral();

        Assert.Equal(RampBuilder.RampLength, ramp.Length);
        Assert.Equal(0, ramp[0]);
        Assert.Equal(32896, ramp[128]); // 128 × 257, échelle mesurée sur pilote réel
        Assert.Equal(65535, ramp[255]);
        Assert.True(RampMath.IsNeutral(ramp));
    }

    [Theory]
    [InlineData(6500)]
    [InlineData(4000)]
    [InlineData(2700)]
    [InlineData(2000)]
    public void Ramp_IsAlwaysMonotonic(int kelvin)
    {
        Assert.True(RampMath.IsMonotonic(RampBuilder.Build(kelvin)));
    }

    [Fact]
    public void WarmRamp_ReducesBlueMaximum()
    {
        ushort[] warm = RampBuilder.Build(2700);

        Assert.Equal(65535, warm[255]);          // rouge non atténué
        Assert.True(warm[767] < 30000);          // bleu nettement réduit (canal B = 512..767)
        Assert.False(RampMath.IsNeutral(warm));
    }

    [Fact]
    public void Blend_FullStrength_ReturnsTarget_AndZero_ReturnsNeutral()
    {
        ushort[] neutral = RampBuilder.Neutral();
        ushort[] target = RampBuilder.Build(2700);

        Assert.Equal(0.0, RampMath.MaxDelta(RampBuilder.Blend(neutral, target, 1.0), target));
        Assert.Equal(0.0, RampMath.MaxDelta(RampBuilder.Blend(neutral, target, 0.0), neutral));

        ushort[] half = RampBuilder.Blend(neutral, target, 0.5);
        Assert.InRange(half[767], target[767], neutral[767]);
        Assert.True(RampMath.IsMonotonic(half));
    }

    [Fact]
    public void BrightnessPercent_ScalesAllChannelsEqually()
    {
        ushort[] full = RampBuilder.Build(4000, 100.0);
        ushort[] half = RampBuilder.Build(4000, 50.0);

        for (int i = 0; i < 256; i++)
        {
            Assert.True(half[i] <= full[i]);
            Assert.True(half[256 + i] <= full[256 + i]);
            Assert.True(half[512 + i] <= full[512 + i]);
        }

        Assert.True(half[255] < full[255] / 2 + 1000);
    }
}

public class RampMathTests
{
    [Fact]
    public void MaxDelta_DetectsDifferences()
    {
        ushort[] a = RampBuilder.Neutral();
        ushort[] b = (ushort[])a.Clone();
        b[10] += 42;

        Assert.Equal(0.0, RampMath.MaxDelta(a, a));
        Assert.Equal(42.0, RampMath.MaxDelta(a, b));
        Assert.True(RampMath.IsCloseTo(a, a));
        Assert.False(RampMath.IsCloseTo(a, b));
        Assert.True(RampMath.IsCloseTo(a, b, 100));
    }

    [Fact]
    public void IsNeutral_RejectsWarmRamp()
    {
        Assert.True(RampMath.IsNeutral(RampBuilder.Neutral()));
        Assert.False(RampMath.IsNeutral(RampBuilder.Build(3200)));
    }
}

public class TransitionEngineTests
{
    [Fact]
    public void Transition_InterpolatesDeterministically_AndFinishesExactly()
    {
        double now = 0.0;
        var engine = new TransitionEngine(() => now);
        engine.Easing = Easing.Linear;

        engine.Start(6500, 2700, 10.0);
        Assert.True(engine.IsActive);

        int atStart = engine.Sample(out bool finishedAtStart);
        Assert.Equal(6500, atStart);
        Assert.False(finishedAtStart);

        now = 5.0;
        int atMid = engine.Sample(out _);
        Assert.InRange(atMid, 4000, 5000);

        now = 10.0;
        int atEnd = engine.Sample(out bool finished);
        Assert.Equal(2700, atEnd);
        Assert.True(finished);
        Assert.False(engine.IsActive);
    }

    [Fact]
    public void Transition_NeverOvershoots_AndIsMonotonic()
    {
        double now = 0.0;
        var engine = new TransitionEngine(() => now);

        engine.Start(6500, 2200, 4.0);

        int previous = 6500;
        for (int step = 0; step <= 40; step++)
        {
            now = step * 0.1;
            int value = engine.Sample(out _);
            Assert.InRange(value, 2200, 6500);
            Assert.True(value <= previous, "la transition doit être monotone décroissante");
            previous = value;
        }

        Assert.Equal(2200, previous);
    }

    [Fact]
    public void ZeroDuration_AppliesImmediately()
    {
        var engine = new TransitionEngine(() => 0.0);

        engine.Start(6500, 3000, 0.0);
        int value = engine.Sample(out bool finished);

        Assert.Equal(3000, value);
        Assert.True(finished);
        Assert.False(engine.IsActive);
    }

    [Fact]
    public void IdenticalTargets_DoNotStartTransition()
    {
        var engine = new TransitionEngine(() => 0.0);

        engine.Start(4000, 4000, 5.0);
        int value = engine.Sample(out bool finished);

        Assert.Equal(4000, value);
        Assert.True(finished);
        Assert.False(engine.IsActive);
    }

    [Fact]
    public void EasingFunctions_HaveCorrectEndpointBehaviour()
    {
        foreach (Easing easing in Enum.GetValues<Easing>())
        {
            Assert.Equal(0.0, EasingFunctions.Apply(easing, 0.0), 12);
            Assert.Equal(1.0, EasingFunctions.Apply(easing, 1.0), 12);
            Assert.Equal(0.5, EasingFunctions.Apply(easing, 0.5), 12);
        }

        // Smootherstep : dérivée nulle aux extrémités => démarrage sans à-coup.
        double nearStart = EasingFunctions.Apply(Easing.SmootherStep, 0.01);
        Assert.True(nearStart < 0.01);
    }
}

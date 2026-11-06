namespace KaraokeToMp4;

/// <summary>Progress report raised while a conversion is running.</summary>
/// <param name="Stage">What the converter is currently doing.</param>
/// <param name="Position">Media time rendered so far.</param>
/// <param name="Duration">Total media duration.</param>
public readonly record struct ConversionProgress(ConversionStage Stage, TimeSpan Position, TimeSpan Duration)
{
    /// <summary>0..1 fraction of the current stage that is complete.</summary>
    public double Fraction => Duration <= TimeSpan.Zero ? 0 : Math.Clamp(Position / Duration, 0, 1);
}

/// <summary>Conversion stages reported through <see cref="ConversionProgress"/>.</summary>
public enum ConversionStage
{
    Preparing,
    SynthesizingAudio,
    EncodingVideo,
    Completed,
}

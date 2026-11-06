namespace KaraokeToMp4;

/// <summary>Raised when an input can't be converted or the encoder fails.</summary>
public class KaraokeConversionException : Exception
{
    public KaraokeConversionException(string message) : base(message) { }
    public KaraokeConversionException(string message, Exception inner) : base(message, inner) { }
}

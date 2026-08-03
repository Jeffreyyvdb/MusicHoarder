namespace MusicHoarder.Api.Audio;

/// <summary>
/// Injectable seam over the static <see cref="ChromaprintComparer"/> so duplicate detection can be
/// tested with synthetic fingerprints (the EF-InMemory tests can't produce real Chromaprint data).
/// </summary>
public interface IFingerprintSimilarityGate
{
    bool TryDecode(string? compressed, out uint[] frames);
    double Similarity(uint[] a, uint[] b);
}

public sealed class FingerprintSimilarityGate : IFingerprintSimilarityGate
{
    public bool TryDecode(string? compressed, out uint[] frames)
        => ChromaprintComparer.TryDecode(compressed, out frames);

    public double Similarity(uint[] a, uint[] b)
        => ChromaprintComparer.Similarity(a, b);
}

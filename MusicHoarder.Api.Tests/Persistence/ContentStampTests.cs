using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Persistence;

/// <summary>
/// A stamp stands in for the values it was fed: equal stamps are taken to mean equal inputs, and a
/// remembered result is handed out on that alone. So what matters is that different inputs cannot
/// reach the same bytes.
/// </summary>
public class ContentStampTests
{
    [Fact]
    public void TheSameValuesInTheSameOrder_GiveTheSameStamp()
    {
        Assert.Equal(Stamp(s => { s.Add("Kanye West"); s.Add(7); }), Stamp(s => { s.Add("Kanye West"); s.Add(7); }));
        Assert.NotEqual(Stamp(s => { s.Add("Kanye West"); s.Add(7); }), Stamp(s => { s.Add(7); s.Add("Kanye West"); }));
    }

    [Fact]
    public void Null_IsNotTheEmptyString_AndNeitherIsNothing()
    {
        var stamps = new[] { Stamp(s => s.Add((string?)null)), Stamp(s => s.Add("")), Stamp(_ => { }) };

        Assert.Equal(3, stamps.Select(Convert.ToHexString).Distinct().Count());
    }

    [Fact]
    public void NeighbouringValues_DoNotRunTogether()
    {
        Assert.NotEqual(Stamp(s => { s.Add("ab"); s.Add("c"); }), Stamp(s => { s.Add("a"); s.Add("bc"); }));
        Assert.NotEqual(Stamp(s => { s.Add("a"); s.Add(""); }), Stamp(s => { s.Add(""); s.Add("a"); }));
    }

    [Fact]
    public void EveryKindOfValue_Counts()
    {
        var id = Guid.NewGuid();
        Action<ContentStamp>[] pairs =
        [
            s => s.Add(true), s => s.Add(false),
            s => s.Add(1), s => s.Add(2),
            s => s.Add(1L), s => s.Add(2L),
            s => s.Add(90.0), s => s.Add(90.5),
            s => s.Add(id), s => s.Add(Guid.Empty),
        ];

        Assert.Equal(pairs.Length, pairs.Select(feed => Convert.ToHexString(Stamp(feed))).Distinct().Count());
    }

    // Values are gathered into a block before they are hashed. Where a value falls against the edge
    // of a block must not show: the same values give the same stamp whether they are fed across
    // many blocks or sit in one, and a change in any block changes it.
    [Fact]
    public void TextLongerThanABlock_AndValuesAcrossBlocks_AreAllCovered()
    {
        var titles = Enumerable.Range(0, 4000).Select(i => $"Track {i}").ToArray();
        var essay = new string('x', 50_000);
        byte[] Feed(string[] values, string text) => Stamp(s =>
        {
            foreach (var value in values)
                s.Add(value);
            s.Add(text);
            s.Add(values.Length);
        });

        var whole = Feed(titles, essay);

        Assert.Equal(whole, Feed([.. titles], new string('x', 50_000)));
        Assert.NotEqual(whole, Feed([.. titles.Select((t, i) => i == 0 ? "Track zero" : t)], essay));
        Assert.NotEqual(whole, Feed([.. titles.Select((t, i) => i == 2500 ? "Track 2500 " : t)], essay));
        Assert.NotEqual(whole, Feed(titles, essay[..^1] + "y"));
    }

    [Fact]
    public void Finish_StartsOver()
    {
        using var stamp = new ContentStamp();
        stamp.Add("first");
        var first = stamp.Finish();
        stamp.Add("first");

        Assert.Equal(first, stamp.Finish());
        Assert.Equal(32, first.Length);
    }

    private static byte[] Stamp(Action<ContentStamp> feed)
    {
        using var stamp = new ContentStamp();
        feed(stamp);
        return stamp.Finish();
    }
}

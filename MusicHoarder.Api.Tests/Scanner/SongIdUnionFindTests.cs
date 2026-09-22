using MusicHoarder.Api.Scanner;

namespace MusicHoarder.Api.Tests.Scanner;

public class SongIdUnionFindTests
{
    [Fact]
    public void Find_RegistersAnUnknownId_AsItsOwnRoot()
    {
        var uf = new SongIdUnionFind();

        Assert.Equal(42, uf.Find(42));
        var cluster = Assert.Single(uf.Clusters());
        Assert.Equal([42], cluster);
    }

    [Fact]
    public void Union_MakesTheLowerRootTheRoot_WhicheverOrderTheIdsArrive()
    {
        var a = new SongIdUnionFind();
        a.Union(3, 9);
        Assert.Equal(3, a.Find(9));
        Assert.Equal(3, a.Find(3));

        var b = new SongIdUnionFind();
        b.Union(9, 3);
        Assert.Equal(3, b.Find(9));
    }

    [Fact]
    public void Union_IsTransitive_AndTheRootIsAlwaysTheLowestIdOfTheSet()
    {
        var uf = new SongIdUnionFind();
        uf.Union(20, 30);
        uf.Union(30, 40);
        uf.Union(5, 40);   // a lower id joining late still becomes the root

        Assert.All(new[] { 5, 20, 30, 40 }, id => Assert.Equal(5, uf.Find(id)));
    }

    [Fact]
    public void Union_OfIdsAlreadyInOneSet_IsANoOp()
    {
        var uf = new SongIdUnionFind();
        uf.Union(1, 2);
        uf.Union(2, 1);
        uf.Union(1, 1);

        var cluster = Assert.Single(uf.Clusters());
        Assert.Equal([1, 2], cluster.Order());
    }

    [Fact]
    public void Clusters_GroupEveryRegisteredId_SingletonsIncluded()
    {
        var uf = new SongIdUnionFind();
        uf.Union(1, 2);
        uf.Union(7, 8);
        uf.Find(5);

        var clusters = uf.Clusters().Select(c => c.Order().ToList()).OrderBy(c => c[0]).ToList();

        Assert.Equal(3, clusters.Count);
        Assert.Equal([1, 2], clusters[0]);
        Assert.Equal([5], clusters[1]);
        Assert.Equal([7, 8], clusters[2]);
    }

    [Fact]
    public void Clusters_MergeTwoExistingSets_WhenALaterLinkBridgesThem()
    {
        var uf = new SongIdUnionFind();
        uf.Union(1, 2);
        uf.Union(7, 8);
        uf.Union(2, 7);

        var cluster = Assert.Single(uf.Clusters());
        Assert.Equal([1, 2, 7, 8], cluster.Order());
        Assert.Equal(1, uf.Find(8));
    }
}

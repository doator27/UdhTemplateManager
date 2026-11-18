using HardwareTemplateBuilder.Core.Services.Import;

namespace HardwareTemplateBuilder.Tests;

/// <summary>Similar callouts and callouts already on the job, in Parse PDF into Bulk Add.</summary>
public class PdfImportSimilarityTests
{
    private static HardwareImportGroup G(string code, string catalog, ImportMatchStatus status = ImportMatchStatus.NeedsReview, int qty = 1) =>
        new()
        {
            ManufacturerCode = code,
            Catalog = catalog,
            SourceKey = ImportText.Key(catalog),
            Status = status,
            Lines = { new ParsedHardwareLine { ManufacturerCode = code, Catalog = catalog, Quantity = qty, SetName = "1" } },
        };

    [Fact]
    public void FindsSimilarCallouts()
    {
        var hinges = new[] { G("S701", "TE 640 3D A8 038"), G("S701", "TE 640 3D A8 144"), G("S701", "TE 640 3D A8 174") };
        var closers = new[] { G("LC", "4040XP RWPA TBSRT"), G("LC", "4040XP CUSH TBSRT") };
        var others = new[] { G("AC01", "9145ML.212"), G("AC01", "9525ML.212"), G("IV", "5BB1 4 1/2 x 4 1/2"), G("LC", "5BB1 4 1/2 x 4 1/2") };
        var all = hinges.Concat(closers).Concat(others).ToList();

        var clusters = HardwareImportSimilarity.FindClusters(all);

        Assert.Equal(2, clusters.Count);
        Assert.Equal(hinges, clusters[0].Groups);
        Assert.Equal(closers, clusters[1].Groups);
        Assert.Equal(3, clusters[0].Unsorted.Count);
        Assert.False(clusters[0].IsDone);
    }

    [Fact]
    public void ClustersNeedSomethingToReview()
    {
        var auto = new[] { G("LC", "4040XP RWPA", ImportMatchStatus.Matched), G("LC", "4040XP CUSH", ImportMatchStatus.Matched) };
        Assert.Empty(HardwareImportSimilarity.FindClusters(auto));
    }

    [Fact]
    public void SortingSimilarCalloutsIntoItems()
    {
        var a = G("S701", "TE 640 3D A8 038", qty: 6);
        var b = G("S701", "TE 640 3D A8 144", qty: 60);
        var c = G("S701", "TE 640 3D A8 174", qty: 12);
        var d = G("S701", "TE 640 3D A8 200", qty: 2);
        var cluster = HardwareImportSimilarity.FindClusters(new[] { a, b, c, d }).Single();

        // a + b are the same item; c is left out; d is the last one, so the page is done.
        cluster.AddItem(new[] { a, b });
        Assert.False(cluster.IsDone);
        cluster.Leave(new[] { c });
        Assert.True(cluster.IsDone);
        Assert.Equal(new[] { d }, cluster.Unsorted);
        HardwareImportSimilarity.Apply(cluster);
        Assert.True(c.LeftOutAsSimilar && c.Skip);
        Assert.Same(a, b.MergedInto);
        Assert.Equal(66, a.TotalQuantity);
        Assert.False(HardwareImportSession.InPlay(b));
        Assert.True(HardwareImportSession.InPlay(a));
        Assert.True(HardwareImportSession.InPlay(d));

        // Undo the left-out one and make each remaining callout its own item.
        cluster.Undo(c);
        cluster.AddEach(new[] { c, d });
        HardwareImportSimilarity.Apply(cluster);
        Assert.Empty(cluster.Unsorted);
        Assert.False(c.Skip);
        Assert.True(HardwareImportSession.InPlay(c));
        Assert.Null(d.MergedInto);

        // Combined callouts take the answer of the one they were combined into (and their own label row).
        a.HardwareItemId = 42;
        a.Label = "TE640";
        a.LabelChoices.Add(new ImportLabelChoice { Source = b, Text = "TE640 144", DefaultText = "TE 640 3D A8 144" });
        HardwareImportSimilarity.CopyCombinedAnswers(new[] { a, b, c, d });
        Assert.Equal(42, b.HardwareItemId);
        Assert.Equal("TE640 144", b.Label);

        // Start over clears everything.
        cluster.Reset();
        HardwareImportSimilarity.Apply(cluster);
        Assert.Equal(4, cluster.Unsorted.Count);
        Assert.Empty(a.MergedGroups);
    }

    [Fact]
    public void LabelLearningRemovesWordsAndAddsText()
    {
        var data = new LabelLearningData();
        var key = HardwareImportLabelLearning.ItemKey(3, "TE640");
        for (int i = 0; i < 5; i++)
            HardwareImportLabelLearning.Observe(data, "TE640 4.5 X 4.5 BAA", "HINGE TE640 4.5 X 4.5", key);

        Assert.Equal(5, data.Words["BAA"].Removed);
        Assert.Equal("HINGE TE640 4.5 X 4.5", HardwareImportLabelLearning.Apply(data, "TE640 4.5 X 4.5 BAA", key, threshold: 5));
        // Below the threshold nothing changes; other items don't get this item's prefix.
        Assert.Equal("TE640 BAA", HardwareImportLabelLearning.Apply(data, "TE640 BAA", key, threshold: 6));
        Assert.Equal("4040XP", HardwareImportLabelLearning.Apply(data, "4040XP BAA", HardwareImportLabelLearning.ItemKey(3, "4040XP"), threshold: 5));
        // Unchanged labels teach nothing.
        HardwareImportLabelLearning.Observe(data, "QQQ", "QQQ", key);
        Assert.False(data.Words.ContainsKey("QQQ"));
    }

    [Fact]
    public void MarksCalloutsAlreadyOnTheJob()
    {
        var items = new Dictionary<int, ImportItemInfo>
        {
            [1] = new(1, 10, 100, "4040XP"),
            [2] = new(2, 20, 200, "5BB1"),
        };
        ImportItemInfo? Item(int? id) => id is int i && items.TryGetValue(i, out var x) ? x : null;

        var resolved = G("LC", "4040XP RWPA");
        resolved.ManufacturerId = 10;
        resolved.HardwareItemId = 1;
        resolved.Label = "4040XP RWPA";
        var byLabel = G("IV", "5BB1 4 1/2 x 4 1/2 NRP");
        byLabel.ManufacturerId = 20;
        var byModel = G("IV", "5BB1");
        byModel.ManufacturerId = 20;
        var otherMfr = G("XX", "5BB1");
        otherMfr.ManufacturerId = 99;
        var fresh = G("LC", "4111 EDA");
        fresh.ManufacturerId = 10;

        var existing = new List<ExistingJobLine>
        {
            new(1, 10, "4040XP", "4040xp rwpa ", "on the job"),
            new(null, 20, "5BB1", "5BB1 4-1/2 x 4-1/2 NRP", "in Bulk Add"),
            new(2, 20, "5BB1", "", "on the job"),
        };
        var groups = new[] { resolved, byLabel, byModel, otherMfr, fresh };
        HardwareImportSession.MarkAlreadyAdded(groups, existing, Item);

        Assert.Equal("Already on the job", resolved.AlreadyAddedReason);
        Assert.Equal("Already in Bulk Add", byLabel.AlreadyAddedReason);
        Assert.Equal("Already on the job", byModel.AlreadyAddedReason);
        Assert.Null(otherMfr.AlreadyAddedReason);
        Assert.Null(fresh.AlreadyAddedReason);
        Assert.Empty(HardwareImportSimilarity.FindClusters(new[] { resolved, G("LC", "4040XP CUSH") }));
    }
}

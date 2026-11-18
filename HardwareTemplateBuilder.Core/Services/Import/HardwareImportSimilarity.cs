using System;
using System.Collections.Generic;
using System.Linq;

namespace HardwareTemplateBuilder.Core.Services.Import;

/// <summary>
/// A set of callouts from one PDF that look like variations of the same product
/// ("TE 640 3D A8 038" / "… 144" / "… 174", "4040XP RWPA" / "4040XP CUSH"). On its wizard page
/// the user sorts them: ticks the ones that are the same item and groups them (repeatedly),
/// makes some their own item, or leaves some out. The page is done when at most one callout is
/// still unsorted (it becomes its own item).
/// </summary>
public sealed class SimilarCluster
{
    /// <summary>Creates a cluster with nothing sorted yet.</summary>
    /// <param name="groups">The callouts, in PDF order.</param>
    public SimilarCluster(IEnumerable<HardwareImportGroup> groups) => Groups = groups.ToList();

    /// <summary>Gets the callouts, in PDF order.</summary>
    public List<HardwareImportGroup> Groups { get; }

    /// <summary>Gets the groups made so far: each is one item (a single callout = its own item).</summary>
    public List<List<HardwareImportGroup>> Items { get; } = new();

    /// <summary>Gets the callouts left out of this job.</summary>
    public HashSet<HardwareImportGroup> LeftOut { get; } = new();

    /// <summary>The callouts not sorted yet, in PDF order.</summary>
    public List<HardwareImportGroup> Unsorted =>
        Groups.Where(g => !LeftOut.Contains(g) && !Items.Any(i => i.Contains(g))).ToList();

    /// <summary>True when the page can be left: at most one callout is unsorted.</summary>
    public bool IsDone => Unsorted.Count <= 1;

    /// <summary>Makes the given callouts one item (they are the same item).</summary>
    /// <param name="sameItem">The callouts.</param>
    public void AddItem(IEnumerable<HardwareImportGroup> sameItem)
    {
        var list = Groups.Where(sameItem.Contains).Where(g => Unsorted.Contains(g)).ToList();
        if (list.Count > 0) Items.Add(list);
    }

    /// <summary>Makes each of the given callouts its own item.</summary>
    /// <param name="each">The callouts.</param>
    public void AddEach(IEnumerable<HardwareImportGroup> each)
    {
        foreach (var g in Groups.Where(each.Contains).Where(g => Unsorted.Contains(g)).ToList()) Items.Add(new List<HardwareImportGroup> { g });
    }

    /// <summary>Leaves the given callouts out of this job.</summary>
    /// <param name="callouts">The callouts.</param>
    public void Leave(IEnumerable<HardwareImportGroup> callouts)
    {
        foreach (var g in Groups.Where(callouts.Contains).Where(g => Unsorted.Contains(g)).ToList()) LeftOut.Add(g);
    }

    /// <summary>Puts a made item (or a left-out callout) back among the unsorted ones.</summary>
    /// <param name="g">Any callout of the item.</param>
    public void Undo(HardwareImportGroup g)
    {
        LeftOut.Remove(g);
        Items.RemoveAll(i => i.Contains(g));
    }

    /// <summary>Starts the page over.</summary>
    public void Reset()
    {
        Items.Clear();
        LeftOut.Clear();
    }
}

/// <summary>Finds callouts that look like variations of the same product.</summary>
public static class HardwareImportSimilarity
{
    /// <summary>
    /// True when two callouts of the same manufacturer look alike: the same callout once sizes
    /// and notes are taken off, the same model word (3+ characters with a digit, e.g. "4040XP"),
    /// or a long shared beginning (at least 5 characters and 60% of the shorter one).
    /// </summary>
    /// <param name="a">One callout.</param>
    /// <param name="b">The other.</param>
    public static bool AreSimilar(HardwareImportGroup a, HardwareImportGroup b)
    {
        if (a.ManufacturerId != null && b.ManufacturerId != null) { if (a.ManufacturerId != b.ManufacturerId) return false; }
        else if (!string.Equals(a.ManufacturerCode, b.ManufacturerCode, StringComparison.OrdinalIgnoreCase)) return false;

        var ka = ImportText.Key(ImportText.StripNotesAndSize(a.Catalog));
        var kb = ImportText.Key(ImportText.StripNotesAndSize(b.Catalog));
        if (ka.Length == 0 || kb.Length == 0) return false;
        if (ka == kb) return true;

        var ta = ModelWord(a.Catalog);
        if (ta.Length >= 3 && ta == ModelWord(b.Catalog)) return true;

        int common = 0;
        while (common < ka.Length && common < kb.Length && ka[common] == kb[common]) common++;
        return common >= 5 && common >= 0.6 * Math.Min(ka.Length, kb.Length);
    }

    /// <summary>
    /// Groups the callouts into clusters of similar ones (2 or more). Callouts already on the job
    /// and learned skips are left out; a cluster is only returned when at least one of its
    /// callouts needs the user anyway (all-automatic clusters are left alone).
    /// </summary>
    /// <param name="groups">All callouts, in PDF order.</param>
    public static List<SimilarCluster> FindClusters(IReadOnlyList<HardwareImportGroup> groups)
    {
        var candidates = groups.Where(g => !g.IsAlreadyAdded && g.Status != ImportMatchStatus.Skipped).ToList();
        var parent = Enumerable.Range(0, candidates.Count).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);

        for (int i = 0; i < candidates.Count; i++)
            for (int j = i + 1; j < candidates.Count; j++)
                if (AreSimilar(candidates[i], candidates[j]))
                    parent[Find(j)] = Find(i);

        return candidates
            .Select((g, i) => (g, root: Find(i)))
            .GroupBy(x => x.root)
            .Select(x => x.Select(y => y.g).ToList())
            .Where(list => list.Count >= 2 && list.Any(g => g.Status == ImportMatchStatus.NeedsReview))
            .OrderBy(list => groups.ToList().IndexOf(list[0]))
            .Select(list => new SimilarCluster(list))
            .ToList();
    }

    /// <summary>
    /// Applies a cluster's page: each multi-callout item is combined into its first callout (an
    /// automatically identified one when there is one); left-out callouts are skipped for this job;
    /// unsorted and single callouts stay their own items. Can be applied again after a change.
    /// </summary>
    /// <param name="cluster">The cluster.</param>
    public static void Apply(SimilarCluster cluster)
    {
        foreach (var g in cluster.Groups)
        {
            g.MergedInto = null;
            g.MergedGroups.Clear();
            if (g.LeftOutAsSimilar)
            {
                g.LeftOutAsSimilar = false;
                g.Skip = false;
            }
        }
        foreach (var g in cluster.LeftOut)
        {
            g.LeftOutAsSimilar = true;
            g.Skip = true;
        }
        foreach (var item in cluster.Items.Where(i => i.Count > 1))
        {
            var primary = item.FirstOrDefault(g => g.Status == ImportMatchStatus.Matched) ?? item[0];
            foreach (var g in item.Where(g => g != primary))
            {
                g.MergedInto = primary;
                primary.MergedGroups.Add(g);
            }
        }
    }

    /// <summary>Copies each combined callout's answer from the callout it was combined into.</summary>
    /// <param name="groups">All callouts.</param>
    public static void CopyCombinedAnswers(IEnumerable<HardwareImportGroup> groups)
    {
        foreach (var g in groups)
        {
            if (g.MergedInto is not { } p) continue;
            g.Skip = p.Skip;
            g.CreateNew = p.CreateNew;
            g.HardwareItemId = p.HardwareItemId;
            var row = p.LabelChoices.FirstOrDefault(r => r.Source == g);
            g.Label = row != null ? row.Text : p.Label;
            g.NewManufacturerId = p.NewManufacturerId;
            g.NewDescriptionId = p.NewDescriptionId;
            g.NewModelNumber = p.NewModelNumber;
        }
    }

    private static string ModelWord(string catalog)
    {
        var first = ImportText.StripNotesAndSize(catalog).Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        var key = ImportText.Key(first);
        return key.Any(char.IsDigit) ? key : string.Empty;
    }
}

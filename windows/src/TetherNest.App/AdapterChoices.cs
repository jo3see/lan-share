namespace TetherNest;

/// <summary>Choices for existing upstream connections, not a scan of nearby Wi-Fi networks.</summary>
public static class AdapterChoices
{
    public static bool IsUsableSource(Adapter a) => a.Wifi && a.Physical && a.Connected && a.Gateway;

    public static Adapter[] Sources(IEnumerable<Adapter> adapters)
    {
        // Do not merge by description or PnP device: distinct connected interfaces can be valid.
        var eligible = adapters.Where(IsUsableSource).GroupBy(a => a.Id).Select(g => g.First())
            .OrderBy(a => a.Id).ToArray();
        return eligible.Select((a, i) => a with {
            ChoiceLabel = eligible.Length == 1 ? "Wi-Fi · connected" :
                a.ConnectionClass + " " + (i + 1) + " · connected"
        }).ToArray();
    }

    public static void RefreshPicker(ComboBox box, Adapter[] items)
    {
        // Don't collapse a menu the user is examining. The next timer refresh applies changes.
        if (box.DroppedDown || box.Items.Cast<Adapter>().SequenceEqual(items)) return;
        var selected = (box.SelectedItem as Adapter)?.Id;
        box.BeginUpdate();
        try {
            box.Items.Clear();
            box.Items.AddRange(items.Cast<object>().ToArray());
            if (items.Length > 0)
                box.SelectedIndex = Math.Max(0, Array.FindIndex(items, a => a.Id == selected));
        } finally { box.EndUpdate(); }
    }
}

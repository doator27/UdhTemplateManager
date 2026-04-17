using Avalonia.Controls;
using HardwareTemplateBuilder.Core.Data;
using System.Linq;

namespace HardwareTemplateBuilder.App.Views;

/// <summary>
/// Read-only view showing the seeded <see cref="HardwareTemplateBuilder.Core.Models.DoorMaterial"/> records.
/// Door materials are fixed to "Hollow Metal" and "Wood" and cannot be added or deleted.
/// </summary>
public partial class DoorMaterialsView : UserControl
{
    /// <summary>Raised when the user requests navigation to a named view.</summary>
    public event System.Action<string>? NavigationRequested;

    /// <summary>Initializes the view.</summary>
    public DoorMaterialsView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            LoadList();
        };
    }

    private void LoadList()
    {
        using var context = DatabaseInitializer.CreateContext();
        RecordList.ItemsSource = context.DoorMaterials.ToList();
        RecordList.DisplayMemberBinding = new Avalonia.Data.Binding("Material");
    }
}

using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SqlBackup.Core;

namespace SqlBackup.Desktop;

public static class DestinationKindMenu
{
    public static ContextMenu Create(Action<DestinationKind> selectKind)
    {
        var menu = new ContextMenu { Placement = PlacementMode.Bottom };
        AddOption(menu, "Local / Network / NAS", DestinationKind.Folder, selectKind);
        AddOption(menu, "Azure Blob Storage", DestinationKind.AzureBlob, selectKind);
        AddOption(menu, "Amazon S3", DestinationKind.AmazonS3, selectKind);
        return menu;
    }

    private static void AddOption(ContextMenu menu, string label, DestinationKind kind,
        Action<DestinationKind> selectKind)
    {
        var option = new MenuItem { Header = label };
        option.Click += (_, _) => selectKind(kind);
        menu.Items.Add(option);
    }
}

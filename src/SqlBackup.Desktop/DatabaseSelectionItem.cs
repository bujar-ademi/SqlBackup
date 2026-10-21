using System.ComponentModel;
using SqlBackup.Core;

namespace SqlBackup.Desktop;

public sealed class DatabaseSelectionItem(DatabaseInfo database, bool isSelected) : INotifyPropertyChanged
{
    private bool selected = isSelected;

    public DatabaseInfo Database { get; } = database;
    public bool IsSystem { get; } = database.Name is "master" or "model" or "msdb" or "tempdb";
    public bool CanSelect { get { return Database.CanBackUp || IsSelected; } }

    public bool IsSelected
    {
        get { return selected; }
        set
        {
            if (selected != value)
            {
                selected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanSelect)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

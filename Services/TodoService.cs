using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace DynamicIsland.Services;

/// <summary>一条待办。</summary>
public sealed class TodoItem : System.ComponentModel.INotifyPropertyChanged
{
    private bool _done;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Text { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public bool Done
    {
        get => _done;
        set
        {
            if (_done == value)
            {
                return;
            }

            _done = value;
            Raise(nameof(Done));
            Raise(nameof(CheckGlyph));
            Raise(nameof(TextBrush));
            Raise(nameof(Decorations));
            Raise(nameof(BoxBackground));
            Raise(nameof(BoxBorder));
            Raise(nameof(RowOpacity));
        }
    }

    /// <summary>勾选框里的对勾（完成后是实心金色框 + 深色勾，很容易看出来）</summary>
    public string CheckGlyph => _done ? "\uE73E" : string.Empty;

    /// <summary>勾选框底色</summary>
    public Brush BoxBackground => _done
        ? new SolidColorBrush(Color.FromRgb(0xFF, 0xD1, 0x66))
        : new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));

    /// <summary>勾选框描边</summary>
    public Brush BoxBorder => _done
        ? new SolidColorBrush(Color.FromRgb(0xFF, 0xD1, 0x66))
        : new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));

    public Brush TextBrush => _done
        ? new SolidColorBrush(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF))
        : new SolidColorBrush(Colors.White);

    /// <summary>做完的划掉</summary>
    public TextDecorationCollection? Decorations => _done ? TextDecorations.Strikethrough : null;

    public double RowOpacity => _done ? 0.6 : 1.0;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}

/// <summary>
/// 待办清单。存在 %APPDATA%\DynamicIsland\todos.json，关掉再开还在。
/// </summary>
public sealed class TodoService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public ObservableCollection<TodoItem> Items { get; } = new();

    public event EventHandler? Changed;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DynamicIsland", "todos.json");

    public int ActiveCount
    {
        get
        {
            int count = 0;
            foreach (var item in Items)
            {
                if (!item.Done)
                {
                    count++;
                }
            }
            return count;
        }
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return;
            }

            var loaded = JsonSerializer.Deserialize<List<TodoItem>>(File.ReadAllText(FilePath), JsonOptions);
            if (loaded is null)
            {
                return;
            }

            Items.Clear();
            foreach (var item in loaded)
            {
                if (!string.IsNullOrWhiteSpace(item.Text))
                {
                    Items.Add(item);
                }
            }
        }
        catch
        {
            // 文件坏了就当空的，不要让程序起不来
        }
    }

    public void Save()
    {
        try
        {
            string? dir = Path.GetDirectoryName(FilePath);
            if (dir is not null)
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(FilePath, JsonSerializer.Serialize(Items.ToList(), JsonOptions));
        }
        catch
        {
            // 忽略
        }
    }

    public void Add(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            return;
        }

        // 新的放在最前面
        Items.Insert(0, new TodoItem { Text = text });
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Toggle(TodoItem item)
    {
        item.Done = !item.Done;
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(TodoItem item)
    {
        if (Items.Remove(item))
        {
            Save();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}

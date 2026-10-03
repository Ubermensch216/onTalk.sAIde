namespace OnTalk.Sidecar.Automation;

/// <summary>화면 좌표(픽셀)로 본 요소 위치. 화면에 없는 요소는 크기가 0이다.</summary>
public readonly record struct AccBounds(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public (int X, int Y) Center => (X + Width / 2, Y + Height / 2);
}

/// <summary>접근성 트리의 한 요소. 온톡에서 읽은 뒤에는 온톡과 무관한 값이다.</summary>
public sealed record AccNode(string Role, string Name, string Value, int State, IReadOnlyList<AccNode> Children)
{
    public const string StaticText = "statictext";
    public const string Whitespace = "whitespace";
    public const string Document = "document";
    public const string EditableText = "text";
    public const string CheckBox = "checkbox";
    public const string ComboBox = "combobox";
    public const string ListItem = "listitem";
    public const string Cell = "cell";
    public const string Graphic = "graphic";

    public const int StateChecked = 0x10;

    public AccBounds Bounds { get; init; }

    /// <summary>뿌리에서 이 요소까지의 자식 순번("0/2/1"). 읽은 직후 같은 요소를 다시 찾는 데 쓴다.</summary>
    public string Path { get; init; } = "";

    /// <summary>클릭 같은 기본 동작이 있는지.</summary>
    public bool HasAction { get; init; }

    public IEnumerable<AccNode> DescendantsAndSelf()
    {
        yield return this;
        foreach (var child in Children)
        {
            foreach (var node in child.DescendantsAndSelf())
            {
                yield return node;
            }
        }
    }

    public IEnumerable<string> StaticTexts() =>
        DescendantsAndSelf().Where(n => n.Role == StaticText).Select(n => n.Name);

    public bool ContainsText(string text) => StaticTexts().Any(t => t == text);

    /// <summary>글자가 정확히 <paramref name="text"/>인 요소를 직접 자식으로 가진 요소. 온톡의 단추는 글자를 감싼 DIV다.</summary>
    public AccNode? ButtonLabeled(string text) =>
        DescendantsAndSelf().FirstOrDefault(n => n.Children.Any(c => c.Role == StaticText && c.Name == text));
}

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

    public const int StateChecked = 0x10;

    public AccBounds Bounds { get; init; }

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
}

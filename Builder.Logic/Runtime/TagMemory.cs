using Builder.Core.Tags;
using Builder.Logic.Expressions;
using ValueType = Builder.Logic.Expressions.ValueType;

namespace Builder.Logic.Runtime;

public sealed class TagMemory
{
    private readonly List<Value> _values = [];
    private readonly List<TagDataType> _types = [];
    private readonly List<Guid> _ids = [];
    private readonly List<string?> _texts = [];
    private readonly Dictionary<Guid, int> _slots = new();
    private readonly Dictionary<int, Value> _forced = new();
    private readonly HashSet<int> _bad = [];
    private HashSet<int> _linkBad = [];

    public int Count => _values.Count;

    public IReadOnlyDictionary<Guid, int> Slots => _slots;

    public IReadOnlyDictionary<int, Value> Forced => _forced;

    public IReadOnlyCollection<int> BadQuality => _bad;

    public int Add(Guid id, TagDataType type, Value initial, string? text = null)
    {
        var slot = _values.Count;
        _values.Add(Coerce(type, initial));
        _types.Add(type);
        _ids.Add(id);
        _texts.Add(text);
        _slots[id] = slot;
        return slot;
    }

    public Guid IdOf(int slot) => _ids[slot];

    public TagDataType TypeOf(int slot) => _types[slot];

    public static ValueType LogicType(TagDataType type) => type == TagDataType.Bool ? ValueType.Bool : ValueType.Number;

    public static bool IsLogicType(TagDataType type) => type is not (TagDataType.String or TagDataType.DateTime);

    public Value Get(int slot)
    {
        var value = _forced.TryGetValue(slot, out var forced) ? forced : _values[slot];
        return _bad.Contains(slot) || _linkBad.Contains(slot) ? value with { Good = false } : value;
    }

    public Value GetUnforced(int slot) => _values[slot];

    public string? GetText(int slot) => _texts[slot];

    public void SetText(int slot, string? text)
    {
        if (!_forced.ContainsKey(slot))
            _texts[slot] = text;
    }

    public void Set(int slot, Value value) => _values[slot] = Coerce(_types[slot], value);

    public void Force(int slot, Value value) => _forced[slot] = Coerce(_types[slot], value);

    public bool Unforce(int slot) => _forced.Remove(slot);

    public IReadOnlyCollection<int> LinkBadQuality => _linkBad;

    public void SetLinkBadQuality(IEnumerable<int> slots) => _linkBad = slots.ToHashSet();

    public void SetBadQuality(int slot, bool bad)
    {
        if (bad)
            _bad.Add(slot);
        else
            _bad.Remove(slot);
    }

    public static Value Coerce(TagDataType type, Value value)
    {
        switch (type)
        {
            case TagDataType.Bool:
                return Value.Of(value.Bool, value.Good);
            case TagDataType.Int16:
                return Value.Of(Math.Clamp(Math.Round(value.Number, MidpointRounding.AwayFromZero), short.MinValue, short.MaxValue), value.Good);
            case TagDataType.Int32 or TagDataType.Enum:
                return Value.Of(Math.Clamp(Math.Round(value.Number, MidpointRounding.AwayFromZero), int.MinValue, int.MaxValue), value.Good);
            case TagDataType.Real:
                return Value.Of((double)(float)value.Number, value.Good);
            default:
                return Value.Of(value.Number, value.Good);
        }
    }
}

namespace Builder.Core.Types;

public sealed class CmLibrary
{
    public const string FileSuffix = ".cmtype.json";

    private readonly Dictionary<string, CmType> _types = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CmTypeError> _errors = [];

    public IReadOnlyCollection<CmType> Types => _types.Values;

    public IReadOnlyList<CmTypeError> Errors => _errors;

    public CmType? Find(string name) => _types.GetValueOrDefault(name);

    public static CmLibrary LoadDirectory(string directory)
    {
        var library = new CmLibrary();
        foreach (var file in Directory.EnumerateFiles(directory, $"*{FileSuffix}").Order(StringComparer.Ordinal))
        {
            try
            {
                library.Add(CmTypeLoader.LoadFile(file), Path.GetFileName(file));
            }
            catch (CmTypeException ex)
            {
                library._errors.AddRange(ex.Errors);
            }
        }
        return library;
    }

    public void Replace(CmType type) => _types[type.Name] = type;

    public void Remove(string name) => _types.Remove(name);

    public void Add(CmType type, string fileName = "")
    {
        if (!_types.TryAdd(type.Name, type))
            _errors.Add(new CmTypeError(fileName, "name", $"CM type '{type.Name}' is defined more than once."));
    }
}

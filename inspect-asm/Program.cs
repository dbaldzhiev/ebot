// Inspect what SerializeMemoryReadingNodeToJson actually produces
// for key dict entry types — specifically how _displayX, _lastValue, _setText, _color look
using System.Text.Json;
using read_memory_64_bit;

// Dump the JSON serializer options converters
var opts = EveOnline64.MemoryReadingJsonSerializerOptions;
Console.WriteLine($"Ignore null: {opts.DefaultIgnoreCondition}");
Console.WriteLine($"Converters: {string.Join(", ", opts.Converters.Select(c => c.GetType().Name))}");
Console.WriteLine($"PropertyNamingPolicy: {opts.PropertyNamingPolicy}");
Console.WriteLine($"WriteIndented: {opts.WriteIndented}");

// Build a fake UITreeNode that mimics what EVE would have
// and see how it gets serialized
var fakeDict = new Dictionary<string, object?>
{
    ["_name"]         = "ShipUI",                    // string
    ["_displayX"]     = (long)42,                    // long  → Int64 converter
    ["_displayY"]     = (long)-10,                   // negative long
    ["_displayWidth"] = (long)200,                   // long
    ["_lastValue"]    = 0.95,                        // double
    ["_setText"]      = "Jita",                     // string
};

var serialized = EveOnline64.SerializeMemoryReadingNodeToJson(fakeDict);
Console.WriteLine("\n=== Serialized dict ===");
Console.WriteLine(serialized);

// Also serialize a long directly
Console.WriteLine("\n=== Serialized long 42 ===");
Console.WriteLine(EveOnline64.SerializeMemoryReadingNodeToJson((long)42));

Console.WriteLine("\n=== Serialized double 0.95 ===");
Console.WriteLine(EveOnline64.SerializeMemoryReadingNodeToJson(0.95));

Console.WriteLine("\n=== Serialized string 'Jita' ===");
Console.WriteLine(EveOnline64.SerializeMemoryReadingNodeToJson("Jita"));

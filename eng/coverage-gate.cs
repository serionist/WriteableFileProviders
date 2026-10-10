#:property RestorePackagesWithLockFile=false
#:property PublishAot=false
// The coverage gate: dotnet run eng/coverage-gate.cs -- <folder with *.cobertura.xml reports>
//
// Every assembly of a project under src/ has a floor in eng/coverage.json: the least line and branch coverage the tests
// must reach, in whole percent. The gate reads every Cobertura report under the folder, keeps the best figures of each
// assembly across them (a test project measures what it references too), and fails when an assembly is below its floor,
// has no floor, or has no figures. It prints the floors an assembly beats by a whole point, so they can be raised.
using System.Globalization;
using System.Text.Json;
using System.Xml;

var reportsFolder = args.Length > 0 ? args[0] : "artifacts/TestResults";
var repoRoot = FindRepoRoot(Directory.GetCurrentDirectory());
var floorsFile = Path.Combine(repoRoot, "eng", "coverage.json");

var floors = JsonSerializer.Deserialize<Dictionary<string, Floor>>(File.ReadAllText(floorsFile), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
    ?? throw new InvalidDataException($"{floorsFile} is empty");
var assemblies = Directory.GetDirectories(Path.Combine(repoRoot, "src")).Select(Path.GetFileName).OfType<string>()
    .Where(name => File.Exists(Path.Combine(repoRoot, "src", name, name + ".csproj"))).Order(StringComparer.Ordinal).ToList();

var reports = Directory.Exists(reportsFolder) ? Directory.GetFiles(reportsFolder, "*.cobertura.xml", SearchOption.AllDirectories) : [];
if (reports.Length == 0)
{
    Console.Error.WriteLine($"No *.cobertura.xml report under {reportsFolder}. Run the tests with --coverage first (AGENTS.md, Coverage).");
    return 1;
}

var figures = new Dictionary<string, Figures>(StringComparer.Ordinal);
foreach (var report in reports)
{
    using var reader = XmlReader.Create(report, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
    while (reader.Read())
    {
        if (reader.NodeType != XmlNodeType.Element || reader.Name != "package") continue;
        var name = reader.GetAttribute("name") ?? "";
        var read = new Figures(Rate(reader, "line-rate"), Rate(reader, "branch-rate"));
        figures[name] = figures.TryGetValue(name, out var seen) ? new Figures(Math.Max(seen.Lines, read.Lines), Math.Max(seen.Branches, read.Branches)) : read;
    }
}

var failures = new List<string>();
var raises = new List<string>();
Console.WriteLine($"{"Assembly",-45} {"Lines",9} {"Floor",6} {"Branches",10} {"Floor",6}");
foreach (var assembly in assemblies)
{
    if (!floors.TryGetValue(assembly, out var floor))
    {
        failures.Add($"{assembly} has no floor in eng/coverage.json");
        continue;
    }
    if (!figures.TryGetValue(assembly, out var measured))
    {
        failures.Add($"{assembly} is in no coverage report: no test project references it");
        continue;
    }
    Console.WriteLine($"{assembly,-45} {measured.Lines,8:F2}% {floor.Lines,5}% {measured.Branches,9:F2}% {floor.Branches,5}%");
    // A rate read back from the report can fall a hair under the whole percent it rounds to.
    if (measured.Lines + 1e-9 < floor.Lines) failures.Add($"{assembly}: lines {measured.Lines:F2}% is below its floor of {floor.Lines}%");
    if (measured.Branches + 1e-9 < floor.Branches) failures.Add($"{assembly}: branches {measured.Branches:F2}% is below its floor of {floor.Branches}%");
    if ((int)measured.Lines > floor.Lines || (int)measured.Branches > floor.Branches)
        raises.Add($"  \"{assembly}\": {{ \"lines\": {Math.Max(floor.Lines, (int)measured.Lines)}, \"branches\": {Math.Max(floor.Branches, (int)measured.Branches)} }}");
}
foreach (var stale in floors.Keys.Except(assemblies, StringComparer.Ordinal))
    failures.Add($"eng/coverage.json has a floor for {stale}, which is no project under src/");

if (raises.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("Floors that can rise (whole points under the measured figures):");
    raises.ForEach(Console.WriteLine);
}
if (failures.Count > 0)
{
    Console.Error.WriteLine();
    failures.ForEach(f => Console.Error.WriteLine("FAIL " + f));
    return 1;
}
Console.WriteLine();
Console.WriteLine("Coverage gate passed.");
return 0;

static double Rate(XmlReader reader, string attribute) =>
    double.TryParse(reader.GetAttribute(attribute), NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) && rate is >= 0 and <= 1
        ? rate * 100
        : throw new InvalidDataException($"package {reader.GetAttribute("name")} has no {attribute} between 0 and 1");

static string FindRepoRoot(string from)
{
    for (var dir = new DirectoryInfo(from); dir != null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "WriteableFileProviders.slnx"))) return dir.FullName;
    throw new DirectoryNotFoundException("Run the gate from inside the repo.");
}

internal sealed record Floor(int Lines, int Branches);

internal sealed record Figures(double Lines, double Branches);
